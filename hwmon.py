#!/usr/bin/env python3
"""llmsv のハードウェアモニタ (温度・電力・使用率・メモリ・ディスク・電気代) を JSON で配る小さなサーバー。

Windows の専用アプリ (windows/HwMonitor) が読む。値の集め方は hermes_honyaku.py の
GpuMonitor・SysMonitor (CpuPower・TempSensors) をそのまま使い回すので、翻訳の画面のメーターと同じ値になる。
翻訳の中継 (hermes-honyaku、8081) とは別のプロセスなので、こちらを再起動しても Hermes などの通信は止まらない。

  GET /api/now                    いまの値 (2 秒ごとに更新)
  GET /api/history?range=24h      履歴 (1h / 3h / 6h / 24h / 7d / 30d)。1h は 10 秒刻み、ほかは 1 分の記録をまとめたもの
  GET /api/energy                 電気代 (今日・今月・日ごと 31 日・月ごと 13 か月)
  GET /health

設定は hermes_honyaku と同じ config.ini (+ config.local.ini) の [hwmon]・[power]・[gpu]・[sysmon]。
記録は SQLite ([hwmon] db、既定 ~/.local/share/hermes-honyaku/hwmon.sqlite)。
1 分ごとの平均を keep_days 日 (既定 35) 残し、日ごとの電力量 (Wh) はずっと残す。
"""

import calendar
import collections
import datetime
import json
import logging
import os
import signal
import sqlite3
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import hermes_honyaku as hh

log = logging.getLogger("hwmon")

# 温度の色の境目 (満杯・黄・赤、℃)。翻訳の画面 (TEMPK) と同じ
TEMPK = {"cpu": (100, 85, 95), "gpu": (90, 75, 85), "ddr": (90, 70, 80), "ssd": (85, 65, 75),
         "board": (80, 55, 70), "lan": (110, 85, 100)}

# 履歴の刻み: range -> (さかのぼる秒, 刻みの秒)。1h はメモリの 2 秒の記録から、ほかは 1 分の記録から
RANGES = {"1h": (3600, 10), "3h": (3 * 3600, 60), "6h": (6 * 3600, 60), "24h": (86400, 240),
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
        self.db.commit()
        self._last_prune = 0

    def add_minute(self, ts, wh, secs, data):
        date = time.strftime("%Y-%m-%d", time.localtime(ts))
        with self.lock:
            self.db.execute("INSERT OR REPLACE INTO minutes (ts, wh, secs, data) VALUES (?, ?, ?, ?)",
                            (ts, wh, secs, json.dumps(data, ensure_ascii=False, separators=(",", ":"))))
            self.db.execute("INSERT INTO days (date, wh, secs) VALUES (?, ?, ?) "
                            "ON CONFLICT(date) DO UPDATE SET wh = wh + excluded.wh, secs = secs + excluded.secs",
                            (date, wh, secs))
            if ts - self._last_prune > 3600:
                self.db.execute("DELETE FROM minutes WHERE ts < ?", (ts - self.keep_days * 86400,))
                self._last_prune = ts
            self.db.commit()

    def minutes_since(self, since):
        with self.lock:
            return self.db.execute("SELECT ts, data FROM minutes WHERE ts >= ? ORDER BY ts", (since,)).fetchall()

    def days(self, since_date):
        with self.lock:
            return self.db.execute("SELECT date, wh, secs FROM days WHERE date >= ? ORDER BY date", (since_date,)).fetchall()


class HwMonitor:
    def __init__(self, cfg):
        self.cfg = cfg
        self.pcfg = {k: cfg.get("power", k) for k in ("other_w", "psu_eff", "psu_w", "cpu_max_w")}
        self.price = cfg.getfloat("hwmon", "price_yen_kwh")
        self.interval = cfg.getfloat("sysmon", "interval")
        self.gpu = hh.GpuMonitor(interval=cfg.getfloat("gpu", "interval"), power_max=cfg.getfloat("gpu", "power_max"))
        self.sys = hh.SysMonitor(interval=self.interval, power={
            "cpu_source": cfg.get("power", "cpu_source").lower(),
            "other_w": cfg.getfloat("power", "other_w"), "psu_eff": cfg.getfloat("power", "psu_eff"),
            "psu_w": cfg.getfloat("power", "psu_w"), "cpu_max_w": cfg.getfloat("power", "cpu_max_w")})
        db = os.path.expanduser(cfg.get("hwmon", "db"))
        self.store = Store(db, cfg.getint("hwmon", "keep_days"))
        log.info("記録: %s (1 分ごとの記録は %d 日残す)", db, self.store.keep_days)
        self.lock = threading.Lock()
        self.now = None
        self.ring = collections.deque(maxlen=1800)  # 2 秒 × 1 時間
        self.minute = None      # いま貯めている 1 分の始まり
        self.acc = {}           # キー -> [合計, 個数]
        self.wh = 0.0           # この 1 分の電力量
        self.secs = 0.0         # この 1 分のうち測れた秒
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
            for k, v in flat.items():
                a = self.acc.setdefault(k, [0.0, 0])
                a[0] += v
                a[1] += 1
            self.ring.append((t, flat))
            view["ts"] = t
            self.now = view

    def _flush_locked(self):
        if not self.acc and self.wh == 0:
            return
        data = {k: round(s / n, 2) for k, (s, n) in self.acc.items() if n}
        try:
            self.store.add_minute(self.minute, self.wh, self.secs, data)
        except Exception:
            log.exception("hwmon: 記録の書き込みに失敗")
        self.acc, self.wh, self.secs = {}, 0.0, 0.0

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
        if rng == "1h":
            with self.lock:
                rows = [(t, f) for t, f in self.ring if t >= since]
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
        return {"range": rng, "step": step, "from": int(since), "to": int(now), "t": ts, "series": series}

    def _pending(self):
        with self.lock:
            return self.minute, self.wh, self.secs

    def energy_brief(self):
        e = self.energy(days=0)
        return {"price": e["price"], "today_kwh": e["today"]["kwh"], "today_yen": e["today"]["yen"],
                "month_kwh": e["month"]["kwh"], "month_yen": e["month"]["yen"], "month_forecast_yen": e["month"]["forecast_yen"]}

    def energy(self, days=31):
        now = datetime.datetime.now()
        today = now.strftime("%Y-%m-%d")
        month = now.strftime("%Y-%m")
        first = (now - datetime.timedelta(days=400)).strftime("%Y-%m-%d") if days else now.strftime("%Y-%m-01")
        rows = {d: [wh, secs] for d, wh, secs in self.store.days(first)}
        m, pwh, psecs = self._pending()
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
        if u.path == "/health":
            return self._json(200, {"ok": True, "version": hh.APP_VERSION})
        if u.path == "/":
            return self._json(200, {"name": "llmsv hwmon", "version": hh.APP_VERSION,
                                    "api": ["/api/now", "/api/history?range=" + "|".join(RANGES), "/api/energy"]})
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
