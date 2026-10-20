using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HwMonitor;

/// <summary>翻訳ログの閲覧ページ (OpenCode 風にブロック単位で出す)。
/// 翻訳の画面 (hermes_honyaku.py の 8765) の /events (SSE) を HonyakuFeed から受け取り、
/// ターンごとに 思考 (英文＋和訳)・コード (Shell などの区切り)・ツール (実行したコマンド)・応答 のブロックに並べて描く。
///   ・思考は「連続した思考のかたまり」を 1 ブロックにまとめる (コード・ツール・応答・ターンの変わり目で区切る)
///   ・後からでもブロックごとに個別に翻訳できる (ダブルクリック / 右クリック→選択を翻訳。英文のまま流れている分を DeepL で訳す)
///   ・ブロックをクリック/ドラッグで選び、Ctrl+C か右クリックでコピーできる
///   ・ログの閲覧なので文はいつも開いたまま。固まらないよう RichTextBox は使わず自前で描く
///     (届いたイベントは貯めて 300ms ごとにまとめて並べ直し、画面に見えているブロックだけ描く)
/// 制約: Shell の「結果」は翻訳の中継のフィードには流れていない (結果は次の要求に入るため)。
///       いまは実行したコマンド (tools) と、ターンのきっかけ (source、前のツール結果の断片) までを出す。</summary>
[DesignerCategory("Code")]
public sealed class TranslationLogPanel : Control
{
    const int MaxBlocks = 3000;
    const int HeaderH = 22, TopPad = 6, BottomPad = 12, MarginL = 10, MarginR = 10, SbW = 14;
    const int BoxInset = 10, BoxPad = 6;
    const int MaxDeepLQueue = 200;

    readonly Func<bool> hideTasks;
    readonly VScrollBar sb = new() { SmallChange = 40 };

    // 届いたイベントは裏のスレッドから貯めるだけ。画面側の処理 (モデル更新・並べ直し) はタイマーでまとめて行う
    readonly ConcurrentQueue<Ev> inbox = new();
    readonly System.Windows.Forms.Timer pump = new() { Interval = 300 };

    // 画面側 (UI スレッド) だけが触る
    readonly List<Blk> blocks = [];
    readonly Dictionary<(int turn, int seg), Sub> subMap = [];   // seg → 思考ブロックの中の 1 区切り (ja で和訳を埋める)
    readonly Dictionary<int, Blk> answerBlk = [];               // ターン → 応答ブロック (届くそばから足す)
    readonly Dictionary<int, bool> turnHidden = [];            // 背景タスクなどで出さないターン
    Blk? openChunk;      // いま伸びている思考のかたまり (null = 直前がコード/ツール/応答/ターンの変わり目)
    long boot;
    bool engineNone;
    string connNote = "";
    int total, scrollOffset, maxScroll;
    bool pinned = true;      // いちばん下に貼り付いて自動で送るか
    bool needRelayout;
    bool showSource = true;  // 思考の下に英文を併記する
    ToolStripMenuItem mSrc = null!;

    // 選択 (ブロック単位。クリック/ドラッグ/Shift で範囲、Ctrl+C でコピー)
    readonly HashSet<Blk> sel = [];
    Blk? anchor;
    bool dragging;

    // DeepL (中継が翻訳を止めている英文を、見ている間だけ・ブロックごとに訳す)。固まらないよう待ち行列に上限。
    // 大きい塊は 1500 字ずつの単位に分けて頼み、上から順に埋めていく (丸ごと 1 回だと数秒待っても進みが見えない)
    readonly List<Work> dq = [];
    readonly System.Windows.Forms.Timer deeplTimer = new() { Interval = 300 };   // 1 単位ずつ直列 (deeplBusy で守る)。間を詰めて進みを機敏に
    bool deeplBusy;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DeepLService? DeepL { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool> AutoTranslate { get; set; } = () => false;
    /// <summary>自動の翻訳を止めた (上限・失敗)。理由を渡す</summary>
    public event Action<string>? AutoStopped;
    /// <summary>翻訳しようとしたが DeepL のキーが無い</summary>
    public event Action? NeedKey;
    /// <summary>原文の併記を切り替えた (右クリックの menu から)。設定に覚えるため</summary>
    public event Action? ShowSourceChanged;
    /// <summary>思考の下に英文を併記するか</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSource { get => showSource; set => mSrc.Checked = value; }

    // フォント (暗い背景向け)
    static readonly Font FHead = new("Yu Gothic UI", 9.5f, FontStyle.Bold);
    static readonly Font FJa = new("Yu Gothic UI", 10f);
    static readonly Font FJaB = new("Yu Gothic UI", 10f, FontStyle.Bold);   // 見出し・強調の行
    static readonly Font FEn = new("Consolas", 8.5f);
    static readonly Font FMono = new("Consolas", 9f);
    static readonly Font FSmall = new("Yu Gothic UI", 8f);
    const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
    const int BulletIndent = 16, NumIndent = 26;
    static readonly Color TurnInk = Color.FromArgb(0x5a, 0xd0, 0xc0);   // ターンの見出し (明るい teal)
    static readonly Color SelBack = Color.FromArgb(0x1e, 0x3a, 0x42);   // 選んだブロックの下じき

    enum BK { Turn, Think, Code, Answer, Tools }

    sealed class Sub   // 思考のかたまりの中の 1 区切り (英文＝En、中継の和訳＝Ja)
    {
        public int Seg;
        public string En = "", Ja = "";
        public Blk Owner = null!;
    }

    sealed class Blk
    {
        public BK Kind;
        public int Turn;
        public string Ja = "", En = "";   // 思考: Ja=和訳 (無ければ空) / En=英文。コード・ツール・応答は Ja に本文
        public string Who = "", Source = "", Model = "", Time = "";
        public List<Sub>? Subs;           // 思考のかたまりを作る区切りたち
        public int H = -1, Y;             // 高さ (未測定 = -1)・上端
        public bool Pending, Deepl;       // DeepL に頼んでいる最中・DeepL で訳した
        public int PendingUnits;          // まだ返ってきていない DeepL の単位の数 (0 になったら Pending を下ろす)
        public List<Item>? Items;         // 思考・応答を段落/箇条書き/見出しに組んだもの (中身が変わったら作り直す)
    }

    enum IK { Para, Bullet, Numbered, Heading }
    sealed class Item { public IK Kind; public string Text = "", Mark = ""; }

    sealed class Work   // DeepL に頼む 1 単位 (ある思考のかたまりの、1500 字ぶんの区切りたち)
    {
        public Blk Chunk = null!;
        public List<Sub> Subs = [];
    }

    sealed class Ev
    {
        public string Type = "", Text = "", Src = "", How = "", Kind = "", Source = "", Who = "", Model = "", Engine = "";
        public int Turn, Seg;
        public bool Task, Ok, HasBoot;
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
        var mTrans = new ToolStripMenuItem("選択を翻訳");
        mTrans.Click += (_, _) => { foreach (var b in blocks.Where(sel.Contains).ToList()) TranslateBlock(b); };
        var mCopySel = new ToolStripMenuItem("選択をコピー");
        mCopySel.Click += (_, _) => CopySelection();
        mSrc = new ToolStripMenuItem("原文 (英文) を併記") { Checked = showSource, CheckOnClick = true };
        mSrc.CheckedChanged += (_, _) => { showSource = mSrc.Checked; foreach (var b in blocks) b.H = -1; needRelayout = true; ShowSourceChanged?.Invoke(); };
        var mCopyAll = new ToolStripMenuItem("表示を全部コピー");
        mCopyAll.Click += (_, _) => CopyAll();
        var mClear = new ToolStripMenuItem("消す");
        mClear.Click += (_, _) => { blocks.Clear(); subMap.Clear(); answerBlk.Clear(); dq.Clear(); sel.Clear(); anchor = null; openChunk = null; needRelayout = true; };
        menu.Items.AddRange([mTrans, mCopySel, new ToolStripSeparator(), mSrc, mCopyAll, mClear]);
        ContextMenuStrip = menu;

        pump.Tick += (_, _) => Pump();
        pump.Start();
        deeplTimer.Tick += async (_, _) => await PumpDeepL();
        deeplTimer.Start();
        new ToolTip { InitialDelay = 600 }.SetToolTip(this,
            "ブロックをクリック/ドラッグで選択 (Shift＋クリックで範囲)、Ctrl+C でコピー。\nダブルクリック (または右クリック→選択を翻訳) で、英文の思考を DeepL で訳します");
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
            case "ja":
                e.Text = Str(ev, "text").Trim(); e.How = Str(ev, "how");
                e.Ok = ev.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                break;
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
            if (boot != 0) { blocks.Clear(); subMap.Clear(); answerBlk.Clear(); turnHidden.Clear(); sel.Clear(); anchor = null; openChunk = null; }
            boot = e.Boot;
        }
        switch (e.Type)
        {
            case "status":
                bool ch = engineNone != (e.Engine == "none");
                engineNone = e.Engine == "none";
                return ch;   // 状態が変わったときだけ見出しを描き直す
            case "turn_start":
                CloseChunk();
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
                    CloseChunk();
                    if (e.Src != "") Add(new Blk { Kind = BK.Code, Turn = e.Turn, Ja = e.Src });
                    return e.Src != "";
                }
                if (openChunk == null || openChunk.Turn != e.Turn) { CloseChunk(); openChunk = new Blk { Kind = BK.Think, Turn = e.Turn, Subs = [] }; Add(openChunk); }
                var sub = new Sub { Seg = e.Seg, En = e.Src, Owner = openChunk };
                openChunk.Subs!.Add(sub);
                subMap[(e.Turn, e.Seg)] = sub;
                openChunk.Deepl = false;   // 内容が増えたので DeepL 結果は作り直す
                RebuildChunk(openChunk);
                return true;
            case "ja":
                if (Hidden(e.Turn) || e.How == "code") return false;
                if (!subMap.TryGetValue((e.Turn, e.Seg), out var sj)) return false;
                bool ok = e.Ok && e.How != "none";   // 中継が訳した分だけ Ja に入れる (none・失敗は英文のまま → DeepL 対象)
                sj.Ja = ok ? e.Text : "";
                RebuildChunk(sj.Owner);
                return true;
            case "answer":
                if (Hidden(e.Turn) || e.Text.Trim() == "") return false;
                CloseChunk();
                if (answerBlk.TryGetValue(e.Turn, out var ab)) { ab.Ja += e.Text; Dirty(ab); }
                else { var nb = new Blk { Kind = BK.Answer, Turn = e.Turn, Ja = e.Text }; answerBlk[e.Turn] = nb; Add(nb); }
                return true;
            case "tools":
                if (Hidden(e.Turn) || e.Text == "") return false;
                CloseChunk();
                Add(new Blk { Kind = BK.Tools, Turn = e.Turn, Ja = e.Text });
                return true;
        }
        return false;
    }

    bool Hidden(int turn) => turnHidden.TryGetValue(turn, out var h) && h;

    // いま伸びている思考のかたまりを閉じる。自動翻訳が入っていれば、まだ訳していない英文の塊を待ち行列へ
    void CloseChunk()
    {
        if (openChunk == null) return;
        var c = openChunk;
        openChunk = null;
        MaybeAuto(c);
    }

    // かたまりの英文 (En = 併記・DeepL のもと) を作り直す。表示は Items が段落/箇条書きに組む
    void RebuildChunk(Blk c)
    {
        if (c.Subs == null) return;
        c.En = string.Join(" ", c.Subs.Where(s => s.En != "").Select(s => s.En));
        Dirty(c);
    }

    // 中身が変わった: 高さと、段落に組んだ Items を作り直させる (幅だけ変わったときは H だけで Items は使い回す)
    static void Dirty(Blk b) { b.H = -1; b.Items = null; }

    void Add(Blk b)
    {
        blocks.Add(b);
        if (blocks.Count > MaxBlocks)
        {
            int drop = blocks.Count - MaxBlocks;
            for (int i = 0; i < drop; i++)
            {
                var old = blocks[i];
                if (old.Subs != null) foreach (var s in old.Subs) subMap.Remove((old.Turn, s.Seg));
                if (old.Kind == BK.Answer) answerBlk.Remove(old.Turn);
                dq.RemoveAll(w => w.Chunk == old);
                sel.Remove(old);
                if (anchor == old) anchor = null;
                if (openChunk == old) openChunk = null;
            }
            blocks.RemoveRange(0, drop);
        }
    }

    // ---- DeepL (中継が止めている英文を訳す) ----
    // 自動翻訳 (DeepL ボタンを押している間): かたまりを閉じたとき、まだ訳していない英文の塊だけ待ち行列へ
    void MaybeAuto(Blk c) { if (AutoTranslate()) Enqueue(c, true); }

    // 手で頼む (ダブルクリック・右クリック)。いつでも訳せる
    void TranslateBlock(Blk b)
    {
        if (b.Kind == BK.Think && b.Subs != null && (DeepL == null || !DeepL.HasKey)) { NeedKey?.Invoke(); return; }
        Enqueue(b, false);
    }

    // かたまりの未翻訳の区切りを 1500 字ずつの単位に分けて待ち行列へ。上から順に埋まって進みが見える
    void Enqueue(Blk c, bool auto)
    {
        if (auto && !Visible) return;   // 隠れている側は自動翻訳しない (翻訳ログを 2 か所に置くので DeepL の二重消費を防ぐ)
        if (c.Kind != BK.Think || c.Subs == null || c.Pending || DeepL == null || !DeepL.HasKey) return;
        var todo = c.Subs.Where(s => s.Ja == "" && s.En != "").ToList();
        if (todo.Count == 0) return;
        long queued = dq.Sum(w => w.Subs.Sum(s => (long)s.En.Length));
        if (dq.Count >= MaxDeepLQueue) { AutoStopped?.Invoke("翻訳が追いつかないので自動翻訳を止めました"); return; }
        if (!DeepL.CanSpend(todo.Sum(s => (long)s.En.Length) + queued))
        { AutoStopped?.Invoke(auto ? "DeepL の今月の上限に近いので止めました" : "DeepL の今月の上限に近いので訳しません"); return; }
        var units = new List<Work>();
        var cur = new List<Sub>(); int chars = 0;
        foreach (var s in todo)
        {
            if (cur.Count > 0 && chars + s.En.Length > 1500) { units.Add(new Work { Chunk = c, Subs = cur }); cur = []; chars = 0; }
            cur.Add(s); chars += s.En.Length;
        }
        if (cur.Count > 0) units.Add(new Work { Chunk = c, Subs = cur });
        c.PendingUnits += units.Count;
        c.Pending = true;
        c.Deepl = false;
        if (auto) dq.AddRange(units); else dq.InsertRange(0, units);   // 手で頼んだ分は先に
        needRelayout = true;
    }

    // 待ち行列から 1 単位ずつ頼み、返ってきた区切りの和訳を埋める (塊が上から順に訳文に変わっていく)
    async Task PumpDeepL()
    {
        if (deeplBusy || dq.Count == 0 || DeepL == null) return;
        deeplBusy = true;
        try
        {
            var work = dq[0];
            dq.RemoveAt(0);
            var res = await DeepL.Translate([.. work.Subs.Select(s => s.En)]);
            if (res != null)
                for (int i = 0; i < work.Subs.Count && i < res.Count; i++) work.Subs[i].Ja = res[i].Trim();
            var c = work.Chunk;
            c.PendingUnits = Math.Max(0, c.PendingUnits - 1);
            if (res == null)
            {
                dq.RemoveAll(w => w.Chunk == c);   // この塊の残りの単位は取り下げる
                c.PendingUnits = 0;
                AutoStopped?.Invoke("DeepL: " + DeepL.Error);
            }
            if (c.PendingUnits == 0) { c.Pending = false; if (c.Subs != null && c.Subs.Any(s => s.Ja != "")) c.Deepl = true; }
            Dirty(c);
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
                return 2 + Measure("応答", FSmall, w) + MeasureItems(ItemsOf(b), w) + 10;
            default: // Think
                int h = MeasureItems(ItemsOf(b), w);
                if (b.Pending) h += 4 + Measure("(翻訳中…)", FSmall, w);
                if (showSource && ChunkHasJa(b) && b.En != "") h += 6 + Measure(b.En, FEn, w);
                return h + 10;
        }
    }

    // 思考のコピー用: 区切りごとに (訳してあれば和訳・まだなら英文) をつなぐ
    static string ChunkDisp(Blk c)
    {
        if (c.Subs == null || c.Subs.Count == 0) return c.Ja != "" ? c.Ja : c.En;
        return string.Join("\n", c.Subs.Select(s => s.Ja != "" ? s.Ja : s.En).Where(t => t != ""));
    }

    static bool ChunkHasJa(Blk c) => c.Subs != null && c.Subs.Any(s => s.Ja != "");

    // ---- 簡易マークダウン (段落・箇条書き・番号・見出し・太字) ----
    // 思考・応答の中身を、読みやすい単位 (Item) に組む。連続したふつうの文は 1 段落にまとめ、
    // 箇条書き (-・*・•・・) / 番号 (1. 1)) / 見出し (#、または全体が **…**) は行を分ける。太字の ** は外す
    static readonly Regex HeadRe = new(@"^\s*#{1,6}\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex BulletRe = new(@"^\s*[-*•・]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex NumRe = new(@"^\s*(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex AllBoldRe = new(@"^\*\*(.+)\*\*$", RegexOptions.Compiled);

    static string CleanBold(string s) => s.Replace("**", "").Replace("__", "").Trim();

    static List<Item> ParseMarkdown(string content)
    {
        var items = new List<Item>();
        var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { items.Add(new Item { Kind = IK.Para, Text = para.ToString() }); para.Clear(); } }
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line == "") { Flush(); continue; }
            Match m;
            if ((m = HeadRe.Match(line)).Success) { Flush(); items.Add(new Item { Kind = IK.Heading, Text = CleanBold(m.Groups[1].Value) }); }
            else if (AllBoldRe.IsMatch(line)) { Flush(); items.Add(new Item { Kind = IK.Heading, Text = CleanBold(line) }); }
            else if ((m = BulletRe.Match(line)).Success) { Flush(); items.Add(new Item { Kind = IK.Bullet, Mark = "•", Text = CleanBold(m.Groups[1].Value) }); }
            else if ((m = NumRe.Match(line)).Success) { Flush(); items.Add(new Item { Kind = IK.Numbered, Mark = m.Groups[1].Value + ".", Text = CleanBold(m.Groups[2].Value) }); }
            else { if (para.Length > 0) para.Append(' '); para.Append(CleanBold(line)); }
        }
        Flush();
        return items;
    }

    List<Item> ItemsOf(Blk b)
    {
        if (b.Items == null)
        {
            string content = b.Kind == BK.Answer ? b.Ja
                : b.Subs == null ? "" : string.Join("\n", b.Subs.Select(s => s.Ja != "" ? s.Ja : s.En));
            if (content == "") content = b.Pending ? "" : "(思考を待っています…)";
            b.Items = ParseMarkdown(content);
        }
        return b.Items;
    }

    static int ItemFont(Item it, out Font f, out int indent)
    {
        f = it.Kind == IK.Heading ? FJaB : FJa;
        indent = it.Kind == IK.Bullet ? BulletIndent : it.Kind == IK.Numbered ? NumIndent : 0;
        return indent;
    }

    static int MeasureItems(List<Item> items, int w)
    {
        int h = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) h += items[i].Kind == IK.Heading ? 9 : 4;
            ItemFont(items[i], out var f, out int ind);
            h += Measure(items[i].Text, f, w - ind);
        }
        return h;
    }

    static int DrawItems(Graphics g, List<Item> items, int x, int y, int w, Color col)
    {
        int cy = y;
        foreach (var it in items)
        {
            if (cy > y) cy += it.Kind == IK.Heading ? 9 : 4;
            ItemFont(it, out var f, out int ind);
            int ih = Measure(it.Text, f, w - ind);
            if (it.Mark != "") TextRenderer.DrawText(g, it.Mark, FJa, new Point(x + (it.Kind == IK.Bullet ? 2 : 0), cy), col, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, it.Text, f, new Rectangle(x + ind, cy, w - ind, ih), col, TextFlags);
            cy += ih;
        }
        return cy - y;
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

    // ---- 入力 (選択・スクロール・翻訳) ----
    Blk? HitTest(int mx, int my)
    {
        if (my < HeaderH || mx >= ClientSize.Width - SbW) return null;
        int cy = my - HeaderH + scrollOffset;
        return blocks.FirstOrDefault(b => cy >= b.Y && cy < b.Y + b.H);
    }

    void SelectRange(Blk a, Blk b)
    {
        int ia = blocks.IndexOf(a), ib = blocks.IndexOf(b);
        if (ia < 0 || ib < 0) return;
        if (ia > ib) (ia, ib) = (ib, ia);
        sel.Clear();
        for (int i = ia; i <= ib; i++) sel.Add(blocks[i]);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.X >= ClientSize.Width - SbW) return;
        var b = HitTest(e.X, e.Y);
        if (e.Button == MouseButtons.Left)
        {
            if (b == null) { sel.Clear(); anchor = null; dragging = false; Invalidate(); return; }
            if ((ModifierKeys & Keys.Shift) != 0 && anchor != null) SelectRange(anchor, b);
            else { sel.Clear(); sel.Add(b); anchor = b; }
            dragging = true;
            Invalidate();
        }
        else if (e.Button == MouseButtons.Right && b != null && !sel.Contains(b))
        {
            sel.Clear(); sel.Add(b); anchor = b; Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || (e.Button & MouseButtons.Left) == 0 || anchor == null) return;
        var b = HitTest(e.X, Math.Clamp(e.Y, HeaderH, ClientSize.Height - 1));
        if (b != null) { SelectRange(anchor, b); Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }

    // ダブルクリックしたブロックを DeepL で訳す (英文の思考なら)
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var b = HitTest(e.X, e.Y);
        if (b != null) TranslateBlock(b);
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C)) { CopySelection(); return true; }
        if (keyData == (Keys.Control | Keys.A)) { sel.Clear(); foreach (var b in blocks) sel.Add(b); Invalidate(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
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
            int y = b.Y - scrollOffset + HeaderH;
            if (sel.Contains(b)) using (var hb = new SolidBrush(SelBack)) g.FillRectangle(hb, 0, y, ClientSize.Width - SbW, b.H);
            DrawBlock(g, b, MarginL, y, w);
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
        TextRenderer.DrawText(g, "翻訳ログ", FHead, new Point(MarginL, 3), Theme.Head);
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
                DrawItems(g, ItemsOf(b), x, y + Measure("応答", FSmall, w) + 2, w, Theme.Ink);
                break;
            default: // Think
                // 色: 和訳が入った=白、英文のまま=薄い白 (ダブルクリックで訳せる)、まだ何も無く翻訳待ち=灰
                var tcol = ChunkHasJa(b) ? Theme.Ink : b.Pending ? Theme.Muted : Theme.EnInk;
                int used = DrawItems(g, ItemsOf(b), x, y, w, tcol);
                int ty = y + used;
                if (b.Pending) { ty += 4; TextRenderer.DrawText(g, "(翻訳中…)", FSmall, new Point(x, ty), Theme.Muted, TextFormatFlags.NoPadding); ty += Measure("(翻訳中…)", FSmall, w); }
                if (showSource && ChunkHasJa(b) && b.En != "")
                    TextRenderer.DrawText(g, b.En, FEn, new Rectangle(x, ty + 6, w, Measure(b.En, FEn, w)), Theme.Muted, TextFlags);
                break;
        }
    }

    // ---- コピー ----
    static string BlockText(Blk b) => b.Kind switch
    {
        BK.Turn => $"# {b.Turn} {b.Who} {b.Time}" + (b.Source != "" ? $"\n前: {b.Source}" : ""),
        BK.Code => b.Ja,
        BK.Tools => b.Ja,
        BK.Answer => "[応答] " + b.Ja,
        _ => ChunkDisp(b),
    };

    void CopySelection() => Copy(blocks.Where(sel.Contains));
    void CopyAll() => Copy(blocks);

    static void Copy(IEnumerable<Blk> list)
    {
        var sbuf = new StringBuilder();
        foreach (var b in list) sbuf.Append(BlockText(b)).Append("\n\n");
        try { if (sbuf.Length > 0) Clipboard.SetText(sbuf.ToString().TrimEnd()); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { pump.Dispose(); deeplTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
