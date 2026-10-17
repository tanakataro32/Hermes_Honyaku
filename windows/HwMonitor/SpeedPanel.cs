using System.ComponentModel;
using System.Globalization;
using System.Drawing.Drawing2D;

namespace HwMonitor;

/// <summary>速度と文脈の散布図。llama-server のログから拾った要求ごとの速さ (/api/speed) を、
/// 横 = 文脈の量 (k トークン)、縦 = 生成・読み込みの速さ (t/s) か MTP の当たり率で 1 点ずつ描く。
/// 色は「設定ごと」(起動したときのモデル・枠の数) か「並列数」(同じ時刻に動いていた要求の数) で分け、色ごとに文脈 20k 刻みの中央値を線で結ぶ。
/// 文脈が長くなるとどれだけ遅くなるか、設定を変えて速くなったかを、普段の使い方の記録から見る</summary>
[DesignerCategory("Code")]
public sealed class SpeedPanel : Control
{
    public static readonly (string key, string label)[] Ys = [("gen", "生成"), ("pp", "読込"), ("draft", "MTP の当たり率")];
    public static readonly (string key, string label)[] ColorModes = [("run", "設定ごと"), ("par", "並列数")];
    public static readonly (int days, string label)[] Periods = [(1, "24時間"), (7, "7日"), (30, "30日"), (0, "全部")];
    // 小さすぎる要求は速さがぶれる (立ち上がりの時間が効く) ので点にしない
    const int MinGen = 16, MinPp = 256;
    const int BarH = 26, PadL = 52, PadR = 14, PadB = 34, Dot = 5;

    SpeedData? data;
    string? error;
    readonly List<(Rectangle r, Action act)> buttons = [];
    readonly List<(Rectangle r, string key)> legend = [];
    List<(PointF p, SpeedPoint pt, string group)> drawn = [];
    readonly ToolTip tip = new() { InitialDelay = 200, AutoPopDelay = 20000 };
    string? shownTip;

    public event EventHandler? SelectionChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Y { get; set; } = "gen";
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ColorBy { get; set; } = "run";
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Days { get; set; } = 7;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Server { get; set; } = "flashnext";
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HashSet<string> Hidden { get; set; } = [];
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SpeedData? Data { get => data; set { data = value; error = null; Invalidate(); } }

    public void ShowError(string message) { error = message; Invalidate(); }

    public SpeedPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Back;
    }

    static DateTime Local(double t) => DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).LocalDateTime;

    // 点にするもの: (横 = 文脈 k, 縦)。生成は生成の真ん中、読み込みは読み込みの真ん中の文脈の量
    (double x, double y)? Value(SpeedPoint p) => Y switch
    {
        "gen" when p.GenTps is double v && p.GenN >= MinGen => ((p.Ctx - p.GenN / 2.0) / 1000, v),
        "pp" when p.PpTps is double v && p.PpN >= MinPp => ((p.PpFrom + p.PpN / 2.0) / 1000, v),
        "draft" when p.Draft is double v && p.GenN >= MinGen => (p.Ctx / 1000.0, v * 100),
        _ => null,
    };

    // 設定の名前: モデルのファイル名から、同じサーバーのほかの設定と共通の頭 (Qwen3.8-Flash-Next- など) を省く
    static string RunKey(double? start) => "run:" + (start ?? 0).ToString("R", CultureInfo.InvariantCulture);

    Dictionary<string, string> RunLabels()
    {
        var runs = data!.Runs.Where(r => r.Server == Server).ToList();
        var models = runs.Select(r => r.Model).Distinct().ToList();
        string prefix = "";
        if (models.Count > 1)
        {
            prefix = models[0];
            foreach (var m in models) while (!m.StartsWith(prefix)) prefix = prefix[..^1];
            int cut = prefix.LastIndexOfAny(['-', '_', '.']);
            prefix = cut >= 0 ? prefix[..(cut + 1)] : "";
        }
        return runs.GroupBy(r => RunKey(r.Start)).ToDictionary(gr => gr.Key, gr =>
        {
            var r = gr.First();
            // ファイル名の末尾の -MTP は「・MTP」と重なるので省く (Qwen3.8-Flash-Next-V100x2-D2-MTP → V100x2-D2 1枠・MTP)
            var name = r.Model[prefix.Length..];
            if (r.Mtp && name.EndsWith("-MTP")) name = name[..^4];
            return
                $"{name} {r.NSlots}枠{(r.Mtp ? "・MTP" : "")} {Local(r.Start):M/d HH:mm}〜";
        });
    }

    string GroupOf(SpeedPoint p) => ColorBy == "par" ? $"並列 {Math.Min(p.Par, 4)}{(p.Par >= 4 ? "+" : "")}" : RunKey(p.Run);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        buttons.Clear();
        legend.Clear();
        drawn = [];
        int x = 8;
        foreach (var (key, label) in Ys) x = Button(g, x, label, Y == key, () => { Y = key; Changed(); }) + 2;
        x += 16;
        foreach (var (key, label) in ColorModes) x = Button(g, x, label, ColorBy == key, () => { ColorBy = key; Changed(); }) + 2;
        x += 16;
        foreach (var (days, label) in Periods) x = Button(g, x, label, Days == days, () => { Days = days; Changed(); }) + 2;
        if (data != null)
        {
            x += 16;
            foreach (var s in data.Runs.Select(r => r.Server).Distinct())
                x = Button(g, x, s, Server == s, () => { Server = s; Changed(); }) + 2;
        }

        if (data == null || error != null)
        {
            TextRenderer.DrawText(g, error ?? "読み込み中…", Theme.Ui, new Rectangle(0, BarH, Width, Height - BarH),
                error != null ? Theme.Warn : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (!data.Runs.Any(r => r.Server == Server) && data.Runs.Count > 0) Server = data.Runs[0].Server;   // 選んでいたサーバーが記録に無いとき
        var labels = RunLabels();
        var pts = data.Points.Where(p => p.Server == Server)
            .Select(p => (p, v: Value(p), grp: GroupOf(p))).Where(t => t.v != null).Select(t => (t.p, v: t.v!.Value, t.grp)).ToList();
        // 凡例 (押すと隠す)。設定ごとは起動した順、並列数は小さい順
        var groups = pts.GroupBy(t => t.grp).Select(gr => (key: gr.Key, n: gr.Count(), order: gr.Min(t => t.p.Run ?? 0))).ToList();
        groups = ColorBy == "par" ? [.. groups.OrderBy(t => t.key)] : [.. groups.OrderBy(t => t.order)];
        var colorOf = new Dictionary<string, Color>();
        int lx = PadL, ly = BarH + 2;
        for (int i = 0; i < groups.Count; i++)
        {
            var (key, n, _) = groups[i];
            colorOf[key] = Theme.Series[i % Theme.Series.Length];
            string name = (labels.TryGetValue(key, out var lb) ? lb : key.StartsWith("run:") ? "設定不明" : key) + $" ({n})";
            var sz = TextRenderer.MeasureText(name, Theme.Small);
            if (lx + sz.Width + 18 > Width - PadR && lx > PadL) { lx = PadL; ly += 16; }
            var r = new Rectangle(lx, ly, sz.Width + 18, 16);
            bool off = Hidden.Contains(key);
            using (var b = new SolidBrush(off ? Theme.Grid : colorOf[key])) g.FillEllipse(b, r.X, r.Y + 4, 8, 8);
            TextRenderer.DrawText(g, name, Theme.Small, new Point(r.X + 12, r.Y), off ? Theme.Grid : Theme.Ink);
            legend.Add((r, key));
            lx = r.Right + 10;
        }
        int top = ly + 22;
        var plot = new Rectangle(PadL, top, Width - PadL - PadR, Height - top - PadB);
        if (plot.Width < 60 || plot.Height < 40) return;
        var shown = pts.Where(t => !Hidden.Contains(t.grp)).ToList();
        if (shown.Count == 0)
        {
            TextRenderer.DrawText(g, pts.Count == 0 ? "この期間の記録がありません (llama-server のログから 30 秒ごとに拾います)" : "凡例で全部隠しています",
                Theme.Ui, plot, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        // 目盛り: 横は文脈の量 (k)、縦は速さ (外れ値で潰れないように 99% の所まで) か当たり率 (0〜100%)
        long nCtx = data.Runs.Where(r => r.Server == Server).Select(r => r.NCtx).DefaultIfEmpty(0).Max();
        double xMax = HistoryChart.Nice(Math.Max(shown.Max(t => t.v.x), 1) * 1.02);
        var ys = shown.Select(t => t.v.y).OrderBy(v => v).ToList();
        double yMax = Y == "draft" ? 100 : HistoryChart.Nice(ys[(int)((ys.Count - 1) * 0.99)] * 1.1);
        if (yMax <= 0) yMax = 1;
        float PX(double v) => plot.X + (float)(v / xMax * plot.Width);
        float PY(double v) => plot.Bottom - (float)(Math.Min(v, yMax) / yMax * plot.Height);
        string yu = Y == "draft" ? "%" : "t/s";
        using var grid = new Pen(Theme.Grid);
        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4;
            g.DrawLine(grid, plot.X, PY(v), plot.Right, PY(v));
            TextRenderer.DrawText(g, $"{v:0.#}{yu}", Theme.Small, new Rectangle(0, (int)PY(v) - 8, PadL - 4, 16), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        double xs = HistoryChart.Nice(xMax / 6);
        for (double v = 0; v <= xMax + 1e-9; v += xs)
        {
            g.DrawLine(grid, PX(v), plot.Y, PX(v), plot.Bottom);
            TextRenderer.DrawText(g, $"{v:0}k", Theme.Small, new Point((int)PX(v) - 12, plot.Bottom + 3), Theme.Muted);
        }
        TextRenderer.DrawText(g, Y == "pp" ? "読み込んでいる所の文脈の量 →" : "文脈の量 (枠が使っている量) →", Theme.Small,
            new Point(plot.Right - 190, plot.Bottom + 17), Theme.Muted);
        using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, plot);
        // OpenCode が圧縮を始める所 (枠の上限 − 予約) の目安
        if (nCtx > 0 && Y != "pp")
        {
            double c = nCtx / 1000.0 - CompactReserveK;
            if (c > 0 && c <= xMax)
                using (var p = new Pen(Color.FromArgb(160, Theme.Warn)) { DashStyle = DashStyle.Dash }) g.DrawLine(p, PX(c), plot.Y, PX(c), plot.Bottom);
        }
        // 点 (古いものから描いて、新しい点を上に)
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (p, v, grp) in shown.OrderBy(t => t.p.T))
        {
            var pp = new PointF(PX(v.x), PY(v.y));
            using var b = new SolidBrush(Color.FromArgb(120, colorOf[grp]));
            g.FillEllipse(b, pp.X - Dot / 2f, pp.Y - Dot / 2f, Dot, Dot);
            drawn.Add((pp, p, grp));
        }
        // 色ごとの傾向: 文脈 xs/2 刻みの中央値 (5 点以上ある刻みだけ) を結ぶ
        double bin = Math.Max(xs / 2, 1);
        foreach (var gr in shown.GroupBy(t => t.grp))
        {
            var line = gr.GroupBy(t => (int)(t.v.x / bin)).Where(b => b.Count() >= 5).OrderBy(b => b.Key)
                .Select(b => { var v = b.Select(t => t.v.y).OrderBy(y => y).ToList(); return new PointF(PX((b.Key + 0.5) * bin), PY(v[v.Count / 2])); })
                .ToArray();
            if (line.Length < 2) continue;
            using (var p = new Pen(Theme.Back, 4f)) g.DrawLines(p, line);
            using (var p = new Pen(colorOf[gr.Key], 2f)) g.DrawLines(p, line);
        }
        g.SmoothingMode = SmoothingMode.None;
        string note = Y switch
        {
            "gen" => $"1 点 = 1 要求 (生成 {MinGen} トークン以上)。線は文脈 {bin:0}k ごとの中央値",
            "pp" => $"1 点 = 1 要求 (読み込み {MinPp} トークン以上)。前の要求から使い回した分は読まないので、新しく読んだ分の速さ",
            _ => "MTP の先読みが当たった割合 (高いほど生成が速くなる)",
        };
        TextRenderer.DrawText(g, note, Theme.Small, new Point(plot.X + 6, plot.Y + 4), Theme.Muted);
    }

    /// <summary>OpenCode が圧縮を始める余裕 (k)。横軸に破線を引く</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CompactReserveK { get; set; } = 20;

    void Changed() { Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }

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

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (r, act) in buttons) if (r.Contains(e.Location)) { act(); return; }
        foreach (var (r, key) in legend)
            if (r.Contains(e.Location)) { if (!Hidden.Remove(key)) Hidden.Add(key); Changed(); return; }
    }

    // 近い点 (6px 以内) の中身を出す
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? text = null;
        double best = 36;
        SpeedPoint? hit = null;
        foreach (var (p, pt, _) in drawn)
        {
            double d = (p.X - e.X) * (p.X - e.X) + (p.Y - e.Y) * (p.Y - e.Y);
            if (d < best) { best = d; hit = pt; }
        }
        if (hit != null)
        {
            var run = data?.Runs.FirstOrDefault(r => r.Server == hit.Server && r.Start == hit.Run);
            text = $"{Local(hit.T):M/d HH:mm:ss} {hit.Server} 枠{hit.Slot}" + (run != null ? $"\n設定: {run.Model} {run.NSlots}枠{(run.Mtp ? "・MTP" : "")}" : "") +
                   $"\n終わったときの文脈 {hit.Ctx:#,0} トークン・並列 {hit.Par}" +
                   (hit.PpN > 0 ? $"\n読込 {hit.PpN:#,0} トークン ({hit.PpFrom:#,0} から){(hit.PpTps is double pv ? $" {pv:0.#} t/s" : "")}" : "") +
                   (hit.GenN > 0 ? $"\n生成 {hit.GenN:#,0} トークン{(hit.GenTps is double gv ? $" {gv:0.##} t/s" : "")}" : "") +
                   (hit.Draft is double dv ? $"\nMTP の当たり率 {dv * 100:0}%" : "");
        }
        if (text != shownTip)
        {
            shownTip = text;
            if (text == null) tip.Hide(this); else tip.Show(text, this, e.X + 14, e.Y + 18);
        }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); shownTip = null; tip.Hide(this); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
}
