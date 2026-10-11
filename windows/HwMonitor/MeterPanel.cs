using System.ComponentModel;

namespace HwMonitor;

/// <summary>いまの値のメーター。温度・電力・使用率・メモリ (+ディスク) の 4 列を横に並べる (翻訳の画面の「項目別」と同じ分け方)</summary>
[DesignerCategory("Code")]
public sealed class MeterPanel : Control
{
    const int RowH = 18, HeadH = 22, Pad = 8, LabelW = 78, ValueW = 70;
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
    public NowData? Data { get => data; set { data = value; error = null; Invalidate(); } }

    public void ShowError(string message) { error = message; Invalidate(); }

    /// <summary>いちばん長い列に合わせた高さ</summary>
    public int WantedHeight()
    {
        if (data == null) return HeadH + RowH * 6 + Pad * 2;
        int rows = Math.Max(Math.Max(data.Temps.Count, (data.Power?.Parts.Count ?? 0) + 1),
                            Math.Max(data.Util.Count, data.Mem.Count + data.Disks.Count + 1));
        return HeadH + RowH * rows + Pad * 2;
    }

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
        int colW = (Width - Pad * 5) / 4;
        Rectangle Col(int i) => new(Pad + i * (colW + Pad), Pad, colW, Height - Pad * 2);
        DrawTemps(g, Col(0));
        DrawPower(g, Col(1));
        DrawUtil(g, Col(2));
        DrawMem(g, Col(3));
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

    // 1 行: 名前・バー・値。pct2 は RAM のキャッシュ (青) を後ろに積む
    void Row(Graphics g, Rectangle c, int i, string label, double? pct, Color fill, string value, Color valueColor,
             string? tipText = null, double? pct2 = null)
    {
        int y = c.Y + HeadH + i * RowH;
        var lr = new Rectangle(c.X, y, LabelW, RowH);
        TextRenderer.DrawText(g, label, Theme.Ui, lr, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var br = new Rectangle(c.X + LabelW, y + 5, Math.Max(20, c.Width - LabelW - ValueW - 4), 8);
        using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
        double w1 = Math.Clamp(pct ?? 0, 0, 100);
        if (w1 > 0)
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, br.X, br.Y, (int)(br.Width * w1 / 100), br.Height);
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
                LevelInk(x.C, x.Warn, x.Hot), $"{x.Tip}\n黄 {x.Warn:0}℃ / 赤 {x.Hot:0}℃");
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
        Row(g, c, 0, "全体", pct, Theme.Level(pct, 80, 95), $"{p.Wall:0}/{p.Full:0}W", Theme.Ink, how);
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
                x.W is double w2 ? $"{w2:0}W" : "-", Theme.Ink, tipText);
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
                x.Throttle == "" ? Theme.Ink : fill, tipText);
        }
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
            Row(g, c, i++, x.Label, pct, pct >= 95 ? Theme.Warn : Theme.Good, val, pct >= 95 ? Theme.Warn : Theme.Ink, tipText, pct2);
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
