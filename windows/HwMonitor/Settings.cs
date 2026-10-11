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
