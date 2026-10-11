namespace HwMonitor;

/// <summary>上の帯 (ToolStrip) の暗い配色。押した状態 (グラフ / 電気代・最前面・和訳) は緑の枠で、文字は白のまま読めるように</summary>
sealed class DarkRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Panel);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Theme.Grid);
        e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var r = new Rectangle(0, 0, e.Item.Width - 1, e.Item.Height - 1);
        bool on = e.Item is ToolStripButton { Checked: true };
        var fill = e.Item.Pressed ? Theme.BarBack : on ? Theme.Accent : e.Item.Selected ? Theme.Grid : Theme.Panel;
        using (var b = new SolidBrush(fill)) e.Graphics.FillRectangle(b, r);
        if (on || e.Item.Selected)
            using (var p = new Pen(on ? Theme.Good : Theme.Edge)) e.Graphics.DrawRectangle(p, r);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is ToolStripButton) e.TextColor = e.Item is ToolStripButton { Checked: true } ? Theme.Head : Theme.Ink;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Theme.Edge);
        int x = e.Item.Width / 2;
        e.Graphics.DrawLine(p, x, 4, x, e.Item.Height - 4);
    }
}
