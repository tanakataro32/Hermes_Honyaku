using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace HwMonitor;

/// <summary>原文 (リアルタイムの生の英文)。翻訳の画面の /events の think を、区切り・翻訳を待たずにそのまま流す欄。
/// 翻訳ログ (区切って訳したブロック) より先に、モデルが今考えている英文が見える。
/// 固まらないよう、届いた think はいったん貯めて 200ms ごとにまとめて欄へ足す。TextBox なので範囲を選んでコピーできる。</summary>
[DesignerCategory("Code")]
public sealed class OriginalPanel : Control
{
    const int MaxChars = 20000;   // 欄に残す長さ (超えたら前を捨てる)

    readonly Func<bool> hideTasks;
    readonly Label head = new()
    {
        Dock = DockStyle.Top, Height = 20, Text = "原文 (Hermes の思考・リアルタイム)",
        ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(8, 3, 0, 0), BackColor = Theme.Back,
    };
    readonly TextBox box = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Theme.BarBack, ForeColor = Theme.EnInk, Font = new Font("Consolas", 9f),
        WordWrap = true, ScrollBars = ScrollBars.Vertical, TabStop = false,
    };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };

    // 裏のスレッドが貯める
    readonly object gate = new();
    readonly StringBuilder pending = new();
    readonly Dictionary<int, bool> turnTask = [];
    long boot;
    int lastTurn = -1;
    bool clear;

    public OriginalPanel(HonyakuFeed feed, Func<bool> hideTasks)
    {
        this.hideTasks = hideTasks;
        BackColor = Theme.Back;
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 2, 4), BackColor = Theme.Back };
        frame.Controls.Add(box);
        Controls.Add(frame);
        Controls.Add(head);
        feed.Event += OnEvent;
        feed.Status += (text, ok) => { if (!ok && text != "") Head("原文 — " + text); else Head("原文 (Hermes の思考・リアルタイム)"); };
        timer.Tick += (_, _) => Flush();
        timer.Start();
    }

    void Head(string t) { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => head.Text = t); }

    static string Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    void OnEvent(JsonElement ev)
    {
        string type = Str(ev, "type");
        if (type != "status" && type != "turn_start" && type != "think") return;
        lock (gate)
        {
            if ((type == "status" || type == "turn_start") && ev.TryGetProperty("boot", out var b) && b.TryGetInt64(out var bt) && bt != boot)
            {
                if (boot != 0) { pending.Clear(); turnTask.Clear(); lastTurn = -1; clear = true; }
                boot = bt;
            }
            if (type == "status") return;
            if (!ev.TryGetProperty("turn", out var tn) || !tn.TryGetInt32(out int n)) return;
            if (type == "turn_start")
            {
                turnTask[n] = ev.TryGetProperty("task", out var tk) && tk.ValueKind == JsonValueKind.True;
                return;
            }
            // think
            if (turnTask.TryGetValue(n, out var task) && task && hideTasks()) return;
            if (n != lastTurn)
            {
                lastTurn = n;
                pending.Append(pending.Length > 0 || clear ? "\n\n" : "").Append("#").Append(n).Append('\n');
            }
            pending.Append(Str(ev, "text"));
            if (pending.Length > MaxChars * 2) pending.Remove(0, pending.Length - MaxChars);
        }
    }

    void Flush()
    {
        if (!IsHandleCreated || !Visible) return;
        string add;
        bool doClear;
        lock (gate)
        {
            if (!clear && pending.Length == 0) return;
            add = pending.ToString();
            pending.Clear();
            doClear = clear;
            clear = false;
        }
        bool atEnd = box.SelectionStart >= box.TextLength - 2;
        if (doClear) box.Clear();
        box.AppendText(add);
        // 長くなりすぎたら前を捨てる (毎回ではなく、1.5 倍を超えたときだけまとめて)
        if (box.TextLength > MaxChars * 3 / 2)
        {
            box.Text = box.Text[^MaxChars..];
            atEnd = true;
        }
        if (atEnd) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
