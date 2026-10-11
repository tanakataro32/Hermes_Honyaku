using System.ComponentModel;

namespace HwMonitor;

/// <summary>メインの窓: 上にいまの値のメーター、真ん中に履歴のグラフ / 電気代、下に和訳。
/// 画面はコードだけで組んでいる (Designer のファイルはない)</summary>
[DesignerCategory("Code")]
public sealed class MainForm : Form
{
    readonly Settings settings;
    readonly HwmonClient api;
    readonly MeterPanel meters = new() { Dock = DockStyle.Top };
    readonly HistoryChart chart = new() { Dock = DockStyle.Fill };
    readonly EnergyPanel energy = new() { Dock = DockStyle.Fill, Visible = false };
    readonly TranslationPanel translation;
    readonly SplitContainer split;
    readonly ToolStrip bar = new() { GripStyle = ToolStripGripStyle.Hidden, Renderer = new DarkRenderer(), BackColor = Theme.Panel, ForeColor = Theme.Ink, Padding = new Padding(4, 2, 4, 2) };
    readonly ToolStripButton tabGraph = new("グラフ") { CheckOnClick = false };
    readonly ToolStripButton tabEnergy = new("電気代") { CheckOnClick = false };
    readonly ToolStripButton topMost = new("最前面") { CheckOnClick = true };
    readonly ToolStripButton showJa = new("和訳") { CheckOnClick = true };
    readonly ToolStripButton showSrc = new("原文を併記") { CheckOnClick = true, ToolTipText = "和訳の 1 区切りごとに、翻訳した英文を下に小さく出す" };
    readonly ToolStripButton showThink = new("原文の欄") { CheckOnClick = true, ToolTipText = "和訳の上に、いま考えている英文を届いたそばから流す (和訳より早い)" };
    readonly ToolStripLabel status = new() { Alignment = ToolStripItemAlignment.Right };
    readonly System.Windows.Forms.Timer nowTimer = new() { Interval = 2000 };
    readonly System.Windows.Forms.Timer slowTimer = new() { Interval = 10000 };
    DateTime lastHistory = DateTime.MinValue, lastEnergy = DateTime.MinValue;
    bool busyNow, busyHist, busyEnergy;

    public MainForm(Settings settings)
    {
        this.settings = settings;
        api = new HwmonClient(() => settings.HwmonBase);
        translation = new TranslationPanel(() => settings.HonyakuBase, () => settings.HideTasks) { Dock = DockStyle.Fill };

        Text = "llmsv ハードウェアモニタ";
        BackColor = Theme.Back;
        ForeColor = Theme.Ink;
        Font = Theme.Ui;
        MinimumSize = new Size(720, 420);
        Size = new Size(1000, 720);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(80, 80);
        if (settings.Bounds is { Length: 4 } bnd)
        {
            var r = new Rectangle(bnd[0], bnd[1], bnd[2], bnd[3]);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) { Location = r.Location; Size = r.Size; }
        }

        // 上の帯: 表示の切り替えと設定
        tabGraph.Click += (_, _) => SetTab("graph");
        tabEnergy.Click += (_, _) => SetTab("energy");
        topMost.Checked = settings.TopMost;
        topMost.CheckedChanged += (_, _) => { TopMost = settings.TopMost = topMost.Checked; settings.Save(); };
        showJa.Checked = settings.ShowTranslation;
        showJa.CheckedChanged += (_, _) => { settings.ShowTranslation = showJa.Checked; ApplyTranslation(); settings.Save(); };
        showSrc.Checked = settings.ShowSource;
        showSrc.CheckedChanged += (_, _) => { settings.ShowSource = translation.ShowSource = showSrc.Checked; settings.Save(); };
        showThink.Checked = settings.ShowThink;
        showThink.CheckedChanged += (_, _) => { settings.ShowThink = translation.ShowThink = showThink.Checked; settings.Save(); };
        var setup = new ToolStripButton("設定…");
        setup.Click += (_, _) => OpenSettings();
        bar.Items.AddRange([tabGraph, tabEnergy, new ToolStripSeparator(), topMost, showJa, showSrc, showThink, new ToolStripSeparator(), setup, status]);

        // 真ん中 (グラフ / 電気代) と下 (和訳) は境目をドラッグで動かせる
        split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Grid, SplitterWidth = 4 };
        split.Panel1.BackColor = split.Panel2.BackColor = Theme.Back;
        split.Panel1.Controls.Add(chart);
        split.Panel1.Controls.Add(energy);
        split.Panel2.Controls.Add(translation);

        Controls.Add(split);
        Controls.Add(meters);
        Controls.Add(bar);
        meters.Height = meters.WantedHeight();

        chart.Range = settings.Range;
        chart.Group = settings.Group;
        chart.SelectionChanged += (_, _) =>
        {
            settings.Range = chart.Range;
            settings.Group = chart.Group;
            settings.Save();
            lastHistory = DateTime.MinValue;
            _ = RefreshHistory();
        };

        nowTimer.Tick += async (_, _) => await RefreshNow();
        slowTimer.Tick += async (_, _) => await RefreshSlow();
        Load += async (_, _) =>
        {
            TopMost = settings.TopMost;
            split.SplitterDistance = Math.Max(120, split.Height - 230);   // 和訳 (+原文の欄) に下の 230px
            SetTab(settings.Tab);
            ApplyTranslation();
            nowTimer.Start();
            slowTimer.Start();
            await RefreshNow();
            await RefreshSlow();
        };
        FormClosing += (_, _) =>
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Bounds = [r.X, r.Y, r.Width, r.Height];
            settings.Save();
            translation.Stop();
        };
    }

    // 100 円までは小数 1 桁 (使い始めは数十銭なので ¥0 にしない)
    static string Yen(double v) => v >= 100 ? $"¥{v:#,0}" : $"¥{v:0.0}";

    void SetTab(string tab)
    {
        settings.Tab = tab == "energy" ? "energy" : "graph";
        settings.Save();
        bool e = settings.Tab == "energy";
        chart.Visible = !e;
        energy.Visible = e;
        tabGraph.Checked = !e;
        tabEnergy.Checked = e;
        _ = RefreshSlow(force: true);
    }

    void ApplyTranslation()
    {
        split.Panel2Collapsed = !settings.ShowTranslation;
        showSrc.Enabled = showThink.Enabled = settings.ShowTranslation;   // 和訳を隠しているときは原文の 2 つも押せない
        translation.ShowSource = settings.ShowSource;
        translation.ShowThink = settings.ShowThink;
        if (settings.ShowTranslation) translation.Start(); else translation.Stop();
    }

    async Task RefreshNow()
    {
        if (busyNow) return;
        busyNow = true;
        try
        {
            var now = await api.Now();
            meters.Data = now;
            int h = meters.WantedHeight();
            if (meters.Height != h) meters.Height = h;
            if (now?.Energy is { } en)
                status.Text = $"今日 {Yen(en.TodayYen)} ({en.TodayKwh:0.00}kWh)・今月 {Yen(en.MonthYen)}  ";
            status.ForeColor = Theme.Ink;
        }
        catch (Exception ex)
        {
            meters.ShowError($"{settings.HwmonBase} につながりません: {ex.Message}");
            status.Text = "つながりません  ";
            status.ForeColor = Theme.Warn;
        }
        finally { busyNow = false; }
    }

    // 履歴: 1 時間は 10 秒ごと、それより長い期間は 1 分ごと。電気代は 1 分ごと
    async Task RefreshSlow(bool force = false)
    {
        var due = settings.Range == "1h" ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(60);
        if (chart.Visible && (force || DateTime.Now - lastHistory >= due)) await RefreshHistory();
        if (energy.Visible && (force || DateTime.Now - lastEnergy >= TimeSpan.FromSeconds(60))) await RefreshEnergy();
    }

    async Task RefreshHistory()
    {
        if (busyHist) return;
        busyHist = true;
        try
        {
            chart.Data = await api.History(chart.Range);
            lastHistory = DateTime.Now;
        }
        catch (Exception ex) { chart.ShowError("履歴を読めません: " + ex.Message); }
        finally { busyHist = false; }
    }

    async Task RefreshEnergy()
    {
        if (busyEnergy) return;
        busyEnergy = true;
        try
        {
            energy.Data = await api.Energy();
            lastEnergy = DateTime.Now;
        }
        catch (Exception ex) { energy.ShowError("電気代を読めません: " + ex.Message); }
        finally { busyEnergy = false; }
    }

    void OpenSettings()
    {
        using var f = new Form
        {
            Text = "設定", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(360, 190), Font = Theme.Ui, TopMost = TopMost,
        };
        var host = new TextBox { Text = settings.Host, Left = 150, Top = 14, Width = 190 };
        var hw = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = settings.HwmonPort, Left = 150, Top = 46, Width = 90 };
        var hy = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = settings.HonyakuPort, Left = 150, Top = 78, Width = 90 };
        var hide = new CheckBox { Text = "背景タスクの和訳を出さない", Checked = settings.HideTasks, Left = 16, Top = 112, Width = 320 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 180, Top = 148, Width = 80 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 266, Top = 148, Width = 80 };
        f.Controls.AddRange([
            new Label { Text = "llmsv のアドレス", Left = 16, Top = 17, Width = 130 }, host,
            new Label { Text = "モニタのポート", Left = 16, Top = 49, Width = 130 }, hw,
            new Label { Text = "翻訳の画面のポート", Left = 16, Top = 81, Width = 130 }, hy,
            hide, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        settings.Host = host.Text.Trim();
        settings.HwmonPort = (int)hw.Value;
        settings.HonyakuPort = (int)hy.Value;
        settings.HideTasks = hide.Checked;
        settings.Save();
        ApplyTranslation();
        _ = RefreshNow();
        _ = RefreshSlow(force: true);
    }
}
