using System.ComponentModel;
using System.Text.Json;

namespace HwMonitor;

/// <summary>Hermes の思考の和訳を小さく流す欄。翻訳の画面 (hermes_honyaku.py の 8765) の /events (SSE) を読む。
/// 切れたら 5 秒おきにつなぎ直す。つなぎ直したときに送られてくる直近のターンは、出したことのある区切りを飛ばす</summary>
[DesignerCategory("Code")]
public sealed class TranslationPanel : Control
{
    const int MaxLines = 300;
    readonly RichTextBox box;
    readonly Label head;
    readonly HashSet<(long boot, int turn, int seg)> seen = [];
    readonly Dictionary<int, (string source, bool task)> turns = [];
    readonly Func<string> baseUrl;
    readonly Func<bool> hideTasks;
    CancellationTokenSource? cts;
    long boot;
    int lastTurn = -1;

    public TranslationPanel(Func<string> baseUrl, Func<bool> hideTasks)
    {
        this.baseUrl = baseUrl;
        this.hideTasks = hideTasks;
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
        Controls.Add(frame);
        Controls.Add(head);
    }

    public void Start()
    {
        Stop();
        cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(cts.Token));
    }

    public void Stop() { cts?.Cancel(); cts = null; }

    async Task Loop(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetHead("和訳 (Hermes の思考)・つないでいます…", Theme.Muted);
                using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl() + "/events");
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                SetHead("和訳 (Hermes の思考)", Theme.Muted);
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
                // 30 秒なにも来なければ切れたとみなす (サーバーは 15 秒ごとに ping を送る)
                while (!ct.IsCancellationRequested)
                {
                    var read = reader.ReadLineAsync(ct).AsTask();
                    if (await Task.WhenAny(read, Task.Delay(30000, ct)) != read) break;
                    var line = await read;
                    if (line == null) break;
                    if (line.StartsWith("data: ")) OnEvent(line[6..]);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                SetHead("和訳 (Hermes の思考)・つながりません: " + e.Message, Theme.Warn);
            }
            try { await Task.Delay(5000, ct); } catch { return; }
        }
    }

    void OnEvent(string json)
    {
        JsonElement ev;
        try { ev = JsonDocument.Parse(json).RootElement; } catch { return; }
        string type = ev.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        if (type == "status" && ev.TryGetProperty("boot", out var b) && b.TryGetInt64(out var bt) && bt != boot)
        {
            // 翻訳の中継が起動し直した (ターンの番号が 1 からやり直しになる)
            boot = bt;
            seen.Clear();
            turns.Clear();
        }
        else if (type == "turn_start")
        {
            int n = ev.GetProperty("turn").GetInt32();
            string src = ev.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            bool task = ev.TryGetProperty("task", out var tk) && tk.ValueKind == JsonValueKind.True;
            turns[n] = (src, task);
        }
        else if (type == "ja")
        {
            int n = ev.GetProperty("turn").GetInt32(), seg = ev.GetProperty("seg").GetInt32();
            string how = ev.TryGetProperty("how", out var h) ? h.GetString() ?? "" : "";
            if (how == "code") return;  // コマンド・プログラムの行は出さない (小さい欄なので)
            if (!seen.Add((boot, n, seg))) return;
            turns.TryGetValue(n, out var info);
            if (info.task && hideTasks()) return;
            string text = ev.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            bool ok = ev.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            Append(n, info.source, text.Trim(), ok && how != "en" && how != "interrupted");
        }
    }

    void SetHead(string text, Color color)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { head.Text = text; head.ForeColor = color; });
    }

    void Append(int turn, string source, string text, bool translated)
    {
        if (IsDisposed || !IsHandleCreated || text == "") return;
        BeginInvoke(() =>
        {
            bool atEnd = box.SelectionStart >= box.TextLength - 1;
            box.SelectionStart = box.TextLength;
            if (turn != lastTurn)
            {
                lastTurn = turn;
                box.SelectionColor = Theme.Accent;
                box.SelectionFont = Theme.Small;
                box.AppendText((box.TextLength > 0 ? "\n" : "") + $"#{turn} {source}  {DateTime.Now:HH:mm}\n");
            }
            box.SelectionColor = translated ? Theme.Ink : Theme.Warn;  // 訳せなかった区切りは原文のまま黄色
            box.SelectionFont = box.Font;
            box.AppendText(text + "\n");
            if (box.Lines.Length > MaxLines)
            {
                box.Select(0, box.GetFirstCharIndexFromLine(box.Lines.Length - MaxLines));
                box.ReadOnly = false;
                box.SelectedText = "";
                box.ReadOnly = true;
            }
            if (atEnd) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Stop();
        base.Dispose(disposing);
    }
}
