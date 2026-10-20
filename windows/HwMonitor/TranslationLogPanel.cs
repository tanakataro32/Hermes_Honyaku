using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace HwMonitor;

/// <summary>翻訳ログの閲覧ページ (OpenCode 風にブロック単位で出す・仮実装)。
/// 翻訳の画面 (hermes_honyaku.py の 8765) の /events (SSE) を HonyakuFeed から受け取り、
/// ターンごとに 思考 (英文＋和訳)・コード (Shell などの区切り)・ツール (実行したコマンド)・応答 のブロックに並べて描く。
///   ・ログの閲覧なので文はいつも開いたまま (OpenCode のようにクリックで畳まない)
///   ・固まらないように RichTextBox は使わず自前で描く。届いたイベントはいったん貯め、300ms ごとにまとめて並べ直して描く
///   ・画面に見えているブロックだけ描く (仮想化)
/// 制約: Shell の「結果」は翻訳の中継のフィードには流れていない (結果は次の要求に入るため)。
///       いまは実行したコマンド (tools) と、ターンのきっかけ (source、前のツール結果の断片) までを出す。</summary>
[DesignerCategory("Code")]
public sealed class TranslationLogPanel : Control
{
    const int MaxBlocks = 3000;
    const int HeaderH = 22, TopPad = 6, BottomPad = 12, MarginL = 10, MarginR = 10, SbW = 14;
    const int BoxInset = 10, BoxPad = 6;

    readonly Func<bool> hideTasks;
    readonly VScrollBar sb = new() { SmallChange = 40 };

    // 届いたイベントは裏のスレッドから貯めるだけ。画面側の処理 (モデル更新・並べ直し) はタイマーでまとめて行う
    readonly ConcurrentQueue<Ev> inbox = new();
    readonly System.Windows.Forms.Timer pump = new() { Interval = 300 };

    // 画面側 (UI スレッド) だけが触る
    readonly List<Blk> blocks = [];
    readonly Dictionary<(int turn, int seg), Blk> segBlk = [];   // seg → 対応する思考ブロック (ja で和訳を埋める)
    readonly Dictionary<int, Blk> answerBlk = [];               // ターン → 応答ブロック (届くそばから足す)
    readonly Dictionary<int, bool> turnHidden = [];            // 背景タスクなどで出さないターン
    long boot;
    bool engineNone;
    string connNote = "";
    int total, scrollOffset, maxScroll;
    bool pinned = true;      // いちばん下に貼り付いて自動で送るか
    bool needRelayout;
    bool showSource = true;  // 思考の下に英文を併記する
    ToolStripMenuItem mSrc = null!;

    // DeepL (中継が翻訳を止めている英文を、見ている間だけ・クリックで訳す)。固まらないよう待ち行列に上限
    readonly List<Blk> dq = [];
    readonly System.Windows.Forms.Timer deeplTimer = new() { Interval = 800 };
    bool deeplBusy;
    const int MaxDeepLQueue = 200;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DeepLService? DeepL { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool> AutoTranslate { get; set; } = () => false;
    /// <summary>自動の翻訳を止めた (上限・失敗)。理由を渡す</summary>
    public event Action<string>? AutoStopped;
    /// <summary>英文のブロックをクリックしたが DeepL のキーが無い</summary>
    public event Action? NeedKey;
    /// <summary>原文の併記を切り替えた (右クリックの menu から)。設定に覚えるため</summary>
    public event Action? ShowSourceChanged;
    /// <summary>思考の下に英文を併記するか</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSource { get => showSource; set => mSrc.Checked = value; }

    // フォント (暗い背景向け)
    static readonly Font FHead = new("Yu Gothic UI", 9.5f, FontStyle.Bold);
    static readonly Font FJa = new("Yu Gothic UI", 10f);
    static readonly Font FEn = new("Consolas", 8.5f);
    static readonly Font FMono = new("Consolas", 9f);
    static readonly Font FSmall = new("Yu Gothic UI", 8f);
    static readonly Color TurnInk = Color.FromArgb(0x5a, 0xd0, 0xc0);   // ターンの見出し (明るい teal)

    enum BK { Turn, Think, Code, Answer, Tools }

    sealed class Blk
    {
        public BK Kind;
        public int Turn, Seg;
        public string Ja = "", En = "";
        public string State = "ok";   // 思考: wait = 和訳待ち / ok / none = 中継が止めている (英文) / bad = 訳せなかった
        public string Who = "", Source = "", Model = "", Time = "";
        public int H = -1, Y;         // 高さ (未測定 = -1)・上端
        public (int t, int s) Key;
        public bool Pending, Deepl;   // DeepL に頼んでいる最中・DeepL で訳した
    }

    sealed class Ev
    {
        public string Type = "", Text = "", Src = "", How = "", Kind = "", Source = "", Who = "", Model = "", Engine = "";
        public int Turn, Seg;
        public bool Task, HasBoot;
        public long Boot;
    }

    public TranslationLogPanel(HonyakuFeed feed, Func<bool> hideTasks)
    {
        this.hideTasks = hideTasks;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Theme.Back;
        feed.Event += OnEvent;
        feed.Status += (text, ok) => { connNote = ok ? "" : text; needRelayout = true; };

        sb.Scroll += (_, ev) => { scrollOffset = ev.NewValue; pinned = scrollOffset >= maxScroll; Invalidate(); };
        Controls.Add(sb);

        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Theme.Panel, ForeColor = Theme.Ink };
        mSrc = new ToolStripMenuItem("原文 (英文) を併記") { Checked = showSource, CheckOnClick = true };
        mSrc.CheckedChanged += (_, _) => { showSource = mSrc.Checked; foreach (var b in blocks) b.H = -1; needRelayout = true; ShowSourceChanged?.Invoke(); };
        var mCopy = new ToolStripMenuItem("表示をコピー");
        mCopy.Click += (_, _) => CopyAll();
        var mClear = new ToolStripMenuItem("消す");
        mClear.Click += (_, _) => { blocks.Clear(); segBlk.Clear(); answerBlk.Clear(); dq.Clear(); needRelayout = true; };
        menu.Items.AddRange([mSrc, mCopy, mClear]);
        ContextMenuStrip = menu;

        pump.Tick += (_, _) => Pump();
        pump.Start();
        deeplTimer.Tick += async (_, _) => await PumpDeepL();
        deeplTimer.Start();
        new ToolTip { InitialDelay = 600 }.SetToolTip(this, "英文のブロックをクリックすると、その区切りだけ DeepL で訳します (上の帯の「DeepL」や「設定…」でキーを入れたとき)");
    }

    static string Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // 裏のスレッド: 必要な値だけ取り出して貯める (JsonElement の寿命に縛られないように)
    void OnEvent(JsonElement ev)
    {
        var e = new Ev { Type = Str(ev, "type") };
        if (ev.TryGetProperty("boot", out var b) && b.TryGetInt64(out var bt)) { e.HasBoot = true; e.Boot = bt; }
        if (ev.TryGetProperty("turn", out var tn) && tn.TryGetInt32(out var n)) e.Turn = n;
        if (ev.TryGetProperty("seg", out var sg) && sg.TryGetInt32(out var s)) e.Seg = s;
        switch (e.Type)
        {
            case "status": e.Engine = Str(ev, "engine"); break;
            case "turn_start":
                e.Source = Str(ev, "source"); e.Who = Str(ev, "who"); e.Model = Str(ev, "model");
                e.Task = ev.TryGetProperty("task", out var tk) && tk.ValueKind == JsonValueKind.True;
                break;
            case "seg": e.Src = Str(ev, "src").Trim(); e.Kind = Str(ev, "kind"); break;
            case "ja": e.Text = Str(ev, "text").Trim(); e.How = Str(ev, "how"); break;
            case "answer": e.Text = Str(ev, "text"); break;
            case "tools":
                var sbt = new StringBuilder();
                if (ev.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var t in arr.EnumerateArray())
                    {
                        string nm = Str(t, "name"), ag = Str(t, "args");
                        sbt.Append("$ ");
                        if (nm != "") { sbt.Append(nm); if (ag != "") sbt.Append("  "); }
                        sbt.Append(ag).Append('\n');
                    }
                e.Text = sbt.ToString().TrimEnd();
                break;
            default: return;   // think・turn_ctx・turn_end・mood などはこのページでは使わない
        }
        inbox.Enqueue(e);
    }

    // 貯めたイベントをまとめて反映し、必要なら並べ直して描く
    void Pump()
    {
        bool changed = false;
        while (inbox.TryDequeue(out var e)) changed |= Apply(e);
        if (!changed && !needRelayout) return;
        needRelayout = false;
        if (Visible && IsHandleCreated && ClientSize.Width > 0) { Relayout(); Invalidate(); }
        else needRelayout = true;   // 隠れている間は並べ直さない (次に出したときに行う)
    }

    bool Apply(Ev e)
    {
        if (e.HasBoot && e.Boot != boot)
        {
            if (boot != 0) { blocks.Clear(); segBlk.Clear(); answerBlk.Clear(); turnHidden.Clear(); }
            boot = e.Boot;
        }
        switch (e.Type)
        {
            case "status":
                bool ch = engineNone != (e.Engine == "none");
                engineNone = e.Engine == "none";
                return ch;   // 状態が変わったときだけ見出しを描き直す
            case "turn_start":
                bool hide = e.Task && hideTasks();
                turnHidden[e.Turn] = hide;
                if (hide) return false;
                Add(new Blk { Kind = BK.Turn, Turn = e.Turn, Who = e.Who, Source = e.Source, Model = e.Model,
                              Time = DateTime.Now.ToString("HH:mm") });
                return true;
            case "seg":
                if (Hidden(e.Turn)) return false;
                if (e.Kind == "code")
                {
                    if (e.Src != "") Add(new Blk { Kind = BK.Code, Turn = e.Turn, Seg = e.Seg, Ja = e.Src });
                    return e.Src != "";
                }
                var tb = new Blk { Kind = BK.Think, Turn = e.Turn, Seg = e.Seg, En = e.Src, State = "wait", Key = (e.Turn, e.Seg) };
                segBlk[(e.Turn, e.Seg)] = tb;
                Add(tb);
                return true;
            case "ja":
                if (Hidden(e.Turn) || e.How == "code") return false;
                string state = e.How == "none" ? "none" : e.How is "en" or "interrupted" or "suspect" ? "bad"
                             : e.Text.StartsWith("[翻訳失敗") ? "bad" : "ok";
                if (segBlk.TryGetValue((e.Turn, e.Seg), out var blk)) { blk.Ja = e.Text; blk.State = state; blk.H = -1; MaybeAuto(blk); }
                else
                {
                    var nb = new Blk { Kind = BK.Think, Turn = e.Turn, Seg = e.Seg, Ja = e.Text, En = e.Text, State = state, Key = (e.Turn, e.Seg) };
                    segBlk[(e.Turn, e.Seg)] = nb;
                    Add(nb);
                    MaybeAuto(nb);
                }
                return true;
            case "answer":
                if (Hidden(e.Turn) || e.Text.Trim() == "") return false;
                if (answerBlk.TryGetValue(e.Turn, out var ab)) { ab.Ja += e.Text; ab.H = -1; }
                else { var nb = new Blk { Kind = BK.Answer, Turn = e.Turn, Ja = e.Text }; answerBlk[e.Turn] = nb; Add(nb); }
                return true;
            case "tools":
                if (Hidden(e.Turn) || e.Text == "") return false;
                Add(new Blk { Kind = BK.Tools, Turn = e.Turn, Ja = e.Text });
                return true;
        }
        return false;
    }

    bool Hidden(int turn) => turnHidden.TryGetValue(turn, out var h) && h;

    void Add(Blk b)
    {
        blocks.Add(b);
        if (blocks.Count > MaxBlocks)
        {
            int drop = blocks.Count - MaxBlocks;
            for (int i = 0; i < drop; i++)
            {
                var old = blocks[i];
                if (old.Kind == BK.Think) segBlk.Remove(old.Key);
                if (old.Kind == BK.Answer) answerBlk.Remove(old.Turn);
            }
            blocks.RemoveRange(0, drop);
        }
    }

    // ---- DeepL (中継が止めている英文を訳す) ----
    // 訳せる相手 = 中継が訳していない英文の思考 (none) か、訳せなかった思考 (bad)。頼み中・訳し済みは除く
    static bool Eligible(Blk b) => b.Kind == BK.Think && (b.State == "none" || b.State == "bad") && b.En != "" && !b.Pending && !b.Deepl;

    // 自動翻訳 (DeepL ボタンを押している間): 新しく届いた英文の思考だけ待ち行列に入れる
    void MaybeAuto(Blk b)
    {
        if (!Eligible(b) || !AutoTranslate() || DeepL == null) return;
        if (dq.Count >= MaxDeepLQueue) { AutoStopped?.Invoke("翻訳が追いつかないので自動翻訳を止めました"); return; }
        if (DeepL.CanSpend(b.En.Length + dq.Sum(q => q.En.Length))) { b.Pending = true; dq.Add(b); }
        else AutoStopped?.Invoke(DeepL.HasKey ? "DeepL の今月の上限に近いので止めました" : "DeepL のキーがありません");
    }

    // 待ち行列から 25 区切り・8000 字までまとめて頼む
    async Task PumpDeepL()
    {
        if (deeplBusy || dq.Count == 0 || DeepL == null) return;
        deeplBusy = true;
        try
        {
            var batch = new List<Blk>();
            int chars = 0;
            foreach (var b in dq)
            {
                if (batch.Count >= 25 || (batch.Count > 0 && chars + b.En.Length > 8000)) break;
                batch.Add(b);
                chars += b.En.Length;
            }
            dq.RemoveRange(0, batch.Count);
            var res = await DeepL.Translate([.. batch.Select(b => b.En)]);
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Pending = false;
                if (res != null && i < res.Count) { batch[i].Ja = res[i].Trim(); batch[i].State = "ok"; batch[i].Deepl = true; }
                batch[i].H = -1;
            }
            if (res == null) { foreach (var b in dq) b.Pending = false; dq.Clear(); AutoStopped?.Invoke("DeepL: " + DeepL.Error); }
            needRelayout = true;
        }
        catch { /* 翻訳の一時的な失敗で固まったり落ちたりしないよう握りつぶす */ }
        finally { deeplBusy = false; }
    }

    // ---- 並べ直し (高さの計算) ----
    int ContentWidth() => Math.Max(50, ClientSize.Width - SbW - MarginL - MarginR);

    static int Measure(string text, Font f, int w) =>
        string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, f, new Size(Math.Max(1, w), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl).Height;

    int BlockHeight(Blk b, int w)
    {
        switch (b.Kind)
        {
            case BK.Turn:
                int th = 12 + Measure("#", FHead, w);
                if (b.Source != "") th += 2 + Measure("前: " + b.Source, FSmall, w);
                return th + 6;
            case BK.Code:
            case BK.Tools:
                int inner = w - BoxInset - BoxPad * 2;
                int ch = BoxPad * 2 + Math.Max(Measure("A", FMono, inner), Measure(b.Ja, FMono, inner));
                return 2 + Measure(b.Kind == BK.Tools ? "実行したコマンド" : "コード", FSmall, w) + ch + 6;
            case BK.Answer:
                return 2 + Measure("応答", FSmall, w) + Measure(b.Ja, FJa, w) + 8;
            default: // Think
                int h = Measure(PrimText(b), FJa, w);
                if (showSource && b.Ja != "" && b.En != "" && b.En != b.Ja) h += 2 + Measure(b.En, FEn, w);
                return h + 6;
        }
    }

    // 思考に出す文: 和訳があれば和訳、無ければ英文。DeepL に頼み中は「(DeepL…)」、DeepL で訳したら「ᴰ」を付ける
    static string PrimText(Blk b)
    {
        string s = b.Ja != "" ? b.Ja : b.En;
        if (s == "") s = "(訳しています…)";
        if (b.Pending) s += "  (DeepL…)";
        else if (b.Deepl) s += "  ᴰ";
        return s;
    }

    void Relayout()
    {
        int w = ContentWidth(), y = TopPad;
        foreach (var b in blocks)
        {
            if (b.H < 0) b.H = BlockHeight(b, w);
            b.Y = y;
            y += b.H;
        }
        total = y + BottomPad;
        LayoutScrollbar();
    }

    void LayoutScrollbar()
    {
        int view = Math.Max(1, ClientSize.Height - HeaderH);
        maxScroll = Math.Max(0, total - view);
        sb.SetBounds(ClientSize.Width - SbW, HeaderH, SbW, view);
        if (maxScroll <= 0) { sb.Enabled = false; sb.Value = 0; scrollOffset = 0; return; }
        sb.Enabled = true;
        sb.Minimum = 0;
        sb.LargeChange = view;
        sb.Maximum = Math.Max(view, total - 1);   // 使える最大の Value = Maximum - LargeChange + 1 = maxScroll
        if (pinned) scrollOffset = maxScroll;
        scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
        sb.Value = Math.Clamp(scrollOffset, sb.Minimum, sb.Maximum - sb.LargeChange + 1);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        foreach (var b in blocks) b.H = -1;   // 幅が変わると折り返しが変わる
        if (Visible && IsHandleCreated && ClientSize.Width > 0) { Relayout(); Invalidate(); } else needRelayout = true;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { foreach (var b in blocks) b.H = -1; if (IsHandleCreated && ClientSize.Width > 0) { Relayout(); Invalidate(); } }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }

    // 英文のブロックをクリックしたら、それだけ DeepL に頼む
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || e.Y < HeaderH || e.X >= ClientSize.Width - SbW) return;
        int cy = e.Y - HeaderH + scrollOffset;
        var b = blocks.FirstOrDefault(x => cy >= x.Y && cy < x.Y + x.H);
        if (b == null || !Eligible(b)) return;
        if (DeepL == null || !DeepL.HasKey) { NeedKey?.Invoke(); return; }
        if (!DeepL.CanSpend(b.En.Length)) { AutoStopped?.Invoke("DeepL の今月の上限に近いので訳しません"); return; }
        b.Pending = true;
        dq.Insert(0, b);   // クリックした分を先に
        needRelayout = true;
    }

    // マウスを乗せたらホイールでスクロールできるように (この窓にはテキスト入力がないので、焦点を取っても害はない)
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (CanFocus) Focus(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (sb.Enabled)
        {
            scrollOffset = Math.Clamp(scrollOffset - e.Delta / 120 * 60, 0, maxScroll);
            pinned = scrollOffset >= maxScroll;
            sb.Value = Math.Clamp(scrollOffset, sb.Minimum, Math.Max(sb.Minimum, sb.Maximum - sb.LargeChange + 1));
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    // ---- 描画 ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Back);
        DrawHeader(g);
        var clip = new Rectangle(0, HeaderH, ClientSize.Width - SbW, ClientSize.Height - HeaderH);
        g.SetClip(clip);
        if (blocks.Count == 0)
        {
            TextRenderer.DrawText(g, "翻訳ログを待っています… (Hermes / OpenCode の思考がここに流れます)",
                FJa, clip, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        int w = ContentWidth(), top = scrollOffset, bottom = scrollOffset + clip.Height;
        foreach (var b in blocks)
        {
            if (b.Y + b.H < top) continue;
            if (b.Y > bottom) break;
            DrawBlock(g, b, MarginL, b.Y - scrollOffset + HeaderH, w);
        }
        g.ResetClip();
    }

    void DrawHeader(Graphics g)
    {
        using var bg = new SolidBrush(Theme.Panel);
        g.FillRectangle(bg, 0, 0, ClientSize.Width, HeaderH);
        using var line = new Pen(Theme.Grid);
        g.DrawLine(line, 0, HeaderH - 1, ClientSize.Width, HeaderH - 1);
        string state = connNote != "" ? "● " + connNote : engineNone ? "● 翻訳は止めています (英文のまま)" : "● つながっています";
        var col = connNote != "" ? Theme.Warn : engineNone ? Theme.Muted : Theme.Good;
        TextRenderer.DrawText(g, "翻訳ログ (OpenCode 風・仮)", FHead, new Point(MarginL, 3), Theme.Head);
        TextRenderer.DrawText(g, state, FSmall, new Rectangle(0, 0, ClientSize.Width - MarginR, HeaderH), col,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    void DrawBlock(Graphics g, Blk b, int x, int y, int w)
    {
        switch (b.Kind)
        {
            case BK.Turn:
                using (var sep = new Pen(Theme.Edge)) g.DrawLine(sep, x, y + 6, x + w, y + 6);
                int hy = y + 12;
                string head = $"#{b.Turn}  {(b.Who != "" ? b.Who : "思考")}   {b.Time}" + (b.Model != "" ? $"   {b.Model}" : "");
                TextRenderer.DrawText(g, head, FHead, new Rectangle(x, hy, w, Measure("#", FHead, w)), TurnInk,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                if (b.Source != "")
                {
                    hy += Measure("#", FHead, w) + 2;
                    TextRenderer.DrawText(g, "前: " + b.Source, FSmall, new Rectangle(x, hy, w, Measure("前: " + b.Source, FSmall, w)),
                        Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                break;
            case BK.Code:
            case BK.Tools:
                string label = b.Kind == BK.Tools ? "実行したコマンド" : "コード";
                TextRenderer.DrawText(g, label, FSmall, new Point(x, y), Theme.Muted, TextFormatFlags.NoPadding);
                int ly = y + Measure(label, FSmall, w) + 2;
                int inner = w - BoxInset - BoxPad * 2;
                int boxH = BoxPad * 2 + Math.Max(Measure("A", FMono, inner), Measure(b.Ja, FMono, inner));
                var box = new Rectangle(x + BoxInset, ly, w - BoxInset, boxH);
                using (var bb = new SolidBrush(Theme.BarBack)) g.FillRectangle(bb, box);
                using (var bar = new SolidBrush(b.Kind == BK.Tools ? Theme.Good : Theme.Cache))
                    g.FillRectangle(bar, x + BoxInset, ly, 3, boxH);
                TextRenderer.DrawText(g, b.Ja, FMono, new Rectangle(box.X + BoxPad, box.Y + BoxPad, inner, box.Height - BoxPad * 2),
                    Theme.EnInk, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                break;
            case BK.Answer:
                TextRenderer.DrawText(g, "応答", FSmall, new Point(x, y), Theme.Muted, TextFormatFlags.NoPadding);
                int ay = y + Measure("応答", FSmall, w) + 2;
                TextRenderer.DrawText(g, b.Ja, FJa, new Rectangle(x, ay, w, Measure(b.Ja, FJa, w)), Theme.Ink,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                break;
            default: // Think
                string prim = PrimText(b);
                // 色: 訳文=白、頼み中/訳待ち=灰、英文のまま=薄い白 (クリックで訳せる)、訳せなかった=黄
                var pcol = b.Pending || b.Ja == "" ? Theme.Muted : b.State == "none" ? Theme.EnInk : b.State == "bad" ? Theme.Warn : Theme.Ink;
                int ph = Measure(prim, FJa, w);
                TextRenderer.DrawText(g, prim, FJa, new Rectangle(x, y, w, Math.Max(ph, Measure("A", FJa, w))),
                    pcol, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                if (showSource && b.Ja != "" && b.En != "" && b.En != b.Ja)
                    TextRenderer.DrawText(g, b.En, FEn, new Rectangle(x, y + ph + 2, w, Measure(b.En, FEn, w)), Theme.Muted,
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                break;
        }
    }

    void CopyAll()
    {
        var sbuf = new StringBuilder();
        foreach (var b in blocks)
            sbuf.AppendLine(b.Kind switch
            {
                BK.Turn => $"\n#{b.Turn} {b.Who} {b.Time}" + (b.Source != "" ? $"\n前: {b.Source}" : ""),
                BK.Code => b.Ja,
                BK.Tools => b.Ja,
                BK.Answer => "[応答] " + b.Ja,
                _ => b.Ja != "" ? b.Ja : b.En,
            });
        try { if (sbuf.Length > 0) Clipboard.SetText(sbuf.ToString()); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { pump.Dispose(); deeplTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
