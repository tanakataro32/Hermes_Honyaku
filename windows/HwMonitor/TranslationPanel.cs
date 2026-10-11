using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace HwMonitor;

/// <summary>Hermes の思考の和訳を小さく流す欄。翻訳の画面 (hermes_honyaku.py の 8765) の /events を HonyakuFeed から受け取る。
/// つなぎ直したときに送られてくる直近のターンは、出したことのある区切りを飛ばす。
///   原文を併記 (ShowSource): 和訳の 1 区切りごとに、翻訳に回した英文 (seg) を下に小さく薄く出す
///   原文の欄 (ShowThink)  : 和訳の上に小さな欄を出し、いま考えている英文 (think) を届いたそばから流す (和訳は翻訳を待つぶん遅れる)</summary>
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

    sealed record Entry(int Turn, string Source, string Ja, string? Src, bool Translated, DateTime At);

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
    }

    /// <summary>和訳の下に原文 (英文の区切り) を出すか。切り替えたら、出してある分も描き直す</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSource
    {
        get => showSource;
        set { if (showSource == value) return; showSource = value; Redraw(); }
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
                bool translated = ok && how != "en" && how != "interrupted";
                segSrc.Remove((n, seg), out var src);
                if (how == "none") src = null;   // 翻訳を止めているときは訳文 = 原文なので、併記しない
                // 訳せなかった区切りは和訳の欄に原文がそのまま出るので、併記はしない
                Add(new Entry(n, info.source, Str(ev, "text").Trim(), translated ? src : null, translated, DateTime.Now));
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
        if (IsDisposed || !IsHandleCreated || e.Ja == "") return;
        BeginInvoke(() =>
        {
            entries.Add(e);
            if (entries.Count > MaxEntries) { entries.RemoveRange(0, entries.Count - MaxEntries); Redraw(); return; }
            bool atEnd = box.SelectionStart >= box.TextLength - 1;
            Write(e);
            if (atEnd) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
        });
    }

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
        box.SelectionColor = e.Translated ? Theme.Ink : Theme.Warn;  // 訳せなかった区切りは原文のまま黄色
        box.SelectionFont = box.Font;
        box.AppendText(e.Ja + "\n");
        if (showSource && !string.IsNullOrEmpty(e.Src))
        {
            box.SelectionColor = Theme.Muted;
            box.SelectionFont = Theme.Small;
            box.AppendText(e.Src + "\n");
        }
    }

    void Redraw()
    {
        if (!IsHandleCreated) return;
        box.Clear();
        lastTurn = -1;
        foreach (var e in entries) Write(e);
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

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
        if (disposing) thinkTimer.Dispose();
        base.Dispose(disposing);
    }
}
