using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace HwMonitor;

/// <summary>履歴の折れ線グラフ。上の段で期間 (1 時間〜30 日) と項目 (電力・温度・使用率・メモリ) を選ぶ。
/// 凡例を押すとその線を隠す / 出す。マウスを乗せた時刻の値を出す</summary>
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
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HistoryData? Data { get => data; set { data = value; ApplyLive(value); error = null; Invalidate(); } }

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

    List<string> Keys() => data == null ? [] :
        data.Series.Keys.Where(k => k.StartsWith(Group + ":")).ToList();

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
        double max = 0;
        foreach (var k in shown) foreach (var v in data.Series[k]) if (v is double d && d > max) max = d;
        double top = Group == "u" ? 100 : Nice(max * 1.08);
        if (top <= 0) top = 1;
        long t0 = data.From, t1 = data.To;
        float X(long t) => plot.X + (float)((t - t0) / (double)Math.Max(1, t1 - t0) * plot.Width);
        float Y(double v) => plot.Bottom - (float)(v / top * plot.Height);

        // 目盛り (横線 5 本と、時刻)
        using var gridPen = new Pen(Theme.Grid);
        for (int i = 0; i <= 4; i++)
        {
            double v = top * i / 4;
            float y = Y(v);
            g.DrawLine(gridPen, plot.X, y, plot.Right, y);
            TextRenderer.DrawText(g, v.ToString(v >= 10 || v == 0 ? "0" : "0.#") + unit, Theme.Small,
                new Rectangle(0, (int)y - 8, PadL - 4, 16), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        foreach (var (t, label) in TimeTicks(t0, t1))
        {
            float xx = X(t);
            g.DrawLine(gridPen, xx, plot.Y, xx, plot.Bottom);
            TextRenderer.DrawText(g, label, Theme.Small, new Point((int)xx - 20, plot.Bottom + 4), Theme.Muted);
        }
        using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, plot);

        // 線。途中で記録が無い所 (null や max_gap より空いた所) は切る
        long gap = data.MaxGap > 0 ? data.MaxGap : (long)(data.Step * 2.5);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var k in shown)
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

        // マウスを乗せた時刻の値
        if (hoverX >= plot.X && hoverX <= plot.Right)
        {
            long th = t0 + (long)((hoverX - plot.X) / (double)plot.Width * (t1 - t0));
            int idx = 0;
            for (int i = 0; i < data.T.Count; i++) if (Math.Abs(data.T[i] + data.Step / 2 - th) < Math.Abs(data.T[idx] + data.Step / 2 - th)) idx = i;
            // 近くに記録が無い所 (記録を始める前など) では値を出さない
            if (Math.Abs(data.T[idx] + data.Step / 2 - th) > gap) return;
            float hx = X(data.T[idx] + data.Step / 2);
            using (var p = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot }) g.DrawLine(p, hx, plot.Y, hx, plot.Bottom);
            var lines = new List<(string, Color)> { (FormatTime(data.T[idx] + data.Step / 2, t1 - t0), Theme.Head) };
            foreach (var k in shown)
                if (data.Series[k][idx] is double v) lines.Add(($"{k[2..]}  {v:0.#}{unit}", ColorOf(k, 0)));
            int bw = lines.Max(l => TextRenderer.MeasureText(l.Item1, Theme.Small).Width) + 12, bh = lines.Count * 15 + 8;
            int bx = hx + 12 + bw > plot.Right ? (int)hx - 12 - bw : (int)hx + 12;
            var box = new Rectangle(bx, plot.Y + 6, bw, bh);
            using (var b = new SolidBrush(Color.FromArgb(230, Theme.Panel))) g.FillRectangle(b, box);
            using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, box);
            for (int i = 0; i < lines.Count; i++)
                TextRenderer.DrawText(g, lines[i].Item1, Theme.Small, new Point(box.X + 6, box.Y + 4 + i * 15), lines[i].Item2);
        }
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
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 }) if (m * p >= v) return m * p;
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
