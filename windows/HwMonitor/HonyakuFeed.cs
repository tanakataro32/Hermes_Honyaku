using System.Text.Json;

namespace HwMonitor;

/// <summary>翻訳の画面 (hermes_honyaku.py の 8765) の /events (SSE) を読み続ける。切れたら 5 秒おきにつなぎ直す。
/// 届いたイベントは Event で配り (裏のスレッドから)、ターンの発信元とコンテキスト量は Turns に覚えておく
/// (枠のメーターで「どの枠が OpenCode か」を見分けるのに使う)</summary>
public sealed class HonyakuFeed(Func<string> baseUrl)
{
    CancellationTokenSource? cts;

    /// <summary>届いたイベント (JSON)。裏のスレッドから呼ばれる</summary>
    public event Action<JsonElement>? Event;
    /// <summary>つながり具合 (文、つながっているか)。裏のスレッドから呼ばれる</summary>
    public event Action<string, bool>? Status;

    public TurnTracker Turns { get; } = new();

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
                Status?.Invoke("つないでいます…", false);
                using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl() + "/events");
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                Status?.Invoke("", true);
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
                // 30 秒なにも来なければ切れたとみなす (サーバーは 15 秒ごとに ping を送る)
                while (!ct.IsCancellationRequested)
                {
                    var read = reader.ReadLineAsync(ct).AsTask();
                    if (await Task.WhenAny(read, Task.Delay(30000, ct)) != read) break;
                    var line = await read;
                    if (line == null) break;
                    if (!line.StartsWith("data: ")) continue;
                    JsonElement ev;
                    try { ev = JsonDocument.Parse(line[6..]).RootElement; } catch { continue; }
                    Turns.Apply(ev);
                    Event?.Invoke(ev);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Status?.Invoke("つながりません: " + e.Message, false);
            }
            try { await Task.Delay(5000, ct); } catch { return; }
        }
    }

    public static string Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>ターンの発信元 (Hermes・OpenCode など)・会話の札・コンテキスト量 (turn_ctx)・終わったか</summary>
public sealed class TurnTracker
{
    /// <summary>Ts = ターンが始まった時刻 (unix 秒、翻訳の中継 = llmsv の時計。hwmon の枠の時刻と比べられる)</summary>
    public sealed record Turn(int N, string Source, string Who, bool Task, long Tokens, double Ts, bool Ended);

    readonly object gate = new();
    readonly Dictionary<int, Turn> turns = [];
    long boot;

    public void Apply(JsonElement ev)
    {
        string type = HonyakuFeed.Str(ev, "type");
        lock (gate)
        {
            // 翻訳の中継の起動番号 (boot) は turn_start と status に付いている。つないだ直後は「直近のターン (turn_start から)」→ status の順に届くので、
            // 最初に知った番号では消さない。番号が変わったとき (中継が起動し直して、ターンの番号が 1 からやり直し) だけ消す
            if ((type == "status" || type == "turn_start") && ev.TryGetProperty("boot", out var b) && b.TryGetInt64(out var bt) && bt != boot)
            {
                if (boot != 0) turns.Clear();
                boot = bt;
            }
            if (type == "status") return;
            if (!ev.TryGetProperty("turn", out var tn) || !tn.TryGetInt32(out int n)) return;
            switch (type)
            {
                case "turn_start":
                    turns[n] = new Turn(n, HonyakuFeed.Str(ev, "source"), HonyakuFeed.Str(ev, "who"),
                        ev.TryGetProperty("task", out var tk) && tk.ValueKind == JsonValueKind.True, 0,
                        ev.TryGetProperty("ts", out var ts) && ts.TryGetDouble(out var tv) ? tv : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, false);
                    while (turns.Count > 60) turns.Remove(turns.Keys.Min());
                    break;
                case "turn_ctx":
                    if (turns.TryGetValue(n, out var t1) && ev.TryGetProperty("tokens", out var tok) && tok.TryGetInt64(out var nt))
                        turns[n] = t1 with { Tokens = nt };
                    break;
                case "turn_end":
                    if (turns.TryGetValue(n, out var t2)) turns[n] = t2 with { Ended = true };
                    break;
            }
        }
    }

    public List<Turn> Snapshot()
    {
        lock (gate) return [.. turns.Values];
    }
}
