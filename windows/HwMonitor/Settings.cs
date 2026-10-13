using System.Text.Json;

namespace HwMonitor;

/// <summary>設定 (%APPDATA%\HwMonitor\settings.json)。接続先・最前面・ウィンドウの位置・グラフの選択を覚えておく</summary>
public sealed class Settings
{
    public string Host { get; set; } = "192.168.1.50";
    public int HwmonPort { get; set; } = 8766;     // hwmon.py (llmsv のハードウェアモニタ)
    public int HonyakuPort { get; set; } = 8765;   // hermes_honyaku.py の画面 (和訳の SSE)
    public bool TopMost { get; set; }
    public bool HideTasks { get; set; } = true;    // 背景タスク (タイトル生成など) の和訳を出さない
    public bool ShowTranslation { get; set; } = true;
    public bool ShowSource { get; set; }               // 和訳の下に原文 (英文の区切り) を併記
    public bool ShowThink { get; set; } = true;        // 和訳の上に、いま考えている英文の欄
    // DeepL (翻訳の中継が翻訳を止めているときなどに、見ている間だけ英文を訳す)。キーはこのファイルにだけ置く (リポジトリには入れない)
    public string DeepLKey { get; set; } = "";
    public bool AutoTranslate { get; set; }              // 押している間、新しく届いた英文を訳す
    public long DeepLCap { get; set; }                   // 自分で決めた月の上限 (文字)。0 = DeepL の上限まで
    // OpenCode は「使っている量 ≥ 枠の上限 − 予約」で圧縮を始める (予約は既定 2 万トークン = 20k)。3 段目の圧縮ラインの位置
    public int CompactReserveK { get; set; } = 20;
    // 枠のメーターの色の基準にする平均: "graph" = グラフの期間の ▶AVE と同じ、"1h" = 直近 1 時間 (グラフの期間を変えても変わらない)
    public string MeterAvg { get; set; } = "graph";
    // 更新の間隔 (秒、上の帯の − ＋)。hwmon.py が 2 秒ごとに読んでいるので 2 秒より短くはしない
    public int IntervalSec { get; set; } = 2;
    public bool DeviceView { get; set; }                 // メーターを部品別 (CPU・GPU ごとに 1 行) に
    // 速度と文脈 (散布図)
    public string SpeedY { get; set; } = "gen";          // gen = 生成 / pp = 読み込み / draft = MTP の当たり率
    public string SpeedColor { get; set; } = "run";      // run = 設定ごと / par = 並列数
    public int SpeedDays { get; set; } = 7;              // 0 = 全部
    public List<string> SpeedHidden { get; set; } = [];  // 凡例で隠したもの
    public string Range { get; set; } = "1h";
    public string Group { get; set; } = "p";
    public string Tab { get; set; } = "graph";
    public int[]? Bounds { get; set; }

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HwMonitor");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public string HwmonBase => $"http://{Host}:{HwmonPort}";
    public string HonyakuBase => $"http://{Host}:{HonyakuPort}";

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
