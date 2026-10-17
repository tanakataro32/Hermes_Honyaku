// llmsv ハードウェアモニタのスマホ版。Windows アプリ (windows/HwMonitor) を iPhone 向けに組み直したもの。
// 読むもの: /api/now (メーター・枠・スレッド)、/api/history (グラフ・枠の色の平均)、/api/speed (速度と文脈)、/events (思考・枠の札。翻訳の画面の中継)
// 決まり (色・段の分け方・枠の札の結び方・速さの色) は Windows アプリと同じ。画面が隠れている間は読みに行かない
'use strict';

const C = {
  back: '#131a1f', panel: '#1b2329', barBack: '#0c1114', ink: '#cfe8e4', head: '#ffffff', muted: '#8a9aa0', en: '#a8bcb8',
  grid: '#2a353c', edge: '#4a5762', good: '#35e05a', warn: '#ffb020', bad: '#f04030', cache: '#3d6f9e', accent: '#107a70',
};
const SERIES = ['#4fc3f7', '#ffb74d', '#81c784', '#e57373', '#ba68c8', '#fff176', '#4db6ac', '#f06292', '#a1887f'];
const RANGES = [['2m', '120秒'], ['10m', '10分'], ['30m', '30分'], ['1h', '1時間'], ['3h', '3時間'], ['6h', '6時間'], ['24h', '24時間'], ['7d', '7日'], ['30d', '30日']];
const GROUPS = [['p', '電力', 'W'], ['t', '温度', '℃'], ['u', '使用率', '%'], ['m', 'メモリ', 'GB'], ['r', 'トークン', 't/s']];
const INTERVALS = [2, 5, 10, 30, 60];
const SPARK_N = 60;

const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const fmt = (v, d = 0) => (v == null || isNaN(v) ? '-' : Number(v).toFixed(d));
const nowSec = () => Date.now() / 1000;

// 端末ごとの覚え (タブ・期間など)。プライベートブラウズなどで使えなくても動くように
const store = {
  get(k, d) { try { const v = localStorage.getItem('hwm.' + k); return v == null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('hwm.' + k, JSON.stringify(v)); } catch { /* 覚えられないだけ */ } },
};

const st = {
  tab: store.get('tab', 'meter'), iv: store.get('iv', 2), range: store.get('range', '1h'), group: store.get('group', 'r'),
  hidden: new Set(store.get('hidden', [])),
  sp: { y: store.get('spY', 'gen'), color: store.get('spColor', 'run'), days: store.get('spDays', 7), server: store.get('spServer', 'flashnext'),
        hidden: new Set(store.get('spHidden', [])) },
  hideTasks: store.get('hideTasks', true), showSrc: store.get('showSrc', false), compactK: 20,
  now: null, hist: null, avg: {}, speed: null, spark: new Map(), colors: new Map(), order: new Map(),
  last: { now: 0, hist: 0, avg: 0, speed: 0 }, busy: {}, hoverX: null, spHit: null,
};

async function getJSON(path) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), 10000);
  try {
    const r = await fetch(path, { cache: 'no-store', signal: ctl.signal });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return await r.json();
  } finally { clearTimeout(t); }
}

// ---------------- 読みに行く (1 秒ごとに、期限の来たものだけ) ----------------

function histDue() {
  const base = { '2m': 2, '10m': 2, '30m': 5, '1h': 10 }[st.range] ?? 60;
  return Math.max(st.iv, base);
}

async function pump() {
  if (document.hidden) return;
  const t = nowSec();
  const jobs = [];
  if (t - st.last.now >= st.iv - 0.2) jobs.push(load('now', async () => { st.now = await getJSON('/api/now'); pushSpark(st.now); renderTop(); if (st.tab === 'meter') renderMeter(); if (st.tab === 'graph' && st.hist && !st.ctxDrawn) { st.ctxDrawn = true; drawHistory(); } }));
  if (st.tab === 'graph' && t - st.last.hist >= histDue() - 0.2) jobs.push(load('hist', async () => { st.hist = await getJSON('/api/history?range=' + st.range); applyLive(st.hist); assignColors(st.hist); drawHistory(); }));
  // 枠の色の基準 (直近 1 時間の生成・読込の平均)
  if (t - st.last.avg >= 60) jobs.push(load('avg', async () => { st.avg = tokenAverages(await getJSON('/api/history?range=1h')); }));
  if (st.tab === 'speed' && t - st.last.speed >= 60) jobs.push(load('speed', async () => { st.speed = await getJSON('/api/speed?days=' + st.sp.days); drawSpeed(); }));
  await Promise.all(jobs);
}

async function load(name, fn) {
  if (st.busy[name]) return;
  st.busy[name] = true;
  st.last[name] = nowSec();
  try { await fn(); setErr(name, ''); } catch (e) { setErr(name, e.message || String(e)); } finally { st.busy[name] = false; }
}

const errs = {};
function setErr(name, msg) {
  errs[name] = msg;
  const all = Object.entries(errs).filter(([, m]) => m).map(([n, m]) => `${n}: ${m}`);
  $('#err').textContent = all.length ? 'つながりません' : '';
  $('#err').title = all.join('\n');
  $('#conn').className = 'dot ' + (errs.now ? 'ng' : st.now ? 'ok' : '');
}

// ---------------- 上の帯 ----------------

function yen(v) { return v == null ? '-' : v >= 100 ? '¥' + Math.round(v).toLocaleString() : '¥' + v.toFixed(1); }

function renderTop() {
  const n = st.now;
  if (!n) return;
  $('#power').textContent = n.power ? `約 ${fmt(n.power.wall)}W` : '';
  const e = n.energy;
  $('#energy').textContent = e ? `今日 ${yen(e.today_yen)} (${fmt(e.today_kwh, 2)}kWh)・今月 ${yen(e.month_yen)}` : '';
}

function setIv(dir) {
  const i = Math.max(0, INTERVALS.indexOf(st.iv));
  st.iv = INTERVALS[Math.min(INTERVALS.length - 1, Math.max(0, i + dir))];
  store.set('iv', st.iv);
  $('#ivLabel').textContent = st.iv + '秒';
  $('#slower').disabled = st.iv >= INTERVALS[INTERVALS.length - 1];
  $('#faster').disabled = st.iv <= INTERVALS[0];
}

// ---------------- 直近の推移 (アプリが受け取った最近 60 回) ----------------

function pushSpark(n) {
  const add = (k, v) => {
    if (v == null || isNaN(v)) return;
    let a = st.spark.get(k);
    if (!a) st.spark.set(k, a = []);
    a.push(+v);
    if (a.length > SPARK_N) a.splice(0, a.length - SPARK_N);
  };
  for (const t of n.temps || []) add('t:' + t.label, t.c);
  if (n.power) { add('p:全体', n.power.wall); for (const p of n.power.parts || []) add('p:' + p.label, p.w); }
  for (const u of n.util || []) add('u:' + u.label, u.pct);
  for (const m of n.mem || []) add('m:' + m.label, m.total ? (m.used || 0) / m.total * 100 : null);
  (n.cores || []).forEach((v, i) => add('c:' + i, v));
}

// 小さな折れ線 (SVG)。max が 0 以下なら推移の最大に合わせる
function spark(key, max, color, cls = '') {
  const v = st.spark.get(key) || [];
  const W = 120, H = 24;
  let body = `<line x1="0" y1="${H - 0.5}" x2="${W}" y2="${H - 0.5}" stroke="${C.grid}" stroke-width="1" vector-effect="non-scaling-stroke"/>`;
  if (v.length >= 2) {
    if (!(max > 0)) max = Math.max(...v) * 1.15 || 1;
    const step = W / (SPARK_N - 1);
    const pts = v.map((x, i) => [W - (v.length - 1 - i) * step, H - Math.min(1, Math.max(0, x / max)) * (H - 1)]);
    const line = pts.map((p) => p[0].toFixed(1) + ',' + p[1].toFixed(1)).join(' ');
    body += `<polygon points="${line} ${pts[pts.length - 1][0].toFixed(1)},${H} ${pts[0][0].toFixed(1)},${H}" fill="${color}" fill-opacity=".28"/>` +
            `<polyline points="${line}" fill="none" stroke="${color}" stroke-width="1.3" vector-effect="non-scaling-stroke"/>`;
  }
  return `<svg class="spark ${cls}" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">${body}</svg>`;
}

// 使用率の色の帯で pct の所の色 (緑 → 70% 黄 → 100% 赤)
function heat(pct) {
  const t = Math.min(1, Math.max(0, pct / 100));
  const mix = (a, b, f) => { const h = (s) => [1, 3, 5].map((i) => parseInt(s.substr(i, 2), 16)); const x = h(a), y = h(b); return `rgb(${x.map((c, i) => Math.round(c + (y[i] - c) * f)).join(',')})`; };
  return t <= 0.35 ? C.good : t <= 0.7 ? mix(C.good, C.warn, (t - 0.35) / 0.35) : mix(C.warn, C.bad, (t - 0.7) / 0.3);
}
const level = (v, warn, hot) => (v >= hot ? C.bad : v >= warn ? C.warn : C.good);
const levelInk = (v, warn, hot) => (v >= hot ? C.bad : v >= warn ? C.warn : C.ink);

function gradBar(pct, cls = '') {
  const p = Math.min(100, Math.max(0, pct || 0));
  // 帯を棒の全長で塗り、埋まっている所まで見せる
  return `<div class="bar ${cls}"><i class="grad" style="width:${p}%;--full:${p > 0 ? (10000 / p).toFixed(1) : 100}%"></i></div>`;
}
function plainBar(pct, color, pct2 = 0) {
  const p = Math.min(100, Math.max(0, pct || 0)), q = Math.min(100 - p, Math.max(0, pct2 || 0));
  return `<div class="bar thin"><i style="width:${p}%;background:${color}"></i>${q > 0 ? `<i style="left:${p}%;width:${q}%;background:${C.cache}"></i>` : ''}</div>`;
}

// ---------------- メーター ----------------

function renderMeter() {
  const n = st.now;
  if (!n) return;
  renderSlots(n);
  // 部品 (CPU・GPU ごとに 1 枚)
  const parts = n.power?.parts || [];
  const devs = [{ label: 'CPU', util: n.util.find((x) => x.kind === 'cpu'), mem: n.mem.find((x) => x.kind === 'ram'),
                  temp: n.temps.find((x) => x.kind === 'cpu'), power: parts.find((x) => x.kind === 'cpu') }];
  for (const u of n.util.filter((x) => x.kind === 'gpu'))
    devs.push({ label: u.label, util: u, mem: n.mem.find((x) => x.kind === 'vram' && x.label === u.label),
                temp: n.temps.find((x) => x.kind === 'gpu' && x.label === u.label), power: parts.find((x) => x.kind === 'gpu' && x.label === u.label) });
  $('#devices').innerHTML = devs.map((d) => {
    const up = d.util?.pct ?? 0, th = d.util?.throttle || '';
    const m = d.mem, mp = m?.total ? (m.used || 0) / m.total * 100 : 0;
    const mt = !m ? '-' : m.cache != null ? `${fmt(m.used)}+${fmt(m.cache)}/${fmt(m.total)}G` : `${fmt(m.used, 1)}/${fmt(m.total)}G`;
    const t = d.temp;
    const bar = th ? plainBar(up, th === 'hot' ? C.bad : C.warn) : gradBar(up);
    return `<div class="card dev">
      <div class="top"><span class="name">${esc(d.label)}</span>${d.util?.reasons?.length ? `<span class="reason">${esc(d.util.reasons.join('・'))}</span>` : ''}
        <span class="pct num" style="color:${th === 'hot' ? C.bad : th === 'warn' ? C.warn : C.ink}">${d.util?.pct != null ? fmt(up) + '%' : '-'}</span></div>
      ${bar}${spark('u:' + d.label, 100, heat(up))}
      <div class="grid3">
        <div><div class="k">${m?.kind === 'ram' ? 'RAM (+キャッシュ)' : 'VRAM'}</div><div class="v num" style="color:${mp >= 95 ? C.warn : C.ink}">${mt}</div>
          ${plainBar(mp, mp >= 95 ? C.warn : C.good, m?.cache != null && m.total ? m.cache / m.total * 100 : 0)}</div>
        <div><div class="k">温度</div><div class="v num" style="color:${t ? levelInk(t.c, t.warn, t.hot) : C.muted}">${t ? fmt(t.c) + '℃' : '-'}</div>
          ${t ? spark('t:' + t.label, t.max, level(t.c, t.warn, t.hot), 's') : ''}</div>
        <div><div class="k">電力</div><div class="v num">${d.power?.w != null ? fmt(d.power.w) + 'W' : '-'}</div>
          ${d.power ? spark('p:' + d.power.label, 0, C.good, 's') : ''}</div>
      </div></div>`;
  }).join('');
  // スレッド
  const cores = n.cores || [];
  $('#coreSum').textContent = cores.length ? `${cores.length} 本・90% 以上 ${cores.filter((c) => c >= 90).length} 本` : '';
  $('#cores').innerHTML = cores.map((v, i) => `<div class="core"><div class="line"><span class="muted">C${i}</span>
    <span class="num" style="color:${v >= 90 ? C.bad : v >= 70 ? C.warn : C.ink}">${fmt(v)}%</span></div>${spark('c:' + i, 100, heat(v), 's')}</div>`).join('') ||
    '<span class="muted small">hwmon.py が古い版です (スレッドは PR #64 から)</span>';
  // 部品に入らない温度・電力・ディスク
  const chips = [];
  for (const t of n.temps.filter((x) => x.kind !== 'cpu' && x.kind !== 'gpu')) chips.push(`<span style="color:${levelInk(t.c, t.warn, t.hot)}">${esc(t.label)} ${fmt(t.c)}℃</span>`);
  for (const p of parts.filter((x) => x.kind !== 'cpu' && x.kind !== 'gpu')) chips.push(`<span class="muted">${esc(p.label)} ${fmt(p.w)}W</span>`);
  if (n.power) chips.push(`<span>全体 ${fmt(n.power.wall)}/${fmt(n.power.full)}W</span>`);
  for (const d of n.disks) { const p = d.total ? (d.used || 0) / d.total * 100 : 0; chips.push(`<span style="color:${p >= 90 ? C.warn : C.ink}">${esc(d.label)} ${fmt(d.used)}/${fmt(d.total)}G</span>`); }
  $('#chips').innerHTML = chips.join('');
}

// ---------------- 枠 (llama-server の枠ごとの使っている量・速さ・札) ----------------

// 翻訳の中継のターン (発信元・会話の札・入力の量・終わったか)。/events から
const turns = new Map();
let turnBoot = 0;
const labels = new Map(), labelAt = new Map(), startSeen = new Map(), lastRatio = new Map();
const HOLD = 10;

function matchSlots(servers) {
  const t0 = nowSec();
  const open = [...turns.values()].filter((t) => !t.ended && t0 - t.ts < 7200);
  const byTime = [], bySize = [];
  for (const srv of servers) for (const s of srv.slots.filter((s) => s.processing)) for (const t of open) {
    const key = srv.server + '#' + s.id;
    if (s.started != null && Math.abs(s.started - t.ts) <= 5) byTime.push([Math.abs(s.started - t.ts), key, t]);
    else if (t.tokens > 0 && s.prompt >= t.tokens * 0.9 && s.prompt <= t.tokens * 1.8 + 20000) bySize.push([Math.abs(s.prompt - t.tokens) / Math.max(1, s.prompt), key, t]);
  }
  const usedSlot = new Set(), usedTurn = new Set();
  for (const [, key, t] of [...byTime.sort((a, b) => a[0] - b[0]), ...bySize.sort((a, b) => a[0] - b[0])]) {
    if (usedSlot.has(key) || usedTurn.has(t.n)) continue;
    usedSlot.add(key); usedTurn.add(t.n);
    labelAt.set(key, t0);
    const who = t.who ? ' · ' + t.who : '';
    labels.set(key, { label: (t.source || '?') + (t.task ? ' (背景)' : '') + who, oc: t.source === 'OpenCode',
                      tip: `#${t.n} ${t.source}${who}` + (t.tokens > 0 ? `\n翻訳の中継が数えた入力 ${t.tokens.toLocaleString()} トークン (ツールの定義を除く)` : '') });
  }
  // 要求のつなぎ目で一瞬結べないときは、最後に結べてから・枠が新しい要求を受けてから HOLD 秒は前の札を残す
  for (const srv of servers) for (const s of srv.slots) {
    const key = srv.server + '#' + s.id;
    const ss = startSeen.get(key);
    if (s.started != null && (!ss || ss.started !== s.started)) startSeen.set(key, { started: s.started, seen: t0 });
    if (!s.processing || usedSlot.has(key)) continue;
    const fresh = t0 - (labelAt.get(key) ?? -1e9) <= HOLD || t0 - (startSeen.get(key)?.seen ?? -1e9) <= HOLD;
    if (!fresh) labels.delete(key);
  }
}

function hsv(h, s, v) {
  const c = v * s, hp = h / 60, x = c * (1 - Math.abs((hp % 2) - 1)), m = v - c;
  const [r, g, b] = hp < 1 ? [c, x, 0] : hp < 2 ? [x, c, 0] : hp < 3 ? [0, c, x] : hp < 4 ? [0, x, c] : hp < 5 ? [x, 0, c] : [c, 0, x];
  return `rgb(${Math.round((r + m) * 255)},${Math.round((g + m) * 255)},${Math.round((b + m) * 255)})`;
}
// 速さの色: 平均の何倍か (0.5 倍以下 赤 → 1 倍 緑 → 2 倍以上 水色)、明るさ = 使っている量 ÷ 圧縮ライン
function speedColor(ratio, fill) {
  const x = Math.min(1, Math.max(-1, Math.log2(Math.max(ratio, 1e-6))));
  return hsv(x < 0 ? 120 * (1 + x) : 120 + 70 * x, 0.8, 0.3 + 0.7 * Math.min(1, Math.max(0, fill)));
}

function speedRatio(server, s) {
  const avg = st.avg[server] || {};
  const rg = avg.gen > 0 && s.tps > 0 ? s.tps / avg.gen : 0;
  const rr = avg.read > 0 && s.pp_tps > 0 ? s.pp_tps / avg.read : 0;
  const key = server + '#' + s.id;
  if (rg === 0 && rr === 0)
    return [lastRatio.get(key) ?? 1, avg.gen == null && avg.read == null ? '平均がまだ無いので緑' : '速さがまだ出ていないので前の色のまま'];
  const r = Math.max(rg, rr);
  lastRatio.set(key, r);
  return [r, rr > rg ? `読込 ${fmt(s.pp_tps, 1)} t/s = 平均 ${fmt(avg.read, 1)} の ${rr.toFixed(2)} 倍` : `生成 ${fmt(s.tps, 1)} t/s = 平均 ${fmt(avg.gen, 1)} の ${rg.toFixed(2)} 倍`];
}

const K = (v) => (v >= 100000 ? (v / 1000).toFixed(0) + 'k' : v >= 1000 ? (v / 1000).toFixed(1) + 'k' : String(v));

function renderSlots(n) {
  const servers = n.slots || [];
  matchSlots(servers);
  $('#slots').innerHTML = servers.map((srv) => `<div class="card"><div class="cardhead"><span>🧩 ${esc(srv.server)}</span>
    ${srv.up ? '' : '<span class="warn small">つながりません</span>'}</div>${srv.slots.map((s) => {
    const key = srv.server + '#' + s.id;
    const lb = labels.get(key);
    const pct = s.n_ctx > 0 ? s.used * 100 / s.n_ctx : 0;
    const compact = Math.max(1, s.n_ctx - st.compactK * 1000);
    let color = C.grid, why = '';
    if (s.processing) { const [r, w] = speedRatio(srv.server, s); color = speedColor(r, s.used / compact); why = w; }
    const val = s.used > 0 ? `${K(s.used)}/${K(s.n_ctx)}` + (s.processing && s.tps > 0 ? ` · ${fmt(s.tps, 1)} t/s` : '') : `空き/${K(s.n_ctx)}`;
    const when = s.last_active ? new Date(s.last_active * 1000).toLocaleTimeString('ja-JP', { hour12: false }) : '-';
    const tip = `${s.processing ? '推論中' : '待機中 (最後に使っていた量)'}\n使っている量 ${s.used.toLocaleString()} / ${s.n_ctx.toLocaleString()} トークン (${pct.toFixed(1)}%)\n` +
      `入力 ${s.prompt.toLocaleString()}・生成 ${s.decoded.toLocaleString()}\n最後に動いた時刻 ${when}` + (lb ? '\n' + lb.tip : '') +
      (s.processing ? `\nバーの色: ${why} (平均: 直近 1 時間)、明るさ: 圧縮ライン ${(compact / 1000).toFixed(0)}k の ${(s.used * 100 / compact).toFixed(0)}%` : '');
    return `<div class="slot"><div class="line"><span class="label ${lb?.oc ? 'oc' : ''}" style="${!lb && !s.processing ? 'color:' + C.muted : ''}">枠${s.id}${lb ? '  ' + esc(lb.label) : s.processing ? '  推論中' : ''}</span>
      <span class="num" style="color:${s.processing ? C.ink : C.muted}">${val}</span></div>
      <div class="bar"><i style="width:${Math.min(100, pct)}%;background:${color}"></i></div>
      <details><summary>くわしく ▾</summary><pre>${esc(tip)}</pre></details></div>`;
  }).join('')}</div>`).join('');
}

// サーバーごとの生成・読込の平均 (0 でない所だけ。グラフの ▶AVE と同じ求め方)
function nonZeroAvg(vals) { const nz = vals.filter((v) => v > 0); return nz.length ? nz.reduce((a, b) => a + b, 0) / nz.length : null; }
function tokenAverages(d) {
  const out = {};
  for (const [k, vals] of Object.entries(d?.series || {})) {
    if (!k.startsWith('r:') || k.endsWith(' 文脈')) continue;
    const read = k.endsWith(' 読込');
    if (!read && !k.endsWith(' 生成')) continue;
    const srv = k.slice(2, k.lastIndexOf(' '));
    (out[srv] ||= {})[read ? 'read' : 'gen'] = nonZeroAvg(vals);
  }
  return out;
}

// ---------------- グラフ (Windows アプリの HistoryChart と同じ描き方) ----------------

function niceTop(v) {
  if (v <= 0) return 0;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v) return m * p;
  return 10 * p;
}

// 読み込みの最中の見込みで右端を埋める (見込みより記録が大きければ記録のまま)
function applyLive(d) {
  for (const [key, list] of Object.entries(d?.live || {})) {
    const vals = d.series[key];
    if (!vals) continue;
    d.t.forEach((t, i) => {
      const end = t + d.step;
      let est = 0;
      for (const lv of list) if (end > lv.from) est += lv.rate * Math.min(1, (end - lv.from) / d.step);
      if (est > 0 && (vals[i] ?? 0) < est) vals[i] = Math.round(est * 10) / 10;
    });
  }
}

const isCtx = (k) => k.endsWith(' 文脈');
function colorOf(k) {
  if (k === 'p:全体') return '#ffffff';
  // トークンは Windows アプリの見た目に合わせて固定: 読込 青・生成 橙・文脈 緑 (サーバーが増えたら次の 3 色)
  if (k.startsWith('r:')) {
    const srv = k.slice(2).split(' ')[0];   // r:<サーバー> 生成 / r:<サーバー> 枠0 文脈
    if (!st.rServers) st.rServers = [];
    if (!st.rServers.includes(srv)) st.rServers.push(srv);
    const kind = k.endsWith(' 読込') ? 0 : isCtx(k) ? 2 : 1;
    return SERIES[(st.rServers.indexOf(srv) * 3 + kind) % SERIES.length];
  }
  // ほかは部品の名前 (頭文字を除いた部分) で決める。同じ部品はどのタブでも同じ色 (assignColors で全タブぶんまとめて決める)
  const name = k.slice(2);
  if (!st.colors.has(name)) st.colors.set(name, SERIES[st.colors.size % SERIES.length]);
  return st.colors.get(name);
}

const rankOf = (name) => (name === '全体' || name === 'RAM' ? 0 : name === 'CPU' ? 1 : 2);
const baseOf = (name) => name.replace(/ \(\d+\)$/, '');   // "V100 (2)" → "V100"

// 部品の名前ごとに、凡例の並び (st.order) と色 (st.colors) を全タブ共通で決める。電力・温度・使用率・メモリの全部の記録から名前を集める。
// 並び: 全体・RAM → CPU → ほかは記録に現れた順。「V100 (2)」のような同じ型の 2 枚目は、記録に後から現れても 1 枚目のすぐ後ろ。
// 色: CPU を先頭 (SERIES の 1 色目) にして、あとは並びの順に当てる。全体は白なので数えない。
// 履歴を受け取るたびに決め直すので、どのタブを先に開いても同じ色になる (Windows アプリと同じ決め方)
function assignColors(d) {
  st.colors = new Map(); st.order = new Map();
  if (!d) return;
  const names = [...new Set(Object.keys(d.series).filter((k) => !k.startsWith('r:')).map((k) => k.slice(2)))];
  const seen = new Map();   // 型名 -> 記録に現れた順
  for (const n of names) if (!seen.has(baseOf(n))) seen.set(baseOf(n), seen.size);
  names.sort((a, b) => rankOf(a) - rankOf(b) || seen.get(baseOf(a)) - seen.get(baseOf(b)) || a.length - b.length || (a < b ? -1 : a > b ? 1 : 0));
  names.forEach((n, i) => st.order.set(n, i));
  let c = 0;
  for (const n of [...names.filter((n) => n === 'CPU'), ...names.filter((n) => n !== 'CPU' && n !== '全体')]) st.colors.set(n, SERIES[c++ % SERIES.length]);
}

function histKeys() {
  const d = st.hist;
  if (!d) return [];
  const keys = Object.keys(d.series).filter((k) => k.startsWith(st.group + ':'));
  if (st.group !== 'r') return keys.sort((a, b) => (st.order.get(a.slice(2)) ?? 1e9) - (st.order.get(b.slice(2)) ?? 1e9));
  // トークンは凡例・指を乗せたときの値を段の順 (生成・読込・文脈) に並べる
  return keys.sort((a, b) => (a.endsWith(' 読込') ? 1 : isCtx(a) ? 2 : 0) - (b.endsWith(' 読込') ? 1 : isCtx(b) ? 2 : 0));
}

function setupCanvas(cv) {
  const r = cv.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
  if (cv.width !== Math.round(r.width * dpr) || cv.height !== Math.round(r.height * dpr)) { cv.width = Math.round(r.width * dpr); cv.height = Math.round(r.height * dpr); }
  const g = cv.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, r.width, r.height);
  g.fillStyle = C.back;
  g.fillRect(0, 0, r.width, r.height);
  g.font = '10px -apple-system, "Hiragino Sans", sans-serif';
  g.textBaseline = 'middle';
  return [g, r.width, r.height];
}

function timeLabel(t, step) {
  const d = new Date(t * 1000), p = (x) => String(x).padStart(2, '0');
  return step >= 86400 ? `${d.getMonth() + 1}/${d.getDate()}` : step < 60 ? `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}` : `${p(d.getHours())}:${p(d.getMinutes())}`;
}
function timeTicks(t0, t1) {
  const span = t1 - t0;
  const step = span <= 120 ? 30 : span <= 600 ? 120 : span <= 1800 ? 600 : span <= 3600 ? 900 : span <= 3 * 3600 ? 3600 : span <= 6 * 3600 ? 7200 : span <= 86400 ? 6 * 3600 : span <= 7 * 86400 ? 2 * 86400 : 7 * 86400;
  const off = -new Date().getTimezoneOffset() * 60;   // 日付の区切りを日本時間の 0 時に
  const out = [];
  for (let t = (Math.floor((t0 + off) / step) + 1) * step - off; t < t1; t += step) out.push([t, timeLabel(t, step)]);
  return out;
}

function renderGraphControls() {
  $('#ranges').innerHTML = RANGES.map(([k, l]) => `<button data-k="${k}" class="${st.range === k ? 'on' : ''}">${l}</button>`).join('');
  $('#groups').innerHTML = GROUPS.map(([k, l]) => `<button data-k="${k}" class="${st.group === k ? 'on' : ''}">${l}</button>`).join('');
  $('#legend').innerHTML = histKeys().map((k) => `<button data-k="${esc(k)}" class="${st.hidden.has(k) ? 'off' : ''}"><i style="background:${colorOf(k)}"></i><span class="t">${esc(k.slice(2))}</span></button>`).join('');
}

function drawHistory() {
  renderGraphControls();
  const cv = $('#hist');
  const [g, W, H] = setupCanvas(cv);
  const d = st.hist;
  const PadL = 42, PadR = 6, PadT = 6, PadB = 18;
  const plot = { x: PadL, y: PadT, w: W - PadL - PadR, h: H - PadT - PadB };
  if (!d || !d.t.length) { g.fillStyle = C.muted; g.textAlign = 'center'; g.fillText(d ? 'まだ記録がありません' : '読み込み中…', W / 2, H / 2); return; }
  const unit = GROUPS.find((x) => x[0] === st.group)[2];
  const shown = histKeys().filter((k) => !st.hidden.has(k));
  const t0 = d.from, t1 = d.to;
  const X = (t) => plot.x + (t - t0) / Math.max(1, t1 - t0) * plot.w;
  const gap = d.max_gap > 0 ? d.max_gap : d.step * 2.5;
  // 段: トークンは生成 2・読込 1・文脈 1 の高さ
  let parts = [];
  if (st.group === 'r') {
    const gen = shown.filter((k) => !k.endsWith(' 読込') && !isCtx(k)), read = shown.filter((k) => k.endsWith(' 読込')), ctx = shown.filter(isCtx);
    if (gen.length) parts.push([gen, '生成', 2]);
    if (read.length) parts.push([read, '読込', 1]);
    if (ctx.length) parts.push([ctx, '文脈 (枠が使っている量)', 1]);
  }
  if (!parts.length) parts = [[shown, null, 1]];
  const PaneGap = 8, usable = plot.h - PaneGap * (parts.length - 1), sum = parts.reduce((a, p) => a + p[2], 0);
  let y = plot.y;
  const panes = parts.map((p, i) => { const h = i === parts.length - 1 ? plot.y + plot.h - y : usable * p[2] / sum; const r = { x: plot.x, y, w: plot.w, h, keys: p[0], title: p[1] }; y += h + PaneGap; return r; });
  const ctxLimit = Math.max(0, ...(st.now?.slots || []).flatMap((s) => s.slots.map((x) => x.n_ctx / 1000)));
  const ticks = timeTicks(t0, t1);
  for (const pr of panes) {
    const ctx = pr.keys.length > 0 && pr.keys.every(isCtx);
    const pu = ctx ? 'k' : unit;
    let max = 0;
    for (const k of pr.keys) for (const v of d.series[k]) if (v != null && v > max) max = v;
    if (ctx) max = Math.max(max, ctxLimit);
    let top = st.group === 'u' ? 100 : niceTop(max * (ctx ? 1 : 1.08));
    if (top <= 0) top = 1;
    const Y = (v) => pr.y + pr.h - v / top * pr.h;
    const mant = top / Math.pow(10, Math.floor(Math.log10(top)));
    const is = (...ms) => ms.some((m) => Math.abs(mant - m) < 1e-9);
    const div = is(2.5, 5) ? 5 : is(1.5, 3, 6) ? 3 : pr.h >= 100 ? 4 : 2;
    g.strokeStyle = C.grid; g.lineWidth = 1; g.setLineDash([]);
    g.textAlign = 'right';
    for (let i = 0; i <= div; i++) {
      if (i === div && pr.y > plot.y) continue;
      const v = top * i / div, yy = Math.round(Y(v)) + 0.5;
      g.beginPath(); g.moveTo(pr.x, yy); g.lineTo(pr.x + pr.w, yy); g.stroke();
      if (pr.h >= 40 || i === 0 || i === div) { g.fillStyle = C.muted; g.fillText((v >= 10 || v === 0 ? v.toFixed(0) : String(+v.toFixed(1))) + pu, PadL - 3, yy); }
    }
    for (const [t] of ticks) { const xx = Math.round(X(t)) + 0.5; g.beginPath(); g.moveTo(xx, pr.y); g.lineTo(xx, pr.y + pr.h); g.stroke(); }
    if (ctx && ctxLimit > 0) {
      const c = Math.max(0, ctxLimit - st.compactK);
      g.strokeStyle = C.warn; g.globalAlpha = 0.65; g.setLineDash([4, 3]);
      g.beginPath(); g.moveTo(pr.x, Y(c)); g.lineTo(pr.x + pr.w, Y(c)); g.stroke();
      g.setLineDash([]); g.globalAlpha = 1;
      g.fillStyle = C.warn; g.textAlign = 'right'; g.fillText(`圧縮 ${c.toFixed(0)}k`, pr.x + pr.w - 3, Y(c) - 7);
    }
    g.strokeStyle = C.edge; g.strokeRect(pr.x + 0.5, pr.y + 0.5, pr.w - 1, pr.h - 1);
    if (pr.title) { const tw = g.measureText(pr.title).width; g.fillStyle = 'rgba(19,26,31,.86)'; g.fillRect(pr.x + 1, pr.y + 1, tw + 8, 14); g.fillStyle = C.muted; g.textAlign = 'left'; g.fillText(pr.title, pr.x + 4, pr.y + 8); }
    // 線 (記録が無い所・max_gap より空いた所で切る)
    for (const k of pr.keys) {
      g.strokeStyle = colorOf(k); g.lineWidth = k === 'p:全体' ? 2 : 1.4;
      const vals = d.series[k];
      let pts = [], prev = 0;
      const flush = () => { if (pts.length >= 2) { g.beginPath(); pts.forEach(([px, py], i) => (i ? g.lineTo(px, py) : g.moveTo(px, py))); g.stroke(); } else if (pts.length === 1) { g.fillStyle = colorOf(k); g.fillRect(pts[0][0] - 1, pts[0][1] - 1, 2, 2); } pts = []; };
      d.t.forEach((t, i) => {
        if (vals[i] == null || (pts.length && t - prev > gap)) { flush(); if (vals[i] == null) return; }
        pts.push([X(t + d.step / 2), Y(vals[i])]);
        prev = t;
      });
      flush();
    }
    // ▶AVE (トークンの生成・読込だけ)
    if (st.group === 'r' && !ctx) for (const k of pr.keys) {
      const avg = nonZeroAvg(d.series[k].filter((v) => v != null));
      if (avg == null) continue;
      const col = colorOf(k), ya = Y(avg), yt = Math.min(Math.max(ya, pr.y + 8), pr.y + pr.h - 8);
      g.strokeStyle = col; g.globalAlpha = 0.45; g.setLineDash([4, 3]); g.lineWidth = 1;
      g.beginPath(); g.moveTo(pr.x, ya); g.lineTo(pr.x + pr.w, ya); g.stroke(); g.setLineDash([]); g.globalAlpha = 1;
      const text = `AVE ${(+avg.toFixed(1))}${unit}`, tw = g.measureText(text).width;
      g.fillStyle = 'rgba(19,26,31,.86)'; g.fillRect(pr.x + 10, yt - 7, tw + 4, 14);
      g.fillStyle = col; g.beginPath(); g.moveTo(pr.x + 1, yt - 5); g.lineTo(pr.x + 9, yt); g.lineTo(pr.x + 1, yt + 5); g.fill();
      g.textAlign = 'left'; g.fillText(text, pr.x + 12, yt);
    }
  }
  g.fillStyle = C.muted; g.textAlign = 'center';
  for (const [t, label] of ticks) g.fillText(label, X(t), plot.y + plot.h + 10);
  // 指でなぞった時刻の値
  if (st.hoverX != null && st.hoverX >= plot.x && st.hoverX <= plot.x + plot.w) {
    const th = t0 + (st.hoverX - plot.x) / plot.w * (t1 - t0);
    let idx = 0;
    d.t.forEach((t, i) => { if (Math.abs(t + d.step / 2 - th) < Math.abs(d.t[idx] + d.step / 2 - th)) idx = i; });
    if (Math.abs(d.t[idx] + d.step / 2 - th) > gap) return;
    const hx = X(d.t[idx] + d.step / 2);
    g.strokeStyle = C.muted; g.setLineDash([2, 2]);
    for (const pr of panes) { g.beginPath(); g.moveTo(hx, pr.y); g.lineTo(hx, pr.y + pr.h); g.stroke(); }
    g.setLineDash([]);
    const span = t1 - t0, dt = new Date((d.t[idx] + d.step / 2) * 1000), p2 = (x) => String(x).padStart(2, '0');
    const head = span <= 600 ? `${p2(dt.getHours())}:${p2(dt.getMinutes())}:${p2(dt.getSeconds())}` : span <= 86400 ? `${p2(dt.getHours())}:${p2(dt.getMinutes())}` : `${dt.getMonth() + 1}/${dt.getDate()} ${p2(dt.getHours())}:${p2(dt.getMinutes())}`;
    const lines = [[head, C.head]];
    for (const k of shown) { const v = d.series[k][idx]; if (v != null) lines.push([`${k.slice(2)}  ${+v.toFixed(1)}${isCtx(k) ? 'k' : unit}`, colorOf(k)]); }
    g.font = '11px -apple-system, "Hiragino Sans", sans-serif';
    const bw = Math.max(...lines.map((l) => g.measureText(l[0]).width)) + 12, bh = lines.length * 15 + 8;
    const bx = hx + 12 + bw > plot.x + plot.w ? hx - 12 - bw : hx + 12;
    g.fillStyle = 'rgba(27,35,41,.94)'; g.fillRect(bx, plot.y + 6, bw, bh);
    g.strokeStyle = C.edge; g.strokeRect(bx + 0.5, plot.y + 6.5, bw - 1, bh - 1);
    g.textAlign = 'left';
    lines.forEach(([t, col], i) => { g.fillStyle = col; g.fillText(t, bx + 6, plot.y + 6 + 11 + i * 15); });
  }
}

// ---------------- 速度と文脈 (Windows アプリの SpeedPanel と同じ) ----------------

const SP_Y = [['gen', '生成'], ['pp', '読込'], ['draft', 'MTP の当たり率']];
const SP_COLOR = [['run', '設定ごと'], ['par', '並列数']];
const SP_DAYS = [[1, '24時間'], [7, '7日'], [30, '30日'], [0, '全部']];
const MIN_GEN = 16, MIN_PP = 256;

function spValue(p) {
  if (st.sp.y === 'gen' && p.gen_tps != null && p.gen_n >= MIN_GEN) return [(p.ctx - p.gen_n / 2) / 1000, p.gen_tps];
  if (st.sp.y === 'pp' && p.pp_tps != null && p.pp_n >= MIN_PP) return [(p.pp_from + p.pp_n / 2) / 1000, p.pp_tps];
  if (st.sp.y === 'draft' && p.draft != null && p.gen_n >= MIN_GEN) return [p.ctx / 1000, p.draft * 100];
  return null;
}
const runKey = (start) => 'run:' + start;
function localTime(t, sec = false) {
  const d = new Date(t * 1000), p = (x) => String(x).padStart(2, '0');
  return `${d.getMonth() + 1}/${d.getDate()} ${p(d.getHours())}:${p(d.getMinutes())}` + (sec ? ':' + p(d.getSeconds()) : '');
}
function runLabels(data) {
  const runs = data.runs.filter((r) => r.server === st.sp.server);
  const models = [...new Set(runs.map((r) => r.model))];
  let prefix = '';
  if (models.length > 1) {
    prefix = models[0];
    for (const m of models) while (!m.startsWith(prefix)) prefix = prefix.slice(0, -1);
    const cut = Math.max(prefix.lastIndexOf('-'), prefix.lastIndexOf('_'), prefix.lastIndexOf('.'));
    prefix = cut >= 0 ? prefix.slice(0, cut + 1) : '';
  }
  const out = {};
  for (const r of runs) out[runKey(r.start)] = `${r.model.slice(prefix.length)} ${r.n_slots}枠${r.mtp ? '・MTP' : ''} ${localTime(r.start)}〜`;
  return out;
}
const spGroup = (p) => (st.sp.color === 'par' ? `並列 ${Math.min(p.par, 4)}${p.par >= 4 ? '+' : ''}` : runKey(p.run));

let spDrawn = [];
function drawSpeed() {
  const data = st.speed;
  $('#spY').innerHTML = SP_Y.map(([k, l]) => `<button data-k="${k}" class="${st.sp.y === k ? 'on' : ''}">${l}</button>`).join('');
  const servers = data ? [...new Set(data.runs.map((r) => r.server))] : [];
  if (data && servers.length && !servers.includes(st.sp.server)) st.sp.server = servers[0];
  $('#spColor').innerHTML = SP_COLOR.map(([k, l]) => `<button data-k="${k}" class="${st.sp.color === k ? 'on' : ''}">${l}</button>`).join('') +
    servers.map((s) => `<button data-server="${esc(s)}" class="${st.sp.server === s ? 'on' : ''}">${esc(s)}</button>`).join('');
  $('#spDays').innerHTML = SP_DAYS.map(([k, l]) => `<button data-k="${k}" class="${st.sp.days === k ? 'on' : ''}">${l}</button>`).join('');
  const cv = $('#speed');
  const [g, W, H] = setupCanvas(cv);
  spDrawn = [];
  if (!data) { g.fillStyle = C.muted; g.textAlign = 'center'; g.fillText('読み込み中…', W / 2, H / 2); $('#spLegend').innerHTML = ''; return; }
  const labels = runLabels(data);
  const pts = data.points.filter((p) => p.server === st.sp.server).map((p) => ({ p, v: spValue(p), grp: spGroup(p) })).filter((x) => x.v);
  const groups = new Map();
  for (const x of pts) { const gr = groups.get(x.grp) || { n: 0, order: Infinity }; gr.n++; gr.order = Math.min(gr.order, x.p.run ?? 0); groups.set(x.grp, gr); }
  const keys = [...groups.keys()].sort((a, b) => (st.sp.color === 'par' ? a.localeCompare(b) : groups.get(a).order - groups.get(b).order));
  const col = new Map(keys.map((k, i) => [k, SERIES[i % SERIES.length]]));
  $('#spLegend').innerHTML = keys.map((k) => `<button data-k="${esc(k)}" class="dot ${st.sp.hidden.has(k) ? 'off' : ''}"><i style="background:${col.get(k)}"></i><span class="t">${esc(labels[k] || (k.startsWith('run:') ? '設定不明' : k))} (${groups.get(k).n})</span></button>`).join('');
  const shown = pts.filter((x) => !st.sp.hidden.has(x.grp));
  const PadL = 42, PadR = 8, PadT = 8, PadB = 30;
  const plot = { x: PadL, y: PadT, w: W - PadL - PadR, h: H - PadT - PadB };
  if (!shown.length) { g.fillStyle = C.muted; g.textAlign = 'center'; g.fillText(pts.length ? '凡例で全部隠しています' : 'この期間の記録がありません', W / 2, H / 2); return; }
  const nCtx = Math.max(0, ...data.runs.filter((r) => r.server === st.sp.server).map((r) => r.n_ctx));
  const xMax = niceTop(Math.max(1, ...shown.map((x) => x.v[0])) * 1.02);
  const ys = shown.map((x) => x.v[1]).sort((a, b) => a - b);
  const yMax = st.sp.y === 'draft' ? 100 : niceTop(ys[Math.floor((ys.length - 1) * 0.99)] * 1.1) || 1;
  const PX = (v) => plot.x + v / xMax * plot.w, PY = (v) => plot.y + plot.h - Math.min(v, yMax) / yMax * plot.h;
  const yu = st.sp.y === 'draft' ? '%' : 't/s';
  g.strokeStyle = C.grid; g.lineWidth = 1;
  g.textAlign = 'right';
  for (let i = 0; i <= 4; i++) { const v = yMax * i / 4, yy = Math.round(PY(v)) + 0.5; g.beginPath(); g.moveTo(plot.x, yy); g.lineTo(plot.x + plot.w, yy); g.stroke(); g.fillStyle = C.muted; g.fillText(+v.toFixed(1) + yu, PadL - 3, yy); }
  const xs = niceTop(xMax / 5);
  g.textAlign = 'center';
  for (let v = 0; v <= xMax + 1e-9; v += xs) {
    const xx = Math.round(PX(v)) + 0.5, label = v.toFixed(0) + 'k', half = g.measureText(label).width / 2;
    g.beginPath(); g.moveTo(xx, plot.y); g.lineTo(xx, plot.y + plot.h); g.stroke();
    g.fillStyle = C.muted; g.fillText(label, Math.min(xx, W - half - 2), plot.y + plot.h + 9);   // 右端の目盛りが切れないように
  }
  g.textAlign = 'right';
  g.fillText(st.sp.y === 'pp' ? '読み込んでいる所の文脈の量 →' : '文脈の量 →', plot.x + plot.w, plot.y + plot.h + 22);
  g.strokeStyle = C.edge; g.strokeRect(plot.x + 0.5, plot.y + 0.5, plot.w - 1, plot.h - 1);
  if (nCtx > 0 && st.sp.y !== 'pp') {
    const c = nCtx / 1000 - st.compactK;
    if (c > 0 && c <= xMax) { g.strokeStyle = C.warn; g.globalAlpha = 0.65; g.setLineDash([4, 3]); g.beginPath(); g.moveTo(PX(c), plot.y); g.lineTo(PX(c), plot.y + plot.h); g.stroke(); g.setLineDash([]); g.globalAlpha = 1; }
  }
  for (const x of shown.slice().sort((a, b) => a.p.t - b.p.t)) {
    const px = PX(x.v[0]), py = PY(x.v[1]);
    g.fillStyle = col.get(x.grp); g.globalAlpha = 0.5;
    g.beginPath(); g.arc(px, py, 2.4, 0, Math.PI * 2); g.fill();
    spDrawn.push([px, py, x.p]);
  }
  g.globalAlpha = 1;
  // 色ごとの傾向: 文脈 xs/2 刻みの中央値 (5 点以上ある刻みだけ)
  const bin = Math.max(xs / 2, 1);
  for (const k of keys) {
    if (st.sp.hidden.has(k)) continue;
    const bins = new Map();
    for (const x of shown) if (x.grp === k) { const b = Math.floor(x.v[0] / bin); (bins.get(b) || bins.set(b, []).get(b)).push(x.v[1]); }
    const line = [...bins.entries()].filter(([, v]) => v.length >= 5).sort((a, b) => a[0] - b[0])
      .map(([b, v]) => { v.sort((p, q) => p - q); return [PX((b + 0.5) * bin), PY(v[Math.floor(v.length / 2)])]; });
    if (line.length < 2) continue;
    for (const [w, c] of [[4, C.back], [2, col.get(k)]]) { g.strokeStyle = c; g.lineWidth = w; g.beginPath(); line.forEach(([px, py], i) => (i ? g.lineTo(px, py) : g.moveTo(px, py))); g.stroke(); }
  }
  // 触れた点の印
  if (st.spHit) { const h = spDrawn.find((d) => d[2] === st.spHit); if (h) { g.strokeStyle = C.head; g.lineWidth = 1.5; g.beginPath(); g.arc(h[0], h[1], 5, 0, Math.PI * 2); g.stroke(); } }
  $('#spNote').textContent = st.sp.y === 'gen' ? `1 点 = 1 要求 (生成 ${MIN_GEN} トークン以上)。線は文脈 ${bin.toFixed(0)}k ごとの中央値。橙の破線は圧縮ライン`
    : st.sp.y === 'pp' ? `1 点 = 1 要求 (読み込み ${MIN_PP} トークン以上)。前の要求から使い回した分は読まないので、新しく読んだ分の速さ`
    : 'MTP の先読みが当たった割合 (高いほど生成が速くなる)';
}

function spInfo(p) {
  if (!p) { $('#spInfo').textContent = '点に触れると中身が出ます'; return; }
  const run = st.speed?.runs.find((r) => r.server === p.server && r.start === p.run);
  $('#spInfo').textContent = `${localTime(p.t, true)} ${p.server} 枠${p.slot}` + (run ? `\n設定: ${run.model} ${run.n_slots}枠${run.mtp ? '・MTP' : ''}` : '') +
    `\n終わったときの文脈 ${p.ctx.toLocaleString()} トークン・並列 ${p.par}` +
    (p.pp_n > 0 ? `\n読込 ${p.pp_n.toLocaleString()} トークン (${p.pp_from.toLocaleString()} から)${p.pp_tps != null ? ` ${p.pp_tps.toFixed(1)} t/s` : ''}` : '') +
    (p.gen_n > 0 ? `\n生成 ${p.gen_n.toLocaleString()} トークン${p.gen_tps != null ? ` ${p.gen_tps.toFixed(2)} t/s` : ''}` : '') +
    (p.draft != null ? `\nMTP の当たり率 ${(p.draft * 100).toFixed(0)}%` : '');
}

// ---------------- 思考 (翻訳の画面の /events を hwmon が中継) ----------------

const feed = { boot: 0, seen: new Set(), turnInfo: new Map(), segSrc: new Map(), engine: '', think: '', thinkTurn: -1, thinkLabel: '', thinkLive: false, lastTurn: -1, entries: [] };
let es = null;

function connectEvents() {
  if (es) return;
  setThinkHead('つないでいます…');
  es = new EventSource('/events');
  es.onmessage = (m) => { let ev; try { ev = JSON.parse(m.data); } catch { return; } onEvent(ev); };
  es.onerror = () => { setThinkHead('つないでいます…'); };
  es.onopen = () => setThinkHead('');
}
function closeEvents() { if (es) { es.close(); es = null; } }

function onEvent(ev) {
  const type = ev.type;
  // 翻訳の中継の起動番号が変わったら (ターンの番号が 1 からやり直し) 覚えていたものを消す
  if ((type === 'status' || type === 'turn_start') && ev.boot != null && ev.boot !== turnBoot) {
    if (turnBoot) { turns.clear(); feed.seen.clear(); feed.turnInfo.clear(); feed.segSrc.clear(); feed.entries = []; feed.thinkTurn = -1; feed.think = ''; $('#feed').innerHTML = ''; feed.lastTurn = -1; }
    turnBoot = ev.boot;
  }
  if (type === 'status') { feed.engine = ev.engine || ''; setThinkHead(''); return; }
  const n = ev.turn;
  if (typeof n !== 'number') return;
  switch (type) {
    case 'turn_start': {
      turns.set(n, { n, source: ev.source || '', who: ev.who || '', task: !!ev.task, tokens: 0, ts: ev.ts ?? nowSec(), ended: false });
      while (turns.size > 60) turns.delete(Math.min(...turns.keys()));
      feed.turnInfo.set(n, { source: ev.source || '', task: !!ev.task });
      if (!(ev.task && st.hideTasks)) { feed.thinkTurn = n; feed.think = ''; feed.thinkLabel = `#${n} ${ev.source || ''}`; feed.thinkLive = true; drawThink(); }
      break;
    }
    case 'turn_ctx': { const t = turns.get(n); if (t && typeof ev.tokens === 'number') t.tokens = ev.tokens; break; }
    case 'turn_end': { const t = turns.get(n); if (t) t.ended = true; if (n === feed.thinkTurn) { feed.thinkLive = false; drawThink(); } break; }
    case 'think': {
      if (n < feed.thinkTurn) break;
      if (n > feed.thinkTurn) {
        const ti = feed.turnInfo.get(n) || {};
        if (ti.task && st.hideTasks) break;
        feed.thinkTurn = n; feed.think = ''; feed.thinkLabel = `#${n} ${ti.source || ''}`; feed.thinkLive = false;
      }
      feed.think += ev.text || '';
      if (feed.think.length > 3000) feed.think = feed.think.slice(-1500);
      scheduleThink();
      break;
    }
    case 'seg': if (typeof ev.seg === 'number') feed.segSrc.set(n + ':' + ev.seg, (ev.src || '').trim()); break;
    case 'ja': {
      if (typeof ev.seg !== 'number' || ev.how === 'code') return;   // コマンド・プログラムの行は出さない
      const id = turnBoot + ':' + n + ':' + ev.seg;
      if (feed.seen.has(id)) return;
      feed.seen.add(id);
      const info = feed.turnInfo.get(n) || {};
      if (info.task && st.hideTasks) return;
      const text = (ev.text || '').trim();
      if (!text) return;
      const src = feed.segSrc.get(n + ':' + ev.seg);
      feed.segSrc.delete(n + ':' + ev.seg);
      const kind = ev.how === 'none' ? 'none' : ev.ok && ev.how !== 'en' && ev.how !== 'interrupted' ? 'ok' : 'bad';
      addEntry({ turn: n, source: info.source || '', text, orig: kind === 'ok' ? src : null, kind, at: new Date() });
      break;
    }
  }
}

function setThinkHead(state) {
  const base = feed.engine === 'none' ? '原文 (Hermes の思考・翻訳は止めています)' : '和訳 (Hermes の思考)';
  $('#thinkHead').textContent = base + (state ? '・' + state : '');
}

let thinkTimer = 0;
function scheduleThink() { if (!thinkTimer) thinkTimer = setTimeout(() => { thinkTimer = 0; drawThink(); }, 250); }
function drawThink() {
  const el = $('#think');
  el.textContent = (feed.thinkLabel ? feed.thinkLabel + (feed.thinkLive ? ' 推測中…' : '') + '\n' : '') + feed.think.slice(-1500);
  el.scrollTop = el.scrollHeight;
}

// 和訳の欄は、画面の残り (原文の欄の下〜下のタブの上) の高さで、欄の中だけを送る (上の帯・原文の欄は動かさない)
function sizeFeed() {
  const box = $('#feed');
  if (st.tab !== 'think') return;
  const nav = $('#tabs').getBoundingClientRect().height;
  box.style.height = Math.max(160, window.innerHeight - box.getBoundingClientRect().top - nav - 8) + 'px';
}

// 1 区切り = 1 つの div (ターンの見出し・文・原文)。300 を超えたら古いものから消す (欄を作り直さない。
// つないだ直後は直近のターンがまとめて届くので、作り直すと画面が固まる)
function addEntry(e) {
  feed.entries.push(e);
  const box = $('#feed');
  const atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 60;
  box.appendChild(entryNode(e));
  while (feed.entries.length > 300) { feed.entries.shift(); box.firstElementChild?.remove(); }
  if (atEnd) box.scrollTop = box.scrollHeight;
}
function entryNode(e) {
  const div = document.createElement('div');
  let html = '';
  if (e.turn !== feed.lastTurn) {
    feed.lastTurn = e.turn;
    html += `<div class="turn">#${e.turn} ${esc(e.source)}  ${e.at.toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })}</div>`;
  }
  html += `<p class="${e.kind === 'none' ? 'en' : e.kind === 'bad' ? 'bad' : ''}">${esc(e.text)}</p>`;
  if (st.showSrc && e.orig) html += `<div class="src">${esc(e.orig)}</div>`;
  div.innerHTML = html;
  return div;
}
function redrawFeed() {
  const box = $('#feed');
  feed.lastTurn = -1;
  const frag = document.createDocumentFragment();
  for (const e of feed.entries) frag.appendChild(entryNode(e));
  box.replaceChildren(frag);
  box.scrollTop = box.scrollHeight;
}
function renderThinkOpts() {
  $('#thinkOpts').innerHTML = `<button data-o="hideTasks" class="${st.hideTasks ? 'on' : ''}">背景タスクを隠す</button>` +
    `<button data-o="showSrc" class="${st.showSrc ? 'on' : ''}">原文を併記</button>`;
}

// ---------------- タブ・操作 ----------------

function setTab(t) {
  st.tab = ['meter', 'graph', 'speed', 'think'].includes(t) ? t : 'meter';
  store.set('tab', st.tab);
  document.querySelectorAll('.tab').forEach((el) => el.classList.toggle('on', el.id === 'tab-' + st.tab));
  document.querySelectorAll('#tabs button').forEach((b) => b.classList.toggle('on', b.dataset.tab === st.tab));
  window.scrollTo(0, 0);
  if (st.tab === 'meter') renderMeter();
  if (st.tab === 'graph') { st.last.hist = 0; drawHistory(); }
  if (st.tab === 'speed') { drawSpeed(); spInfo(st.spHit); }
  if (st.tab === 'think') { renderThinkOpts(); drawThink(); sizeFeed(); $('#feed').scrollTop = $('#feed').scrollHeight; }
  pump();
}

function bind() {
  document.querySelectorAll('#tabs button').forEach((b) => b.addEventListener('click', () => setTab(b.dataset.tab)));
  $('#slower').addEventListener('click', () => setIv(+1));
  $('#faster').addEventListener('click', () => setIv(-1));
  $('#ranges').addEventListener('click', (e) => { const k = e.target.closest('button')?.dataset.k; if (!k) return; st.range = k; store.set('range', k); st.hist = null; st.last.hist = 0; st.hoverX = null; drawHistory(); pump(); });
  $('#groups').addEventListener('click', (e) => { const k = e.target.closest('button')?.dataset.k; if (!k) return; st.group = k; store.set('group', k); drawHistory(); });
  $('#legend').addEventListener('click', (e) => { const k = e.target.closest('button')?.dataset.k; if (!k) return; st.hidden.has(k) ? st.hidden.delete(k) : st.hidden.add(k); store.set('hidden', [...st.hidden]); drawHistory(); });
  const hist = $('#hist');
  const hov = (e) => { const r = hist.getBoundingClientRect(); st.hoverX = e.clientX - r.left; drawHistory(); };
  hist.addEventListener('pointerdown', hov);
  hist.addEventListener('pointermove', (e) => { if (e.pointerType === 'mouse' || e.buttons || e.pressure > 0) hov(e); });
  $('#spY').addEventListener('click', (e) => { const k = e.target.closest('button')?.dataset.k; if (!k) return; st.sp.y = k; store.set('spY', k); drawSpeed(); });
  $('#spColor').addEventListener('click', (e) => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.server) { st.sp.server = b.dataset.server; store.set('spServer', st.sp.server); } else { st.sp.color = b.dataset.k; store.set('spColor', st.sp.color); }
    drawSpeed();
  });
  $('#spDays').addEventListener('click', (e) => { const k = e.target.closest('button')?.dataset.k; if (k == null) return; st.sp.days = +k; store.set('spDays', st.sp.days); st.last.speed = 0; pump(); drawSpeed(); });
  $('#spLegend').addEventListener('click', (e) => { const k = e.target.closest('button')?.dataset.k; if (!k) return; st.sp.hidden.has(k) ? st.sp.hidden.delete(k) : st.sp.hidden.add(k); store.set('spHidden', [...st.sp.hidden]); drawSpeed(); });
  $('#speed').addEventListener('pointerdown', (e) => {
    const r = $('#speed').getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
    let best = 22 * 22, hit = null;
    for (const [px, py, p] of spDrawn) { const d = (px - x) ** 2 + (py - y) ** 2; if (d < best) { best = d; hit = p; } }
    st.spHit = hit; spInfo(hit); drawSpeed();
  });
  $('#thinkOpts').addEventListener('click', (e) => {
    const o = e.target.closest('button')?.dataset.o; if (!o) return;
    st[o] = !st[o]; store.set(o, st[o]); renderThinkOpts();
    if (o === 'showSrc') redrawFeed();
  });
  // 画面が隠れている間は読みに行かない・中継も切る (電池と通信のため)。戻ったらすぐ読む
  document.addEventListener('visibilitychange', () => { if (document.hidden) closeEvents(); else { connectEvents(); st.last.now = 0; pump(); } });
  window.addEventListener('resize', () => { if (st.tab === 'graph') drawHistory(); if (st.tab === 'speed') drawSpeed(); sizeFeed(); });
}

bind();
setIv(0);
setTab(location.hash.slice(1) || st.tab);   // #graph などでタブを選んで開ける (ホーム画面のブックマーク用)
spInfo(null);
connectEvents();
setInterval(pump, 1000);
