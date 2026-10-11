namespace HwMonitor;

/// <summary>色と字 (翻訳の画面のシステム欄に合わせた暗い配色)</summary>
static class Theme
{
    public static readonly Color Back = Color.FromArgb(0x13, 0x1a, 0x1f);
    public static readonly Color Panel = Color.FromArgb(0x1b, 0x23, 0x29);
    public static readonly Color BarBack = Color.FromArgb(0x0c, 0x11, 0x14);
    public static readonly Color Ink = Color.FromArgb(0xcf, 0xe8, 0xe4);
    public static readonly Color Head = Color.White;
    public static readonly Color Muted = Color.FromArgb(0x8a, 0x9a, 0xa0);
    public static readonly Color EnInk = Color.FromArgb(0xa8, 0xbc, 0xb8);   // 訳していない英文 (クリックで DeepL に訳せる)
    public static readonly Color Grid = Color.FromArgb(0x2a, 0x35, 0x3c);
    public static readonly Color Edge = Color.FromArgb(0x4a, 0x57, 0x62);
    public static readonly Color Good = Color.FromArgb(0x35, 0xe0, 0x5a);
    public static readonly Color Warn = Color.FromArgb(0xff, 0xb0, 0x20);
    public static readonly Color Bad = Color.FromArgb(0xf0, 0x40, 0x30);
    public static readonly Color Cache = Color.FromArgb(0x3d, 0x6f, 0x9e);
    public static readonly Color Accent = Color.FromArgb(0x10, 0x7a, 0x70);

    // グラフの線の色 (暗い背景で見分けやすいもの)。全体の電力は白
    public static readonly Color[] Series =
    [
        Color.FromArgb(0x4f, 0xc3, 0xf7), Color.FromArgb(0xff, 0xb7, 0x4d), Color.FromArgb(0x81, 0xc7, 0x84),
        Color.FromArgb(0xe5, 0x73, 0x73), Color.FromArgb(0xba, 0x68, 0xc8), Color.FromArgb(0xff, 0xf1, 0x76),
        Color.FromArgb(0x4d, 0xb6, 0xac), Color.FromArgb(0xf0, 0x62, 0x92), Color.FromArgb(0xa1, 0x88, 0x7f),
    ];

    public static readonly Font Ui = new("Yu Gothic UI", 9f);
    public static readonly Font UiBold = new("Yu Gothic UI", 9f, FontStyle.Bold);
    public static readonly Font UiBig = new("Yu Gothic UI", 13f, FontStyle.Bold);
    public static readonly Font Num = new("Consolas", 9f);
    public static readonly Font Small = new("Yu Gothic UI", 8f);

    public static Color Level(double v, double warn, double hot) => v >= hot ? Bad : v >= warn ? Warn : Good;
}
