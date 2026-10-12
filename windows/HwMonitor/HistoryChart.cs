using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace HwMonitor;

/// <summary>履歴の折れ線グラフ。上の段で期間 (1 時間〜30 日) と項目 (電力・温度・使用率・メモリ) を選ぶ。
/// 凡例を押すとその線を隠す / 出す。マウスを乗せた時刻の値を出す。トークンは生成・読込・文脈 (枠が使っている量) の 3 段に分ける</summary>
[DesignerCategory("Code")]
public sealed class HistoryChart : Control
{
    public static readonly (string key, string label)[] Ranges =
        [("2m", "120秒"), ("10m", "10分"), ("30m", "30分"), ("1h", "1時間"), ("3h", "3時間"), ("6h", "6時間"), ("24h", "24時間"), ("7d", "7日"), ("30d", "30日")];
    public static readonly (string key, string label, string unit)[] Groups =
        [("p", "電力", "W"), ("t", "温度", "℃"), ("u", "使用率", "%"), ("m", "メモリ", "GB"), ("r", "トークン", "t/s")];

    const int BarH = 26, LegendH = 20, PadL = 52, PadR = 14, PadB = 22;
    readonly List<(Rectangle r, Action act)> buttons = [];
    readonly List<(Rectangle r, string key)> legend = [];
    readonly HashSet<string> hidden = [];
    readonly Dictionary<string, Color> colors = [];
    HistoryData? data;
    string? error;
    int hoverX = -1;

    public event EventHandler? SelectionChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Range { get; set; } = "1h";
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Group { get; set; } = "p";
    /// <summary>OpenCode が圧縮を始める余裕 (k トークン)。枠の上限 − これ の高さに 3 段目の圧縮ラインを引く</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CompactReserveK { get; set; } = 20;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HistoryData? Data { get => data; set { data = value; ApplyLive(value); FillCtx(value); error = null; Invalidate(); } }

    // 枠が使っている量 (文脈) をアプリが見た分だけ覚えておく (/api/now の 2 秒ごと、24 時間まで)。
    // hwmon.py が記録していない間 (反映前・起動し直す前) は、これで 3 段目を描く。記録があればそちらを使い、無い刻みだけ埋める
    readonly List<(double t, Dictionary<string, double> v)> ctxSeen = [];
    double ctxLimit;   // 枠の上限 (k トークン、いちばん大きい枠)。3 段目に線を引く

    public void AddSlots(NowData now)
    {
        var v = new Dictionary<string, double>();
        double lim = 0;
        foreach (var srv in now.Slots)
            foreach (var s in srv.Slots)
            {
                lim = Math.Max(lim, s.NCtx / 1000.0);
                if (s.Used > 0) v[CtxKey(srv.Server, s.Id)] = Math.Round(s.Used / 1000.0, 1);   // hwmon.py と同じ (使ったことのある枠だけ)
            }
        if (lim > 0) ctxLimit = lim;
        if (v.Count == 0 || now.Ts <= 0) return;
        ctxSeen.Add((now.Ts, v));
        int old = ctxSeen.FindIndex(x => x.t >= now.Ts - 86400);
        if (old > 0) ctxSeen.RemoveRange(0, old);
    }

    static string CtxKey(string server, int id) => $"r:{server} 枠{id} 文脈";
    static bool IsCtx(string key) => key.EndsWith(" 文脈");

    // 見た値の無い刻みを、直前に見た値で埋めてよい間隔 (秒)。/api/now は 2 秒ごとだが、タイマーのずれや応答待ちで
    // 2 秒の刻みに 1 つも入らないことがあり、そこで線が切れて欠けて見える。使っている量は段のように動くので前の値のままでよい
    const double CtxHoldMax = 12;

    void FillCtx(HistoryData? d)
    {
        if (d == null || d.T.Count == 0 || ctxSeen.Count == 0) return;
        var index = new Dictionary<long, int>();
        for (int i = 0; i < d.T.Count; i++) index[d.T[i]] = i;
        var sums = new Dictionary<string, (double[] sum, int[] n)>();
        (double[] sum, int[] n) Acc(string k) =>
            sums.TryGetValue(k, out var a) ? a : sums[k] = (new double[d.T.Count], new int[d.T.Count]);
        foreach (var (t, v) in ctxSeen)
        {
            if (!index.TryGetValue((long)t / d.Step * d.Step, out int i)) continue;   // 記録の刻みに入らない所は捨てる
            foreach (var (k, x) in v)
            {
                var a = Acc(k);
                a.sum[i] += x;
                a.n[i]++;
            }
        }
        // 1 つも見ていない刻みは、その刻みの終わりより前に最後に見た値で埋める。
        // ただし前後に見た時刻が CtxHoldMax 秒より空いていたら (アプリを閉じていた・つながらなかった) 埋めずに線を切る
        var hold = new Dictionary<string, double?[]>();
        int j = -1;
        for (int i = 0; i < d.T.Count; i++)
        {
            long end = d.T[i] + d.Step;
            while (j + 1 < ctxSeen.Count && ctxSeen[j + 1].t < end) j++;
            if (j < 0) continue;
            var (pt, pv) = ctxSeen[j];
            double next = j + 1 < ctxSeen.Count ? ctxSeen[j + 1].t : Math.Max(d.To, d.T[i]);   // 右端はいまの時刻まで
            if (next - pt > CtxHoldMax) continue;
            foreach (var (k, x) in pv)
            {
                if (!hold.TryGetValue(k, out var h)) hold[k] = h = new double?[d.T.Count];
                h[i] = x;
                Acc(k);
            }
        }
        foreach (var (k, (sum, n)) in sums)
        {
            if (!d.Series.TryGetValue(k, out var vals)) d.Series[k] = vals = Enumerable.Repeat<double?>(null, d.T.Count).ToList();
            hold.TryGetValue(k, out var h);
            for (int i = 0; i < d.T.Count && i < vals.Count; i++)
                if (vals[i] == null)
                    vals[i] = n[i] > 0 ? Math.Round(sum[i] / n[i], 1) : h?[i];
        }
    }

    // 読み込みの最中の見込みで、右端 (まだ数が増えていない刻み) を埋める。見込みより記録が大きければ記録のまま
    static void ApplyLive(HistoryData? d)
    {
        if (d == null) return;
        foreach (var (key, list) in d.Live)
        {
            if (!d.Series.TryGetValue(key, out var vals)) continue;
            for (int i = 0; i < d.T.Count && i < vals.Count; i++)
            {
                double end = d.T[i] + d.Step, est = 0;
                foreach (var lv in list)   // 読んでいる枠ごとの見込みを足す (枠ごとの速さは GPU を分け合ったあとの値)
                    if (end > lv.From) est += lv.Rate * Math.Min(1, (end - lv.From) / d.Step);   // 読み始めが刻みの途中なら、その割合だけ
                if (est > 0 && (vals[i] ?? 0) < est) vals[i] = Math.Round(est, 1);
            }
        }
    }

    public void ShowError(string message) { error = message; Invalidate(); }

    public HistoryChart()
    {
        DoubleBuffered = true;
        BackColor = Theme.Back;
    }

    List<string> Keys()
    {
        if (data == null) return [];
        var keys = data.Series.Keys.Where(k => k.StartsWith(Group + ":")).ToList();
        if (Group != "r") return keys;
        // トークンは凡例・マウスを乗せたときの値を段の順 (生成・読込・文脈) に並べる。
        // 色は並べ替える前の記録の順で決める (読込が青・生成が橙のまま)
        foreach (var k in keys) ColorOf(k, 0);
        return keys.OrderBy(k => k.EndsWith(" 読込") ? 1 : IsCtx(k) ? 2 : 0).ToList();
    }

    Color ColorOf(string key, int i)
    {
        if (key == "p:全体") return Color.White;
        if (!colors.TryGetValue(key, out var c)) colors[key] = c = Theme.Series[colors.Count % Theme.Series.Length];
        return c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        buttons.Clear();
        legend.Clear();
        // 上の段: 期間と項目のボタン
        int x = 8;
        foreach (var (key, label) in Ranges)
            x = Button(g, x, label, Range == key, () => { Range = key; SelectionChanged?.Invoke(this, EventArgs.Empty); }) + 2;
        x += 16;
        foreach (var (key, label, _) in Groups)
            x = Button(g, x, label, Group == key, () => { Group = key; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }) + 2;

        var plot = new Rectangle(PadL, BarH + LegendH + 6, Width - PadL - PadR, Height - BarH - LegendH - 6 - PadB);
        if (plot.Width < 40 || plot.Height < 30) return;
        if (data == null || data.T.Count == 0)
        {
            TextRenderer.DrawText(g, error ?? (data == null ? "読み込み中…" : "まだ記録がありません (1 分ごとに貯まります)"),
                Theme.Ui, plot, error != null ? Theme.Warn : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var keys = Keys();
        // 凡例
        int lx = PadL;
        for (int i = 0; i < keys.Count; i++)
        {
            string name = keys[i][2..];
            var col = ColorOf(keys[i], i);
            var sz = TextRenderer.MeasureText(name, Theme.Small);
            var r = new Rectangle(lx, BarH + 2, sz.Width + 18, LegendH - 2);
            bool off = hidden.Contains(keys[i]);
            using (var b = new SolidBrush(off ? Theme.Grid : col)) g.FillRectangle(b, r.X, r.Y + 6, 12, 4);
            TextRenderer.DrawText(g, name, Theme.Small, new Point(r.X + 15, r.Y + 1), off ? Theme.Grid : Theme.Ink);
            string k = keys[i];
            legend.Add((r, k));
            lx = r.Right + 8;
        }

        var unit = Groups.First(x => x.key == Group).unit;
        var shown = keys.Where(k => !hidden.Contains(k)).ToList();
        long t0 = data.From, t1 = data.To;
        float X(long t) => plot.X + (float)((t - t0) / (double)Math.Max(1, t1 - t0) * plot.Width);

        // 段ごとに、目盛り (横線と値・時刻の縦線) と線を描く。目盛りの上限は段に出ている線だけで決める
        using var gridPen = new Pen(Theme.Grid);
        long gap = data.MaxGap > 0 ? data.MaxGap : (long)(data.Step * 2.5);   // 途中で記録が無い所 (null や max_gap より空いた所) は線を切る
        var panes = Panes(shown, plot);
        foreach (var (pr, pkeys, title) in panes)
        {
            bool ctx = pkeys.Count > 0 && pkeys.All(IsCtx);   // 3 段目 (文脈、k トークン)
            string pu = ctx ? "k" : unit;
            double max = 0;
            foreach (var k in pkeys) foreach (var v in data.Series[k]) if (v is double d && d > max) max = d;
            if (ctx) max = Math.Max(max, ctxLimit);   // 枠の上限まで入るように
            double top = Group == "u" ? 100 : Nice(max * (ctx ? 1.0 : 1.08));
            if (top <= 0) top = 1;
            float Y(double v) => pr.Bottom - (float)(v / top * pr.Height);

            // 横線の数は、上限の頭の数字で決めて目盛りを切りのよい値にする:
            // 2.5・5 は 5 等分 (10・20…50)、1.5・3・6 は 3 等分 (20・40・60)、1・2・4・8 は 4 等分 (低い段は 2 等分)
            double mant = top / Math.Pow(10, Math.Floor(Math.Log10(top)));
            bool Is(params double[] ms) => ms.Any(m => Math.Abs(mant - m) < 1e-9);
            int div = Is(2.5, 5) ? 5 : Is(1.5, 3, 6) ? 3 : pr.Height >= 120 ? 4 : 2;
            for (int i = 0; i <= div; i++)
            {
                // 2 段目からは一番上の値を出さない (上の段の 0 と重なるので)
                if (i == div && pr.Y > plot.Y) continue;   // 上の線は段の枠で描かれる
                double v = top * i / div;
                float y = Y(v);
                g.DrawLine(gridPen, pr.X, y, pr.Right, y);
                TextRenderer.DrawText(g, v.ToString(v >= 10 || v == 0 ? "0" : "0.#") + pu, Theme.Small,
                    new Rectangle(0, (int)y - 8, PadL - 4, 16), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            // 文脈の段は、OpenCode が圧縮を始める高さ (枠の上限 − 予約) に線を引く。
            // OpenCode は「使っている量 ≥ 枠の上限 − 予約 (既定 2 万)」で古いやり取りをまとめ直す
            if (ctx && ctxLimit > 0)
            {
                double compact = Math.Max(0, ctxLimit - CompactReserveK);
                using (var p = new Pen(Color.FromArgb(160, Theme.Warn)) { DashStyle = DashStyle.Dash }) g.DrawLine(p, pr.X, Y(compact), pr.Right, Y(compact));
                string lt = $"圧縮 {compact:0}k";
                var lsz = TextRenderer.MeasureText(lt, Theme.Small);
                var lp = new Point(pr.Right - lsz.Width - 4, (int)Y(compact) - lsz.Height);
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Back))) g.FillRectangle(b, lp.X - 2, lp.Y, lsz.Width + 4, lsz.Height);
                TextRenderer.DrawText(g, lt, Theme.Small, lp, Theme.Warn);
            }
            foreach (var (t, _) in TimeTicks(t0, t1)) g.DrawLine(gridPen, X(t), pr.Y, X(t), pr.Bottom);
            using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, pr);
            if (title != null)   // 後ろを塗って、目盛りや上限の線と重なっても読めるように
            {
                var tsz = TextRenderer.MeasureText(title, Theme.Small);
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Back))) g.FillRectangle(b, pr.X + 1, pr.Y + 1, tsz.Width + 6, tsz.Height + 3);
                TextRenderer.DrawText(g, title, Theme.Small, new Point(pr.X + 4, pr.Y + 3), Theme.Muted);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var k in pkeys)
            {
                using var pen = new Pen(ColorOf(k, 0), k == "p:全体" ? 2f : 1.4f);
                var vals = data.Series[k];
                var pts = new List<PointF>();
                long prev = 0;
                for (int i = 0; i < data.T.Count; i++)
                {
                    if (vals[i] is not double v || (pts.Count > 0 && data.T[i] - prev > gap))
                    {
                        Flush(g, pen, pts);
                        if (vals[i] is not double) continue;
                    }
                    pts.Add(new PointF(X(data.T[i] + data.Step / 2), Y(vals[i]!.Value)));
                    prev = data.T[i];
                }
                Flush(g, pen, pts);
            }
            g.SmoothingMode = SmoothingMode.None;

            // トークンは、0 でない所だけの平均 (止まっている間を除いた、動いているときの速さ) を軸のところに ▶AVE で出す
            if (Group == "r" && !ctx)
                foreach (var k in pkeys)
                {
                    var nz = data.Series[k].Where(v => v is > 0).Select(v => v!.Value).ToList();
                    if (nz.Count == 0) continue;
                    double avg = nz.Average();
                    // 文字が段からはみ出さないように寄せる。段が 16px より低いと Math.Clamp は例外になるので Min・Max で
                    float y = Math.Min(Math.Max(Y(avg), pr.Y + 8), pr.Bottom - 8);
                    var col = ColorOf(k, 0);
                    using (var p = new Pen(Color.FromArgb(110, col)) { DashStyle = DashStyle.Dash }) g.DrawLine(p, pr.X, Y(avg), pr.Right, Y(avg));
                    string text = $"AVE {avg:0.#}{unit}";
                    var sz = TextRenderer.MeasureText(text, Theme.Small);
                    var box = new Rectangle(pr.X + 10, (int)y - sz.Height / 2, sz.Width, sz.Height);
                    using (var b = new SolidBrush(Color.FromArgb(220, Theme.Back))) g.FillRectangle(b, box);
                    using (var b = new SolidBrush(col)) g.FillPolygon(b, [new PointF(pr.X + 1, y - 5), new PointF(pr.X + 9, y), new PointF(pr.X + 1, y + 5)]);
                    TextRenderer.DrawText(g, text, Theme.Small, box.Location, col);
                }
        }
        // 時刻は一番下の段の下に 1 回だけ
        foreach (var (t, label) in TimeTicks(t0, t1))
            TextRenderer.DrawText(g, label, Theme.Small, new Point((int)X(t) - 20, plot.Bottom + 4), Theme.Muted);

        // マウスを乗せた時刻の値
        if (hoverX >= plot.X && hoverX <= plot.Right)
        {
            long th = t0 + (long)((hoverX - plot.X) / (double)plot.Width * (t1 - t0));
            int idx = 0;
            for (int i = 0; i < data.T.Count; i++) if (Math.Abs(data.T[i] + data.Step / 2 - th) < Math.Abs(data.T[idx] + data.Step / 2 - th)) idx = i;
            // 近くに記録が無い所 (記録を始める前など) では値を出さない
            if (Math.Abs(data.T[idx] + data.Step / 2 - th) > gap) return;
            float hx = X(data.T[idx] + data.Step / 2);
            using (var p = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot })
                foreach (var (pr, _, _) in panes) g.DrawLine(p, hx, pr.Y, hx, pr.Bottom);
            var lines = new List<(string, Color)> { (FormatTime(data.T[idx] + data.Step / 2, t1 - t0), Theme.Head) };
            foreach (var k in shown)
                if (data.Series[k][idx] is double v) lines.Add(($"{k[2..]}  {v:0.#}{(IsCtx(k) ? "k" : unit)}", ColorOf(k, 0)));
            int bw = lines.Max(l => TextRenderer.MeasureText(l.Item1, Theme.Small).Width) + 12, bh = lines.Count * 15 + 8;
            int bx = hx + 12 + bw > plot.Right ? (int)hx - 12 - bw : (int)hx + 12;
            var box = new Rectangle(bx, plot.Y + 6, bw, bh);
            using (var b = new SolidBrush(Color.FromArgb(230, Theme.Panel))) g.FillRectangle(b, box);
            using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, box);
            for (int i = 0; i < lines.Count; i++)
                TextRenderer.DrawText(g, lines[i].Item1, Theme.Small, new Point(box.X + 6, box.Y + 4 + i * 15), lines[i].Item2);
        }
    }

    const int PaneGap = 10;

    /// <summary>グラフの段。トークンは生成 (上・高さ 2/4)・読込 (1/4)・文脈 (1/4) に分け、段ごとの目盛りで描く。
    /// 読込は何百 t/s の山になるので、同じ目盛りだと数十 t/s の生成がつぶれる。文脈は単位が違う (k トークン)。
    /// 凡例で段の線を全部隠すと、残りの段が全体を使う</summary>
    List<(Rectangle r, List<string> keys, string? title)> Panes(List<string> shown, Rectangle plot)
    {
        var parts = new List<(List<string> keys, string? title, int weight)>();
        if (Group == "r")
        {
            var gen = shown.Where(k => !k.EndsWith(" 読込") && !IsCtx(k)).ToList();
            var read = shown.Where(k => k.EndsWith(" 読込")).ToList();
            var ctx = shown.Where(IsCtx).ToList();
            if (gen.Count > 0) parts.Add((gen, "生成", 2));
            if (read.Count > 0) parts.Add((read, "読込", 1));
            if (ctx.Count > 0) parts.Add((ctx, "文脈 (枠が使っている量)", 1));
        }
        if (parts.Count == 0) parts.Add((shown, null, 1));
        if (parts.Count == 1) return [(plot, parts[0].keys, parts[0].title)];

        var list = new List<(Rectangle, List<string>, string?)>();
        int usable = plot.Height - PaneGap * (parts.Count - 1), sum = parts.Sum(p => p.weight), y = plot.Y;
        for (int i = 0; i < parts.Count; i++)
        {
            int h = i == parts.Count - 1 ? plot.Bottom - y : usable * parts[i].weight / sum;
            list.Add((new Rectangle(plot.X, y, plot.Width, h), parts[i].keys, parts[i].title));
            y += h + PaneGap;
        }
        return list;
    }

    static void Flush(Graphics g, Pen pen, List<PointF> pts)
    {
        if (pts.Count >= 2) g.DrawLines(pen, pts.ToArray());
        else if (pts.Count == 1) g.DrawEllipse(pen, pts[0].X - 1, pts[0].Y - 1, 2, 2);
        pts.Clear();
    }

    int Button(Graphics g, int x, string text, bool on, Action act)
    {
        var sz = TextRenderer.MeasureText(text, Theme.Ui);
        var r = new Rectangle(x, 3, sz.Width + 12, BarH - 6);
        using (var b = new SolidBrush(on ? Theme.Accent : Theme.Panel)) g.FillRectangle(b, r);
        using (var p = new Pen(on ? Theme.Good : Theme.Edge)) g.DrawRectangle(p, r);
        TextRenderer.DrawText(g, text, Theme.Ui, r, on ? Theme.Head : Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        buttons.Add((r, act));
        return r.Right;
    }

    static double Nice(double v)
    {
        if (v <= 0) return 0;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (m * p >= v) return m * p;   // 刻みを細かくして、上の空きを小さく
        return 10 * p;
    }

    static DateTime Local(long t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

    static string FormatTime(long t, long span) =>
        span <= 600 ? Local(t).ToString("HH:mm:ss") : span <= 86400 ? Local(t).ToString("HH:mm") : Local(t).ToString("M/d HH:mm");

    // 時刻の目盛り: 期間に合わせて切りのよい間隔で
    static IEnumerable<(long, string)> TimeTicks(long t0, long t1)
    {
        long span = t1 - t0;
        long step = span <= 120 ? 20 : span <= 600 ? 60 : span <= 1800 ? 300 : span <= 3600 ? 600 : span <= 3 * 3600 ? 1800 : span <= 6 * 3600 ? 3600 : span <= 86400 ? 4 * 3600 : span <= 7 * 86400 ? 86400 : 5 * 86400;
        long offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;  // 日付の区切りを日本時間の 0 時に
        for (long t = ((t0 + offset) / step + 1) * step - offset; t < t1; t += step)
            yield return (t, step >= 86400 ? Local(t).ToString("M/d") : step < 60 ? Local(t).ToString("HH:mm:ss") : Local(t).ToString("HH:mm"));
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hoverX = e.X; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (r, act) in buttons) if (r.Contains(e.Location)) { act(); Invalidate(); return; }
        foreach (var (r, key) in legend)
            if (r.Contains(e.Location)) { if (!hidden.Remove(key)) hidden.Add(key); Invalidate(); return; }
    }
}
