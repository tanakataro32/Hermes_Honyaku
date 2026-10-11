using System.ComponentModel;

namespace HwMonitor;

/// <summary>電気代: 今日・今月・月末の見込みと、日ごと (31 日) の棒グラフ、月ごとの表。
/// 値はコンセント側の推定の電力を積み上げたもの (hwmon.py が止まっていた時間は入らない。棒の薄い所は測れなかった割合)</summary>
[DesignerCategory("Code")]
public sealed class EnergyPanel : Control
{
    const int TopH = 74, MonthW = 190, PadB = 22;
    EnergyData? data;
    string? error;
    int hover = -1;
    readonly List<Rectangle> bars = [];

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EnergyData? Data { get => data; set { data = value; error = null; Invalidate(); } }

    public void ShowError(string message) { error = message; Invalidate(); }

    public EnergyPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Back;
    }

    static string Yen(double v) => v >= 1000 ? $"¥{v:#,0}" : $"¥{v:0.#}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        bars.Clear();
        if (data == null)
        {
            TextRenderer.DrawText(g, error ?? "読み込み中…", Theme.Ui, new Point(10, 10), error != null ? Theme.Warn : Theme.Muted);
            return;
        }
        // 上の段: 3 つの数字
        var d = data;
        Block(g, 10, "今日", Yen(d.Today.Yen), $"{d.Today.Kwh:0.00} kWh" + (d.Today.AvgW is double tAvg ? $"・平均 {tAvg:0}W" : ""),
            d.Today.Coverage < 0.95 ? $"測れた時間 {d.Today.Coverage * 100:0}%" : "");
        Block(g, 200, "今月", Yen(d.Month.Yen), $"{d.Month.Kwh:0.0} kWh" + (d.Month.AvgW is double mAvg ? $"・平均 {mAvg:0}W" : ""), "");
        Block(g, 390, "月末の見込み", d.Month.ForecastYen is double f ? Yen(f) : "-",
            d.Month.ForecastKwh is double fk ? $"{fk:0} kWh (いまの平均が続いたら)" : "記録が 10 分たまると出ます", "");
        TextRenderer.DrawText(g, $"単価 {d.Price:0.##} 円/kWh (config の [hwmon] price_yen_kwh)・電力はコンセント側の推定",
            Theme.Small, new Point(10, TopH - 16), Theme.Muted);

        // 日ごとの棒グラフ
        var plot = new Rectangle(46, TopH + 10, Width - 46 - MonthW - 20, Height - TopH - 10 - PadB);
        if (plot.Width > 60 && plot.Height > 30 && d.Days.Count > 0)
        {
            double max = Math.Max(1, d.Days.Max(x => x.Yen));
            double top = Nice(max * 1.1);
            using var grid = new Pen(Theme.Grid);
            for (int i = 0; i <= 4; i++)
            {
                double v = top * i / 4;
                int y = plot.Bottom - (int)(v / top * plot.Height);
                g.DrawLine(grid, plot.X, y, plot.Right, y);
                TextRenderer.DrawText(g, Yen(v), Theme.Small, new Rectangle(0, y - 8, plot.X - 4, 16), Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            float bw = plot.Width / (float)d.Days.Count;
            for (int i = 0; i < d.Days.Count; i++)
            {
                var day = d.Days[i];
                int h = (int)(day.Yen / top * plot.Height);
                var r = new Rectangle(plot.X + (int)(i * bw) + 1, plot.Bottom - h, Math.Max(1, (int)bw - 2), h);
                bool today = i == d.Days.Count - 1;
                // 測れた割合が低い日は薄く (その日の電気代は実際より少なく出ている)
                int alpha = day.Coverage >= 0.95 || today ? 255 : 110;
                using (var b = new SolidBrush(Color.FromArgb(alpha, hover == i ? Theme.Warn : today ? Theme.Accent : Theme.Good)))
                    g.FillRectangle(b, r);
                bars.Add(new Rectangle(plot.X + (int)(i * bw), plot.Y, Math.Max(1, (int)bw), plot.Height));
                var dt = DateTime.Parse(day.Date);
                if (dt.Day == 1 || i == 0 || i % 5 == 0)
                    TextRenderer.DrawText(g, dt.ToString("M/d"), Theme.Small, new Point(plot.X + (int)(i * bw) - 6, plot.Bottom + 4), Theme.Muted);
            }
            using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, plot);
            if (hover >= 0 && hover < d.Days.Count)
            {
                var day = d.Days[hover];
                string t = $"{DateTime.Parse(day.Date):M/d (ddd)}  {Yen(day.Yen)}  {day.Kwh:0.00} kWh" +
                           (day.Coverage < 0.95 ? $"  (測れた時間 {day.Coverage * 100:0}%)" : "");
                TextRenderer.DrawText(g, t, Theme.Ui, new Point(plot.X + 6, plot.Y + 4), Theme.Head);
            }
        }

        // 月ごとの表
        int mx = Width - MonthW - 10, my = TopH + 10;
        TextRenderer.DrawText(g, "月ごと", Theme.UiBold, new Point(mx, my), Theme.Head);
        my += 20;
        foreach (var m in d.Months.AsEnumerable().Reverse().Take(Math.Max(1, (Height - my - 4) / 17)))
        {
            TextRenderer.DrawText(g, m.Month.Replace('-', '/'), Theme.Num, new Point(mx, my), Theme.Muted);
            TextRenderer.DrawText(g, $"{m.Kwh,6:0.0}kWh {Yen(m.Yen),8}", Theme.Num, new Rectangle(mx + 60, my, MonthW - 60, 16), Theme.Ink, TextFormatFlags.Right);
            my += 17;
        }
    }

    static void Block(Graphics g, int x, string title, string big, string sub, string note)
    {
        TextRenderer.DrawText(g, title, Theme.Ui, new Point(x, 6), Theme.Muted);
        TextRenderer.DrawText(g, big, Theme.UiBig, new Point(x, 20), Theme.Head);
        TextRenderer.DrawText(g, sub, Theme.Small, new Point(x + 2, 44), Theme.Ink);
        if (note != "") TextRenderer.DrawText(g, note, Theme.Small, new Point(x + 100, 6), Theme.Warn);
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
