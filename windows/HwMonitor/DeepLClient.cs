using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HwMonitor;

/// <summary>DeepL API (英語 → 日本語)。キーが「:fx」で終わる無料プランは api-free.deepl.com、それ以外は api.deepl.com。
/// 無料プランは月 50 万字まで (超えると 456 で断られるだけで、課金はされない)。文字数は原文 (英語) で数えられる</summary>
public sealed class DeepLClient(Func<string> key)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    string Base => key().Trim().EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";

    HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, Base + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key().Trim());
        return r;
    }

    static async Task Check(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        string why = (int)res.StatusCode switch
        {
            403 => "キーが違います (403)",
            456 => "今月の文字数の上限に達しました (456)",
            429 => "頼みすぎです。少し待ってください (429)",
            _ => $"HTTP {(int)res.StatusCode}",
        };
        string body = await res.Content.ReadAsStringAsync();
        throw new InvalidOperationException(why + (body.Length > 0 && body.Length < 200 ? ": " + body : ""));
    }

    /// <summary>まとめて翻訳する (順番どおりに返る)</summary>
    public async Task<List<string>> Translate(IReadOnlyList<string> texts)
    {
        using var req = Req(HttpMethod.Post, "/v2/translate");
        var body = JsonSerializer.Serialize(new { text = texts, source_lang = "EN", target_lang = "JA", preserve_formatting = true });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        await Check(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return [.. doc.RootElement.GetProperty("translations").EnumerateArray().Select(t => t.GetProperty("text").GetString() ?? "")];
    }

    /// <summary>今月使った文字数と上限 (DeepL の数え方そのもの)</summary>
    public async Task<(long used, long limit)> Usage()
    {
        using var req = Req(HttpMethod.Get, "/v2/usage");
        using var res = await Http.SendAsync(req);
        await Check(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("character_count").GetInt64(), doc.RootElement.GetProperty("character_limit").GetInt64());
    }
}

/// <summary>DeepL の使い方の決まり: キー・今月の使用量 (DeepL に聞いた値に、こちらで頼んだ分を足していく)・自分で決めた上限。
/// 上限の 98% を超える頼み方は断る (自動の翻訳はそこで止まる)</summary>
public sealed class DeepLService(Settings settings)
{
    readonly DeepLClient client = new(() => settings.DeepLKey);

    public long Used { get; private set; } = -1;    // 今月使った文字数 (-1 = まだ聞いていない)
    public long Limit { get; private set; } = -1;   // DeepL の上限 (無料プランは 50 万)
    public string Error { get; private set; } = "";
    public event Action? Changed;

    public bool HasKey => !string.IsNullOrWhiteSpace(settings.DeepLKey);

    /// <summary>使ってよい上限 = 設定の上限 (0 なら DeepL の上限) と DeepL の上限の小さいほう</summary>
    public long Cap
    {
        get
        {
            long api = Limit > 0 ? Limit : 500_000;
            return settings.DeepLCap > 0 ? Math.Min(settings.DeepLCap, api) : api;
        }
    }

    public bool CanSpend(long chars) => HasKey && (Used < 0 || Used + chars <= Cap * 0.98);

    public async Task RefreshUsage()
    {
        if (!HasKey) { Used = Limit = -1; Error = ""; Changed?.Invoke(); return; }
        try { (Used, Limit) = await client.Usage(); Error = ""; }
        catch (Exception e) { Error = e.Message; }
        Changed?.Invoke();
    }

    /// <summary>翻訳する。失敗したら null (理由は Error)</summary>
    public async Task<List<string>?> Translate(List<string> texts)
    {
        try
        {
            var r = await client.Translate(texts);
            if (Used >= 0) Used += texts.Sum(t => (long)t.Length);
            Error = "";
            Changed?.Invoke();
            return r;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Changed?.Invoke();
            return null;
        }
    }
}
