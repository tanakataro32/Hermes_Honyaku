using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace HwMonitor;

/// <summary>いまの値のメーター。項目別 = 温度・電力・使用率・メモリ (+ディスク) の 4 列 (翻訳の画面の「項目別」と同じ分け方)、
/// 部品別 = CPU・GPU ごとに 1 行 (使用率・メモリ・温度・電力) と、スレッドごとの使用率。
/// 各行の右に直近の推移 (アプリが受け取った最近 60 回ぶん) を小さく描く。使用率のバーは長さに沿って緑→黄→赤</summary>
[DesignerCategory("Code")]
public sealed class MeterPanel : Control
{
    const int RowH = 18, HeadH = 22, Pad = 8, LabelW = 78, ValueW = 70, DevRowH = 22, SparkN = 60, CoreCellW = 140;
    // 直近の推移: キー (t:温度 p:電力 u:使用率 m:メモリ % c:スレッド) -> 最近 SparkN 回の値
    readonly Dictionary<string, List<double>> hist = [];
    readonly ToolTip tip = new() { InitialDelay = 300, AutoPopDelay = 15000 };
    readonly List<(Rectangle r, string text)> hits = [];
    string? shownTip;

    NowData? data;
    string? error;

    public MeterPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Back;
        ForeColor = Theme.Ink;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public NowData? Data { get => data; set { data = value; error = null; if (value != null) Push(value); Invalidate(); } }

    bool deviceView;
    /// <summary>部品別 (CPU・GPU ごとに 1 行) で出す。false = 項目別 (温度・電力・使用率・メモリの 4 列)</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DeviceView { get => deviceView; set { deviceView = value; Invalidate(); } }

    void Push(NowData d)
    {
        void Add(string key, double? v)
        {
            if (v is not double x) return;
            if (!hist.TryGetValue(key, out var l)) hist[key] = l = [];
            l.Add(x);
            if (l.Count > SparkN) l.RemoveRange(0, l.Count - SparkN);
        }
        foreach (var t in d.Temps) Add("t:" + t.Label, t.C);
        if (d.Power is { } p)
        {
            Add("p:全体", p.Wall);
            foreach (var x in p.Parts) Add("p:" + x.Label, x.W);
        }
        foreach (var u in d.Util) Add("u:" + u.Label, u.Pct);
        foreach (var m in d.Mem) Add("m:" + m.Label, m.Total > 0 ? (m.Used ?? 0) / m.Total * 100 : null);
        for (int i = 0; i < d.Cores.Count; i++) Add("c:" + i, d.Cores[i]);
    }

    public void ShowError(string message) { error = message; Invalidate(); }

    /// <summary>いちばん長い列に合わせた高さ</summary>
    public int WantedHeight()
    {
        if (data == null) return HeadH + RowH * 6 + Pad * 2;
        if (deviceView)
        {
            var (lw, rw) = DeviceCols();
            int left = HeadH + Devices().Count * DevRowH + 6 + Chips(lw).Select(c => c.row + 1).DefaultIfEmpty(0).Max() * RowH;
            int right = HeadH + CoreRows(rw) * RowH;
            return Math.Max(left, right) + Pad * 2;
        }
        int colW = (Width - Pad * 5) / 4;
        int util = data.Util.Count + (data.Cores.Count > 0 ? 1 + CoreRows(colW) : 0);
        int rows = Math.Max(Math.Max(data.Temps.Count, (data.Power?.Parts.Count ?? 0) + 1),
                            Math.Max(util, data.Mem.Count + data.Disks.Count + 1));
        return HeadH + RowH * rows + Pad * 2;
    }

    int CoresPerRow(int width) => Math.Max(1, width / CoreCellW);
    int CoreRows(int width) => data == null || data.Cores.Count == 0 ? 0 : (data.Cores.Count + CoresPerRow(width) - 1) / CoresPerRow(width);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        hits.Clear();
        if (data == null)
        {
            TextRenderer.DrawText(g, error ?? "読み込み中…", Theme.Ui, new Point(Pad, Pad), error != null ? Theme.Warn : Theme.Muted);
            return;
        }
        if (deviceView) DrawDevices(g);
        else
        {
            int colW = (Width - Pad * 5) / 4;
            Rectangle Col(int i) => new(Pad + i * (colW + Pad), Pad, colW, Height - Pad * 2);
            DrawTemps(g, Col(0));
            DrawPower(g, Col(1));
            DrawUtil(g, Col(2));
            DrawMem(g, Col(3));
        }
        if (error != null)
            TextRenderer.DrawText(g, error, Theme.Small, new Point(Width - 260, 2), Theme.Warn);
    }

    void Header(Graphics g, Rectangle c, string name, string summary, Color sumColor)
    {
        TextRenderer.DrawText(g, name, Theme.UiBold, new Point(c.X, c.Y), Theme.Head);
        TextRenderer.DrawText(g, summary, Theme.Num, new Rectangle(c.X, c.Y, c.Width, HeadH - 4), sumColor,
            TextFormatFlags.Right | TextFormatFlags.Top);
        using var p = new Pen(Theme.Grid);
        g.DrawLine(p, c.X, c.Y + HeadH - 4, c.Right, c.Y + HeadH - 4);
    }

    // 1 行: 名前・バー・(直近の推移)・値。pct2 は RAM のキャッシュ (青) を後ろに積む。
    // spark = 推移のキー (sparkMax が上端)。gradient = バーを長さに沿って緑→黄→赤に (使用率)
    void Row(Graphics g, Rectangle c, int i, string label, double? pct, Color fill, string value, Color valueColor,
             string? tipText = null, double? pct2 = null, string? spark = null, double sparkMax = 100, bool gradient = false)
    {
        int y = c.Y + HeadH + i * RowH;
        var lr = new Rectangle(c.X, y, LabelW, RowH);
        TextRenderer.DrawText(g, label, Theme.Ui, lr, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        int avail = Math.Max(20, c.Width - LabelW - ValueW - 4);
        int sw = spark != null && avail >= 150 ? Math.Min(110, (int)(avail * 0.38)) : 0;   // 狭いときは推移を出さない
        var br = new Rectangle(c.X + LabelW, y + 5, sw > 0 ? avail - sw - 6 : avail, 8);
        double w1 = Math.Clamp(pct ?? 0, 0, 100);
        if (gradient) Bar(g, br, w1);
        else
        {
            using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
            if (w1 > 0)
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, br.X, br.Y, (int)(br.Width * w1 / 100), br.Height);
        }
        if (sw > 0) Spark(g, new Rectangle(br.Right + 6, y + 2, sw, RowH - 4), spark!, sparkMax, gradient ? Heat(w1) : fill);
        if (pct2 is double p2 && p2 > 0)
        {
            double w2 = Math.Clamp(p2, 0, 100 - w1);
            using var b = new SolidBrush(Theme.Cache);
            g.FillRectangle(b, br.X + (int)(br.Width * w1 / 100), br.Y, (int)(br.Width * w2 / 100), br.Height);
        }
        using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, br);
        TextRenderer.DrawText(g, value, Theme.Num, new Rectangle(c.Right - ValueW, y, ValueW, RowH), valueColor,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (!string.IsNullOrEmpty(tipText)) hits.Add((new Rectangle(c.X, y, c.Width, RowH), tipText));
    }

    // 使用率のバー: 長さに沿って緑 (0%)→黄 (70%)→赤 (100%)。埋まっている所までを見せる (btop と同じ見せ方)
    static void Bar(Graphics g, Rectangle br, double pct)
    {
        using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
        int w = (int)(br.Width * Math.Clamp(pct, 0, 100) / 100);
        if (w <= 0 || br.Width <= 0) return;
        using var lg = new LinearGradientBrush(new Rectangle(br.X - 1, br.Y, br.Width + 2, br.Height), Theme.Good, Theme.Bad, 0f)
        {
            InterpolationColors = new ColorBlend { Colors = [Theme.Good, Theme.Good, Theme.Warn, Theme.Bad], Positions = [0f, 0.35f, 0.7f, 1f] },
        };
        g.FillRectangle(lg, br.X, br.Y, w, br.Height);
    }

    // バーの色の帯で、pct の所の色 (推移の線・スレッドの色に使う)
    static Color Heat(double pct)
    {
        double t = Math.Clamp(pct, 0, 100) / 100;
        static Color Mix(Color a, Color b, double f) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
        return t <= 0.35 ? Theme.Good : t <= 0.7 ? Mix(Theme.Good, Theme.Warn, (t - 0.35) / 0.35) : Mix(Theme.Warn, Theme.Bad, (t - 0.7) / 0.3);
    }

    // 直近の推移 (最近 SparkN 回、右端が今)。薄く塗った山と線。max が 0 以下なら推移の最大に合わせる (部品ごとの電力など)
    void Spark(Graphics g, Rectangle r, string key, double max, Color color)
    {
        using (var p = new Pen(Theme.Grid)) g.DrawLine(p, r.X, r.Bottom, r.Right, r.Bottom);
        if (!hist.TryGetValue(key, out var v) || v.Count < 2) return;
        if (max <= 0) max = v.Max() * 1.15;
        if (max <= 0) return;
        float step = r.Width / (float)(SparkN - 1);
        var pts = new List<PointF>();
        for (int i = 0; i < v.Count; i++)
            pts.Add(new PointF(r.Right - (v.Count - 1 - i) * step, r.Bottom - (float)(Math.Clamp(v[i] / max, 0, 1) * r.Height)));
        var area = new List<PointF>(pts) { new(pts[^1].X, r.Bottom), new(pts[0].X, r.Bottom) };
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb(70, color))) g.FillPolygon(b, area.ToArray());
        using (var p = new Pen(color, 1.2f)) g.DrawLines(p, pts.ToArray());
        g.SmoothingMode = old;
    }

    // スレッドごとの使用率のます目 (C0・C1…): 名前・推移・%
    void DrawCores(Graphics g, Rectangle area, int row0)
    {
        var cores = data!.Cores;
        int per = CoresPerRow(area.Width), gap = 8;
        int cw = (area.Width - gap * (per - 1)) / per;
        for (int i = 0; i < cores.Count; i++)
        {
            int col = i % per, row = row0 + i / per;
            var cell = new Rectangle(area.X + col * (cw + gap), area.Y + HeadH + row * RowH, cw, RowH);
            double v = cores[i];
            TextRenderer.DrawText(g, "C" + i, Theme.Small, new Rectangle(cell.X, cell.Y, 28, RowH), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            string pv = $"{v:0}%";
            TextRenderer.DrawText(g, pv, Theme.Num, new Rectangle(cell.Right - 40, cell.Y, 40, RowH), v >= 90 ? Theme.Bad : v >= 70 ? Theme.Warn : Theme.Ink,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var sr = new Rectangle(cell.X + 28, cell.Y + 2, Math.Max(10, cw - 28 - 44), RowH - 4);
            using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, sr);
            Spark(g, sr, "c:" + i, 100, Heat(v));
            hits.Add((cell, $"スレッド C{i} (論理 CPU {i}) の使用率 {v:0.#}%\n推論に使っているスレッドは 100% に張り付く (llama.cpp の -t の数)"));
        }
    }

    void DrawTemps(Graphics g, Rectangle c)
    {
        var t = data!.Temps;
        // 閉じた要約と同じく、黄・赤の境目にいちばん近いもの
        var worst = t.OrderByDescending(x => x.C / Math.Max(1, x.Warn)).FirstOrDefault();
        Header(g, c, "🌡 温度", worst == null ? "" : $"{worst.Label} {worst.C:0}℃",
            worst == null ? Theme.Ink : LevelInk(worst.C, worst.Warn, worst.Hot));
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            Row(g, c, i, x.Label, x.C / x.Max * 100, Theme.Level(x.C, x.Warn, x.Hot), $"{x.C:0}℃",
                LevelInk(x.C, x.Warn, x.Hot), $"{x.Tip}\n黄 {x.Warn:0}℃ / 赤 {x.Hot:0}℃", spark: "t:" + x.Label, sparkMax: x.Max);
        }
    }

    void DrawPower(Graphics g, Rectangle c)
    {
        var p = data!.Power;
        if (p == null) { Header(g, c, "⚡ 電力 (推定)", "-", Theme.Ink); return; }
        Header(g, c, "⚡ 電力 (推定)", $"約 {p.Wall:0}W", Theme.Ink);
        string how = $"コンセント側の見込み = 部品の合計 {p.Dc:0}W ÷ 電源の効率 {p.Eff * 100:0}%\n" +
                     $"GPU は nvidia-smi、CPU は {p.CpuSrc} の実測。その他・電源ロスは見込み\nバーの満杯 {p.Full:0}W";
        double pct = p.Full > 0 ? p.Wall / p.Full * 100 : 0;
        Row(g, c, 0, "全体", pct, Theme.Level(pct, 80, 95), $"{p.Wall:0}/{p.Full:0}W", Theme.Ink, how, spark: "p:全体", sparkMax: p.Full);
        for (int i = 0; i < p.Parts.Count; i++)
        {
            var x = p.Parts[i];
            string tipText = x.Kind switch
            {
                "gpu" => $"GPU {x.Label} (nvidia-smi の実測)",
                "cpu" => $"CPU ソケット全体 ({p.CpuSrc})",
                "other" => "マザーボード・メモリ・SSD・ファンなどの見込み (config の [power] other_w)",
                "loss" => "電源ユニットの変換ロスの見込み (config の [power] psu_eff)",
                _ => x.Label,
            };
            Row(g, c, i + 1, x.Label, x.W is double w && p.Wall > 0 ? w / p.Wall * 100 : null, Theme.Good,
                x.W is double w2 ? $"{w2:0}W" : "-", Theme.Ink, tipText, spark: x.Est ? null : "p:" + x.Label, sparkMax: 0);
        }
    }

    void DrawUtil(Graphics g, Rectangle c)
    {
        var u = data!.Util;
        Header(g, c, "🚀 使用率", string.Join(" ", u.Select(x => x.Pct is double v ? $"{v:0}%" : "-")), Theme.Ink);
        for (int i = 0; i < u.Count; i++)
        {
            var x = u[i];
            var fill = x.Throttle == "hot" ? Theme.Bad : x.Throttle == "warn" ? Theme.Warn : Theme.Good;
            string tipText = x.Kind == "cpu" ? "CPU の使用率 (/proc/stat)" :
                $"GPU の使用率 (計算が動いていた時間の割合)\nSM クロック {x.Clock:0} / 最大 {x.ClockMax:0} MHz\n" +
                $"クロックを下げている理由: {(x.Reasons.Count > 0 ? string.Join("・", x.Reasons) : "なし")}";
            Row(g, c, i, x.Label, x.Pct, fill, x.Pct is double v ? $"{v:0}%" : "-",
                x.Throttle == "" ? Theme.Ink : fill, tipText, spark: "u:" + x.Label, gradient: x.Throttle == "");
        }
        if (data.Cores.Count == 0) return;
        // 使用率の下に、スレッドごとの使用率 (1 行ぶんの区切りのあと)
        int y = c.Y + HeadH + u.Count * RowH + RowH / 2 - 1;
        using (var p = new Pen(Theme.Grid)) g.DrawLine(p, c.X, y, c.Right, y);
        DrawCores(g, c, u.Count + 1);
    }


    void DrawMem(Graphics g, Rectangle c)
    {
        var m = data!.Mem;
        double vu = m.Where(x => x.Kind == "vram").Sum(x => x.Used ?? 0), vt = m.Where(x => x.Kind == "vram").Sum(x => x.Total ?? 0);
        Header(g, c, "▦ メモリ", vt > 0 ? $"VRAM {vu:0}/{vt:0}G" : "", Theme.Ink);
        int i = 0;
        foreach (var x in m)
        {
            double pct = x.Total > 0 ? (x.Used ?? 0) / x.Total.Value * 100 : 0;
            double? pct2 = x.Cache is double ca && x.Total > 0 ? ca / x.Total.Value * 100 : null;
            // RAM は「使用中+キャッシュ/全体」を整数で (小数まで出すと値の欄からはみ出す)
            string val = x.Cache is double cc ? $"{x.Used:0}+{cc:0}/{x.Total:0}G" : $"{x.Used:0.0}/{x.Total:0}G";
            string tipText = x.Kind == "ram"
                ? $"使用中 {x.Used:0.0}G (緑) + キャッシュ {x.Cache:0.0}G (青) / 全体 {x.Total:0.0}G\nキャッシュは llama.cpp が mmap で読んだモデルなど (空けられるが、空けると SSD から読み直し)"
                : $"{x.Label} の VRAM";
            Row(g, c, i++, x.Label, pct, pct >= 95 ? Theme.Warn : Theme.Good, val, pct >= 95 ? Theme.Warn : Theme.Ink, tipText, pct2, spark: "m:" + x.Label);
        }
        if (data!.Disks.Count == 0) return;
        // メモリとディスクの間に 1 行ぶんの区切り
        int y = c.Y + HeadH + i * RowH + RowH / 2 - 1;
        using (var p = new Pen(Theme.Grid)) g.DrawLine(p, c.X, y, c.Right, y);
        i++;
        foreach (var d in data.Disks)
        {
            double pct = d.Total > 0 ? (d.Used ?? 0) / d.Total.Value * 100 : 0;
            Row(g, c, i++, d.Label, pct, Theme.Level(pct, 80, 90), $"{d.Used:0}/{d.Total:0}G",
                pct >= 90 ? Theme.Warn : Theme.Ink, d.Tip);
        }
    }

    // ---- 部品別: CPU・GPU ごとに 1 行 (使用率・メモリ・温度・電力)、右にスレッド、下にほかの温度・電力・ディスク ----

    sealed record Device(string Label, UtilItem? Util, MemItem? Mem, TempItem? Temp, PowerPart? Power);

    List<Device> Devices()
    {
        var d = data!;
        var parts = d.Power?.Parts ?? [];
        var list = new List<Device>
        {
            new("CPU", d.Util.FirstOrDefault(x => x.Kind == "cpu"), d.Mem.FirstOrDefault(x => x.Kind == "ram"),
                d.Temps.FirstOrDefault(x => x.Kind == "cpu"), parts.FirstOrDefault(x => x.Kind == "cpu")),
        };
        foreach (var u in d.Util.Where(x => x.Kind == "gpu"))
            list.Add(new(u.Label, u, d.Mem.FirstOrDefault(x => x.Kind == "vram" && x.Label == u.Label),
                d.Temps.FirstOrDefault(x => x.Kind == "gpu" && x.Label == u.Label), parts.FirstOrDefault(x => x.Kind == "gpu" && x.Label == u.Label)));
        return list;
    }

    (int left, int right) DeviceCols()
    {
        int w = Width - Pad * 3;
        int lw = Math.Max(300, (int)(w * 0.64));
        return (lw, Math.Max(100, w - lw));
    }

    // 部品の行に入らないもの (メモリ・SSD・マザボなどの温度、その他・電源ロスの電力、ディスク) を「名前 値」で折り返して並べる
    List<(string text, Color color, string tip, int x, int row)> Chips(int width)
    {
        var d = data!;
        var items = new List<(string text, Color color, string tip)>();
        foreach (var t in d.Temps.Where(x => x.Kind is not ("cpu" or "gpu")))
            items.Add(($"{t.Label} {t.C:0}℃", LevelInk(t.C, t.Warn, t.Hot), $"{t.Tip}\n黄 {t.Warn:0}℃ / 赤 {t.Hot:0}℃"));
        foreach (var x in (d.Power?.Parts ?? []).Where(x => x.Kind is not ("cpu" or "gpu")))
            items.Add(($"{x.Label} {x.W:0}W", Theme.Muted, x.Kind == "other" ? "マザーボード・メモリ・SSD・ファンなどの見込み" : "電源ユニットの変換ロスの見込み"));
        foreach (var k in d.Disks)
        {
            double pct = k.Total > 0 ? (k.Used ?? 0) / k.Total.Value * 100 : 0;
            items.Add(($"{k.Label} {k.Used:0}/{k.Total:0}G", pct >= 90 ? Theme.Warn : Theme.Ink, k.Tip));
        }
        var outList = new List<(string, Color, string, int, int)>();
        int x0 = 0, row = 0;
        foreach (var (text, color, tip) in items)
        {
            int w = TextRenderer.MeasureText(text, Theme.Ui).Width;
            if (x0 > 0 && x0 + w > width) { x0 = 0; row++; }
            outList.Add((text, color, tip, x0, row));
            x0 += w + 18;
        }
        return outList;
    }

    void DrawDevices(Graphics g)
    {
        var d = data!;
        var (lw, rw) = DeviceCols();
        var L = new Rectangle(Pad, Pad, lw, Height - Pad * 2);
        var R = new Rectangle(Pad * 2 + lw, Pad, rw, Height - Pad * 2);
        var p = d.Power;
        Header(g, L, "🧩 部品別", p == null ? "" : $"約 {p.Wall:0}/{p.Full:0}W", Theme.Ink);
        const int NameW = 60, PctW = 44, MemTW = 100, MemBW = 56, TempW = 48, PowW = 52, G = 8;
        int flex = L.Width - (NameW + PctW + MemTW + MemBW + TempW + PowW + G * 8);
        bool sparks = flex >= 160;   // 狭いときは推移を出さずにバーを長く
        int barW = sparks ? (int)(flex * 0.45) : Math.Max(30, flex + G * 2);
        int s1 = sparks ? (int)(flex * 0.30) : 0, s2 = sparks ? flex - barW - s1 : 0;
        var devs = Devices();
        for (int i = 0; i < devs.Count; i++)
        {
            var dv = devs[i];
            int y = L.Y + HeadH + i * DevRowH, x = L.X;
            var row = new Rectangle(L.X, y, L.Width, DevRowH);
            Rectangle Cell(int w) { var r = new Rectangle(x, y, w, DevRowH); x += w + G; return r; }
            const TextFormatFlags VC = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, dv.Label, Theme.UiBold, Cell(NameW), Theme.Head, VC | TextFormatFlags.Left);
            // 使用率 (長さに沿って緑→黄→赤。クロックを下げているときは黄・赤の一色)
            double up = dv.Util?.Pct ?? 0;
            string th = dv.Util?.Throttle ?? "";
            var bar = Cell(barW);
            var br = new Rectangle(bar.X, y + 7, bar.Width, 8);
            if (th == "") Bar(g, br, up);
            else
            {
                using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
                using (var b = new SolidBrush(th == "hot" ? Theme.Bad : Theme.Warn)) g.FillRectangle(b, br.X, br.Y, (int)(br.Width * Math.Clamp(up, 0, 100) / 100), br.Height);
            }
            using (var pe = new Pen(Theme.Edge)) g.DrawRectangle(pe, br);
            TextRenderer.DrawText(g, dv.Util?.Pct is double v ? $"{v:0}%" : "-", Theme.Num, Cell(PctW),
                th == "hot" ? Theme.Bad : th == "warn" ? Theme.Warn : Theme.Ink, VC | TextFormatFlags.Right);
            if (sparks) { var sr = Cell(s1); Spark(g, new Rectangle(sr.X, y + 3, sr.Width, DevRowH - 6), "u:" + dv.Label, 100, Heat(up)); }
            // メモリ (RAM は使用中 + キャッシュ、GPU は VRAM)
            var m = dv.Mem;
            double mp = m?.Total > 0 ? (m.Used ?? 0) / m.Total.Value * 100 : 0;
            string mt = m == null ? "-" : m.Cache is double cc ? $"{m.Used:0}+{cc:0}/{m.Total:0}G" : $"{m.Used:0.0}/{m.Total:0}G";
            TextRenderer.DrawText(g, mt, Theme.Num, Cell(MemTW), mp >= 95 ? Theme.Warn : Theme.Ink, VC | TextFormatFlags.Right);
            var mb = Cell(MemBW);
            var mr = new Rectangle(mb.X, y + 7, mb.Width, 8);
            using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, mr);
            int w1 = (int)(mr.Width * Math.Clamp(mp, 0, 100) / 100);
            using (var b = new SolidBrush(mp >= 95 ? Theme.Warn : Theme.Good)) g.FillRectangle(b, mr.X, mr.Y, w1, mr.Height);
            if (m?.Cache is double ca && m.Total > 0)
                using (var b = new SolidBrush(Theme.Cache)) g.FillRectangle(b, mr.X + w1, mr.Y, (int)(mr.Width * Math.Clamp(ca / m.Total.Value * 100, 0, 100 - mp) / 100), mr.Height);
            using (var pe = new Pen(Theme.Edge)) g.DrawRectangle(pe, mr);
            // 温度・電力
            var t = dv.Temp;
            TextRenderer.DrawText(g, t == null ? "-" : $"{t.C:0}℃", Theme.Num, Cell(TempW), t == null ? Theme.Muted : LevelInk(t.C, t.Warn, t.Hot), VC | TextFormatFlags.Right);
            if (sparks && t != null) { var sr = Cell(s2); Spark(g, new Rectangle(sr.X, y + 3, sr.Width, DevRowH - 6), "t:" + t.Label, t.Max, Theme.Level(t.C, t.Warn, t.Hot)); }
            else if (sparks) x += s2 + G;   // 温度が無い部品は推移の場所を空ける
            TextRenderer.DrawText(g, dv.Power?.W is double pw ? $"{pw:0}W" : "-", Theme.Num, Cell(PowW), Theme.Ink, VC | TextFormatFlags.Right);
            string tip = $"{dv.Label}: 使用率 {(dv.Util?.Pct is double u2 ? $"{u2:0}%" : "-")}" +
                         (dv.Util?.Reasons.Count > 0 ? $" (クロックを下げている理由: {string.Join("・", dv.Util.Reasons)})" : "") +
                         $"\n{(m?.Kind == "ram" ? "RAM 使用中 + キャッシュ" : "VRAM")} {mt}" +
                         (t != null ? $"\n温度 {t.C:0.#}℃ (黄 {t.Warn:0}℃ / 赤 {t.Hot:0}℃)" : "") +
                         (dv.Power?.W is double pw2 ? $"\n電力 {pw2:0.#}W" : "") +
                         (sparks ? "\n小さな折れ線は直近の推移 (使用率・温度)" : "");
            hits.Add((row, tip));
        }
        // 下: 部品の行に入らない温度・電力・ディスク
        int yc = L.Y + HeadH + devs.Count * DevRowH + 3;
        using (var pe = new Pen(Theme.Grid)) g.DrawLine(pe, L.X, yc, L.Right, yc);
        foreach (var (text, color, tip, cx, crow) in Chips(L.Width))
        {
            var r = new Rectangle(L.X + cx, yc + 3 + crow * RowH, TextRenderer.MeasureText(text, Theme.Ui).Width, RowH);
            TextRenderer.DrawText(g, text, Theme.Ui, r, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (tip != "") hits.Add((r, tip));
        }
        // 右: スレッドごとの使用率
        Header(g, R, "🧵 スレッド", d.Cores.Count > 0 ? $"{d.Cores.Count} 本・90% 以上 {d.Cores.Count(c => c >= 90)} 本" : "", Theme.Ink);
        if (d.Cores.Count > 0) DrawCores(g, R, 0);
    }

    static Color LevelInk(double v, double warn, double hot) => v >= hot ? Theme.Bad : v >= warn ? Theme.Warn : Theme.Ink;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = hits.FirstOrDefault(x => x.r.Contains(e.Location));
        if (h.text != shownTip)
        {
            shownTip = h.text;
            if (h.text == null) tip.Hide(this); else tip.Show(h.text, this, e.X + 14, e.Y + 18);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        shownTip = null;
        tip.Hide(this);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
}
