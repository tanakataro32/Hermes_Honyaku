using System.ComponentModel;

namespace HwMonitor;

/// <summary>llama-server の枠のメーター。サーバーごとに 1 段、枠を横に並べる (使っている量 / 枠の上限、生成の速さ)。
/// 推論中の枠は、翻訳の画面のターン (発信元・コンテキスト量) と入力の量が近いものを探して「OpenCode」などの札を付ける。
/// 待機中の枠は、最後に使っていた量と札を薄く残す</summary>
[DesignerCategory("Code")]
public sealed class SlotPanel : Control
{
    const int RowH = 36, NameW = 76, Pad = 8, Gap = 8;
    readonly TurnTracker turns;
    readonly Dictionary<(string, int), (string label, bool openCode, string tip)> labels = [];
    readonly ToolTip tip = new() { InitialDelay = 300, AutoPopDelay = 15000 };
    readonly List<(Rectangle r, string text)> hits = [];
    string? shownTip;
    List<SlotServer> data = [];

    public SlotPanel(TurnTracker turns)
    {
        this.turns = turns;
        DoubleBuffered = true;
        BackColor = Theme.Back;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<SlotServer> Data
    {
        get => data;
        set { data = value ?? []; Match(); Invalidate(); }
    }

    public int WantedHeight() => data.Count == 0 ? 0 : Pad + data.Count * RowH + 2;

    // 推論中の枠と、終わっていないターンを 1 対 1 で結ぶ。
    //   1. 時刻: 枠が新しい要求を受けた時刻と、翻訳の中継がターンを始めた時刻が 5 秒以内 (どちらも llmsv の時計)。近い順に
    //   2. 量 (時刻で結べなかったもの。アプリを途中で開いたときなど): 枠の入力が、中継の数えた量の 0.9〜1.8 倍 + 2 万トークン以内。
    //      中継はメッセージだけを数え、llama-server はツールの定義 (Hermes で約 1 万トークン) も数えるので、枠のほうが多い
    void Match()
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var open = turns.Snapshot().Where(t => !t.Ended && now - t.Ts < 7200).ToList();
        var byTime = new List<(double score, (string, int) key, TurnTracker.Turn t)>();
        var bySize = new List<(double score, (string, int) key, TurnTracker.Turn t)>();
        foreach (var srv in data)
            foreach (var s in srv.Slots.Where(s => s.Processing))
                foreach (var t in open)
                {
                    if (s.Started is double st && Math.Abs(st - t.Ts) <= 5) byTime.Add((Math.Abs(st - t.Ts), (srv.Server, s.Id), t));
                    else if (t.Tokens > 0 && s.Prompt >= t.Tokens * 0.9 && s.Prompt <= t.Tokens * 1.8 + 20000)
                        bySize.Add((Math.Abs(s.Prompt - t.Tokens) / (double)Math.Max(1, s.Prompt), (srv.Server, s.Id), t));
                }
        var usedSlot = new HashSet<(string, int)>();
        var usedTurn = new HashSet<int>();
        foreach (var (_, key, t) in byTime.OrderBy(p => p.score).Concat(bySize.OrderBy(p => p.score)))
        {
            if (usedSlot.Contains(key) || usedTurn.Contains(t.N)) continue;
            usedSlot.Add(key);
            usedTurn.Add(t.N);
            string who = t.Who != "" ? " · " + t.Who : "";
            string label = (t.Source == "" ? "?" : t.Source) + (t.Task ? " (背景)" : "") + who;
            labels[key] = (label, t.Source == "OpenCode", $"#{t.N} {t.Source}{who}" + (t.Tokens > 0 ? $"\n翻訳の中継が数えた入力 {t.Tokens:#,0} トークン (ツールの定義を除く)" : ""));
        }
        // 推論中なのに結べなかった枠は札を消す (前の要求の札を残さない)
        foreach (var srv in data)
            foreach (var s in srv.Slots.Where(s => s.Processing && !usedSlot.Contains((srv.Server, s.Id))))
                labels.Remove((srv.Server, s.Id));
    }

    static string K(double v) => v >= 100_000 ? $"{v / 1000:0}k" : v >= 1000 ? $"{v / 1000:0.0}k" : $"{v:0}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        hits.Clear();
        using (var p = new Pen(Theme.Grid)) g.DrawLine(p, Pad, 1, Width - Pad, 1);
        int y = Pad / 2 + 2;
        foreach (var srv in data)
        {
            TextRenderer.DrawText(g, "🧩 " + srv.Server, Theme.UiBold, new Rectangle(Pad, y, NameW, 18), srv.Up ? Theme.Head : Theme.Warn,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (!srv.Up)
            {
                TextRenderer.DrawText(g, "つながりません", Theme.Small, new Point(Pad, y + 18), Theme.Warn);
                y += RowH;
                continue;
            }
            int n = Math.Max(1, srv.Slots.Count);
            int cw = (Width - Pad * 2 - NameW - Gap * (n - 1)) / n;
            for (int i = 0; i < srv.Slots.Count; i++)
            {
                var s = srv.Slots[i];
                var cell = new Rectangle(Pad + NameW + i * (cw + Gap), y, cw, RowH - 4);
                labels.TryGetValue((srv.Server, s.Id), out var lb);
                double pct = s.NCtx > 0 ? s.Used * 100.0 / s.NCtx : 0;
                // 上の行: 枠の番号と札 (OpenCode は緑)
                string head = $"枠{s.Id}" + (lb.label != null ? "  " + lb.label : s.Processing ? "  推論中" : "");
                TextRenderer.DrawText(g, head, Theme.Small, new Rectangle(cell.X, cell.Y, cell.Width, 15),
                    lb.openCode ? Theme.Good : s.Processing ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                // 下の行: バーと「使っている量/上限 速さ」
                string val = s.Used > 0 ? $"{K(s.Used)}/{K(s.NCtx)}" + (s.Processing && s.Tps > 0 ? $" {s.Tps:0.#}t/s" : "") : $"空き/{K(s.NCtx)}";
                int vw = TextRenderer.MeasureText(val, Theme.Num).Width;
                var br = new Rectangle(cell.X, cell.Y + 19, Math.Max(10, cell.Width - vw - 4), 8);
                using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
                var fill = !s.Processing ? Theme.Grid : pct >= 90 ? Theme.Bad : pct >= 75 ? Theme.Warn : lb.openCode ? Theme.Good : Theme.Cache;
                if (pct > 0) using (var b = new SolidBrush(fill)) g.FillRectangle(b, br.X, br.Y, Math.Max(1, (int)(br.Width * Math.Min(100, pct) / 100)), br.Height);
                using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, br);
                TextRenderer.DrawText(g, val, Theme.Num, new Point(cell.Right - vw, cell.Y + 14), s.Processing ? Theme.Ink : Theme.Muted);
                string when = s.LastActive is double la ? DateTimeOffset.FromUnixTimeMilliseconds((long)(la * 1000)).LocalDateTime.ToString("HH:mm:ss") : "-";
                hits.Add((cell, $"{srv.Server} 枠{s.Id}: {(s.Processing ? "推論中" : "待機中 (最後に使っていた量)")}\n" +
                                $"使っている量 {s.Used:#,0} / {s.NCtx:#,0} トークン ({pct:0.0}%)\n" +
                                $"入力 {s.Prompt:#,0}・生成 {s.Decoded:#,0}" + (s.Processing ? $"・{s.Tps:0.#} トークン/秒" : "") +
                                $"\n最後に動いた時刻 {when}" + (lb.tip != null ? "\n" + lb.tip : "")));
            }
            y += RowH;
        }
    }

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

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); shownTip = null; tip.Hide(this); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
}
