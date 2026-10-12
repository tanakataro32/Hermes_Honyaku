using System.Text.Json;

namespace HwMonitor;

// hwmon.py の JSON (キーは snake_case)。値が取れないときは null が来るので double? にしている

public sealed class NowData
{
    public double Ts { get; set; }
    public PowerData? Power { get; set; }
    public List<TempItem> Temps { get; set; } = [];
    public List<UtilItem> Util { get; set; } = [];
    public List<MemItem> Mem { get; set; } = [];
    public List<DiskItem> Disks { get; set; } = [];
    public EnergyBrief? Energy { get; set; }
    public List<SlotServer> Slots { get; set; } = [];
}

/// <summary>llama-server 1 台の枠 (/slots)</summary>
public sealed class SlotServer
{
    public string Server { get; set; } = "";
    public bool Up { get; set; }
    public List<SlotInfo> Slots { get; set; } = [];
}

public sealed class SlotInfo
{
    public int Id { get; set; }
    public long NCtx { get; set; }
    public long Used { get; set; }       // 枠が使っている量 (入力 + 生成)。待機中は最後に使っていた量
    public long Prompt { get; set; }     // 要求の入力
    public long Decoded { get; set; }    // 生成した量
    public bool Processing { get; set; }
    public double? LastActive { get; set; }
    public double? Started { get; set; }  // いまの要求を受けた時刻 (unix 秒、llmsv の時計)
    public double Tps { get; set; }      // 生成の速さ (トークン/秒)
    public double PpTps { get; set; }    // 読み込みの速さ (トークン/秒、読み込みの最中だけ。ほかは 0)
}

public sealed class PowerData
{
    public double Wall { get; set; }
    public double Dc { get; set; }
    public double Full { get; set; }
    public double Eff { get; set; }
    public string CpuSrc { get; set; } = "";
    public List<PowerPart> Parts { get; set; } = [];
}

public sealed class PowerPart
{
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public double? W { get; set; }
    public bool Est { get; set; }
}

public sealed class TempItem
{
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public double C { get; set; }
    public double Max { get; set; }
    public double Warn { get; set; }
    public double Hot { get; set; }
    public string Tip { get; set; } = "";
}

public sealed class UtilItem
{
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public double? Pct { get; set; }
    public string Throttle { get; set; } = "";
    public double? Clock { get; set; }
    public double? ClockMax { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class MemItem
{
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public double? Used { get; set; }
    public double? Cache { get; set; }
    public double? Total { get; set; }
}

public sealed class DiskItem
{
    public string Label { get; set; } = "";
    public string Tip { get; set; } = "";
    public double? Used { get; set; }
    public double? Total { get; set; }
}

public sealed class EnergyBrief
{
    public double Price { get; set; }
    public double TodayKwh { get; set; }
    public double TodayYen { get; set; }
    public double MonthKwh { get; set; }
    public double MonthYen { get; set; }
    public double? MonthForecastYen { get; set; }
}

public sealed class HistoryData
{
    public string Range { get; set; } = "";
    public int Step { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public List<long> T { get; set; } = [];
    public Dictionary<string, List<double?>> Series { get; set; } = [];
    public int MaxGap { get; set; }   // これより空いたら線を切る (秒)
    /// <summary>読み込みの最中の見込み (キー -> いつから・速さ)。llama-server は 2048 トークンごとにしか数を増やさないので、
    /// 増えるまでの右端はゼロになる。その間を見込みの速さで描く (記録の値ではない)</summary>
    public Dictionary<string, List<LiveRate>> Live { get; set; } = [];
}

public sealed class LiveRate
{
    public double From { get; set; }
    public double Rate { get; set; }
}

public sealed class EnergyData
{
    public double Price { get; set; }
    public EnergyToday Today { get; set; } = new();
    public EnergyMonth Month { get; set; } = new();
    public List<EnergyDay> Days { get; set; } = [];
    public List<EnergyMonthRow> Months { get; set; } = [];
    public CompareData? Compare { get; set; }
}

/// <summary>電気代と、同じだけ推論したときのレンタル (GPU の時間貸し・API の従量) の比べ</summary>
public sealed class CompareData
{
    public ComparePrices Prices { get; set; } = new();
    public List<ComparePeriod> Periods { get; set; } = [];
    public List<CompareDay> Days { get; set; } = [];
}

public sealed class ComparePrices
{
    public double YenKwh { get; set; }
    public double UsdJpy { get; set; }
    public double GpuUsdPerHour { get; set; }
    public Dictionary<string, ApiPrice> Api { get; set; } = [];
}

public sealed class ApiPrice
{
    public double In { get; set; }
    public double Out { get; set; }
}

public sealed class CostSet
{
    public double? Elec { get; set; }
    public double? GpuBusy { get; set; }
    public double? GpuFull { get; set; }
    public double? Api { get; set; }
}

public sealed class ComparePeriod
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Kwh { get; set; }
    public double Hours { get; set; }
    public double BusyH { get; set; }
    public long In { get; set; }
    public long Out { get; set; }
    public List<CompareServer> Servers { get; set; } = [];
    public List<string> ApiMissing { get; set; } = [];
    public CostSet Yen { get; set; } = new();
    public CostSet PerMout { get; set; } = new();
}

public sealed class CompareServer
{
    public string Server { get; set; } = "";
    public long In { get; set; }
    public long Proc { get; set; }
    public long Out { get; set; }
    public double BusyH { get; set; }
    public double? ApiYen { get; set; }
}

public sealed class CompareDay
{
    public string Date { get; set; } = "";
    public long In { get; set; }
    public long Out { get; set; }
    public double BusyH { get; set; }
    public CostSet Yen { get; set; } = new();
}

public sealed class EnergyToday
{
    public string Date { get; set; } = "";
    public double Kwh { get; set; }
    public double Yen { get; set; }
    public double Coverage { get; set; }
    public double? AvgW { get; set; }
}

public sealed class EnergyMonth
{
    public string Month { get; set; } = "";
    public double Kwh { get; set; }
    public double Yen { get; set; }
    public double? ForecastKwh { get; set; }
    public double? ForecastYen { get; set; }
    public double? AvgW { get; set; }
}

public sealed class EnergyDay
{
    public string Date { get; set; } = "";
    public double Kwh { get; set; }
    public double Yen { get; set; }
    public double Coverage { get; set; }
}

public sealed class EnergyMonthRow
{
    public string Month { get; set; } = "";
    public double Kwh { get; set; }
    public double Yen { get; set; }
}

public sealed class HwmonClient(Func<string> baseUrl)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    async Task<T?> Get<T>(string path)
    {
        var s = await Http.GetStringAsync(baseUrl() + path);
        return JsonSerializer.Deserialize<T>(s, Json);
    }

    public Task<NowData?> Now() => Get<NowData>("/api/now");
    public Task<HistoryData?> History(string range) => Get<HistoryData>("/api/history?range=" + range);
    public Task<EnergyData?> Energy() => Get<EnergyData>("/api/energy");
}
