#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Hermes Honyaku
==============
Hermes Agent と llama-server の間に挟む中継サーバー。
モデルが返す思考 (reasoning_content / <think> ... </think>) をリアルタイムに抜き出し、
文単位で翻訳サーバーに投げて、ブラウザ画面に「英語の原文」と「日本語訳」を並べて流します。

- 依存ライブラリなし (Python 3.10 以上の標準ライブラリのみ)
- 起動:  python3 hermes_honyaku.py [config.ini のパス]
- 画面:  http://<このマシン>:8765/
- Hermes 側の設定:  ~/.hermes/config.yaml の model.base_url を http://127.0.0.1:8081/v1 に変更

構成:
  Hermes Agent --> [proxy :8081] --> llama-server :8080 (本体モデル)
                        |
                        +--> 翻訳サーバー (Windows 機の llama-server :8082 など)
                        +--> ブラウザ表示 :8765 (Server-Sent Events)
"""

import collections
import configparser
import datetime
import http.client
import json
import logging
import os
import queue
import re
import socket
import sys
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

APP_DIR = os.path.dirname(os.path.abspath(__file__))
log = logging.getLogger("honyaku")

HOP_BY_HOP = {
    "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
    "te", "trailers", "transfer-encoding", "upgrade",
}


# --------------------------------------------------------------------------
# 設定
# --------------------------------------------------------------------------
class Config:
    def __init__(self, path):
        cp = configparser.ConfigParser(interpolation=None)
        cp.read_dict({
            "proxy": {"listen_host": "127.0.0.1", "listen_port": "8081",
                      "upstream": "http://127.0.0.1:8080", "timeout": "600"},
            "ui": {"listen_host": "0.0.0.0", "listen_port": "8765",
                   "replay_turns": "5", "show_answer": "true"},
            "translator": {"engine": "openai", "url": "http://192.168.1.8:8082/v1",
                           "model": "honyaku", "api_key": "", "workers": "2",
                           "timeout": "120", "temperature": "0.2",
                           "deepl_key": "", "deepl_url": "https://api-free.deepl.com/v2/translate"},
            "segment": {"max_chars": "400", "min_chars": "24", "idle_flush_sec": "2.0"},
            "log": {"dir": "logs", "level": "INFO"},
        })
        if path and os.path.exists(path):
            cp.read(path, encoding="utf-8")
            self.path = path
        else:
            self.path = None
        self.cp = cp

    def get(self, sec, key):
        return self.cp.get(sec, key).strip()

    def getint(self, sec, key):
        return self.cp.getint(sec, key)

    def getfloat(self, sec, key):
        return self.cp.getfloat(sec, key)

    def getbool(self, sec, key):
        return self.cp.getboolean(sec, key)


# --------------------------------------------------------------------------
# イベントバス (ブラウザへの配信 + 履歴)
# --------------------------------------------------------------------------
class EventBus:
    """ブラウザ向けのイベント配信。

    - 直近のイベントはリングバッファに残し、再接続 (Last-Event-ID) の取りこぼしを埋める
    - ターンごとの要約 (思考全文・回答全文・セグメント) も持ち、画面を開き直したときは
      こちらから合成して送る (思考はトークン単位で届くので、生イベントの再生では多すぎる)
    """

    def __init__(self, keep=6000, keep_turns=40):
        self.lock = threading.Lock()
        self.events = collections.deque(maxlen=keep)
        self.next_id = 0
        self.subs = set()
        self.turns = collections.OrderedDict()
        self.keep_turns = keep_turns

    def publish(self, ev):
        ev["ts"] = round(time.time(), 3)
        with self.lock:
            self.next_id += 1
            ev["id"] = self.next_id
            if ev.get("type") != "status":
                self.events.append(ev)
            self._summarize(ev)
            subs = list(self.subs)
        for q in subs:
            try:
                q.put_nowait(ev)
            except queue.Full:
                pass

    def _summarize(self, ev):
        t = ev.get("type")
        n = ev.get("turn")
        if t == "turn_start":
            self.turns[n] = {"start": ev, "think": [], "answer": [], "segs": {}, "end": None}
            while len(self.turns) > self.keep_turns:
                self.turns.popitem(last=False)
            return
        info = self.turns.get(n)
        if info is None:
            return
        if t == "think":
            info["think"].append(ev["text"])
        elif t == "answer":
            info["answer"].append(ev["text"])
        elif t == "seg":
            info["segs"][ev["seg"]] = {"seg": ev, "ja": None}
        elif t == "ja":
            info["segs"].setdefault(ev["seg"], {"seg": None, "ja": None})["ja"] = ev
        elif t == "turn_end":
            info["end"] = ev

    def subscribe(self):
        q = queue.Queue(maxsize=20000)
        with self.lock:
            self.subs.add(q)
        return q

    def unsubscribe(self, q):
        with self.lock:
            self.subs.discard(q)

    def since(self, last_id):
        """last_id より後のイベント。バッファから消えていれば None (全再生が必要)"""
        with self.lock:
            if self.events and self.events[0]["id"] > last_id + 1:
                return None
            return [e for e in self.events if e["id"] > last_id]

    def latest_id(self):
        with self.lock:
            return self.next_id

    def replay_from_recent_turns(self, n):
        """直近 n ターンを要約から合成したイベント列で返す。id はすべて現在の最新 id"""
        with self.lock:
            cur = self.next_id
            turns = list(self.turns.values())[-n:] if n > 0 else []
        out = []
        for info in turns:
            tn = info["start"]["turn"]
            out.append(dict(info["start"], id=cur))
            think = "".join(info["think"])
            if think:
                out.append({"type": "think", "turn": tn, "text": think, "id": cur, "ts": info["start"]["ts"]})
            answer = "".join(info["answer"])
            if answer:
                out.append({"type": "answer", "turn": tn, "text": answer, "id": cur, "ts": info["start"]["ts"]})
            for k in sorted(info["segs"]):
                pair = info["segs"][k]
                if pair["seg"]:
                    out.append(dict(pair["seg"], id=cur))
                if pair["ja"]:
                    out.append(dict(pair["ja"], id=cur))
            if info["end"]:
                out.append(dict(info["end"], id=cur))
        return out


BUS = EventBus()


# --------------------------------------------------------------------------
# JSONL ログ (思考の原文セグメントと訳文だけを残す)
# --------------------------------------------------------------------------
class JsonlLogger:
    def __init__(self, directory):
        self.dir = directory
        self.lock = threading.Lock()
        if self.dir:
            os.makedirs(self.dir, exist_ok=True)

    def write(self, ev):
        if not self.dir:
            return
        if ev.get("type") not in ("turn_start", "seg", "ja", "turn_end"):
            return
        name = datetime.date.today().strftime("%Y%m%d") + ".jsonl"
        line = json.dumps(ev, ensure_ascii=False)
        with self.lock:
            with open(os.path.join(self.dir, name), "a", encoding="utf-8") as f:
                f.write(line + "\n")


# --------------------------------------------------------------------------
# <think> タグ分離 (reasoning_content で来ない場合の保険)
# --------------------------------------------------------------------------
class ThinkTagSplitter:
    OPEN = "<think>"
    CLOSE = "</think>"

    def __init__(self):
        self.buf = ""
        self.in_think = False

    def feed(self, text):
        """content の断片を受け取り、[(kind, text)] を返す。kind は 'think' か 'answer'。"""
        self.buf += text
        out = []
        while self.buf:
            tag = self.CLOSE if self.in_think else self.OPEN
            i = self.buf.find(tag)
            if i >= 0:
                if i > 0:
                    out.append(("think" if self.in_think else "answer", self.buf[:i]))
                self.buf = self.buf[i + len(tag):]
                self.in_think = not self.in_think
                continue
            # タグが途中で切れて届いている可能性があるので、末尾のタグ候補だけ残す
            keep = 0
            for k in range(min(len(tag) - 1, len(self.buf)), 0, -1):
                if tag.startswith(self.buf[-k:]):
                    keep = k
                    break
            emit = self.buf[:len(self.buf) - keep] if keep else self.buf
            if emit:
                out.append(("think" if self.in_think else "answer", emit))
            self.buf = self.buf[len(self.buf) - keep:] if keep else ""
            break
        return out

    def flush(self):
        rest, self.buf = self.buf, ""
        if not rest:
            return []
        return [("think" if self.in_think else "answer", rest)]


# --------------------------------------------------------------------------
# 文分割
# --------------------------------------------------------------------------
BOUNDARY_RE = re.compile(r"(?<=[.!?。！？])[ \t]+|\n+")
ABBREV = {"e.g", "i.e", "etc", "vs", "cf", "mr", "mrs", "ms", "dr", "st", "no", "fig", "approx", "ref", "vol"}
NUMBER_DOT_RE = re.compile(r"(?:^|\s)\d+[.)]$")


def bad_period_boundary(before):
    """ピリオド区切りとして不適切か (番号付き "2." / 略語 "e.g." / 頭文字 "J." など)"""
    if not before.endswith("."):
        return False
    tail = before[:-1]
    if NUMBER_DOT_RE.search(before):
        return True
    last = (tail.split() or [""])[-1].lower().strip("(\"'")
    if last in ABBREV or last.rstrip(".") in ABBREV:
        return True
    if len(last) == 1 and last.isalpha():
        return True
    return False


class Segmenter:
    def __init__(self, max_chars, min_chars):
        self.buf = ""
        self.max_chars = max_chars
        self.min_chars = min_chars
        self.last_feed = time.time()

    def feed(self, text):
        self.buf += text
        self.last_feed = time.time()
        return self._drain()

    def _drain(self):
        segs = []
        while True:
            cut = None
            for m in BOUNDARY_RE.finditer(self.buf):
                is_newline = "\n" in m.group(0)
                if not is_newline:
                    # 区切りの手前が短すぎる / 番号や略語の直後では切らない
                    if m.start() < self.min_chars or bad_period_boundary(self.buf[:m.start()]):
                        continue
                    # 空白区切りは、続きが届いていることを確認してから切る
                    if m.end() >= len(self.buf):
                        break
                cut = (m.start(), m.end())
                break
            if cut is None:
                if len(self.buf) > self.max_chars:
                    # 長すぎる場合は空白で強制分割
                    sp = self.buf.rfind(" ", self.min_chars, self.max_chars)
                    if sp < 0:
                        sp = self.max_chars
                    cut = (sp, sp + 1)
                else:
                    break
            seg = self.buf[:cut[0]].strip()
            self.buf = self.buf[cut[1]:]
            if seg:
                segs.append(seg)
        return segs

    def idle_for(self):
        return time.time() - self.last_feed

    def flush(self):
        rest, self.buf = self.buf.strip(), ""
        return [rest] if rest else []


# --------------------------------------------------------------------------
# ターン (1回の chat/completions 呼び出し)
# --------------------------------------------------------------------------
class Turn:
    counter = 0
    counter_lock = threading.Lock()
    active = {}
    active_lock = threading.Lock()

    def __init__(self, model, context, cfg, translator):
        with Turn.counter_lock:
            Turn.counter += 1
            self.n = Turn.counter
        self.model = model
        self.context = context
        self.translator = translator
        self.lock = threading.Lock()
        self.segmenter = Segmenter(cfg.getint("segment", "max_chars"), cfg.getint("segment", "min_chars"))
        self.tags = ThinkTagSplitter()
        self.seg_count = 0
        self.think_chars = 0
        self.started = time.time()
        self.finished = False
        with Turn.active_lock:
            Turn.active[self.n] = self
        ev = {"type": "turn_start", "turn": self.n, "model": model, "context": context}
        BUS.publish(ev)
        self.translator.jlog.write(ev)

    def feed_think(self, text):
        if not text:
            return
        self.think_chars += len(text)
        BUS.publish({"type": "think", "turn": self.n, "text": text})
        with self.lock:
            segs = self.segmenter.feed(text)
        for s in segs:
            self._submit(s)

    def feed_content(self, text):
        if not text:
            return
        for kind, part in self.tags.feed(text):
            if kind == "think":
                self.feed_think(part)
            else:
                BUS.publish({"type": "answer", "turn": self.n, "text": part})

    def idle_flush(self, idle_sec):
        with self.lock:
            if self.finished or not self.segmenter.buf.strip():
                return
            if self.segmenter.idle_for() < idle_sec:
                return
            segs = self.segmenter.flush()
        for s in segs:
            self._submit(s)

    def _submit(self, text):
        with self.lock:
            self.seg_count += 1
            seg_id = self.seg_count
        self.translator.submit(self.n, seg_id, text)

    def finish(self, reason="stop"):
        for kind, part in self.tags.flush():
            if kind == "think":
                self.feed_think(part)
            else:
                BUS.publish({"type": "answer", "turn": self.n, "text": part})
        with self.lock:
            self.finished = True
            segs = self.segmenter.flush()
        for s in segs:
            self._submit(s)
        with Turn.active_lock:
            Turn.active.pop(self.n, None)
        ev = {"type": "turn_end", "turn": self.n, "reason": reason,
              "think_chars": self.think_chars, "segments": self.seg_count,
              "elapsed": round(time.time() - self.started, 1)}
        BUS.publish(ev)
        self.translator.jlog.write(ev)


# --------------------------------------------------------------------------
# 翻訳
# --------------------------------------------------------------------------
JA_RE = re.compile("[\u3040-\u30ff\u4e00-\u9fff]")
KANA_RE = re.compile("[\u3040-\u30ff]")
THINK_STRIP_RE = re.compile(r"<think>.*?</think>\s*", re.S)
CODEISH_RE = re.compile(r"`[^`]*`|https?://\S+|\S*[/\\._\-]\S*")


def looks_japanese(text):
    """日本語の文とみなせるか。コード・パス・URL は判定から除く"""
    plain = CODEISH_RE.sub(" ", text)
    letters = [c for c in plain if c.isalpha()]
    if not letters:
        return False
    ja = sum(1 for c in letters if JA_RE.match(c))
    if ja == 0:
        return False
    ratio = ja / len(letters)
    # かなを含み、かつ日本語がそれなりの割合を占めていれば日本語の文とみなす
    if KANA_RE.search(plain) and ratio > 0.15:
        return True
    return ratio > 0.3


# 小型モデルは英語入力に英語で返しがちなので、指示は英語で強く書き、日本語の応答例 (few-shot) を付ける
SYSTEM_PROMPT = (
    "You are a professional English-to-Japanese translator. "
    "The input is an AI agent's internal reasoning (its private notes while working). "
    "Translate it into natural Japanese written as a first-person monologue "
    "(e.g. 「〜しよう」「〜だ」「〜かもしれない」「〜する必要がある」). "
    "Output ONLY the Japanese translation. Never reply in English. Never add explanations, notes, or the original text. "
    "Keep code, shell commands, file paths, URLs, and identifiers exactly as they are. "
    "If the input is already Japanese, output it unchanged."
)
FEW_SHOT = [
    {"role": "user", "content": "Let me check the config first. The file may be large, so I should be careful."},
    {"role": "assistant", "content": "まず設定を確認しよう。ファイルが大きいかもしれないので注意が必要だ。"},
    {"role": "user", "content": "The user wants me to check disk usage. I'll run `df -h` and then look at /var/log for errors."},
    {"role": "assistant", "content": "ユーザーはディスク使用量の確認を求めている。`df -h` を実行してから、/var/log のエラーを見よう。"},
    {"role": "user", "content": "Since they're independent, I can batch these calls."},
    {"role": "assistant", "content": "これらは互いに独立しているので、まとめて呼び出せる。"},
]
RETRY_PREFIX = "Translate the following into Japanese. Reply with Japanese text only.\n\n"


class Translator:
    def __init__(self, cfg, jlog):
        self.cfg = cfg
        self.jlog = jlog
        self.engine = cfg.get("translator", "engine").lower()
        self.url = cfg.get("translator", "url").rstrip("/")
        self.model = cfg.get("translator", "model")
        self.api_key = cfg.get("translator", "api_key")
        self.timeout = cfg.getfloat("translator", "timeout")
        self.temperature = cfg.getfloat("translator", "temperature")
        self.deepl_key = cfg.get("translator", "deepl_key")
        self.deepl_url = cfg.get("translator", "deepl_url")
        self.idle_flush_sec = cfg.getfloat("segment", "idle_flush_sec")
        self.q = queue.Queue()
        self.status = "unknown"
        self.status_lock = threading.Lock()
        self.workers = max(1, cfg.getint("translator", "workers"))
        self.last_error = ""

    def start(self):
        for i in range(self.workers):
            t = threading.Thread(target=self._worker, name=f"translator-{i}", daemon=True)
            t.start()
        threading.Thread(target=self._idle_loop, name="idle-flush", daemon=True).start()

    def submit(self, turn, seg_id, text):
        ev = {"type": "seg", "turn": turn, "seg": seg_id, "src": text}
        BUS.publish(ev)
        self.jlog.write(ev)
        self.q.put((turn, seg_id, text))
        self._publish_status()

    def _set_status(self, st, err=""):
        with self.status_lock:
            changed = (st != self.status) or (err != self.last_error)
            self.status = st
            self.last_error = err
        if changed:
            self._publish_status()

    def _publish_status(self):
        BUS.publish({"type": "status", "translator": self.status, "engine": self.engine,
                     "queue": self.q.qsize(), "error": self.last_error})

    def _idle_loop(self):
        while True:
            time.sleep(0.5)
            with Turn.active_lock:
                turns = list(Turn.active.values())
            for t in turns:
                try:
                    t.idle_flush(self.idle_flush_sec)
                except Exception:
                    log.exception("idle flush failed")

    def _worker(self):
        while True:
            turn, seg_id, text = self.q.get()
            t0 = time.time()
            try:
                ja, how = self.translate(text)
                self._set_status("ok")
                ev = {"type": "ja", "turn": turn, "seg": seg_id, "text": ja, "how": how,
                      "ok": how != "en", "sec": round(time.time() - t0, 2)}
            except Exception as e:
                log.warning("translate failed: %s", e)
                self._set_status("error", str(e)[:200])
                ev = {"type": "ja", "turn": turn, "seg": seg_id, "text": f"[翻訳失敗: {e}]",
                      "how": self.engine, "ok": False, "sec": round(time.time() - t0, 2)}
            BUS.publish(ev)
            self.jlog.write(ev)
            self._publish_status()

    # ---- エンジン ----
    def translate(self, text):
        """(訳文, how) を返す。how が "en" のときは日本語にできなかった (原文のまま/英語のまま)"""
        if self.engine == "none":
            return text, "none"
        if looks_japanese(text):
            return text, "skip"
        if self.engine == "deepl":
            return self._deepl(text), "deepl"
        # コードやパスだけの行は翻訳しない
        if not re.search(r"[A-Za-z]{3,}", text):
            return text, "skip"
        out = self._openai(text)
        if looks_japanese(out):
            return out, "openai"
        # 英語のまま返ってきたら、指示を前置きして温度 0 でもう一度
        log.info("translator replied in English, retrying: %r", out[:60])
        out2 = self._openai(RETRY_PREFIX + text, temperature=0.0)
        if looks_japanese(out2):
            return out2, "openai-retry"
        return out2 or out, "en"

    def _openai(self, text, temperature=None):
        payload = {
            "model": self.model,
            "messages": [{"role": "system", "content": SYSTEM_PROMPT}] + FEW_SHOT + [
                {"role": "user", "content": text},
            ],
            "temperature": self.temperature if temperature is None else temperature,
            "max_tokens": 96 + len(text) * 2,
            "stream": False,
            # Qwen3 系: 翻訳モデル自身の思考を止める (対応していないサーバーでは無視される)
            "chat_template_kwargs": {"enable_thinking": False},
        }
        headers = {"Content-Type": "application/json"}
        if self.api_key:
            headers["Authorization"] = "Bearer " + self.api_key
        req = urllib.request.Request(self.url + "/chat/completions",
                                     data=json.dumps(payload).encode("utf-8"),
                                     headers=headers, method="POST")
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        msg = data["choices"][0]["message"]
        out = (msg.get("content") or "").strip()
        out = THINK_STRIP_RE.sub("", out).strip()
        if len(out) >= 2 and out[0] in "\"「" and out[-1] in "\"」":
            out = out[1:-1]
        return out or "(空の応答)"

    def _deepl(self, text):
        if not self.deepl_key:
            raise RuntimeError("deepl_key が未設定")
        body = urllib.parse.urlencode({"text": text, "target_lang": "JA", "source_lang": "EN"}).encode()
        req = urllib.request.Request(self.deepl_url, data=body, method="POST", headers={
            "Authorization": "DeepL-Auth-Key " + self.deepl_key,
            "Content-Type": "application/x-www-form-urlencoded",
        })
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        return data["translations"][0]["text"]


# --------------------------------------------------------------------------
# 非ストリーミング応答の組み立て (Hermes が stream=false で来た場合用)
# --------------------------------------------------------------------------
class ResponseAccumulator:
    def __init__(self):
        self.id = None
        self.model = None
        self.created = None
        self.fingerprint = None
        self.content = ""
        self.reasoning = ""
        self.tool_calls = {}
        self.finish_reason = None
        self.usage = None

    def add(self, obj):
        self.id = self.id or obj.get("id")
        self.model = self.model or obj.get("model")
        self.created = self.created or obj.get("created")
        self.fingerprint = self.fingerprint or obj.get("system_fingerprint")
        if obj.get("usage"):
            self.usage = obj["usage"]
        for ch in obj.get("choices") or []:
            if ch.get("finish_reason"):
                self.finish_reason = ch["finish_reason"]
            d = ch.get("delta") or {}
            if d.get("content"):
                self.content += d["content"]
            r = d.get("reasoning_content") or d.get("reasoning")
            if r:
                self.reasoning += r
            for tc in d.get("tool_calls") or []:
                idx = tc.get("index", 0)
                cur = self.tool_calls.setdefault(idx, {"id": None, "type": "function",
                                                       "function": {"name": "", "arguments": ""}})
                if tc.get("id"):
                    cur["id"] = tc["id"]
                if tc.get("type"):
                    cur["type"] = tc["type"]
                fn = tc.get("function") or {}
                if fn.get("name"):
                    cur["function"]["name"] += fn["name"]
                if fn.get("arguments"):
                    cur["function"]["arguments"] += fn["arguments"]

    def build(self):
        msg = {"role": "assistant", "content": self.content if self.content else None}
        if self.reasoning:
            msg["reasoning_content"] = self.reasoning
        if self.tool_calls:
            msg["tool_calls"] = [self.tool_calls[k] for k in sorted(self.tool_calls)]
            if msg["content"] is None:
                pass
        elif msg["content"] is None:
            msg["content"] = ""
        out = {
            "id": self.id or "chatcmpl-honyaku",
            "object": "chat.completion",
            "created": self.created or int(time.time()),
            "model": self.model or "",
            "choices": [{"index": 0, "message": msg, "finish_reason": self.finish_reason or "stop"}],
        }
        if self.fingerprint:
            out["system_fingerprint"] = self.fingerprint
        if self.usage:
            out["usage"] = self.usage
        return out


# --------------------------------------------------------------------------
# 中継サーバー
# --------------------------------------------------------------------------
def summarize_context(req):
    """画面のターン見出し用に、直近のメッセージを短く要約する。"""
    try:
        msgs = req.get("messages") or []
        if not msgs:
            return ""
        m = msgs[-1]
        role = m.get("role", "?")
        c = m.get("content")
        if isinstance(c, list):
            c = " ".join(p.get("text", "") for p in c if isinstance(p, dict))
        if not isinstance(c, str):
            c = ""
        if role == "tool" and c.lstrip().startswith("{"):
            try:
                j = json.loads(c)
                if isinstance(j, dict):
                    for k in ("output", "result", "content", "error", "stdout"):
                        if isinstance(j.get(k), str):
                            c = j[k]
                            break
            except Exception:
                pass
        c = re.sub(r"\s+", " ", c).strip()
        if role == "tool":
            name = m.get("name") or ""
            return f"tool{('(' + name + ')') if name else ''}: {c[:100]}"
        return f"{role}: {c[:120]}"
    except Exception:
        return ""


class ProxyHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    cfg = None
    translator = None
    upstream = None  # (scheme, host, port)

    def log_message(self, fmt, *args):
        log.debug("proxy %s - " + fmt, self.client_address[0], *args)

    def do_GET(self):
        self._proxy()

    def do_POST(self):
        self._proxy()

    def do_PUT(self):
        self._proxy()

    def do_DELETE(self):
        self._proxy()

    def do_PATCH(self):
        self._proxy()

    def do_OPTIONS(self):
        self._proxy()

    def do_HEAD(self):
        self._proxy()

    # ---- 共通 ----
    def _read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n > 0 else b""

    def _upstream_headers(self, body_len):
        h = {}
        for k, v in self.headers.items():
            lk = k.lower()
            if lk in HOP_BY_HOP or lk in ("host", "content-length", "accept-encoding"):
                continue
            h[k] = v
        h["Host"] = f"{self.upstream[1]}:{self.upstream[2]}"
        h["Content-Length"] = str(body_len)
        h["Connection"] = "close"
        return h

    def _connect(self):
        scheme, host, port = self.upstream
        timeout = self.cfg.getfloat("proxy", "timeout")
        if scheme == "https":
            return http.client.HTTPSConnection(host, port, timeout=timeout)
        return http.client.HTTPConnection(host, port, timeout=timeout)

    def _begin(self, status, headers, chunked, content_length=None):
        self.send_response(status)
        for k, v in headers:
            if k.lower() in HOP_BY_HOP or k.lower() in ("content-length", "content-encoding"):
                continue
            self.send_header(k, v)
        if chunked:
            self.send_header("Transfer-Encoding", "chunked")
        else:
            self.send_header("Content-Length", str(content_length or 0))
        self.end_headers()
        self._chunked = chunked

    def _write(self, data):
        if not data:
            return
        if self._chunked:
            self.wfile.write(b"%x\r\n" % len(data) + data + b"\r\n")
        else:
            self.wfile.write(data)
        self.wfile.flush()

    def _end(self):
        if self._chunked:
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()

    def _send_error_json(self, status, message):
        body = json.dumps({"error": {"message": message, "type": "proxy_error"}}).encode()
        self._begin(status, [("Content-Type", "application/json")], False, len(body))
        self._write(body)

    # ---- 振り分け ----
    def _proxy(self):
        body = self._read_body()
        path = urllib.parse.urlsplit(self.path).path.rstrip("/")
        if self.command == "POST" and path.endswith("/chat/completions"):
            try:
                req = json.loads(body.decode("utf-8"))
                if not isinstance(req, dict):
                    raise ValueError
            except Exception:
                req = None
            if req is not None:
                return self._chat(req)
        return self._passthrough(body)

    def _passthrough(self, body):
        conn = None
        try:
            conn = self._connect()
            conn.request(self.command, self.path, body=body, headers=self._upstream_headers(len(body)))
            resp = conn.getresponse()
            hdrs = resp.getheaders()
            cl = resp.getheader("Content-Length")
            if self.command == "HEAD":
                self._begin(resp.status, hdrs, False, int(cl or 0))
                return
            if cl is not None:
                data = resp.read()
                self._begin(resp.status, hdrs, False, len(data))
                self._write(data)
            else:
                self._begin(resp.status, hdrs, True)
                while True:
                    chunk = resp.read1(65536) if hasattr(resp, "read1") else resp.read(65536)
                    if not chunk:
                        break
                    self._write(chunk)
                self._end()
        except (BrokenPipeError, ConnectionResetError):
            log.info("client disconnected (passthrough)")
        except Exception as e:
            log.warning("passthrough error: %s", e)
            try:
                self._send_error_json(502, f"upstream error: {e}")
            except Exception:
                pass
        finally:
            if conn:
                conn.close()

    def _chat(self, req):
        client_stream = bool(req.get("stream"))
        up_req = dict(req)
        up_req["stream"] = True
        if not client_stream:
            so = dict(up_req.get("stream_options") or {})
            so["include_usage"] = True
            up_req["stream_options"] = so
        body = json.dumps(up_req, ensure_ascii=False).encode("utf-8")

        conn = None
        turn = None
        try:
            conn = self._connect()
            conn.request("POST", self.path, body=body, headers=self._upstream_headers(len(body)))
            resp = conn.getresponse()
            ctype = resp.getheader("Content-Type") or ""
            if resp.status != 200 or "text/event-stream" not in ctype:
                # エラー等はそのまま返す
                data = resp.read()
                self._begin(resp.status, resp.getheaders(), False, len(data))
                self._write(data)
                if resp.status != 200:
                    BUS.publish({"type": "error", "text": f"上流 {resp.status}: {data[:300].decode('utf-8', 'replace')}"})
                return

            turn = Turn(req.get("model") or "", summarize_context(req), self.cfg, self.translator)
            acc = ResponseAccumulator()
            if client_stream:
                self._begin(200, resp.getheaders(), True)

            finish = "stop"
            while True:
                line = resp.readline()
                if not line:
                    break
                if client_stream:
                    self._write(line)
                s = line.strip()
                if not s.startswith(b"data:"):
                    continue
                payload = s[5:].strip()
                if payload == b"[DONE]":
                    continue
                try:
                    obj = json.loads(payload.decode("utf-8"))
                except Exception:
                    continue
                if not client_stream:
                    acc.add(obj)
                for ch in obj.get("choices") or []:
                    d = ch.get("delta") or {}
                    r = d.get("reasoning_content") or d.get("reasoning")
                    if r:
                        turn.feed_think(r)
                    c = d.get("content")
                    if c:
                        turn.feed_content(c)
                    if ch.get("finish_reason"):
                        finish = ch["finish_reason"]
            turn.finish(finish)
            turn = None

            if client_stream:
                self._end()
            else:
                data = json.dumps(acc.build(), ensure_ascii=False).encode("utf-8")
                self._begin(200, [("Content-Type", "application/json")], False, len(data))
                self._write(data)
        except (BrokenPipeError, ConnectionResetError):
            log.info("client disconnected (chat)")
            if turn:
                turn.finish("client_disconnected")
        except Exception as e:
            log.exception("chat proxy error")
            if turn:
                turn.finish("error")
            try:
                self._send_error_json(502, f"upstream error: {e}")
            except Exception:
                pass
        finally:
            if conn:
                conn.close()


# --------------------------------------------------------------------------
# 表示用 Web サーバー
# --------------------------------------------------------------------------
INDEX_HTML = r"""<!doctype html>
<html lang="ja"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Hermes Honyaku</title>
<style>
:root{--bg:#0f1418;--panel:#151c22;--line:#25313a;--ink:#dfe7ec;--muted:#7f929e;--en:#a9bac6;--ja:#f2f6f8;--acc:#4fc3b8;--warn:#e5a33b;--bad:#f08a82;--ans:#8fb0c8}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.7 "Noto Sans JP","Hiragino Sans","Yu Gothic UI",system-ui,sans-serif}
header{position:sticky;top:0;z-index:5;display:flex;gap:16px;align-items:center;padding:8px 16px;background:var(--panel);border-bottom:1px solid var(--line);font-size:13px;flex-wrap:wrap}
header b{font-size:15px;color:var(--acc)}
.dot{display:inline-block;width:9px;height:9px;border-radius:50%;background:var(--muted);margin-right:5px;vertical-align:middle}
.dot.ok{background:var(--acc)}.dot.error{background:var(--bad)}.dot.busy{background:var(--warn)}
header label{color:var(--muted);cursor:pointer;user-select:none}
header .sp{flex:1}
main{padding:12px 16px 40vh}
.turn{border:1px solid var(--line);border-radius:8px;margin:0 0 14px;background:var(--panel);overflow:hidden}
.turn h3{margin:0;padding:6px 12px;font-size:12.5px;font-weight:600;color:var(--muted);border-bottom:1px solid var(--line);display:flex;gap:12px;flex-wrap:wrap}
.turn h3 .n{color:var(--acc);font-family:ui-monospace,Consolas,monospace}
.turn h3 .ctx{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--en)}
.turn h3 .st{font-family:ui-monospace,Consolas,monospace}
.cols{display:grid;grid-template-columns:1fr 1fr;gap:0}
@media (max-width:900px){.cols{grid-template-columns:1fr}}
.col{padding:10px 12px;min-width:0}
.col+.col{border-left:1px solid var(--line)}
@media (max-width:900px){.col+.col{border-left:none;border-top:1px solid var(--line)}}
.col .cap{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--muted);margin-bottom:6px}
.think{white-space:pre-wrap;word-break:break-word;color:var(--en);font-size:13.5px;line-height:1.6}
.think.live::after{content:"▍";color:var(--acc);animation:bl 1s steps(2) infinite}
@keyframes bl{50%{opacity:0}}
.answer{white-space:pre-wrap;word-break:break-word;color:var(--ans);font-size:13px;margin-top:10px;padding-top:8px;border-top:1px dashed var(--line)}
.answer:empty{display:none}
body.noans .answer{display:none}
.seg{margin:0 0 8px;padding:6px 10px;border-left:3px solid var(--line);border-radius:0 4px 4px 0}
.seg .src{color:var(--muted);font-size:12px;line-height:1.5}
.seg .ja{color:var(--ja);font-size:15px}
.seg.pending{border-left-color:var(--warn)}
.seg.done{border-left-color:var(--acc)}
.seg.done .src{display:none}
body.showsrc .seg.done .src{display:block}
.seg.bad{border-left-color:var(--bad)}.seg.bad .ja{color:var(--bad)}
.seg.untranslated{border-left-color:var(--warn)}.seg.untranslated .ja{color:var(--en)}
.empty{color:var(--muted);padding:40px;text-align:center}
#tobottom{position:fixed;right:20px;bottom:20px;z-index:6;display:none;background:var(--acc);color:#0f1418;border:none;border-radius:20px;padding:8px 16px;font:600 13px/1 inherit;cursor:pointer;box-shadow:0 2px 10px rgba(0,0,0,.4)}
#tobottom.show{display:block}
</style></head><body>
<header>
 <b>Hermes Honyaku</b>
 <span><span id="sdot" class="dot"></span><span id="stext">接続中…</span></span>
 <span title="翻訳サーバーの状態"><span id="tdot" class="dot"></span>翻訳: <span id="ttext">-</span> <span id="tq"></span></span>
 <span class="sp"></span>
 <label><input type="checkbox" id="auto" checked> 自動スクロール</label>
 <label><input type="checkbox" id="newest"> 新しいターンを上に</label>
 <label><input type="checkbox" id="showsrc"> 訳文の下に原文も表示</label>
 <label><input type="checkbox" id="showans" checked> 回答本文も表示</label>
 <label><button id="clear" style="background:none;border:1px solid var(--line);color:var(--muted);border-radius:4px;padding:2px 8px;cursor:pointer">画面を消去</button></label>
</header>
<button id="tobottom" type="button">↓ 最新へ (自動スクロール再開)</button>
<main id="main"><div class="empty" id="empty">Hermes Agent からの要求を待っています。<br>~/.hermes/config.yaml の model.base_url を中継サーバーに向けてください。</div></main>
<script>
(function(){
const main=document.getElementById('main'), empty=document.getElementById('empty');
const turns={};
const auto=document.getElementById('auto'), newest=document.getElementById('newest'), tobottom=document.getElementById('tobottom');
const showsrc=document.getElementById('showsrc'), showans=document.getElementById('showans');
// 表示設定はブラウザに記憶する
function pref(key,el,apply){try{const v=localStorage.getItem('hh.'+key);if(v!==null)el.checked=(v==='1')}catch(e){}apply(el.checked);
  el.addEventListener('change',()=>{try{localStorage.setItem('hh.'+key,el.checked?'1':'0')}catch(e){}apply(el.checked)})}
pref('showsrc',showsrc,v=>document.body.classList.toggle('showsrc',v));
pref('showans',showans,v=>document.body.classList.toggle('noans',!v));
pref('newest',newest,v=>{const els=[...main.querySelectorAll('.turn')];els.sort((a,b)=>(Number(a.id.slice(1))-Number(b.id.slice(1)))*(v?-1:1));els.forEach(e=>main.appendChild(e));if(v)window.scrollTo(0,0);updateBtn()});
pref('auto',auto,v=>updateBtn());
document.getElementById('clear').onclick=()=>{for(const k in turns){turns[k].el.remove();delete turns[k]}};
function esc(s){return s.replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]))}
function fmt(ts){const d=new Date(ts*1000);return d.toTimeString().slice(0,8)}
function atBottom(){return document.documentElement.scrollHeight-window.scrollY-window.innerHeight<80}
function updateBtn(){tobottom.classList.toggle('show',!newest.checked&&!auto.checked)}
let programmatic=false;
function scroll(){if(newest.checked||!auto.checked)return;programmatic=true;window.scrollTo(0,document.documentElement.scrollHeight);requestAnimationFrame(()=>{programmatic=false})}
// 読んでいる途中で上にスクロールしたら自動スクロールを止める。最下部まで戻したら再開
window.addEventListener('scroll',()=>{if(programmatic||newest.checked)return;
  if(auto.checked&&!atBottom()){auto.checked=false;updateBtn()}
  else if(!auto.checked&&atBottom()){auto.checked=true;updateBtn()}},{passive:true});
tobottom.onclick=()=>{auto.checked=true;updateBtn();scroll()};
function turn(n){return turns[n]}
function onTurnStart(ev){
  empty.style.display='none';
  const el=document.createElement('section');el.className='turn';el.id='t'+ev.turn;
  el.innerHTML='<h3><span class="n">#'+ev.turn+'</span><span>'+fmt(ev.ts)+'</span><span>'+esc(ev.model||'')+'</span><span class="ctx">'+esc(ev.context||'')+'</span><span class="st">思考中…</span></h3>'+
   '<div class="cols"><div class="col"><div class="cap">Thinking (原文)</div><div class="think live"></div><div class="answer"></div></div>'+
   '<div class="col"><div class="cap">日本語</div><div class="segs"></div></div></div>';
  if(newest.checked)main.insertBefore(el,main.firstElementChild.nextSibling);else main.appendChild(el);
  turns[ev.turn]={el,think:el.querySelector('.think'),answer:el.querySelector('.answer'),segs:el.querySelector('.segs'),st:el.querySelector('.st'),segEls:{}};
  // 古いターンは間引く
  const keys=Object.keys(turns).map(Number).sort((a,b)=>a-b);
  while(keys.length>40){const k=keys.shift();turns[k].el.remove();delete turns[k]}
  scroll();
}
function onThink(ev){const t=turn(ev.turn);if(!t)return;t.think.appendChild(document.createTextNode(ev.text));scroll()}
function onAnswer(ev){const t=turn(ev.turn);if(!t)return;t.answer.appendChild(document.createTextNode(ev.text));scroll()}
function onSeg(ev){const t=turn(ev.turn);if(!t)return;
  let s=t.segEls[ev.seg];
  if(!s){s=document.createElement('div');s.className='seg pending';s.dataset.seg=ev.seg;
    s.innerHTML='<div class="src"></div><div class="ja"></div>';
    // seg 番号順に挿入
    let after=null;for(const c of t.segs.children){if(Number(c.dataset.seg)<ev.seg)after=c}
    if(after)after.after(s);else t.segs.prepend(s);
    t.segEls[ev.seg]=s;}
  s.querySelector('.src').textContent=ev.src;scroll()}
function onJa(ev){const t=turn(ev.turn);if(!t)return;let s=t.segEls[ev.seg];if(!s){onSeg({turn:ev.turn,seg:ev.seg,src:''});s=t.segEls[ev.seg]}
  s.querySelector('.ja').textContent=ev.text;s.classList.remove('pending');
  s.classList.add(ev.how==='en'?'untranslated':ev.ok?'done':'bad');
  if(ev.how==='skip'||ev.how==='en')s.querySelector('.src').style.display='none';scroll()}
function onTurnEnd(ev){const t=turn(ev.turn);if(!t)return;t.think.classList.remove('live');
  t.st.textContent=(ev.reason==='stop'||ev.reason==='tool_calls'||ev.reason==='length'?'完了':ev.reason)+' · '+ev.elapsed+'s · '+ev.think_chars+'字 · '+ev.segments+'文';
  if(!t.think.textContent.trim())t.think.textContent='(思考なし)'}
function onStatus(ev){const d=document.getElementById('tdot');d.className='dot '+(ev.translator==='ok'?(ev.queue>0?'busy':'ok'):ev.translator==='error'?'error':'');
  document.getElementById('ttext').textContent=ev.engine+(ev.translator==='error'?' エラー: '+ev.error:'');
  document.getElementById('tq').textContent=ev.queue>0?'(待ち '+ev.queue+')':''}
function onError(ev){const d=document.createElement('div');d.className='seg bad';d.innerHTML='<div class="ja"></div>';d.querySelector('.ja').textContent=ev.text;main.appendChild(d)}
const H={turn_start:onTurnStart,think:onThink,answer:onAnswer,seg:onSeg,ja:onJa,turn_end:onTurnEnd,status:onStatus,error:onError};
function connect(){
  const es=new EventSource('/events');
  es.onopen=()=>{document.getElementById('sdot').className='dot ok';document.getElementById('stext').textContent='接続中'};
  es.onerror=()=>{document.getElementById('sdot').className='dot error';document.getElementById('stext').textContent='再接続待ち…'};
  es.onmessage=e=>{try{const ev=JSON.parse(e.data);const h=H[ev.type];if(h)h(ev)}catch(err){console.error(err)}};
}
connect();
})();
</script></body></html>
"""


class UIHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    cfg = None
    translator = None

    def log_message(self, fmt, *args):
        log.debug("ui %s - " + fmt, self.client_address[0], *args)

    def _send(self, status, ctype, data):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = urllib.parse.urlsplit(self.path).path
        if path in ("/", "/index.html"):
            return self._send(200, "text/html; charset=utf-8", INDEX_HTML.encode("utf-8"))
        if path == "/events":
            return self._events()
        if path == "/api/status":
            body = {"translator": self.translator.status, "engine": self.translator.engine,
                    "queue": self.translator.q.qsize(), "error": self.translator.last_error,
                    "turns": Turn.counter, "active": sorted(Turn.active.keys())}
            return self._send(200, "application/json; charset=utf-8", json.dumps(body, ensure_ascii=False).encode())
        if path == "/api/history":
            qs = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
            since = int((qs.get("since") or ["0"])[0])
            evs = BUS.since(since)
            return self._send(200, "application/json; charset=utf-8", json.dumps(evs, ensure_ascii=False).encode())
        if path == "/health":
            return self._send(200, "text/plain", b"ok")
        return self._send(404, "text/plain", b"not found")

    def _events(self):
        last = self.headers.get("Last-Event-ID")
        qs = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
        if last is None and qs.get("since"):
            last = qs["since"][0]
        q = BUS.subscribe()
        try:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream; charset=utf-8")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "close")
            self.send_header("X-Accel-Buffering", "no")
            self.end_headers()
            backlog = None
            if last is not None and str(last).isdigit():
                backlog = BUS.since(int(last))
                seen = int(last)
            if backlog is None:
                backlog = BUS.replay_from_recent_turns(self.cfg.getint("ui", "replay_turns"))
                seen = BUS.latest_id()
            for ev in backlog:
                self._emit(ev)
                seen = max(seen, ev["id"])
            # 状態を一度送る
            self._emit({"id": seen, "type": "status", "translator": self.translator.status,
                        "engine": self.translator.engine, "queue": self.translator.q.qsize(),
                        "error": self.translator.last_error, "ts": time.time()})
            while True:
                try:
                    ev = q.get(timeout=15)
                except queue.Empty:
                    self.wfile.write(b": ping\n\n")
                    self.wfile.flush()
                    continue
                if ev["id"] <= seen:
                    continue
                self._emit(ev)
                seen = ev["id"]
        except (BrokenPipeError, ConnectionResetError, socket.timeout):
            pass
        except Exception as e:
            log.debug("events closed: %s", e)
        finally:
            BUS.unsubscribe(q)

    def _emit(self, ev):
        data = json.dumps(ev, ensure_ascii=False)
        self.wfile.write(f"id: {ev.get('id', 0)}\ndata: {data}\n\n".encode("utf-8"))
        self.wfile.flush()


# --------------------------------------------------------------------------
# 起動
# --------------------------------------------------------------------------
class Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    request_queue_size = 64


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(APP_DIR, "config.ini")
    cfg = Config(path)
    logging.basicConfig(level=getattr(logging, cfg.get("log", "level").upper(), logging.INFO),
                        format="%(asctime)s %(levelname)s %(message)s")
    if cfg.path:
        log.info("config: %s", cfg.path)
    else:
        log.warning("config.ini が見つからないので既定値で起動します (%s)", path)

    logdir = cfg.get("log", "dir")
    if logdir and not os.path.isabs(logdir):
        logdir = os.path.join(APP_DIR, logdir)
    jlog = JsonlLogger(logdir)

    translator = Translator(cfg, jlog)
    translator.start()

    up = urllib.parse.urlsplit(cfg.get("proxy", "upstream"))
    ProxyHandler.cfg = cfg
    ProxyHandler.translator = translator
    ProxyHandler.upstream = (up.scheme or "http", up.hostname, up.port or (443 if up.scheme == "https" else 80))
    UIHandler.cfg = cfg
    UIHandler.translator = translator

    proxy = Server((cfg.get("proxy", "listen_host"), cfg.getint("proxy", "listen_port")), ProxyHandler)
    ui = Server((cfg.get("ui", "listen_host"), cfg.getint("ui", "listen_port")), UIHandler)
    threading.Thread(target=proxy.serve_forever, name="proxy", daemon=True).start()
    threading.Thread(target=ui.serve_forever, name="ui", daemon=True).start()
    log.info("proxy  : http://%s:%d/v1  ->  %s", cfg.get("proxy", "listen_host"), cfg.getint("proxy", "listen_port"), cfg.get("proxy", "upstream"))
    log.info("ui     : http://%s:%d/", cfg.get("ui", "listen_host"), cfg.getint("ui", "listen_port"))
    log.info("translator: engine=%s url=%s model=%s workers=%d", translator.engine, translator.url, translator.model, translator.workers)
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        log.info("bye")


if __name__ == "__main__":
    main()
