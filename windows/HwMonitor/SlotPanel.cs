using System.ComponentModel;

namespace HwMonitor;

/// <summary>llama-server の枠のメーター。サーバーごとに 1 段、枠を横に並べる (使っている量 / 枠の上限、生成の速さ)。
/// 推論中の枠は、翻訳の画面のターン (発信元・コンテキスト量) と入力の量が近いものを探して「OpenCode」などの札を付ける。
/// 待機中の枠は、最後に使っていた量と札を薄く残す。
/// 推論中のバーは緑で、明るさ = 使っている量 (圧縮ラインで一番明るい)。
/// 読込・生成が終わるたびに、その間に増えた分 (始まったときの位置 → 終わったときの位置) を
/// 読込 = 青・生成 = 橙 (グラフの線と同じ色) で左から右へ伸ばして見せ、数秒点滅させて消す</summary>
[DesignerCategory("Code")]
public sealed class SlotPanel : Control
{
    const int RowH = 36, NameW = 76, Pad = 8, Gap = 8;
    readonly TurnTracker turns;
    readonly Dictionary<(string, int), (string label, bool openCode, string tip)> labels = [];
    readonly Dictionary<(string, int), double> labelAt = [];                 // 札を最後に結べた時刻 (この PC の時計)
    readonly Dictionary<(string, int), (double started, double seen)> startSeen = [];   // 枠が新しい要求を受けたのに気づいた時刻
    readonly Dictionary<(string, int), Track> tracks = [];                  // 読込・生成の始まりの位置と、光らせている増えた分
    readonly System.Windows.Forms.Timer anim = new() { Interval = 33 };     // 光らせている間だけ動かす
    readonly ToolTip tip = new() { InitialDelay = 300, AutoPopDelay = 15000 };
    readonly List<(Rectangle r, string text)> hits = [];
    string? shownTip;
    List<SlotServer> data = [];

    public SlotPanel(TurnTracker turns)
    {
        this.turns = turns;
        DoubleBuffered = true;
        BackColor = Theme.Back;
        anim.Tick += (_, _) => { Invalidate(); if (!Flashing()) anim.Stop(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) anim.Dispose();
        base.Dispose(disposing);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<SlotServer> Data
    {
        get => data;
        set { data = value ?? []; Match(); Follow(); Invalidate(); }
    }

    /// <summary>いま渡す Data を hwmon.py が読んだ時刻 (unix 秒、llmsv の時計)。Flashed で渡す</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Ts { get; set; }

    /// <summary>増えた分を光らせ始めたとき (サーバー, 枠, 読込か, 始まりの量, 終わりの量, 時刻)。グラフの文脈の段に ● を置くのに使う</summary>
    public event Action<string, int, bool, long, long, double>? Flashed;

    /// <summary>OpenCode が圧縮を始める余裕 (k トークン)。明るさの満杯 = 枠の上限 − これ</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CompactReserveK { get; set; } = 20;

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
            labelAt[key] = now;
            string who = t.Who != "" ? " · " + t.Who : "";
            string label = (t.Source == "" ? "?" : t.Source) + (t.Task ? " (背景)" : "") + who;
            labels[key] = (label, t.Source == "OpenCode", $"#{t.N} {t.Source}{who}" + (t.Tokens > 0 ? $"\n翻訳の中継が数えた入力 {t.Tokens:#,0} トークン (ツールの定義を除く)" : ""));
        }
        // 推論中なのに結べなかった枠は札を消す (前の要求の札を残さない)。
        // ただし要求のつなぎ目 (中継がターンを閉じたのに枠がまだ動いている・枠が先に次の要求を受けた) では一瞬結べないので、
        // 最後に結べてから、または枠が新しい要求を受けてから HoldSec 秒は前の札を残す
        foreach (var srv in data)
            foreach (var s in srv.Slots)
            {
                var key = (srv.Server, s.Id);
                if (s.Started is double st && (!startSeen.TryGetValue(key, out var ss) || ss.started != st)) startSeen[key] = (st, now);
                if (!s.Processing || usedSlot.Contains(key)) continue;
                bool fresh = (labelAt.TryGetValue(key, out var at) && now - at <= HoldSec)
                             || (startSeen.TryGetValue(key, out var sn) && now - sn.seen <= HoldSec);
                if (!fresh) labels.Remove(key);
            }
    }

    const double HoldSec = 10;

    // 読込・生成で増えた分を光らせる。
    //   読込: 新しい要求を受ける直前の位置 (前の要求が終わったときの量) → 入力の量 (読込が終わった位置)
    //   生成: 入力の量 → 生成が終わったときの量
    // 枠の様子は更新の間隔 (2 秒〜) ごとにしか見えないので、読込・生成を見ないうちに次へ進んでいたらその時点でまとめて光らせる
    sealed class Track
    {
        public double? Started;     // いまの要求を受けた時刻 (変わったら新しい要求)
        public int Phase;           // 0 = 待機、1 = 読込、2 = 生成
        public long Used;           // 前に見たときの使っている量
        public long ReadFrom, GenFrom;
        public readonly List<Flash> Flashes = [];
    }

    sealed record Flash(bool Read, long From, long To, double At);

    const double GrowSec = 0.6, FlashSec = 4;   // 左から右へ伸ばす時間、光らせておく時間
    static readonly Color ReadColor = Theme.Series[0], GenColor = Theme.Series[1];   // グラフの読込 (青)・生成 (橙)

    static double Clock() => Environment.TickCount64 / 1000.0;

    void Follow()
    {
        double now = Clock();
        foreach (var srv in data)
            foreach (var s in srv.Slots)
            {
                int phase = !s.Processing ? 0 : s.Decoded > 0 ? 2 : 1;
                if (!tracks.TryGetValue((srv.Server, s.Id), out var tr))
                {
                    // アプリを開いたとき (途中から見たとき) は光らせず、いまの様子だけ覚える
                    tracks[(srv.Server, s.Id)] = new Track { Started = s.Started, Phase = phase, Used = s.Used, GenFrom = s.Prompt };
                    continue;
                }
                if (s.Started != null && s.Started != tr.Started)
                {
                    // 新しい要求。前の要求の生成を見届けていなければ、前に見た量で締める
                    if (tr.Phase == 2) Add(srv.Server, s.Id, tr, false, tr.GenFrom, tr.Used, now);
                    tr.Started = s.Started;
                    tr.ReadFrom = tr.Used;
                    tr.Phase = 1;
                }
                if (tr.Phase == 1 && phase != 1)
                {
                    // 読込が終わった (生成に入った、または生成を見ないうちに要求が終わった)
                    Add(srv.Server, s.Id, tr, true, tr.ReadFrom, s.Prompt, now);
                    tr.GenFrom = s.Prompt;
                    tr.Phase = phase;
                    if (phase == 0) Add(srv.Server, s.Id, tr, false, s.Prompt, s.Used, now);
                }
                else if (tr.Phase == 2 && phase == 0)
                {
                    Add(srv.Server, s.Id, tr, false, tr.GenFrom, s.Used, now);
                    tr.Phase = 0;
                }
                else if (tr.Phase == 0 && phase == 2)
                {
                    tr.GenFrom = s.Prompt;   // 要求の始まりを見ていないとき (hwmon を起動し直した直後など)
                    tr.Phase = 2;
                }
                tr.Used = s.Used;
                tr.Flashes.RemoveAll(f => now - f.At > FlashSec);
            }
        if (Flashing() && !anim.Enabled) anim.Start();
    }

    // 増えていないとき (圧縮・ほかの会話で量が減った) は光らせない
    void Add(string server, int id, Track tr, bool read, long from, long to, double now)
    {
        if (to <= from) return;
        tr.Flashes.Add(new Flash(read, from, to, now));
        Flashed?.Invoke(server, id, read, from, to, Ts);
    }

    bool Flashing()
    {
        double now = Clock();
        return tracks.Values.Any(t => t.Flashes.Any(f => now - f.At <= FlashSec));
    }

    static Color Hsv(double h, double s, double v)
    {
        double c = v * s, hp = h / 60, x = c * (1 - Math.Abs(hp % 2 - 1)), m = v - c;
        var (r, g, b) = hp < 1 ? (c, x, 0.0) : hp < 2 ? (x, c, 0.0) : hp < 3 ? (0.0, c, x) : hp < 4 ? (0.0, x, c) : hp < 5 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // 光らせている増えた分を描く。0〜GrowSec 秒: 始まりの位置から終わりの位置へ伸ばす (先頭に白い線)。
    // そのあと 0.8 秒ごとに 2〜3 回点滅しながら FlashSec 秒で薄れて、もとのバーに戻る
    void DrawFlashes(Graphics g, Rectangle br, long nCtx, Track tr, double now)
    {
        if (nCtx <= 0) return;
        foreach (var f in tr.Flashes)
        {
            double age = now - f.At;
            if (age < 0 || age > FlashSec) continue;
            int x0 = br.X + (int)(br.Width * Math.Min(1.0, f.From / (double)nCtx));
            int x1 = br.X + (int)(br.Width * Math.Min(1.0, f.To / (double)nCtx));
            if (x1 - x0 < 2) x1 = Math.Min(br.Right, x0 + 2);
            double grow = Math.Min(1, age / GrowSec);
            int xe = x0 + (int)Math.Round((x1 - x0) * (1 - Math.Pow(1 - grow, 3)));
            double a = age < GrowSec ? 1 : (1 - (age - GrowSec) / (FlashSec - GrowSec)) * (0.6 + 0.4 * Math.Cos(2 * Math.PI * (age - GrowSec) / 0.8));
            var c = f.Read ? ReadColor : GenColor;
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * Math.Clamp(a, 0, 1)), c)))
                g.FillRectangle(b, x0, br.Y, Math.Max(1, xe - x0), br.Height);
            using (var p = new Pen(Color.FromArgb((int)(200 * Math.Clamp(a, 0, 1)), c)))
                g.DrawRectangle(p, x0, br.Y - 2, Math.Max(1, xe - x0), br.Height + 3);
            if (grow < 1) using (var p = new Pen(Color.White, 2)) g.DrawLine(p, xe, br.Y - 2, xe, br.Bottom + 1);
        }
    }

    // 上の行の右に出す「読込 +12.3k」「生成 +1.2k」(薄れていく)
    (string text, Color color)[] FlashTexts(Track? tr, double now) =>
        tr == null ? [] : tr.Flashes.Where(f => now - f.At <= FlashSec)
            .Select(f => ((f.Read ? "読込 +" : "生成 +") + K(f.To - f.From),
                          Mix(Theme.Back, f.Read ? ReadColor : GenColor, Math.Clamp(1 - Math.Max(0, now - f.At - 2) / (FlashSec - 2), 0, 1))))
            .ToArray();

    static string K(double v) => v >= 100_000 ? $"{v / 1000:0}k" : v >= 1000 ? $"{v / 1000:0.0}k" : $"{v:0}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        hits.Clear();
        double now = Clock();
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
                tracks.TryGetValue((srv.Server, s.Id), out var tr);
                double pct = s.NCtx > 0 ? s.Used * 100.0 / s.NCtx : 0;
                // 上の行: 枠の番号と札 (OpenCode は緑)。右に、光らせている増えた分
                int fx = cell.Right;
                foreach (var (ft, fc) in FlashTexts(tr, now).Reverse())
                {
                    int w = TextRenderer.MeasureText(ft, Theme.Num).Width;
                    fx -= w;
                    TextRenderer.DrawText(g, ft, Theme.Num, new Point(fx, cell.Y), fc);
                    fx -= 4;
                }
                string head = $"枠{s.Id}" + (lb.label != null ? "  " + lb.label : s.Processing ? "  推論中" : "");
                TextRenderer.DrawText(g, head, Theme.Small, new Rectangle(cell.X, cell.Y, Math.Max(0, fx - cell.X), 15),
                    lb.openCode ? Theme.Good : s.Processing ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                // 下の行: バーと「使っている量/上限 速さ」
                string val = s.Used > 0 ? $"{K(s.Used)}/{K(s.NCtx)}" + (s.Processing && s.Tps > 0 ? $" {s.Tps:0.#}t/s" : "") : $"空き/{K(s.NCtx)}";
                int vw = TextRenderer.MeasureText(val, Theme.Num).Width;
                var br = new Rectangle(cell.X, cell.Y + 19, Math.Max(10, cell.Width - vw - 4), 8);
                using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
                double compact = Math.Max(1, s.NCtx - CompactReserveK * 1000.0);   // 明るさの満杯 (OpenCode が圧縮を始める量)
                var fill = s.Processing ? Hsv(120, 0.8, 0.3 + 0.7 * Math.Clamp(s.Used / compact, 0, 1)) : Theme.Grid;
                if (pct > 0) using (var b = new SolidBrush(fill)) g.FillRectangle(b, br.X, br.Y, Math.Max(1, (int)(br.Width * Math.Min(100, pct) / 100)), br.Height);
                using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, br);
                if (tr != null) DrawFlashes(g, br, s.NCtx, tr, now);
                TextRenderer.DrawText(g, val, Theme.Num, new Point(cell.Right - vw, cell.Y + 14), s.Processing ? Theme.Ink : Theme.Muted);
                string when = s.LastActive is double la ? DateTimeOffset.FromUnixTimeMilliseconds((long)(la * 1000)).LocalDateTime.ToString("HH:mm:ss") : "-";
                hits.Add((cell, $"{srv.Server} 枠{s.Id}: {(s.Processing ? "推論中" : "待機中 (最後に使っていた量)")}\n" +
                                $"使っている量 {s.Used:#,0} / {s.NCtx:#,0} トークン ({pct:0.0}%)\n" +
                                $"入力 {s.Prompt:#,0}・生成 {s.Decoded:#,0}" + (s.Processing ? $"・{s.Tps:0.#} トークン/秒" : "") +
                                $"\n最後に動いた時刻 {when}" + (lb.tip != null ? "\n" + lb.tip : "") +
                                (s.Processing ? $"\nバーの明るさ: 圧縮ライン {compact / 1000:0}k の {s.Used * 100 / compact:0}%" : "") +
                                (tr != null && tr.Flashes.Any(f => now - f.At <= FlashSec) ? "\n" + string.Join("\n", tr.Flashes.Where(f => now - f.At <= FlashSec).Select(f =>
                                    $"{(f.Read ? "読込" : "生成")} {f.From:#,0} → {f.To:#,0} (+{f.To - f.From:#,0})")) : "")));
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
