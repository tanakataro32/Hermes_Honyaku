using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HwMonitor;

/// <summary>Hermes の思考の和訳を小さく流す欄。翻訳の画面 (hermes_honyaku.py の 8765) の /events を HonyakuFeed から受け取る。
/// つなぎ直したときに送られてくる直近のターンは、出したことのある区切りを飛ばす。
///   原文を併記 (ShowSource): 和訳の 1 区切りごとに、翻訳に回した英文 (seg) を下に小さく薄く出す
///   原文の欄 (ShowThink)  : 和訳の上に小さな欄を出し、いま考えている英文 (think) を届いたそばから流す (和訳は翻訳を待つぶん遅れる)
///   DeepL               : 訳されていない英文 (翻訳の中継が翻訳を止めている・訳せなかった区切り) を、
///                          自動 (AutoTranslate が true の間に新しく届いた分) か、行のクリック (その区切りだけ) で DeepL に訳してもらう</summary>
[DesignerCategory("Code")]
public sealed class TranslationPanel : Control
{
    const int MaxEntries = 300, ThinkTail = 1500;
    readonly RichTextBox box;
    readonly Label head;
    readonly Panel thinkFrame;
    readonly Label thinkHead;
    readonly TextBox thinkBox;
    readonly System.Windows.Forms.Timer thinkTimer = new() { Interval = 250 };
    readonly Func<bool> hideTasks;
    // 欄の見出し。翻訳の中継が翻訳を止めている (engine = none) ときは「原文」にする
    string headBase = "和訳 (Hermes の思考)";
    string headState = "";
    Color headColor = Theme.Muted;

    // SSE を読む側 (裏のスレッド) だけが触る
    readonly HashSet<(long boot, int turn, int seg)> seen = [];
    readonly Dictionary<int, (string source, bool task)> turns = [];
    readonly Dictionary<(int turn, int seg), string> segSrc = [];
    long boot;

    // 原文の欄 (裏のスレッドが書き、タイマーで画面に出す)
    readonly object thinkLock = new();
    readonly StringBuilder think = new();
    int thinkTurn = -1;
    string thinkLabel = "";
    bool thinkLive, thinkDirty;

    // 画面側 (UI スレッド) だけが触る
    readonly List<Entry> entries = [];
    int lastTurn = -1;
    bool showSource;
    // DeepL に頼む待ち行列と、頼んでいる最中か
    readonly List<Entry> queue = [];
    readonly System.Windows.Forms.Timer deeplTimer = new() { Interval = 800 };
    bool deeplBusy;
    // 翻訳が集中したときに固まらないための歯止め:
    //   待ち行列はここで頭打ち (超えたら自動翻訳を止める)、描き直しは redrawTimer がまとめて 1 回だけ行う
    const int MaxQueue = 200;
    readonly System.Windows.Forms.Timer redrawTimer = new() { Interval = 400 };
    bool redrawPending, redrawing;

    /// <summary>1 区切り。Kind: ok = 翻訳の中継が訳した / none = 中継が翻訳を止めている (英文のまま) / bad = 中継が訳せなかった / deepl = DeepL で訳した</summary>
    sealed class Entry
    {
        public int Turn;
        public string Source = "";
        public string Text = "";      // 欄に出す文 (訳文、または英文)
        public string? Orig;          // 英文 (併記と DeepL に渡す用)
        public string Kind = "ok";
        public DateTime At;
        public bool Pending;          // DeepL に頼んでいる最中
        public int Start, Len;        // 欄の中の位置 (クリックでどの区切りか調べる)
        public bool Untranslated => Kind is "none" or "bad";
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DeepLService? DeepL { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool> AutoTranslate { get; set; } = () => false;
    /// <summary>自動の翻訳を止めた (上限・失敗)。理由を渡す</summary>
    public event Action<string>? AutoStopped;
    /// <summary>行をクリックしたが DeepL のキーが無い</summary>
    public event Action? NeedKey;

    public TranslationPanel(HonyakuFeed feed, Func<bool> hideTasks)
    {
        this.hideTasks = hideTasks;
        feed.Event += OnEvent;
        feed.Status += (text, ok) => { headState = text; headColor = ok || text.StartsWith("つないで") ? Theme.Muted : Theme.Warn; SetHead(); };
        BackColor = Theme.Back;
        head = new Label { Dock = DockStyle.Top, Height = 20, Text = "和訳 (Hermes の思考)", ForeColor = Theme.Muted, Font = Theme.Ui, Padding = new Padding(6, 3, 0, 0) };
        box = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Back, ForeColor = Theme.Ink,
            Font = new Font("Yu Gothic UI", 9.5f), ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false,
        };
        // 文字が窓の端にくっつかないよう、余白の枠の中に置く
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 2, 2, 4), BackColor = Theme.Back };
        frame.Controls.Add(box);

        // 原文の欄: 和訳の上に 4 行ほど。いちばん新しいところだけを出す
        thinkHead = new Label { Dock = DockStyle.Top, Height = 16, Text = "原文", ForeColor = Theme.Muted, Font = Theme.Small };
        thinkBox = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.BarBack,
            ForeColor = Theme.Muted, Font = new Font("Consolas", 8.5f), WordWrap = true, ScrollBars = ScrollBars.None, TabStop = false,
        };
        thinkFrame = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(8, 0, 6, 4), BackColor = Theme.Back, Visible = false };
        thinkFrame.Controls.Add(thinkBox);
        thinkFrame.Controls.Add(thinkHead);
        thinkFrame.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Grid });  // 和訳との区切り

        Controls.Add(frame);
        Controls.Add(thinkFrame);
        Controls.Add(head);
        thinkTimer.Tick += (_, _) => DrawThink();
        deeplTimer.Tick += async (_, _) => await PumpDeepL();
        deeplTimer.Start();
        // 描き直しは 400ms に 1 回だけ。届くそばから全描き直しをすると、翻訳が集中したとき固まる
        redrawTimer.Tick += (_, _) => { if (redrawPending && !redrawing) { redrawPending = false; Redraw(true); } };
        redrawTimer.Start();
        box.MouseUp += OnBoxClick;
        new ToolTip { InitialDelay = 600 }.SetToolTip(box, "英文の行をクリックすると、その区切りだけ DeepL で訳します (上の帯の「設定…」でキーを入れたとき)");
    }

    /// <summary>和訳の下に原文 (英文の区切り) を出すか。切り替えたら、出してある分も描き直す</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSource
    {
        get => showSource;
        set { if (showSource == value) return; showSource = value; Redraw(true); }
    }

    /// <summary>いま考えている英文の欄を出すか</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowThink
    {
        get => thinkFrame.Visible;
        set
        {
            thinkFrame.Visible = value;
            if (value) { lock (thinkLock) thinkDirty = true; thinkTimer.Start(); DrawThink(); } else thinkTimer.Stop();
        }
    }

    static string Str(JsonElement ev, string name) => ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    void OnEvent(JsonElement ev)
    {
        string type = Str(ev, "type");
        // 翻訳の中継の起動番号。つないだ直後は「直近のターン」→ status の順に届くので、最初に知った番号では消さない。
        // 番号が変わったとき (中継が起動し直して、ターンの番号が 1 からやり直し) だけ消す
        if ((type == "status" || type == "turn_start") && ev.TryGetProperty("boot", out var b) && b.TryGetInt64(out var bt) && bt != boot)
        {
            if (boot != 0)
            {
                seen.Clear();
                turns.Clear();
                segSrc.Clear();
                lock (thinkLock) { thinkTurn = -1; think.Clear(); thinkDirty = true; }
            }
            boot = bt;
        }
        if (type == "status")
        {
            string engine = Str(ev, "engine");
            string hb = engine == "none" ? "原文 (Hermes の思考・翻訳は止めています)" : "和訳 (Hermes の思考)";
            if (hb != headBase) { headBase = hb; SetHead(); }
            return;
        }
        if (!ev.TryGetProperty("turn", out var tn) || !tn.TryGetInt32(out int n)) return;
        switch (type)
        {
            case "turn_start":
                bool task = ev.TryGetProperty("task", out var tk) && tk.ValueKind == JsonValueKind.True;
                turns[n] = (Str(ev, "source"), task);
                if (!(task && hideTasks()))
                    lock (thinkLock)
                    {
                        thinkTurn = n;
                        think.Clear();
                        thinkLabel = $"#{n} {Str(ev, "source")}";
                        thinkLive = true;
                        thinkDirty = true;
                    }
                break;
            case "think":
                // 推測中はトークン単位で届く。開き直したときは、そのターンの全文がまとめて 1 回届く
                lock (thinkLock)
                {
                    if (n < thinkTurn) break;
                    if (n > thinkTurn)
                    {
                        turns.TryGetValue(n, out var ti);
                        if (ti.task && hideTasks()) break;
                        thinkTurn = n;
                        think.Clear();
                        thinkLabel = $"#{n} {ti.source}";
                        thinkLive = false;
                    }
                    think.Append(Str(ev, "text"));
                    if (think.Length > ThinkTail * 2) think.Remove(0, think.Length - ThinkTail);
                    thinkDirty = true;
                }
                break;
            case "turn_end":
                lock (thinkLock) if (n == thinkTurn) { thinkLive = false; thinkDirty = true; }
                break;
            case "seg":
                if (ev.TryGetProperty("seg", out var sg) && sg.TryGetInt32(out int s0))
                    segSrc[(n, s0)] = Str(ev, "src").Trim();
                break;
            case "ja":
                if (!ev.TryGetProperty("seg", out var sj) || !sj.TryGetInt32(out int seg)) return;
                string how = Str(ev, "how");
                if (how == "code") return;  // コマンド・プログラムの行は出さない (小さい欄なので)
                if (!seen.Add((boot, n, seg))) return;
                turns.TryGetValue(n, out var info);
                if (info.task && hideTasks()) return;
                bool ok = ev.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                string text = Str(ev, "text").Trim();
                segSrc.Remove((n, seg), out var src);
                // none = 翻訳を止めている (text が英文そのもの)。bad = 訳せなかった (英文は src)
                string kind = how == "none" ? "none" : ok && how != "en" && how != "interrupted" ? "ok" : "bad";
                Add(new Entry { Turn = n, Source = info.source, Text = text, Orig = kind == "none" ? text : (src ?? (kind == "bad" ? text : null)),
                                Kind = kind, At = DateTime.Now });
                break;
        }
    }

    void SetHead()
    {
        if (IsDisposed || !IsHandleCreated) return;
        string text = headBase + (headState == "" ? "" : "・" + headState);
        var color = headColor;
        BeginInvoke(() => { head.Text = text; head.ForeColor = color; });
    }

    void Add(Entry e)
    {
        if (IsDisposed || !IsHandleCreated || e.Text == "") return;
        BeginInvoke(() =>
        {
            entries.Add(e);
            // 自動の翻訳: 押している間に新しく届いた、訳されていない区切りだけ
            if (e.Untranslated && AutoTranslate() && DeepL != null && !string.IsNullOrEmpty(e.Orig))
            {
                if (queue.Count >= MaxQueue) AutoStopped?.Invoke("翻訳が追いつかないので自動翻訳を止めました");
                else if (DeepL.CanSpend(e.Orig.Length + queue.Sum(q => q.Orig!.Length))) { e.Pending = true; queue.Add(e); }
                else AutoStopped?.Invoke(DeepL.HasKey ? "DeepL の今月の上限に近いので止めました" : "DeepL のキーがありません");
            }
            if (entries.Count > MaxEntries) { entries.RemoveRange(0, entries.Count - MaxEntries); RequestRedraw(); return; }
            bool atEnd = AtEnd();
            Write(e);
            if (atEnd) ScrollToEnd();
        });
    }

    // いちばん下が見えているか (見ていれば新しい区切りで下へ送る。上を読んでいるときは動かさない)
    bool AtEnd() => box.TextLength == 0 || box.GetPositionFromCharIndex(Math.Max(0, box.TextLength - 1)).Y <= box.ClientSize.Height;

    void ScrollToEnd() { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }

    void Write(Entry e)
    {
        box.SelectionStart = box.TextLength;
        if (e.Turn != lastTurn)
        {
            lastTurn = e.Turn;
            box.SelectionColor = Theme.Accent;
            box.SelectionFont = Theme.Small;
            box.AppendText((box.TextLength > 0 ? "\n" : "") + $"#{e.Turn} {e.Source}  {e.At:HH:mm}\n");
        }
        e.Start = box.TextLength;
        // 色: 訳文 = 白、英文のまま = 薄い白 (クリックで訳せる)、訳せなかった = 黄、DeepL に頼んでいる最中 = 灰
        box.SelectionColor = e.Pending ? Theme.Muted : e.Kind == "bad" ? Theme.Warn : e.Kind == "none" ? Theme.EnInk : Theme.Ink;
        box.SelectionFont = box.Font;
        box.AppendText(e.Text + (e.Pending ? "  (DeepL…)" : e.Kind == "deepl" ? "  ᴰ" : "") + "\n");
        if (showSource && (e.Kind is "ok" or "deepl") && !string.IsNullOrEmpty(e.Orig) && e.Orig != e.Text)
        {
            box.SelectionColor = Theme.Muted;
            box.SelectionFont = Theme.Small;
            box.AppendText(e.Orig + "\n");
        }
        e.Len = box.TextLength - e.Start;
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    const int WM_SETREDRAW = 0x0B, EM_LINESCROLL = 0xB6;

    /// <summary>全部描き直す。keepView = 上を読んでいるときはその位置 (いちばん上に見えていた区切り) を保つ</summary>
    void Redraw(bool keepView = false)
    {
        if (!IsHandleCreated || redrawing) return;
        redrawing = true;
        try
        {
        bool atEnd = AtEnd();
        Entry? top = null;
        if (keepView && !atEnd)
        {
            int ci = box.GetCharIndexFromPosition(new Point(2, 2));
            top = entries.FirstOrDefault(x => ci < x.Start + x.Len);
        }
        SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            box.Clear();
            lastTurn = -1;
            foreach (var e in entries) Write(e);
            if (top != null)
            {
                box.SelectionStart = 0;
                box.ScrollToCaret();
                SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, box.GetLineFromCharIndex(top.Start));
            }
            else ScrollToEnd();
        }
        finally
        {
            SendMessage(box.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            box.Invalidate();
        }
        }
        finally { redrawing = false; }
    }

    // 行のクリック: 訳されていない区切りなら、それだけ DeepL に頼む (文字を選んでいるとき = コピーしたいときは何もしない)
    void OnBoxClick(object? sender, MouseEventArgs ev)
    {
        if (ev.Button != MouseButtons.Left || box.SelectionLength > 0) return;
        int ci = box.GetCharIndexFromPosition(ev.Location);
        var e = entries.FirstOrDefault(x => ci >= x.Start && ci < x.Start + x.Len);
        if (e == null || !e.Untranslated || e.Pending || string.IsNullOrEmpty(e.Orig)) return;
        if (DeepL == null || !DeepL.HasKey) { NeedKey?.Invoke(); return; }
        if (!DeepL.CanSpend(e.Orig.Length)) { AutoStopped?.Invoke("DeepL の今月の上限に近いので訳しません"); return; }
        e.Pending = true;
        queue.Insert(0, e);   // クリックした分を先に
        RequestRedraw();
    }

    // 待ち行列から 25 区切り・8000 字までまとめて頼む
    async Task PumpDeepL()
    {
        if (deeplBusy || queue.Count == 0 || DeepL == null) return;
        deeplBusy = true;
        try
        {
            var batch = new List<Entry>();
            int chars = 0;
            foreach (var e in queue)
            {
                if (batch.Count >= 25 || (batch.Count > 0 && chars + e.Orig!.Length > 8000)) break;
                batch.Add(e);
                chars += e.Orig!.Length;
            }
            queue.RemoveRange(0, batch.Count);
            var res = await DeepL.Translate([.. batch.Select(e => e.Orig!)]);
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Pending = false;
                if (res != null && i < res.Count) { batch[i].Text = res[i].Trim(); batch[i].Kind = "deepl"; }
            }
            if (res == null)
            {
                foreach (var e in queue) e.Pending = false;
                queue.Clear();
                AutoStopped?.Invoke("DeepL: " + DeepL.Error);
            }
            RequestRedraw();
        }
        catch { /* 翻訳の一時的な失敗で固まったり落ちたりしないよう握りつぶす (理由は DeepL.Error に残る) */ }
        finally { deeplBusy = false; }
    }

    // 描き直しを予約する (すぐにはやらず、redrawTimer が 400ms に 1 回だけまとめて行う)。
    // 届くそばから全描き直しをすると、翻訳が集中したときに UI が飽和して固まる
    void RequestRedraw() => redrawPending = true;

    void DrawThink()
    {
        string text, label;
        bool live;
        lock (thinkLock)
        {
            if (!thinkDirty) return;
            thinkDirty = false;
            text = think.Length > ThinkTail ? think.ToString(think.Length - ThinkTail, ThinkTail) : think.ToString();
            label = thinkLabel;
            live = thinkLive;
        }
        thinkHead.Text = thinkTurn < 0 ? "原文" : $"原文  {label}  {(live ? "推測中…" : "")}";
        thinkHead.ForeColor = live ? Theme.Good : Theme.Muted;
        // 改行をまとめて詰め、最後が見えるようにする
        thinkBox.Text = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        thinkBox.SelectionStart = thinkBox.TextLength;
        thinkBox.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { thinkTimer.Dispose(); deeplTimer.Dispose(); redrawTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
