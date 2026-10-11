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
}

public sealed class EnergyData
{
    public double Price { get; set; }
    public EnergyToday Today { get; set; } = new();
    public EnergyMonth Month { get; set; } = new();
    public List<EnergyDay> Days { get; set; } = [];
    public List<EnergyMonthRow> Months { get; set; } = [];
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
