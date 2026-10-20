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
    readonly HonyakuFeed feed;
    readonly SlotPanel slots;
    readonly CostPanel cost = new() { Dock = DockStyle.Fill, Visible = false };
    readonly HistoryChart chart = new() { Dock = DockStyle.Fill };
    readonly EnergyPanel energy = new() { Dock = DockStyle.Fill, Visible = false };
    readonly SpeedPanel speed = new() { Dock = DockStyle.Fill, Visible = false };
    readonly TranslationLogPanel transLog;
    readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Theme.Back };
    readonly ToolStrip bar = new() { GripStyle = ToolStripGripStyle.Hidden, Renderer = new DarkRenderer(), BackColor = Theme.Panel, ForeColor = Theme.Ink, Padding = new Padding(4, 2, 4, 2) };
    readonly ToolStripButton tabGraph = new("グラフ") { CheckOnClick = false };
    readonly ToolStripButton tabEnergy = new("電気代") { CheckOnClick = false };
    readonly ToolStripButton tabCost = new("コスト比較") { CheckOnClick = false, ToolTipText = "電気代と、同じだけ推論したときのレンタル (GPU の時間貸し・API の従量) を比べる" };
    readonly ToolStripButton tabSpeed = new("速度と文脈") { CheckOnClick = false,
        ToolTipText = "要求ごとの生成・読み込みの速さを、文脈の量に対して点で描く (llama-server のログから)。\n設定 (モデル・枠の数) を変えたときの比べや、文脈が長くなるとどれだけ遅くなるかを見る" };
    readonly ToolStripButton tabLog = new("翻訳ログ") { CheckOnClick = false,
        ToolTipText = "Hermes / OpenCode の思考を OpenCode 風にブロックで出す (仮)。\nターンごとに 思考 (英文＋和訳)・コード・実行したコマンド・応答 を並べる。右クリックで原文の併記・コピー" };
    readonly ToolStripButton deviceView = new("部品別") { CheckOnClick = true,
        ToolTipText = "上のメーターを部品別 (CPU・GPU ごとに 1 行、スレッドごとの使用率) にする。\n押していないときは項目別 (温度・電力・使用率・メモリの 4 列)" };
    // 更新の間隔 (btop の − 2000ms ＋ と同じ)。hwmon.py が 2 秒ごとに読んでいるので 2 秒より短くはしない
    static readonly int[] Intervals = [2, 5, 10, 30, 60];
    readonly ToolStripButton slower = new("−") { ToolTipText = "更新の間隔を長くする" };
    readonly ToolStripLabel intervalLabel = new() { ToolTipText = "上のメーター・枠と、グラフ (120 秒・10 分) を取りに行く間隔" };
    readonly ToolStripButton faster = new("＋") { ToolTipText = "更新の間隔を短くする (2 秒まで)" };
    readonly ToolStripButton topMost = new("最前面") { CheckOnClick = true };
    readonly ToolStripButton autoDeepL = new("DeepL") { CheckOnClick = true,
        ToolTipText = "押している間、新しく届いた英文 (訳されていない区切り) を DeepL で訳す。\n押していなくても、英文の行をクリックすればその区切りだけ訳す。\nキーは「設定…」で入れる" };
    readonly ToolStripLabel deeplUsage = new() { Alignment = ToolStripItemAlignment.Right };
    readonly DeepLService deepl;
    int usageTicks;
    readonly ToolStripLabel status = new() { Alignment = ToolStripItemAlignment.Right };
    readonly System.Windows.Forms.Timer nowTimer = new() { Interval = 2000 };
    readonly System.Windows.Forms.Timer slowTimer = new() { Interval = 2000 };   // 履歴・電気代 (間隔は RefreshSlow で期間ごとに)
    DateTime lastHistory = DateTime.MinValue, lastEnergy = DateTime.MinValue, lastSpeed = DateTime.MinValue;
    bool busyNow, busyHist, busyEnergy, busySpeed;

    public MainForm(Settings settings)
    {
        this.settings = settings;
        api = new HwmonClient(() => settings.HwmonBase);
        feed = new HonyakuFeed(() => settings.HonyakuBase);
        transLog = new TranslationLogPanel(feed, () => settings.HideTasks) { Dock = DockStyle.Fill, Visible = false };
        deepl = new DeepLService(settings);
        transLog.DeepL = deepl;
        transLog.AutoTranslate = () => settings.AutoTranslate;
        transLog.AutoStopped += why => { autoDeepL.Checked = false; deeplUsage.Text = why + "  "; deeplUsage.ForeColor = Theme.Warn; };
        transLog.NeedKey += () => OpenSettings("DeepL で訳すには、DeepL API のキーを入れてください");
        transLog.ShowSource = settings.ShowSource;
        transLog.ShowSourceChanged += () => { settings.ShowSource = transLog.ShowSource; settings.Save(); };
        deepl.Changed += ShowUsage;
        slots = new SlotPanel(feed.Turns) { Dock = DockStyle.Top, Height = 0 };

        Text = "llmsv ハードウェアモニタ  " + VersionText();
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);   // exe に入れたアイコン (app.ico) をタイトルバーとタスクバーにも
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
        tabCost.Click += (_, _) => SetTab("cost");
        tabSpeed.Click += (_, _) => SetTab("speed");
        tabLog.Click += (_, _) => SetTab("log");
        deviceView.Checked = meters.DeviceView = settings.DeviceView;
        deviceView.CheckedChanged += (_, _) =>
        {
            meters.DeviceView = settings.DeviceView = deviceView.Checked;
            meters.Height = meters.WantedHeight();
            settings.Save();
        };
        slower.Click += (_, _) => StepInterval(+1);
        faster.Click += (_, _) => StepInterval(-1);
        ApplyInterval();
        topMost.Checked = settings.TopMost;
        topMost.CheckedChanged += (_, _) => { TopMost = settings.TopMost = topMost.Checked; settings.Save(); };
        autoDeepL.Checked = settings.AutoTranslate;
        autoDeepL.CheckedChanged += (_, _) =>
        {
            if (autoDeepL.Checked && !deepl.HasKey)
            {
                autoDeepL.Checked = false;
                OpenSettings("DeepL で訳すには、DeepL API のキーを入れてください");
                return;
            }
            settings.AutoTranslate = autoDeepL.Checked;
            settings.Save();
            if (autoDeepL.Checked) _ = deepl.RefreshUsage();
        };
        var setup = new ToolStripButton("設定…");
        setup.Click += (_, _) => OpenSettings();
        bar.Items.AddRange([tabGraph, tabEnergy, tabCost, tabSpeed, tabLog, new ToolStripSeparator(), deviceView, slower, intervalLabel, faster,
                            new ToolStripSeparator(), topMost, autoDeepL,
                            new ToolStripSeparator(), setup, status, deeplUsage]);

        // 真ん中: タブで切り替える 1 枚 (グラフ / 電気代 / コスト比較 / 速度と文脈 / 翻訳ログ)。見えているものだけ表示する
        content.Controls.Add(chart);
        content.Controls.Add(energy);
        content.Controls.Add(cost);
        content.Controls.Add(speed);
        content.Controls.Add(transLog);

        Controls.Add(content);
        Controls.Add(slots);
        Controls.Add(meters);
        Controls.Add(bar);
        meters.Height = meters.WantedHeight();

        chart.Range = settings.Range;
        chart.Group = settings.Group;
        chart.Smooth = [.. settings.SmoothOn];
        chart.CompactReserveK = slots.CompactReserveK = speed.CompactReserveK = settings.CompactReserveK;
        // 枠のメーターが増えた分を光らせたら、グラフの文脈の段にも ● を置く
        slots.Flashed += (server, id, read, from, to, ts) => { chart.AddMark(server, id, read, from, to, ts); if (chart.Visible) chart.Invalidate(); };
        speed.Y = settings.SpeedY;
        speed.ColorBy = settings.SpeedColor;
        speed.Days = settings.SpeedDays;
        speed.Hidden = [.. settings.SpeedHidden];
        speed.SelectionChanged += (_, _) =>
        {
            bool reload = settings.SpeedDays != speed.Days;
            settings.SpeedY = speed.Y;
            settings.SpeedColor = speed.ColorBy;
            settings.SpeedDays = speed.Days;
            settings.SpeedHidden = [.. speed.Hidden];
            settings.Save();
            if (reload) _ = RefreshSpeed(force: true);
        };
        chart.SelectionChanged += (_, _) =>
        {
            settings.Range = chart.Range;
            settings.Group = chart.Group;
            settings.SmoothOn = [.. chart.Smooth];
            settings.Save();
            lastHistory = DateTime.MinValue;
            _ = RefreshHistory();
        };

        nowTimer.Tick += async (_, _) => await RefreshNow();
        slowTimer.Tick += async (_, _) =>
        {
            await RefreshSlow();
            // DeepL の使用量は 5 分ごとに聞き直す (間はこちらで頼んだ分を足している)
            if (deepl.HasKey && ++usageTicks >= 150) { usageTicks = 0; await deepl.RefreshUsage(); }
        };
        Load += async (_, _) =>
        {
            TopMost = settings.TopMost;
            SetTab(settings.Tab);
            feed.Start();
            nowTimer.Start();
            slowTimer.Start();
            await RefreshNow();
            await RefreshSlow();
            await deepl.RefreshUsage();
        };
        FormClosing += (_, _) =>
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Bounds = [r.X, r.Y, r.Width, r.Height];
            settings.Save();
            feed.Stop();
        };
    }

    // 100 円までは小数 1 桁 (使い始めは数十銭なので ¥0 にしない)
    static string Yen(double v) => v >= 100 ? $"¥{v:#,0}" : $"¥{v:0.0}";

    void SetTab(string tab)
    {
        settings.Tab = tab is "energy" or "cost" or "speed" or "log" ? tab : "graph";
        settings.Save();
        chart.Visible = tabGraph.Checked = settings.Tab == "graph";
        energy.Visible = tabEnergy.Checked = settings.Tab == "energy";
        cost.Visible = tabCost.Checked = settings.Tab == "cost";
        speed.Visible = tabSpeed.Checked = settings.Tab == "speed";
        transLog.Visible = tabLog.Checked = settings.Tab == "log";
        _ = RefreshSlow(force: true);
    }

    // タイトルバーの版: csproj の <Version> と、ビルドしたときのコミット (SDK が InformationalVersion の + の後ろに入れる) の頭 7 桁。
    // 例「v1.1.0 (1d353f4)」。VS で pull してビルドし直したかを見分けられる
    static string VersionText()
    {
        string v = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? Application.ProductVersion;
        int plus = v.IndexOf('+');
        return plus < 0 ? "v" + v : $"v{v[..plus]} ({v[(plus + 1)..][..Math.Min(7, v.Length - plus - 1)]})";
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
            slots.Ts = now?.Ts ?? 0;
            slots.Data = now?.Slots ?? [];
            if (now != null) chart.AddSlots(now);   // 文脈の段 (hwmon.py が記録していない間は、アプリが見た分で描く)
            int sh = slots.WantedHeight();
            if (slots.Height != sh) slots.Height = sh;
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

    // 履歴: 120 秒・10 分は 2 秒ごと、30 分は 5 秒ごと、1 時間は 10 秒ごと、それより長い期間は 1 分ごと (更新の間隔より短くはしない)。
    // 電気代と速度と文脈は 1 分ごと
    async Task RefreshSlow(bool force = false)
    {
        int iv = settings.IntervalSec;
        var due = TimeSpan.FromSeconds(Math.Max(iv, settings.Range switch { "2m" or "10m" => 2, "30m" => 5, "1h" => 10, _ => 60 }));
        if (chart.Visible && (force || DateTime.Now - lastHistory >= due)) await RefreshHistory();
        if ((energy.Visible || cost.Visible) && (force || DateTime.Now - lastEnergy >= TimeSpan.FromSeconds(60))) await RefreshEnergy();
        await RefreshSpeed(force);
    }

    async Task RefreshSpeed(bool force = false)
    {
        if (!speed.Visible || busySpeed || (!force && DateTime.Now - lastSpeed < TimeSpan.FromSeconds(60))) return;
        busySpeed = true;
        try
        {
            speed.Data = await api.Speed(speed.Days);
            lastSpeed = DateTime.Now;
        }
        catch (Exception ex) { speed.ShowError("速度と文脈を読めません: " + ex.Message); }
        finally { busySpeed = false; }
    }

    // 更新の間隔を 1 段変える (dir = +1 で長く、-1 で短く)
    void StepInterval(int dir)
    {
        int i = Array.IndexOf(Intervals, settings.IntervalSec);
        if (i < 0) i = 0;
        settings.IntervalSec = Intervals[Math.Clamp(i + dir, 0, Intervals.Length - 1)];
        settings.Save();
        ApplyInterval();
    }

    void ApplyInterval()
    {
        int iv = Math.Max(2, settings.IntervalSec);
        nowTimer.Interval = iv * 1000;
        chart.NowIntervalSec = iv;
        intervalLabel.Text = $"{iv}秒";
        slower.Enabled = iv < Intervals[^1];
        faster.Enabled = iv > Intervals[0];
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
            var en = await api.Energy();
            energy.Data = en;
            cost.Data = en?.Compare;
            lastEnergy = DateTime.Now;
        }
        catch (Exception ex) { energy.ShowError("電気代を読めません: " + ex.Message); cost.ShowError("読めません: " + ex.Message); }
        finally { busyEnergy = false; }
    }

    // 上の帯の右: DeepL の今月の使用量 (上限の 8 割を超えたら黄)
    void ShowUsage()
    {
        if (!deepl.HasKey) { deeplUsage.Text = ""; return; }
        if (deepl.Error != "") { deeplUsage.Text = "DeepL: " + deepl.Error + "  "; deeplUsage.ForeColor = Theme.Warn; return; }
        if (deepl.Used < 0) { deeplUsage.Text = ""; return; }
        static string Man(long v) => $"{v / 10000.0:0.#}万";
        deeplUsage.Text = $"DeepL 今月 {Man(deepl.Used)}/{Man(deepl.Cap)}字  ";
        deeplUsage.ForeColor = deepl.Used >= deepl.Cap * 0.8 ? Theme.Warn : Theme.Ink;
    }

    void OpenSettings() => OpenSettings(null);

    void OpenSettings(string? message)
    {
        using var f = new Form
        {
            Text = "設定", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(360, 340), Font = Theme.Ui, TopMost = TopMost,
        };
        var host = new TextBox { Text = settings.Host, Left = 150, Top = 14, Width = 190 };
        var hw = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = settings.HwmonPort, Left = 150, Top = 46, Width = 90 };
        var hy = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = settings.HonyakuPort, Left = 150, Top = 78, Width = 90 };
        var hide = new CheckBox { Text = "背景タスクの和訳を出さない", Checked = settings.HideTasks, Left = 16, Top = 112, Width = 320 };
        // DeepL: キーは %APPDATA%\HwMonitor\settings.json にだけ置く。上限は万字 (0 = DeepL の上限まで)
        var key = new TextBox { Text = settings.DeepLKey, Left = 150, Top = 150, Width = 190, UseSystemPasswordChar = true };
        var cap = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = Math.Min(10000, settings.DeepLCap / 10000), Left = 150, Top = 182, Width = 90 };
        var note = new Label { Left = 16, Top = 214, Width = 330, Height = 32, ForeColor = SystemColors.GrayText,
            Text = "DeepL API Free (月 50 万字まで無料) のキー。上限 0 = DeepL の上限まで。\nキーはこの PC の設定ファイルにだけ保存します" };
        var reserve = new NumericUpDown { Minimum = 0, Maximum = 200, Value = Math.Clamp(settings.CompactReserveK, 0, 200), Left = 150, Top = 250, Width = 90 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 180, Top = 298, Width = 80 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 266, Top = 298, Width = 80 };
        f.Controls.AddRange([
            new Label { Text = "llmsv のアドレス", Left = 16, Top = 17, Width = 130 }, host,
            new Label { Text = "モニタのポート", Left = 16, Top = 49, Width = 130 }, hw,
            new Label { Text = "翻訳の画面のポート", Left = 16, Top = 81, Width = 130 }, hy,
            hide,
            new Label { Text = "DeepL API のキー", Left = 16, Top = 153, Width = 130 }, key,
            new Label { Text = "月の上限 (万字)", Left = 16, Top = 185, Width = 130 }, cap,
            note,
            new Label { Text = "圧縮の余裕 (k)", Left = 16, Top = 252, Width = 130 }, reserve,
            ok, cancel]);
        if (message != null) { f.Text = "設定 — " + message; key.Select(); }
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        settings.Host = host.Text.Trim();
        settings.HwmonPort = (int)hw.Value;
        settings.HonyakuPort = (int)hy.Value;
        settings.HideTasks = hide.Checked;
        settings.DeepLKey = key.Text.Trim();
        settings.DeepLCap = (long)cap.Value * 10000;
        settings.CompactReserveK = (int)reserve.Value;
        chart.CompactReserveK = slots.CompactReserveK = speed.CompactReserveK = settings.CompactReserveK;
        speed.Invalidate();
        _ = deepl.RefreshUsage();
        settings.Save();
        feed.Start();   // 接続先が変わったかもしれないのでつなぎ直す
        _ = RefreshNow();
        _ = RefreshSlow(force: true);
    }
}
