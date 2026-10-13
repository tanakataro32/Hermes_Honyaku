using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace HwMonitor;

/// <summary>llama-server の枠のメーター。サーバーごとに 1 段、枠を横に並べる (使っている量 / 枠の上限、生成の速さ)。
/// 推論中の枠は、翻訳の画面のターン (発信元・コンテキスト量) と入力の量が近いものを探して「OpenCode」などの札を付ける。
/// 待機中の枠は、最後に使っていた量と札を薄く残す。
/// 推論中のバーは緑で、明るさ = 使っている量 (圧縮ラインで一番明るい)。
/// 読込・生成が終わるたびに、その間に増えた分 (始まったときの位置 → 終わったときの位置) を
/// 読込 = 青・生成 = 橙 (グラフの線と同じ色) で左から右へ伸ばして見せ、数秒点滅させて消す。
/// 上の行の右の「読込 +12.3k」「生成 +1.2k」は大きな字で、増えた瞬間に弾んで激しく点滅する</summary>
[DesignerCategory("Code")]
public sealed class SlotPanel : Control
{
    const int RowH = 52, NameW = 76, Pad = 8, Gap = 8, LampH = 26, BarH = 12;
    static readonly Font LampLabFont = new("Yu Gothic UI", 11f, FontStyle.Bold);   // 「読込 +」「生成 +」
    static readonly Font LampNumFont = new("Consolas", 15f, FontStyle.Bold);       // その右の数字
    static readonly Font ValFont = new("Consolas", 12f, FontStyle.Bold);           // バーの右の「207k/262k 24.9t/s」
    const string ValTemplate = "888k/888k 88.8t/s";   // その欄の幅 (いちばん長くなるとき。Consolas は数字の幅が同じ)
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
        public Flash? LastRead, LastGen;   // 上の行の右の数字 (次に増えるまで出しておく)
    }

    sealed record Flash(bool Read, long From, long To, double At);

    const double GrowSec = 0.6, FlashSec = 5;   // 左から右へ伸ばす時間、光らせておく時間
    const double StrobeSec = 1.2, StrobeHalf = 0.09;   // 増えた直後に激しく点滅する時間と、点いている・消えている時間
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
        var f = new Flash(read, from, to, now);
        tr.Flashes.Add(f);
        if (read) tr.LastRead = f; else tr.LastGen = f;
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

    // 増えた直後の点滅が「点いている」か (StrobeSec 秒の間、StrobeHalf 秒ごとに点いたり消えたり)
    static bool StrobeOn(double age) => age < StrobeSec && (int)(age / StrobeHalf) % 2 == 0;

    // 光らせている増えた分を描く。0〜GrowSec 秒: 始まりの位置から終わりの位置へ伸ばす (先頭に白く光る線)。
    // 伸び切った瞬間に白く光り、StrobeSec 秒まで激しく点滅、そのあと 0.3 秒ごとに点滅しながら FlashSec 秒で薄れて、もとのバーに戻る。
    // バーの上下にはみ出す光の帯も付ける
    void DrawFlashes(Graphics g, Rectangle br, long nCtx, Track tr, double now)
    {
        if (nCtx <= 0) return;
        foreach (var f in tr.Flashes)
        {
            double age = now - f.At;
            if (age < 0 || age > FlashSec) continue;
            int x0 = br.X + (int)(br.Width * Math.Min(1.0, f.From / (double)nCtx));
            int x1 = br.X + (int)(br.Width * Math.Min(1.0, f.To / (double)nCtx));
            if (x1 - x0 < 3) x1 = Math.Min(br.Right, x0 + 3);
            double grow = Math.Min(1, age / GrowSec);
            int xe = x0 + (int)Math.Round((x1 - x0) * (1 - Math.Pow(1 - grow, 3)));
            int w = Math.Max(1, xe - x0);
            double env = age < GrowSec ? 1 : 1 - (age - GrowSec) / (FlashSec - GrowSec);
            double a = age < GrowSec ? 1 : env * (age < StrobeSec ? (StrobeOn(age) ? 1 : 0.15) : (int)((age - StrobeSec) / 0.3) % 2 == 0 ? 1 : 0.35);
            a = Math.Clamp(a, 0, 1);
            var c = f.Read ? ReadColor : GenColor;
            // 伸び切った直後 0.15 秒は白く光らせる
            var fc = age >= GrowSec && age < GrowSec + 0.15 ? Mix(c, Color.White, 0.8) : c;
            using (var b = new SolidBrush(Color.FromArgb((int)(110 * a), c)))   // 上下にはみ出す光の帯
                g.FillRectangle(b, x0, br.Y - 5, w, br.Height + 10);
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), fc)))
                g.FillRectangle(b, x0, br.Y, w, br.Height);
            using (var p = new Pen(Color.FromArgb((int)(230 * a), c), 2))
                g.DrawRectangle(p, x0, br.Y - 3, w, br.Height + 5);
            if (grow < 1)
            {
                using (var p = new Pen(Color.FromArgb(120, Color.White), 7)) g.DrawLine(p, xe, br.Y - 6, xe, br.Bottom + 5);
                using (var p = new Pen(Color.White, 3)) g.DrawLine(p, xe, br.Y - 6, xe, br.Bottom + 5);
            }
        }
    }

    // 上の行の右に出す「読込 +12.3k」「生成 +1.2k」。数字は次に増えるまで出しておく。
    // 増えた瞬間: 数字が 1.6 倍に跳ねて弾みながら戻り、StrobeSec 秒の間 白と色で激しく点滅、まわりに光の輪。
    // そのあと FlashSec 秒かけて落ち着いた色 (LampDim。暗くしすぎず読める明るさ) に戻る
    const double LampDim = 0.75;

    void DrawLamp(Graphics g, Rectangle r, int labW, string lab, Flash f, double now)
    {
        double age = Math.Max(0, now - f.At);
        var c = f.Read ? ReadColor : GenColor;
        bool on = StrobeOn(age);
        double heat = Math.Pow(Math.Max(0, 1 - age / FlashSec), 2);   // 1 → 0 (FlashSec 秒で落ち着く)
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (heat > 0)
        {
            // 下地: 点いている間は色でべた塗り、そのあとは薄く残して消す。まわりに 3 重の光の輪
            int a = on ? 210 : (int)(80 * heat);
            using (var path = Round(r, 6))
            using (var b = new SolidBrush(Color.FromArgb(a, c)))
                g.FillPath(b, path);
            for (int k = 1; k <= 3; k++)
                using (var path = Round(Rectangle.Inflate(r, k * 2, k * 2), 6 + k * 2))
                using (var p = new Pen(Color.FromArgb((int)(Math.Max(a, 140 * heat) * 0.6 / k), c), 2))
                    g.DrawPath(p, path);
        }
        var ink = on ? Color.White : Mix(Mix(Theme.Back, c, LampDim + (1 - LampDim) * heat), Color.White, 0.5 * heat);
        TextRenderer.DrawText(g, lab, LampLabFont, new Rectangle(r.X + 5, r.Y, labW, r.Height), ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        // 数字は図形にして描く (拡大して弾ませる・縁を光らせるため)。右に寄せ、数字のまん中を中心に拡大する
        var nr = new Rectangle(r.X + 5 + labW, r.Y, r.Width - labW - 10, r.Height);
        using (var tp = new GraphicsPath())
        using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
        {
            tp.AddString(LampNum(f.To - f.From), LampNumFont.FontFamily, (int)LampNumFont.Style, LampNumFont.SizeInPoints * g.DpiY / 72f, nr, sf);
            double s = Math.Max(0.85, 1 + 0.6 * Math.Exp(-age / 0.2) * Math.Cos(2 * Math.PI * age / 0.35));
            if (Math.Abs(s - 1) > 0.005)
            {
                var bb = tp.GetBounds();
                using var m = new Matrix();
                m.Translate(bb.X + bb.Width / 2, bb.Y + bb.Height / 2);
                m.Scale((float)s, (float)s);
                m.Translate(-(bb.X + bb.Width / 2), -(bb.Y + bb.Height / 2));
                tp.Transform(m);
            }
            if (heat > 0.05)
                using (var p = new Pen(Color.FromArgb((int)(200 * heat), on ? c : Color.White), 4) { LineJoin = LineJoin.Round })
                    g.DrawPath(p, tp);
            using (var b = new SolidBrush(ink)) g.FillPath(b, tp);
        }
        g.Restore(state);
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 右の数字は k トークン・小数 1 桁にそろえる (0.8k・12.3k・123.4k)。欄の幅は 999.9k が入る分
    static string LampNum(long v) => $"{Math.Min(v, 999_949) / 1000.0:0.0}k";

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
                // 位置と桁は固定: 右端から「読込 +___._k」「生成 +___._k」の 2 つの欄を必ず取り、数字は欄の中で右に寄せる
                // (まだ出ていない欄も場所だけ取る。札の長さや数字の大きさで動かない)
                int labW = TextRenderer.MeasureText("読込 +", LampLabFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                int numW = TextRenderer.MeasureText(LampNum(999_900), LampNumFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                int fieldW = labW + numW + 10, fx = cell.Right - fieldW * 2 - 8;
                for (int li = 0; li < 2; li++)
                    if ((li == 0 ? tr?.LastRead : tr?.LastGen) is Flash lf)
                        DrawLamp(g, new Rectangle(fx + 2 + li * (fieldW + 6), cell.Y + 1, fieldW, LampH - 2), labW, li == 0 ? "読込 +" : "生成 +", lf, now);
                string head = $"枠{s.Id}" + (lb.label != null ? "  " + lb.label : s.Processing ? "  推論中" : "");
                TextRenderer.DrawText(g, head, Theme.Small, new Rectangle(cell.X, cell.Y, Math.Max(0, fx - cell.X), LampH), lb.openCode ? Theme.Good : s.Processing ? Theme.Ink : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                // 下の行: バーと「使っている量/上限 速さ」
                string val = s.Used > 0 ? $"{K(s.Used)}/{K(s.NCtx)}" + (s.Processing && s.Tps > 0 ? $" {s.Tps:0.#}t/s" : "") : $"空き/{K(s.NCtx)}";
                // 欄の幅は固定 (速さの桁や待機中で数字の長さが変わっても、バーの幅を変えない)。数字は欄の右に寄せる
                var vs = TextRenderer.MeasureText(val, ValFont);
                int vw = vs.Width;
                int vField = Math.Max(vw, TextRenderer.MeasureText(ValTemplate, ValFont).Width);
                var br = new Rectangle(cell.X, cell.Y + LampH + 8, Math.Max(10, cell.Width - vField - 6), BarH);
                using (var b = new SolidBrush(Theme.BarBack)) g.FillRectangle(b, br);
                double compact = Math.Max(1, s.NCtx - CompactReserveK * 1000.0);   // 明るさの満杯 (OpenCode が圧縮を始める量)
                var fill = s.Processing ? Hsv(120, 0.8, 0.3 + 0.7 * Math.Clamp(s.Used / compact, 0, 1)) : Theme.Grid;
                if (pct > 0) using (var b = new SolidBrush(fill)) g.FillRectangle(b, br.X, br.Y, Math.Max(1, (int)(br.Width * Math.Min(100, pct) / 100)), br.Height);
                using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, br);
                if (tr != null) DrawFlashes(g, br, s.NCtx, tr, now);
                TextRenderer.DrawText(g, val, ValFont, new Point(cell.Right - vw, br.Y + br.Height / 2 - vs.Height / 2), s.Processing ? Theme.Ink : Theme.Muted);
                // 増えた直後 0.6 秒は、枠のまわりも色の枠で光らせる
                if (tr?.Flashes.Where(f => now - f.At is >= 0.0 and < 0.6).OrderBy(f => now - f.At).FirstOrDefault() is Flash nf)
                    using (var p = new Pen(Color.FromArgb((int)(255 * (1 - (now - nf.At) / 0.6)), nf.Read ? ReadColor : GenColor), 2))
                        g.DrawRectangle(p, Rectangle.Inflate(cell, 3, 2));
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
