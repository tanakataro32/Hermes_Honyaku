using System.ComponentModel;

namespace HwMonitor;

/// <summary>コスト比較: 電気代と、同じだけ推論したときのレンタル (GPU の時間貸し・API の従量) を、期間ごとの表と日ごとの棒で並べる。
/// トークン数は hwmon.py が llama-server の /slots から数えたもの (数え始めてからの分だけ)</summary>
[DesignerCategory("Code")]
public sealed class CostPanel : Control
{
    const int Pad = 10, RowH = 38, HeadH = 34;
    static readonly (string key, string label, Color color)[] Kinds =
    [
        ("elec", "電気代", Theme.Good),
        ("api", "API 従量", Theme.Series[0]),
        ("gpu_busy", "GPU 時間貸し (推論中だけ)", Theme.Series[1]),
        ("gpu_full", "GPU 時間貸し (つけっぱなし)", Theme.Series[3]),
    ];
    CompareData? data;
    string? error;
    int hover = -1;
    readonly List<Rectangle> bars = [];

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CompareData? Data { get => data; set { data = value; error = null; Invalidate(); } }

    public void ShowError(string message) { error = message; Invalidate(); }

    public CostPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Back;
    }

    static double? Get(CostSet c, string key) => key switch
    {
        "elec" => c.Elec, "api" => c.Api, "gpu_busy" => c.GpuBusy, "gpu_full" => c.GpuFull, _ => null,
    };

    static string Yen(double? v) => v is not double d ? "-" : d >= 1000 ? $"¥{d:#,0}" : d >= 10 ? $"¥{d:0}" : $"¥{d:0.0}";
    static string Tok(long v) => v >= 1_000_000 ? $"{v / 1e6:0.00}M" : v >= 1000 ? $"{v / 1e3:0.0}k" : $"{v}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        bars.Clear();
        if (data == null)
        {
            TextRenderer.DrawText(g, error ?? "読み込み中…", Theme.Ui, new Point(Pad, Pad), error != null ? Theme.Warn : Theme.Muted);
            return;
        }
        // ---- 表: 期間 × (トークン・推論時間・4 つの費用)。費用の下の小さい字は 100 万出力トークンあたり ----
        string[] heads = ["期間", "生成", "入力", "推論時間", .. Kinds.Select(k => k.label)];
        int[] widths = [56, 70, 70, 76, 120, 120, 170, 180];
        int x0 = Pad, y = Pad;
        int x = x0;
        for (int i = 0; i < heads.Length; i++)
        {
            var col = i >= 4 ? Kinds[i - 4].color : Theme.Muted;
            TextRenderer.DrawText(g, heads[i], Theme.Ui, new Rectangle(x, y, widths[i] - 6, HeadH - 14), col,
                i == 0 ? TextFormatFlags.Left : TextFormatFlags.Right);
            if (i >= 4)
                TextRenderer.DrawText(g, "/100万出力", Theme.Small, new Rectangle(x, y + 16, widths[i] - 6, 14), Theme.Muted, TextFormatFlags.Right);
            x += widths[i];
        }
        y += HeadH;
        using (var p = new Pen(Theme.Grid)) g.DrawLine(p, x0, y - 4, x, y - 4);
        foreach (var pr in data.Periods)
        {
            x = x0;
            // 最安を太字で (その期間に生成が無ければ比べない)
            string? best = pr.Out > 0 ? Kinds.Select(k => (k.key, v: Get(pr.Yen, k.key))).Where(t => t.v != null).MinBy(t => t.v)!.key : null;
            string[] cells = [pr.Label, Tok(pr.Out), Tok(pr.In), $"{pr.BusyH:0.0}/{pr.Hours:0.0}時間"];
            for (int i = 0; i < cells.Length; i++)
            {
                TextRenderer.DrawText(g, cells[i], i == 0 ? Theme.UiBold : Theme.Num, new Rectangle(x, y, widths[i] - 6, 18),
                    i == 0 ? Theme.Head : Theme.Ink, i == 0 ? TextFormatFlags.Left : TextFormatFlags.Right);
                x += widths[i];
            }
            for (int k = 0; k < Kinds.Length; k++)
            {
                var (key, _, color) = Kinds[k];
                int w = widths[4 + k] - 6;
                bool isBest = key == best;
                TextRenderer.DrawText(g, Yen(Get(pr.Yen, key)), isBest ? Theme.UiBold : Theme.Num, new Rectangle(x, y, w, 18),
                    isBest ? Theme.Head : Theme.Ink, TextFormatFlags.Right);
                TextRenderer.DrawText(g, Yen(Get(pr.PerMout, key)), Theme.Small, new Rectangle(x, y + 18, w, 14), Theme.Muted, TextFormatFlags.Right);
                x += widths[4 + k];
            }
            y += RowH;
        }
        // 注意書き
        var pz = data.Prices;
        string api = string.Join("・", pz.Api.Select(a => $"{a.Key} 入力${a.Value.In}/出力${a.Value.Out}"));
        string[] notes =
        [
            $"単価: 電気 {pz.YenKwh:0.#}円/kWh・GPU 時間貸し ${pz.GpuUsdPerHour:0.00}/時 (V100+P40 相当)・API $/100万トークン {api}・1ドル {pz.UsdJpy:0}円 (config の [rental])",
            "電気代はサーバー全体の推定 (推論していない時間も含む)。推論時間 = どれかの枠が動いていた時間 / 記録できた時間。太字はその期間でいちばん安いもの",
            "API の入力は、llama-server がキャッシュから使い回した分も満額で数えている (キャッシュ割引のある API ではもう少し安い)。トークンは数え始めてからの分だけ",
        ];
        y += 2;
        foreach (var n in notes)
        {
            TextRenderer.DrawText(g, n, Theme.Small, new Rectangle(x0, y, Width - Pad * 2, 14), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += 15;
        }

        // ---- 日ごとの棒: 31 日 × (電気代・API・GPU 推論中だけ)。つけっぱなしは高すぎて比べにくいので線で ----
        var plot = new Rectangle(Pad + 50, y + 26, Width - Pad * 2 - 50, Height - y - 26 - 22);
        if (plot.Height < 40 || data.Days.Count == 0) return;
        int lx = plot.X;
        foreach (var (key, label, color) in Kinds)
        {
            using (var b = new SolidBrush(color)) g.FillRectangle(b, lx, y + 10, 12, 6);
            TextRenderer.DrawText(g, label + (key == "gpu_full" ? " (線)" : ""), Theme.Small, new Point(lx + 15, y + 5), Theme.Ink);
            lx += TextRenderer.MeasureText(label, Theme.Small).Width + (key == "gpu_full" ? 50 : 30);
        }
        var days = data.Days;
        double max = days.Max(d => new[] { d.Yen.Elec ?? 0, d.Yen.Api ?? 0, d.Yen.GpuBusy ?? 0, d.Yen.GpuFull ?? 0 }.Max());
        double top = Nice(Math.Max(1, max) * 1.1);
        using var grid = new Pen(Theme.Grid);
        for (int i = 0; i <= 4; i++)
        {
            double v = top * i / 4;
            int yy = plot.Bottom - (int)(v / top * plot.Height);
            g.DrawLine(grid, plot.X, yy, plot.Right, yy);
            TextRenderer.DrawText(g, Yen(v), Theme.Small, new Rectangle(0, yy - 8, plot.X - 4, 16), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        float cw = plot.Width / (float)days.Count;
        float bw = Math.Max(1, (cw - 4) / 3);
        var line = new List<PointF>();
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            float cx = plot.X + i * cw + 2;
            double?[] vals = [d.Yen.Elec, d.Yen.Api, d.Yen.GpuBusy];
            for (int k = 0; k < 3; k++)
            {
                int h = (int)((vals[k] ?? 0) / top * plot.Height);
                using var b = new SolidBrush(Color.FromArgb(hover == i ? 255 : 210, Kinds[k].color));
                g.FillRectangle(b, cx + k * bw, plot.Bottom - h, Math.Max(1, bw - 1), h);
            }
            if (d.Yen.GpuFull is double gf) line.Add(new PointF(plot.X + i * cw + cw / 2, plot.Bottom - (float)(gf / top * plot.Height)));
            bars.Add(new Rectangle((int)(plot.X + i * cw), plot.Y, (int)Math.Ceiling(cw), plot.Height));
            var dt = DateTime.Parse(d.Date);
            if (i % 5 == 0 || i == days.Count - 1)
                TextRenderer.DrawText(g, dt.ToString("M/d"), Theme.Small, new Point((int)cx - 4, plot.Bottom + 4), Theme.Muted);
        }
        if (line.Count >= 2) using (var p = new Pen(Kinds[3].color, 1.5f)) g.DrawLines(p, line.ToArray());
        using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, plot);
        if (hover >= 0 && hover < days.Count)
        {
            var d = days[hover];
            string t = $"{DateTime.Parse(d.Date):M/d (ddd)}  生成 {Tok(d.Out)}・入力 {Tok(d.In)}・推論 {d.BusyH:0.0}時間   " +
                       $"電気 {Yen(d.Yen.Elec)}  API {Yen(d.Yen.Api)}  GPU 推論中 {Yen(d.Yen.GpuBusy)}  つけっぱなし {Yen(d.Yen.GpuFull)}";
            TextRenderer.DrawText(g, t, Theme.Ui, new Point(plot.X + 6, plot.Y + 4), Theme.Head);
        }
    }

    static double Nice(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 }) if (m * p >= v) return m * p;
        return 10 * p;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = bars.FindIndex(r => r.Contains(e.Location));
        if (h != hover) { hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
}
