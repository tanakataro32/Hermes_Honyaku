#!/usr/bin/env python3
"""llmsv のハードウェアモニタ (温度・電力・使用率・メモリ・ディスク・電気代) を JSON で配る小さなサーバー。

Windows の専用アプリ (windows/HwMonitor) が読む。値の集め方は hermes_honyaku.py の
GpuMonitor・SysMonitor (CpuPower・TempSensors) をそのまま使い回すので、翻訳の画面のメーターと同じ値になる。
翻訳の中継 (hermes-honyaku、8081) とは別のプロセスなので、こちらを再起動しても Hermes などの通信は止まらない。

  GET /api/now                    いまの値 (2 秒ごとに更新)
  GET /api/history?range=24h      履歴 (2m / 10m / 30m / 1h / 3h / 6h / 24h / 7d / 30d)。1h までは 2〜10 秒刻み、ほかは 1 分の記録をまとめたもの
  GET /api/energy                 電気代 (今日・今月・日ごと 31 日・月ごと 13 か月) と、トークン数・レンタルとの比べ (compare)
  GET /api/speed?days=7           要求ごとの速さと文脈の量 (llama-server のログから。アプリの「速度と文脈」の散布図)
  GET /health

トークン数は llama-server の /slots ([hwmon] llama_servers) を 0.5 秒ごとに読んで数える
(枠ごとの n_decoded・n_prompt_tokens の増え方。終わる直前の 0.5 秒ぶんは数え漏れることがあるので概数)。

設定は hermes_honyaku と同じ config.ini (+ config.local.ini) の [hwmon]・[power]・[gpu]・[sysmon]。
記録は SQLite ([hwmon] db、既定 ~/.local/share/hermes-honyaku/hwmon.sqlite)。
1 分ごとの平均を keep_days 日 (既定 35) 残し、日ごとの電力量 (Wh) はずっと残す。
"""

import bisect
import calendar
import collections
import datetime
import json
import logging
import os
import re
import signal
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import hermes_honyaku as hh

log = logging.getLogger("hwmon")

# 温度の色の境目 (満杯・黄・赤、℃)。翻訳の画面 (TEMPK) と同じ
TEMPK = {"cpu": (100, 85, 95), "gpu": (90, 75, 85), "ddr": (90, 70, 80), "ssd": (85, 65, 75),
         "board": (80, 55, 70), "lan": (110, 85, 100)}

# 履歴の刻み: range -> (さかのぼる秒, 刻みの秒)。1 時間までは (メモリの) 2 秒の記録から、ほかは 1 分の記録から
RANGES = {"2m": (120, 2), "10m": (600, 2), "30m": (1800, 5), "1h": (3600, 10), "3h": (3 * 3600, 60), "6h": (6 * 3600, 60), "24h": (86400, 240),
          "7d": (7 * 86400, 1800), "30d": (30 * 86400, 7200)}


def snapshot(gpus, sm, pcfg):
    """GPU と /proc・hwmon の値から、画面に出す形と、履歴に残す平たい形 (キー -> 数値) を作る。
    キーは「種類:名前」 (p = 電力 W、t = 温度 ℃、u = 使用率 %、m = 使用中のメモリ GB)"""
    flat = {}
    gpus = gpus or []
    sm = sm or {}
    pw = sm.get("power") or {}
    other = float(pcfg.get("other_w", 50))
    eff = float(pcfg.get("psu_eff", 0.9))
    eff = eff if 0 < eff <= 1 else 1.0
    cpu_w = pw.get("cpu")
    gpu_w = sum(g.get("power") or 0 for g in gpus)
    dc = gpu_w + (cpu_w or 0) + other
    wall = dc / eff
    max_dc = sum(g.get("power_limit") or g.get("max_power") or 0 for g in gpus) + float(pcfg.get("cpu_max_w", 88)) + other
    psu_w = float(pcfg.get("psu_w", 0))
    full = psu_w if psu_w > 0 else max_dc / eff
    parts = [{"key": "gpu:" + g["label"], "label": g["label"], "kind": "gpu", "w": g.get("power")} for g in gpus]
    parts.append({"key": "cpu", "label": "CPU", "kind": "cpu", "w": cpu_w})
    parts.append({"key": "other", "label": "その他", "kind": "other", "w": other, "est": True})
    parts.append({"key": "loss", "label": "電源ロス", "kind": "loss", "w": wall - dc, "est": True})
    flat["p:全体"] = wall
    for g in gpus:
        flat["p:" + g["label"]] = g.get("power")
    flat["p:CPU"] = cpu_w

    temps = []
    sys_temps = sm.get("temps") or []
    items = [t for t in sys_temps if t.get("kind") == "cpu"]
    items += [{"kind": "gpu", "label": g["label"], "temp": g.get("temp"), "tip": f"GPU {g['index']} ({g['name']}) nvidia-smi"} for g in gpus]
    items += [t for t in sys_temps if t.get("kind") != "cpu"]
    for t in items:
        if t.get("temp") is None:
            continue
        mx, warn, hot = TEMPK.get(t["kind"], TEMPK["board"])
        temps.append({"label": t["label"], "kind": t["kind"], "c": t["temp"], "max": mx, "warn": warn, "hot": hot, "tip": t.get("tip", "")})
        flat["t:" + t["label"]] = t["temp"]

    util = [{"label": "CPU", "kind": "cpu", "pct": sm.get("cpu"), "throttle": ""}]
    flat["u:CPU"] = sm.get("cpu")
    for g in gpus:
        th = g.get("throttle") or []
        level = "hot" if any(x["kind"] in ("thermal", "hw") for x in th) else "warn" if th else ""
        util.append({"label": g["label"], "kind": "gpu", "pct": g.get("util"), "throttle": level,
                     "clock": g.get("clock"), "clock_max": g.get("clock_max"),
                     "reasons": [x["text"] for x in th]})
        flat["u:" + g["label"]] = g.get("util")

    mem = []
    if sm.get("mem_total"):
        mem.append({"label": "RAM", "kind": "ram", "used": sm.get("mem_used"), "cache": sm.get("mem_cache"), "total": sm.get("mem_total")})
        flat["m:RAM"] = sm.get("mem_used")
    for g in gpus:
        mem.append({"label": g["label"], "kind": "vram", "used": g["mem_used"] / 1024.0, "total": g["mem_total"] / 1024.0})
        flat["m:" + g["label"]] = g["mem_used"] / 1024.0

    disks = [{"label": d.get("label_short") or d.get("device"), "tip": d.get("label") or "", "used": d.get("used"), "total": d.get("total")}
             for d in (sm.get("disks") or [])]
    view = {"power": {"wall": wall, "dc": dc, "full": full, "eff": eff, "parts": parts, "cpu_src": pw.get("cpu_src", "")},
            "temps": temps, "util": util, "mem": mem, "disks": disks}
    flat = {k: round(v, 2) for k, v in flat.items() if isinstance(v, (int, float))}
    return view, flat, wall


def parse_pairs(text):
    """「名前=値, 名前=値」を [(名前, 値), ...] に"""
    out = []
    for part in (text or "").split(","):
        if "=" in part:
            k, v = part.split("=", 1)
            if k.strip() and v.strip():
                out.append((k.strip(), v.strip()))
    return out


class SlotWatcher:
    """llama-server の /slots を読んで、枠ごとの使っているトークン数と、読み込み・生成したトークン数・推論していた時間を数える。
    数えた分は take() で取り出す (HwMonitor が 2 秒ごとに取り、1 分ごとに記録する)"""

    def __init__(self, servers, interval=0.5):
        self.servers = servers  # [(名前, URL)]
        self.interval = interval
        self.lock = threading.Lock()
        self.slots = {}   # (名前, 枠) -> 状態
        self.cnt = {name: {"in": 0, "proc": 0, "out": 0, "busy": 0.0} for name, _ in servers}
        self.busy_any = 0.0
        # 読み込み (プロンプトの処理) の数は、この版の llama-server では処理が終わった瞬間にまとめて増える。
        # そのまま 2 秒の 1 点に乗せると何百 t/s の山になるので、(名前, 始まり, 終わり, トークン数) を渡し、
        # HwMonitor がその時間に割り振る (グラフの速さだけ。日ごとの合計はそのまま)
        self.spans = []
        self.up = {name: False for name, _ in servers}
        self.last = None
        if servers:
            log.info("トークン: %s", ", ".join(f"{n}={u}" for n, u in servers))
            threading.Thread(target=self._loop, name="slots", daemon=True).start()

    def _loop(self):
        while True:
            try:
                self._poll()
            except Exception:
                log.exception("slots: 読み取りに失敗 (続けます)")
            time.sleep(self.interval)

    def _get(self, url):
        with urllib.request.urlopen(url.rstrip("/") + "/slots", timeout=1.5) as r:
            return json.loads(r.read())

    def _poll(self):
        now = time.monotonic()
        dt = now - self.last if self.last is not None and now - self.last < 5 else 0.0
        self.last = now
        got = {}
        for name, url in self.servers:
            try:
                got[name] = self._get(url)
            except Exception:
                got[name] = None
        with self.lock:
            any_busy = False
            for name, slots in got.items():
                self.up[name] = slots is not None
                if not slots:
                    continue
                c = self.cnt[name]
                busy = False
                for sl in slots:
                    key = (name, sl.get("id"))
                    st = self.slots.setdefault(key, {"task": None, "dec": 0, "proc": 0, "prompt_added": False, "used": 0,
                                                     "prompt": 0, "decoded": 0, "tps": 0.0, "processing": False, "p0": 0, "d0": 0, "started": None,
                                                     "last_active": None, "n_ctx": 0})
                    nt = sl.get("next_token") or {}
                    if isinstance(nt, list):
                        nt = nt[0] if nt else {}
                    dec = int(nt.get("n_decoded") or 0)
                    pp = int(sl.get("n_prompt_tokens_processed") or 0)
                    npt = int(sl.get("n_prompt_tokens") or 0)
                    st["n_ctx"] = int(sl.get("n_ctx") or 0)
                    if sl.get("is_processing"):
                        busy = True
                        if sl.get("id_task") != st["task"]:  # 新しい要求
                            # started = 新しい要求を初めて見た時刻 (アプリが翻訳の中継のターンの始まりと時刻で結ぶのに使う。0.5 秒ほど遅れる)
                            st.update(task=sl.get("id_task"), dec=0, proc=0, prompt_added=False, tps=0.0, p0=0, d0=0,
                                      started=time.time(), proc_t=time.time(), pp_rate=0.0, pp_span=0.0)
                        d_out = max(0, dec - st["dec"])
                        c["out"] += d_out
                        d_proc = max(0, pp - st["proc"])
                        if d_proc:
                            c["proc"] += d_proc
                            t_now = time.time()
                            a0 = st.get("proc_t") or t_now
                            self.spans.append((name, a0, t_now, d_proc))  # 前に増えた時 (または要求を受けた時) から今まで
                            st["proc_t"] = t_now
                            if t_now - a0 >= 0.5:  # この枠の直前のバッチの速さ (読んでいる最中のグラフの右端の見込みに使う)
                                st["pp_rate"], st["pp_span"] = d_proc / (t_now - a0), t_now - a0
                        if not st["prompt_added"] and npt > 0:
                            # 初めて見えたときの量。llama.cpp の版によって n_prompt_tokens に生成した分が入るので、
                            # そのとき生成済みの分 (d0) を引いて「要求の入力」にする (API の入力に当たる量。キャッシュの分も含む)
                            st.update(prompt_added=True, p0=npt, d0=dec)
                            c["in"] += max(0, npt - dec)
                        if dt > 0:
                            st["tps"] = st["tps"] * 0.6 + d_out / dt * 0.4 if st["tps"] else d_out / dt
                        # 枠が使っている量: n_prompt_tokens に生成分が入る版でも入らない版でも合うように
                        used = max(npt, st["p0"] + dec - st["d0"]) if st["prompt_added"] else npt + dec
                        st.update(dec=max(dec, st["dec"]), proc=max(pp, st["proc"]), used=used, prompt=max(0, st["p0"] - st["d0"]),
                                  decoded=dec, processing=True, last_active=time.time())
                    else:
                        st.update(processing=False, tps=0.0)  # 使っていた量 (used) は残しておく
                        if not st["used"] and npt > 0:
                            # 起動し直した直後で、まだ動いているところを見ていない枠。止まっている枠も n_prompt_tokens に
                            # 前の要求の量 (この版は生成分も入る) が残っているので、それを初めの値にする
                            st["used"] = npt
                if busy:
                    c["busy"] += dt
                    any_busy = True
            if any_busy:
                self.busy_any += dt

    def take(self):
        """前回から数えた分を取り出す: ({名前: {in, proc, out, busy}}, 推論していた秒 (どれかの枠が動いていた),
        読み込みの区間 [(名前, 始まり, 終わり, トークン数)])"""
        with self.lock:
            out = self.cnt
            self.cnt = {name: {"in": 0, "proc": 0, "out": 0, "busy": 0.0} for name, _ in self.servers}
            b, self.busy_any = self.busy_any, 0.0
            spans, self.spans = self.spans, []
        return out, b, spans

    def reading(self):
        """いま読み込み (プロンプトの処理) の最中の枠: {名前: [(前に数が増えた時, その枠の直前のバッチの速さ), ...]}。
        数は 2048 トークン (1 バッチ) ごとにしか増えないので、増えるまでの間はグラフの右端がゼロになる。アプリはこの見込みで右端を描く。
        この要求で 1 バッチ以上読み進んだ枠だけ (ほかの枠の後ろで待っているだけの枠は入れない)。
        直前のバッチの 2 倍 (最低 15 秒) たっても増えない枠は、ほかの枠を待っているとみなして外す"""
        out = {}
        now = time.time()
        with self.lock:
            for (name, _), st in self.slots.items():
                if self._reading(st, now):
                    out.setdefault(name, []).append((st["proc_t"], st["pp_rate"]))
        return out

    @staticmethod
    def _reading(st, now):
        """この枠がいま読み込みの最中か (reading() の説明の条件)。lock の中で呼ぶ"""
        return bool(st["processing"] and st["decoded"] == 0 and st.get("pp_rate")
                    and now - st["proc_t"] <= max(2 * st.get("pp_span", 0), 15))

    def view(self):
        now = time.time()
        with self.lock:
            out = []
            for name, _ in self.servers:
                # tps = 生成の速さ、pp_tps = 読み込みの速さ (読み込みの最中だけ、直前のバッチの速さ。ほかは 0)。アプリのバーの色に使う
                slots = [dict(id=k[1], **{x: v[x] for x in ("n_ctx", "used", "prompt", "decoded", "processing", "last_active", "started")},
                              tps=round(v["tps"], 1), pp_tps=round(v["pp_rate"], 1) if self._reading(v, now) else 0.0)
                         for k, v in sorted(self.slots.items(), key=lambda kv: kv[0][1]) if k[0] == name]
                out.append({"server": name, "up": self.up.get(name, False), "slots": slots})
            return out


class CoreStat:
    """スレッド (論理 CPU) ごとの使用率 (/proc/stat の cpu0, cpu1, … の前回との差)。アプリの「スレッド」の欄"""

    def __init__(self):
        self.prev = self._read()

    @staticmethod
    def _read():
        out = {}
        try:
            with open("/proc/stat") as f:
                for line in f:
                    if line.startswith("cpu") and line[3:4].isdigit():
                        p = line.split()
                        v = [int(x) for x in p[1:]]
                        idle = v[3] + (v[4] if len(v) > 4 else 0)   # idle + iowait
                        out[int(p[0][3:])] = (sum(v), idle)
        except OSError:
            pass
        return out

    def take(self):
        cur = self._read()
        pct = []
        for i in sorted(cur):
            a, b = self.prev.get(i), cur[i]
            dt = b[0] - a[0] if a else 0
            pct.append(round(max(0.0, min(100.0, (1 - (b[1] - a[1]) / dt) * 100)), 1) if dt > 0 else 0.0)
        self.prev = cur
        return pct


class ReqLog:
    """llama-server のログ (journalctl -u <ユニット>) から、要求ごとの速さと文脈の量を拾って SQLite に残す。
    アプリの「速度と文脈」の散布図 (文脈が長くなると生成・読み込みがどれだけ遅くなるか) に使う。
    1 件 = 1 要求: 読み込み (prompt eval time)・生成 (eval time)・MTP の当たり率 (draft acceptance)・終わったときの文脈の量 (stop processing: n_tokens)。
    起動のたびに、モデルのファイル名と枠の数 (load_model) を「設定」として付ける (設定を変えたときの比べ用)。
    ログは 30 秒ごとに続きを読む (読んだ所は meta に覚えておく。初めは journal に残っている全部)"""

    RX = {
        "load": re.compile(r"load_model: loading model '([^']+)'"),
        "slots": re.compile(r"load_model: initializing, n_slots = (\d+), n_ctx_slot = (\d+)"),
        "mtp": re.compile(r"creating MTP draft context|common_speculative_init"),
        "prompt": re.compile(r"id\s+(\d+) \| task (\d+) \| prompt eval time =\s*([\d.]+) ms /\s*(\d+) tokens"),
        "eval": re.compile(r"id\s+(\d+) \| task (\d+) \|\s+eval time =\s*([\d.]+) ms /\s*(\d+) tokens"),
        "draft": re.compile(r"id\s+(\d+) \| task (\d+) \| draft acceptance = ([\d.]+)"),
        "release": re.compile(r"id\s+(\d+) \| task (\d+) \| stop processing: n_(?:tokens|past) = (\d+)"),
    }

    def __init__(self, path, units, interval=30):
        self.units = units          # [(名前, systemd のユニット)]
        self.interval = interval
        self.lock = threading.Lock()
        self.db = sqlite3.connect(path, check_same_thread=False, timeout=30)
        self.db.execute("CREATE TABLE IF NOT EXISTS requests (server TEXT, ts REAL, slot INTEGER, task INTEGER, run REAL,"
                        " pp_n INTEGER, pp_ms REAL, gen_n INTEGER, gen_ms REAL, draft REAL, ctx INTEGER, PRIMARY KEY (server, ts, task))")
        self.db.execute("CREATE TABLE IF NOT EXISTS runs (server TEXT, start REAL, model TEXT, n_slots INTEGER, n_ctx INTEGER, mtp INTEGER,"
                        " PRIMARY KEY (server, start))")
        self.db.execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)")
        self.db.commit()
        self.pending = {}           # (名前, 枠, 要求) -> 途中まで拾った値
        if units:
            threading.Thread(target=self._loop, name="reqlog", daemon=True).start()

    def _meta(self, key, value=None):
        if value is None:
            r = self.db.execute("SELECT value FROM meta WHERE key = ?", (key,)).fetchone()
            return json.loads(r[0]) if r else None
        self.db.execute("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                        (key, json.dumps(value)))

    def _loop(self):
        while True:
            for name, unit in self.units:
                try:
                    self._read(name, unit)
                except Exception:
                    log.exception("reqlog: %s のログを読めません (続けます)", unit)
            time.sleep(self.interval)

    def _read(self, name, unit):
        cur_key, run_key = f"cursor:{unit}", f"run:{name}"
        with self.lock:
            cursor = self._meta(cur_key)
            run = self._meta(run_key) or {}
        cmd = ["journalctl", "-u", unit, "-o", "json", "--output-fields=MESSAGE", "--no-pager"]
        if cursor:
            cmd += ["--after-cursor", cursor]
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
        rows, runs, last = [], [], None
        for line in p.stdout.splitlines():
            try:
                e = json.loads(line)
            except ValueError:
                continue
            last = e.get("__CURSOR") or last
            msg = e.get("MESSAGE")
            if not isinstance(msg, str):
                continue
            ts = int(e.get("__REALTIME_TIMESTAMP", 0)) / 1e6
            m = self.RX["load"].search(msg)
            if m:   # 起動し直した: 新しい設定
                model = os.path.basename(m.group(1))
                model = re.sub(r"-\d{5}-of-\d{5}", "", model).removesuffix(".gguf")
                run = {"start": ts, "model": model, "n_slots": 0, "n_ctx": 0, "mtp": 0}
                self.pending = {k: v for k, v in self.pending.items() if k[0] != name}
                continue
            m = self.RX["slots"].search(msg)
            if m and run:
                run.update(n_slots=int(m.group(1)), n_ctx=int(m.group(2)))
                runs.append(dict(run))
                continue
            if self.RX["mtp"].search(msg) and run:
                run["mtp"] = 1
                continue
            for kind in ("prompt", "eval", "draft", "release"):
                m = self.RX[kind].search(msg)
                if not m:
                    continue
                key = (name, int(m.group(1)), int(m.group(2)))
                r = self.pending.setdefault(key, {})
                if kind == "prompt":
                    r.update(pp_ms=float(m.group(3)), pp_n=int(m.group(4)))
                elif kind == "eval":
                    r.update(gen_ms=float(m.group(3)), gen_n=int(m.group(4)))
                elif kind == "draft":
                    r["draft"] = float(m.group(3))
                else:   # 終わり: 1 件として残す
                    self.pending.pop(key, None)
                    if "gen_n" in r or "pp_n" in r:
                        rows.append((name, ts, key[1], key[2], run.get("start"), r.get("pp_n"), r.get("pp_ms"), r.get("gen_n"),
                                     r.get("gen_ms"), r.get("draft"), int(m.group(3))))
                break
        with self.lock:
            for rn in runs:
                self.db.execute("INSERT OR REPLACE INTO runs (server, start, model, n_slots, n_ctx, mtp) VALUES (?, ?, ?, ?, ?, ?)",
                                (name, rn["start"], rn["model"], rn["n_slots"], rn["n_ctx"], rn["mtp"]))
            if run.get("start") is not None:   # MTP は n_slots の後に分かるので、いまの設定は毎回書き直す
                self.db.execute("UPDATE runs SET mtp = ? WHERE server = ? AND start = ?", (run.get("mtp", 0), name, run["start"]))
            self.db.executemany("INSERT OR IGNORE INTO requests (server, ts, slot, task, run, pp_n, pp_ms, gen_n, gen_ms, draft, ctx)"
                                " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", rows)
            if last:
                self._meta(cur_key, last)
            self._meta(run_key, run)
            self.db.commit()
        if rows:
            log.info("reqlog: %s の要求 %d 件を記録", name, len(rows))

    def speed(self, days):
        """散布図の点。並列数 (par) = 生成の間に同じサーバーで動いていたほかの要求の数 (+1)"""
        since = time.time() - days * 86400 if days > 0 else 0
        with self.lock:
            runs = self.db.execute("SELECT server, start, model, n_slots, n_ctx, mtp FROM runs ORDER BY start").fetchall()
            reqs = self.db.execute("SELECT server, ts, slot, run, pp_n, pp_ms, gen_n, gen_ms, draft, ctx FROM requests"
                                   " WHERE ts >= ? ORDER BY server, ts", (since - 3600,)).fetchall()
        # 要求ごとの [始まり, 終わり] (終わり − 読み込み − 生成)。並列数は生成の真ん中の時刻で数える
        starts, ends = collections.defaultdict(list), collections.defaultdict(list)
        for r in reqs:
            starts[r[0]].append(r[1] - ((r[5] or 0) + (r[7] or 0)) / 1000.0)
            ends[r[0]].append(r[1])
        for d in (starts, ends):
            for v in d.values():
                v.sort()
        points = []
        for r in reqs:
            server, ts, slot, run, pp_n, pp_ms, gen_n, gen_ms, draft, ctx = r
            if ts < since:
                continue
            mid = ts - (gen_ms or 0) / 2000.0
            # 始まりが mid 以前の数 − 終わりが mid より前の数 = mid に動いていた要求の数 (自分を含む)
            par = max(1, bisect.bisect_right(starts[server], mid) - bisect.bisect_left(ends[server], mid))
            gen_n, pp_n = gen_n or 0, pp_n or 0
            points.append({
                "t": round(ts, 1), "server": server, "slot": slot, "run": run, "par": par, "ctx": ctx,
                "gen_n": gen_n, "gen_tps": round(gen_n / gen_ms * 1000, 2) if gen_n and gen_ms else None,
                "pp_n": pp_n, "pp_tps": round(pp_n / pp_ms * 1000, 1) if pp_n and pp_ms else None,
                "pp_from": max(0, ctx - gen_n - pp_n),   # 読み込みを始めたときの文脈の量 (前の要求から使い回した分)
                "draft": draft})
        return {"runs": [{"server": s, "start": st, "model": m, "n_slots": n, "n_ctx": c, "mtp": bool(mt)} for s, st, m, n, c, mt in runs],
                "points": points}


class Store:
    """SQLite の記録: minutes (1 分ごとの平均と電力量) と days (日ごとの電力量)"""

    def __init__(self, path, keep_days):
        self.path = path
        self.keep_days = keep_days
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.lock = threading.Lock()
        self.db = sqlite3.connect(path, check_same_thread=False)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("CREATE TABLE IF NOT EXISTS minutes (ts INTEGER PRIMARY KEY, wh REAL, secs REAL, data TEXT)")
        self.db.execute("CREATE TABLE IF NOT EXISTS days (date TEXT PRIMARY KEY, wh REAL, secs REAL)")
        # 日ごと・llama-server ごとのトークン数と推論していた秒。server = '*' の行は「どれかが推論していた秒」と、
        # トークンを数えていた間の電力量・秒 (比べはこの時間の電気代で行う。数え始める前の電気代を混ぜない)
        self.db.execute("CREATE TABLE IF NOT EXISTS tokens (date TEXT, server TEXT, tin INTEGER, tproc INTEGER, tout INTEGER, busy REAL,"
                        " wh REAL DEFAULT 0, secs REAL DEFAULT 0, PRIMARY KEY (date, server))")
        cols = [r[1] for r in self.db.execute("PRAGMA table_info(tokens)")]
        for col in ("wh", "secs"):
            if col not in cols:
                self.db.execute(f"ALTER TABLE tokens ADD COLUMN {col} REAL DEFAULT 0")
        self.db.commit()
        self._last_prune = 0

    def add_minute(self, ts, wh, secs, data, toks=None):
        date = time.strftime("%Y-%m-%d", time.localtime(ts))
        with self.lock:
            for server, c in (toks or {}).items():
                if any(c.values()):
                    self.db.execute("INSERT INTO tokens (date, server, tin, tproc, tout, busy, wh, secs) VALUES (?, ?, ?, ?, ?, ?, ?, ?) "
                                    "ON CONFLICT(date, server) DO UPDATE SET tin = tin + excluded.tin, tproc = tproc + excluded.tproc,"
                                    " tout = tout + excluded.tout, busy = busy + excluded.busy, wh = wh + excluded.wh, secs = secs + excluded.secs",
                                    (date, server, c["in"], c["proc"], c["out"], c["busy"], c.get("wh", 0.0), c.get("secs", 0.0)))
            self.db.execute("INSERT OR REPLACE INTO minutes (ts, wh, secs, data) VALUES (?, ?, ?, ?)",
                            (ts, wh, secs, json.dumps(data, ensure_ascii=False, separators=(",", ":"))))
            self.db.execute("INSERT INTO days (date, wh, secs) VALUES (?, ?, ?) "
                            "ON CONFLICT(date) DO UPDATE SET wh = wh + excluded.wh, secs = secs + excluded.secs",
                            (date, wh, secs))
            if ts - self._last_prune > 3600:
                self.db.execute("DELETE FROM minutes WHERE ts < ?", (ts - self.keep_days * 86400,))
                self._last_prune = ts
            self.db.commit()

    def add_to_minute(self, ts, key, delta):
        """書き込み済みの 1 分の平均に足す (読み込みを、かかった時間に割り振るとき)"""
        with self.lock:
            row = self.db.execute("SELECT data FROM minutes WHERE ts = ?", (ts,)).fetchone()
            if not row:
                return
            data = json.loads(row[0])
            data[key] = round(data.get(key, 0.0) + delta, 2)
            self.db.execute("UPDATE minutes SET data = ? WHERE ts = ?", (json.dumps(data, ensure_ascii=False, separators=(",", ":")), ts))
            self.db.commit()

    def minutes_since(self, since):
        with self.lock:
            return self.db.execute("SELECT ts, data FROM minutes WHERE ts >= ? ORDER BY ts", (since,)).fetchall()

    def days(self, since_date):
        with self.lock:
            return self.db.execute("SELECT date, wh, secs FROM days WHERE date >= ? ORDER BY date", (since_date,)).fetchall()

    def tokens(self, since_date):
        with self.lock:
            return self.db.execute("SELECT date, server, tin, tproc, tout, busy, wh, secs FROM tokens WHERE date >= ? ORDER BY date",
                                   (since_date,)).fetchall()


class HwMonitor:
    def __init__(self, cfg):
        self.cfg = cfg
        self.pcfg = {k: cfg.get("power", k) for k in ("other_w", "psu_eff", "psu_w", "cpu_max_w")}
        self.price = cfg.getfloat("hwmon", "price_yen_kwh")
        # レンタルとの比べ: GPU の時間貸し ($/時) と API の従量 ($/100 万トークン、入力/出力)、ドル円
        self.usd_jpy = cfg.getfloat("rental", "usd_jpy")
        self.gpu_usd_h = cfg.getfloat("rental", "gpu_usd_per_hour")
        self.api = {}
        for name, v in parse_pairs(cfg.get("rental", "api")):
            try:
                pin, pout = (float(x) for x in v.split("/"))
                self.api[name] = (pin, pout)
            except ValueError:
                log.warning("[rental] api の %s が読めません (入力/出力 の $ を / で区切る): %s", name, v)
        self.slots = SlotWatcher(parse_pairs(cfg.get("hwmon", "llama_servers")))
        self.cores = CoreStat()
        self.interval = cfg.getfloat("sysmon", "interval")
        self.gpu = hh.GpuMonitor(interval=cfg.getfloat("gpu", "interval"), power_max=cfg.getfloat("gpu", "power_max"))
        self.sys = hh.SysMonitor(interval=self.interval, power={
            "cpu_source": cfg.get("power", "cpu_source").lower(),
            "other_w": cfg.getfloat("power", "other_w"), "psu_eff": cfg.getfloat("power", "psu_eff"),
            "psu_w": cfg.getfloat("power", "psu_w"), "cpu_max_w": cfg.getfloat("power", "cpu_max_w")})
        db = os.path.expanduser(cfg.get("hwmon", "db"))
        self.store = Store(db, cfg.getint("hwmon", "keep_days"))
        log.info("記録: %s (1 分ごとの記録は %d 日残す)", db, self.store.keep_days)
        try:
            units = parse_pairs(cfg.get("hwmon", "llama_units"))
        except Exception:
            units = []
        self.reqlog = ReqLog(db, units)
        if units:
            log.info("要求の記録: %s のログから", ", ".join(f"{n}={u}" for n, u in units))
        self.lock = threading.Lock()
        self.now = None
        self.ring = collections.deque(maxlen=1800)  # 2 秒 × 1 時間
        self.minute = None      # いま貯めている 1 分の始まり
        self.acc = {}           # キー -> [合計, 個数]
        self.wh = 0.0           # この 1 分の電力量
        self.secs = 0.0         # この 1 分のうち測れた秒
        self.toks = {}          # この 1 分のトークン数: 名前 -> {in, proc, out, busy}。'*' は「どれかが推論していた秒」
        self.last = None
        threading.Thread(target=self._loop, name="hwmon", daemon=True).start()

    def _loop(self):
        time.sleep(min(self.interval, 3) + 0.5)  # 最初の 1 回 (CPU 使用率・nvidia-smi) がそろうのを待つ
        while True:
            try:
                self._tick()
            except Exception:
                log.exception("hwmon: 集計に失敗 (続けます)")
            time.sleep(self.interval)

    def _tick(self):
        t = time.time()
        view, flat, wall = snapshot(self.gpu.current(), self.sys.current(), self.pcfg)
        cnt, busy_any, spans = self.slots.take()
        dt_tok = t - self.last if self.last is not None and 0 < t - self.last <= 10 else 0
        for name, c in cnt.items():
            if dt_tok:  # 履歴のグラフ用は 1 秒あたり。読み込みは下の _spread で、かかった時間に割り振って足す
                flat[f"r:{name} 読込"] = 0.0
                flat[f"r:{name} 生成"] = round(c["out"] / dt_tok, 1)
        view["slots"] = self.slots.view()
        view["cores"] = self.cores.take()   # スレッドごとの使用率 (記録には入れない)
        # 枠が使っている量 (文脈、k トークン)。使ったことのある枠だけ (待機中は最後に使っていた量のまま)。アプリのトークンの 3 段目
        for srv in view["slots"]:
            for sl in srv["slots"]:
                if sl["used"] > 0:
                    flat[f"r:{srv['server']} 枠{sl['id']} 文脈"] = round(sl["used"] / 1000.0, 1)
        m = int(t // 60 * 60)
        with self.lock:
            if self.minute is not None and m != self.minute:
                self._flush_locked()
            if self.minute is None or m != self.minute:
                self.minute = m
            # 電力量 = 前回からの秒 × いまの W。止まっていた間 (10 秒より空いた) は数えない
            if self.last is not None and 0 < t - self.last <= 10:
                dt = t - self.last
                self.wh += wall * dt / 3600.0
                self.secs += dt
            self.last = t
            for name, c in cnt.items():
                a = self.toks.setdefault(name, {"in": 0, "proc": 0, "out": 0, "busy": 0.0})
                for k in a:
                    a[k] += c[k]
            if busy_any:
                self.toks.setdefault("*", {"in": 0, "proc": 0, "out": 0, "busy": 0.0})["busy"] += busy_any
            for k, v in flat.items():
                a = self.acc.setdefault(k, [0.0, 0])
                a[0] += v
                a[1] += 1
            self.ring.append((t, flat))
            for span in spans:
                self._spread_locked(*span)
            view["ts"] = t
            self.now = view

    def _spread_locked(self, name, a, b, n):
        """読み込み n トークンを、区間 [a, b] の速さ (n ÷ 秒) として、重なる 2 秒の記録・いまの 1 分・書き込み済みの 1 分に足す"""
        key = f"r:{name} 読込"
        rate = n / max(b - a, 0.5)
        if b - a < 0.5:
            a = b - 0.5
        prev = None
        for t, f in self.ring:
            start = prev if prev is not None and t - prev <= 10 else t - self.interval
            prev = t
            ov = min(t, b) - max(start, a)
            if ov <= 0 or key not in f:
                continue
            add = rate * ov / (t - start)   # その 2 秒の平均の速さに足す分
            f[key] = round(f[key] + add, 1)
            if t >= self.minute:            # いま貯めている 1 分にも
                acc = self.acc.setdefault(key, [0.0, 0])
                acc[0] += add
        # 書き込み済みの 1 分 (読み込みが分をまたいだとき)
        m = int(a // 60 * 60)
        while m < self.minute and m < b:
            ov = min(m + 60, b) - max(m, a)
            if ov > 0:
                try:
                    self.store.add_to_minute(m, key, rate * ov / 60)
                except Exception:
                    log.exception("hwmon: 読み込みの割り振りの書き込みに失敗")
            m += 60

    def _flush_locked(self):
        if not self.acc and self.wh == 0:
            return
        data = {k: round(s / n, 2) for k, (s, n) in self.acc.items() if n}
        if self.slots.servers:  # トークンを数えていた間の電力量と秒 (比べ用)
            a = self.toks.setdefault("*", {"in": 0, "proc": 0, "out": 0, "busy": 0.0})
            a["wh"] = a.get("wh", 0.0) + self.wh
            a["secs"] = a.get("secs", 0.0) + self.secs
        try:
            self.store.add_minute(self.minute, self.wh, self.secs, data, self.toks)
        except Exception:
            log.exception("hwmon: 記録の書き込みに失敗")
        self.acc, self.wh, self.secs, self.toks = {}, 0.0, 0.0, {}

    def flush(self):
        with self.lock:
            self._flush_locked()

    # ---- API ----
    def current(self):
        with self.lock:
            if self.now is None:
                return None
            out = dict(self.now)
        out["energy"] = self.energy_brief()
        return out

    def history(self, rng):
        span, step = RANGES.get(rng, RANGES["24h"])
        now = time.time()
        since = now - span
        buckets = collections.OrderedDict()
        filled = False
        if span <= 3600:
            # 2 秒の記録はメモリにしか無いので、起動し直した直後は足りない。足りない前の部分は 1 分の記録で埋める
            with self.lock:
                rows = [(t, f) for t, f in self.ring if t >= since]
            start = rows[0][0] if rows else now
            if start - since > 60:
                filled = True
                old = [(ts + 30, json.loads(d)) for ts, d in self.store.minutes_since(int(since)) if ts + 60 <= start]
                rows = old + rows
        else:
            rows = [(ts + 30, json.loads(d)) for ts, d in self.store.minutes_since(int(since))]
            # まだ書き込んでいないいまの 1 分も足す
            with self.lock:
                if self.acc:
                    rows.append((self.minute + 30, {k: s / n for k, (s, n) in self.acc.items() if n}))
        for t, f in rows:
            b = int(t // step * step)
            acc = buckets.setdefault(b, {})
            for k, v in f.items():
                a = acc.setdefault(k, [0.0, 0])
                a[0] += v
                a[1] += 1
        keys = []
        for acc in buckets.values():
            for k in acc:
                if k not in keys:
                    keys.append(k)
        ts = list(buckets.keys())
        series = {k: [round(acc[k][0] / acc[k][1], 2) if k in acc else None for acc in buckets.values()] for k in keys}
        # 線をつなぐ間隔の上限 (これより空いたら記録が無いとみなして切る)。1h の前の部分は 1 分ごとなので広めに
        max_gap = 150 if filled else max(int(step * 2.5), 6)
        # 読み込みの最中の見込み (まだ数が増えていない分。右端をゼロにしないため。記録の値ではない)
        live = {f"r:{name} 読込": [{"from": round(f, 1), "rate": round(r, 1)} for f, r in lst] for name, lst in self.slots.reading().items()}
        return {"range": rng, "step": step, "from": int(since), "to": int(now), "t": ts, "series": series, "max_gap": max_gap, "live": live}

    def _pending(self):
        with self.lock:
            return self.minute, self.wh, self.secs, {k: dict(v) for k, v in self.toks.items()}

    def energy_brief(self):
        e = self.energy(days=0)
        return {"price": e["price"], "today_kwh": e["today"]["kwh"], "today_yen": e["today"]["yen"],
                "month_kwh": e["month"]["kwh"], "month_yen": e["month"]["yen"], "month_forecast_yen": e["month"]["forecast_yen"]}

    def compare(self, rows, ptoks, m, today, month, now):
        """電気代と、同じだけ推論したときのレンタル (GPU の時間貸し・API の従量) の比べ。
        rows = 日ごとの [Wh, 測れた秒]、ptoks = まだ書いていない 1 分のトークン数"""
        first = min((now - datetime.timedelta(days=30)).strftime("%Y-%m-%d"), now.strftime("%Y-%m-01"))
        tok = {}  # 日 -> 名前 -> [in, proc, out, busy, wh, secs]
        for d, server, tin, tproc, tout, busy, wh, secs in self.store.tokens(first):
            tok.setdefault(d, {})[server] = [tin, tproc, tout, busy, wh or 0.0, secs or 0.0]
        if m is not None and ptoks:
            d = time.strftime("%Y-%m-%d", time.localtime(m))
            for server, c in ptoks.items():
                a = tok.setdefault(d, {}).setdefault(server, [0, 0, 0, 0.0, 0.0, 0.0])
                a[0] += c["in"]; a[1] += c["proc"]; a[2] += c["out"]; a[3] += c["busy"]
            # まだ書いていない 1 分の電力量も足す
            _, pwh, psecs, _ = self._pending()
            a = tok[d].setdefault("*", [0, 0, 0, 0.0, 0.0, 0.0])
            a[4] += pwh; a[5] += psecs
        yen = self.usd_jpy

        def period(dates):
            wh = secs = busy_any = 0.0   # トークンを数えていた間だけ
            servers = {}
            for d in dates:
                for server, (tin, tproc, tout, busy, s_wh, s_secs) in tok.get(d, {}).items():
                    if server == "*":
                        busy_any += busy
                        wh += s_wh
                        secs += s_secs
                        continue
                    a = servers.setdefault(server, [0, 0, 0, 0.0])
                    a[0] += tin; a[1] += tproc; a[2] += tout; a[3] += busy
            busy_any = min(busy_any, secs) if secs else busy_any   # 読む間隔のずれで、数えていた時間を少し超えることがある
            tin = sum(v[0] for v in servers.values())
            tout = sum(v[2] for v in servers.values())
            api_yen, api_missing = 0.0, []
            srv = []
            for name, (a_in, a_proc, a_out, a_busy) in servers.items():
                price = self.api.get(name)
                cost = (a_in * price[0] + a_out * price[1]) / 1e6 * yen if price else None
                if price:
                    api_yen += cost
                elif a_in or a_out:
                    api_missing.append(name)
                srv.append({"server": name, "in": a_in, "proc": a_proc, "out": a_out, "busy_h": round(a_busy / 3600, 3),
                            "api_yen": None if cost is None else round(cost, 2)})
            elec = wh / 1000.0 * self.price
            costs = {"elec": elec, "gpu_busy": busy_any / 3600 * self.gpu_usd_h * yen,
                     "gpu_full": secs / 3600 * self.gpu_usd_h * yen, "api": api_yen}
            return {"kwh": round(wh / 1000.0, 3), "hours": round(secs / 3600, 2), "busy_h": round(busy_any / 3600, 3),
                    "in": tin, "out": tout, "servers": srv, "api_missing": api_missing,
                    "yen": {k: round(v, 2) for k, v in costs.items()},
                    # 100 万出力トークンあたり (出力が無ければ null)
                    "per_mout": {k: (round(v / tout * 1e6, 1) if tout else None) for k, v in costs.items()}}

        days31 = [(now - datetime.timedelta(days=i)).strftime("%Y-%m-%d") for i in range(30, -1, -1)]
        mdays = sorted(d for d in set(rows) | set(tok) if d.startswith(month))
        out = {"prices": {"yen_kwh": self.price, "usd_jpy": self.usd_jpy, "gpu_usd_per_hour": self.gpu_usd_h,
                          "api": {k: {"in": v[0], "out": v[1]} for k, v in self.api.items()}},
               "periods": [dict(period([today]), key="today", label="今日"),
                           dict(period(days31[-7:]), key="7d", label="7 日"),
                           dict(period(mdays), key="month", label="今月"),
                           dict(period(days31), key="31d", label="31 日")],
               "days": []}
        for d in days31:
            p = period([d])
            out["days"].append({"date": d, "in": p["in"], "out": p["out"], "busy_h": p["busy_h"], "yen": p["yen"]})
        return out

    def energy(self, days=31):
        now = datetime.datetime.now()
        today = now.strftime("%Y-%m-%d")
        month = now.strftime("%Y-%m")
        first = (now - datetime.timedelta(days=400)).strftime("%Y-%m-%d") if days else now.strftime("%Y-%m-01")
        rows = {d: [wh, secs] for d, wh, secs in self.store.days(first)}
        m, pwh, psecs, ptoks = self._pending()
        if m is not None and pwh:
            d = time.strftime("%Y-%m-%d", time.localtime(m))
            r = rows.setdefault(d, [0.0, 0.0])
            r[0] += pwh
            r[1] += psecs
        price = self.price
        yen = lambda kwh: round(kwh * price, 1)
        kwh = lambda wh: round(wh / 1000.0, 3)
        t = rows.get(today, [0.0, 0.0])
        elapsed = now.hour * 3600 + now.minute * 60 + now.second
        mwh = sum(v[0] for d, v in rows.items() if d.startswith(month))
        msecs = sum(v[1] for d, v in rows.items() if d.startswith(month))
        dim = calendar.monthrange(now.year, now.month)[1]
        # 今月の見込み: 測れた時間の平均の W がひと月続いたら
        fwh = mwh / msecs * dim * 86400 if msecs > 600 else None
        out = {"price": price,
               "today": {"date": today, "kwh": kwh(t[0]), "yen": yen(t[0] / 1000.0),
                         "coverage": round(min(1.0, t[1] / elapsed), 3) if elapsed else 0.0,
                         "avg_w": round(t[0] / t[1] * 3600, 1) if t[1] else None},
               "month": {"month": month, "kwh": kwh(mwh), "yen": yen(mwh / 1000.0),
                         "forecast_kwh": kwh(fwh) if fwh is not None else None,
                         "forecast_yen": yen(fwh / 1000.0) if fwh is not None else None,
                         "avg_w": round(mwh / msecs * 3600, 1) if msecs else None}}
        if days:
            day_list = []
            for i in range(days - 1, -1, -1):
                d = (now - datetime.timedelta(days=i)).strftime("%Y-%m-%d")
                wh, secs = rows.get(d, [0.0, 0.0])
                span = elapsed if d == today else 86400
                day_list.append({"date": d, "kwh": kwh(wh), "yen": yen(wh / 1000.0),
                                 "coverage": round(min(1.0, secs / span), 3) if span else 0.0})
            months = collections.OrderedDict()
            for d in sorted(rows):
                a = months.setdefault(d[:7], [0.0, 0.0])
                a[0] += rows[d][0]
                a[1] += rows[d][1]
            out["days"] = day_list
            out["compare"] = self.compare(rows, ptoks, m, today, month, now)
            out["months"] = [{"month": k, "kwh": kwh(v[0]), "yen": yen(v[0] / 1000.0)} for k, v in list(months.items())[-13:]]
        return out


class Handler(BaseHTTPRequestHandler):
    mon = None
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        log.debug("%s %s", self.address_string(), fmt % args)

    def _json(self, code, body):
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Access-Control-Allow-Origin", "*")  # 読むだけの値なので、ほかの画面からも読めるように
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        u = urllib.parse.urlsplit(self.path)
        qs = urllib.parse.parse_qs(u.query)
        if u.path == "/api/now":
            now = self.mon.current()
            return self._json(200 if now else 503, now or {"error": "集計の準備中です (起動して数秒)"})
        if u.path == "/api/history":
            rng = (qs.get("range") or ["24h"])[0]
            if rng not in RANGES:
                return self._json(400, {"error": "range は " + " / ".join(RANGES)})
            return self._json(200, self.mon.history(rng))
        if u.path == "/api/energy":
            return self._json(200, self.mon.energy())
        if u.path == "/api/speed":
            try:
                days = float((urllib.parse.parse_qs(u.query).get("days") or ["7"])[0])
            except ValueError:
                return self._json(400, {"error": "days は数 (0 = 全部)"})
            return self._json(200, self.mon.reqlog.speed(days))
        if u.path == "/health":
            return self._json(200, {"ok": True, "version": hh.APP_VERSION})
        if u.path == "/":
            return self._json(200, {"name": "llmsv hwmon", "version": hh.APP_VERSION,
                                    "api": ["/api/now", "/api/history?range=" + "|".join(RANGES), "/api/energy", "/api/speed?days=7"]})
        return self._json(404, {"error": "not found"})


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(hh.APP_DIR, "config.ini")
    cfg = hh.Config(path)
    logging.basicConfig(level=getattr(logging, cfg.get("log", "level").upper(), logging.INFO),
                        format="%(asctime)s %(levelname)s %(message)s")
    hh.log.setLevel(logging.INFO)
    log.info("llmsv hwmon %s (config: %s%s)", hh.APP_VERSION, cfg.path, (" + " + cfg.local_path) if cfg.local_path else "")
    mon = HwMonitor(cfg)
    Handler.mon = mon

    def stop(signum, frame):
        log.info("止めます (いまの 1 分を書き込んでから)")
        mon.flush()
        os._exit(0)
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    host, port = cfg.get("hwmon", "listen_host"), cfg.getint("hwmon", "listen_port")
    srv = ThreadingHTTPServer((host, port), Handler)
    srv.daemon_threads = True
    log.info("api    : http://%s:%d/api/now", host, port)
    srv.serve_forever()


if __name__ == "__main__":
    main()
