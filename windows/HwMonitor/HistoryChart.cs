using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace HwMonitor;

/// <summary>履歴の折れ線グラフ。上の段で期間 (1 時間〜30 日) と項目 (電力・温度・使用率・メモリ) を選ぶ。
/// 凡例を押すとその線を隠す / 出す。マウスを乗せた時刻の値を出す。トークンは生成・読込・文脈 (枠が使っている量) の 3 段に分ける。
/// 「なめらか」で、電力・使用率・トークン (生成・読込) の線を前後の平均で均して描く</summary>
[DesignerCategory("Code")]
public sealed class HistoryChart : Control
{
    public static readonly (string key, string label)[] Ranges =
        [("2m", "120秒"), ("10m", "10分"), ("30m", "30分"), ("1h", "1時間"), ("3h", "3時間"), ("6h", "6時間"), ("24h", "24時間"), ("7d", "7日"), ("30d", "30日")];
    public static readonly (string key, string label, string unit)[] Groups =
        [("p", "電力", "W"), ("t", "温度", "℃"), ("u", "使用率", "%"), ("m", "メモリ", "GB"), ("r", "トークン", "t/s")];

    const int BarH = 26, LegendH = 20, PadL = 52, PadR = 14, PadB = 22;
    readonly List<(Rectangle r, Action act)> buttons = [];
    readonly List<(Rectangle r, string key)> legend = [];
    readonly HashSet<string> hidden = [];
    // 部品の名前 (キーの頭文字 "t:" などを除いた部分) -> 色。データを読むたびに全タブぶんまとめて決め直すので、
    // 同じ部品はどのタブでも同じ色、どのタブを先に開いても同じ色になる
    readonly Dictionary<string, Color> colors = [];
    readonly Dictionary<string, int> order = [];   // 部品の名前 -> 凡例の並び (全タブ共通)
    readonly List<string> rServers = [];   // トークンのサーバー (現れた順)。読込 青・生成 橙・文脈 緑、2 つ目のサーバーは次の 3 色
    HistoryData? data;
    string? error;
    int hoverX = -1;

    public event EventHandler? SelectionChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Range { get; set; } = "1h";
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Group { get; set; } = "p";
    /// <summary>OpenCode が圧縮を始める余裕 (k トークン)。枠の上限 − これ の高さに 3 段目の圧縮ラインを引く</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CompactReserveK { get; set; } = 20;
    /// <summary>なめらかにしている項目 (Groups の key)。項目ごとに覚える。線を前後 SmoothWindow 秒の平均で均して描く (描くときだけ。記録や電気代は変えない)</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HashSet<string> Smooth { get; set; } = [];
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HistoryData? Data { get => data; set { data = value; ApplyLive(value); FillCtx(value); AssignColors(value); error = null; Invalidate(); } }

    // 枠が使っている量 (文脈) をアプリが見た分だけ覚えておく (/api/now の 2 秒ごと、24 時間まで)。
    // hwmon.py が記録していない間 (反映前・起動し直す前) は、これで 3 段目を描く。記録があればそちらを使い、無い刻みだけ埋める
    readonly List<(double t, Dictionary<string, double> v)> ctxSeen = [];
    double ctxLimit;   // 枠の上限 (k トークン、いちばん大きい枠)。3 段目に線を引く

    public void AddSlots(NowData now)
    {
        var v = new Dictionary<string, double>();
        double lim = 0;
        foreach (var srv in now.Slots)
            foreach (var s in srv.Slots)
            {
                lim = Math.Max(lim, s.NCtx / 1000.0);
                if (s.Used > 0) v[CtxKey(srv.Server, s.Id)] = Math.Round(s.Used / 1000.0, 1);   // hwmon.py と同じ (使ったことのある枠だけ)
            }
        if (lim > 0) ctxLimit = lim;
        if (v.Count == 0 || now.Ts <= 0) return;
        ctxSeen.Add((now.Ts, v));
        int old = ctxSeen.FindIndex(x => x.t >= now.Ts - 86400);
        if (old > 0) ctxSeen.RemoveRange(0, old);
    }

    // 枠のメーターが増えた分を光らせた所 (読込・生成が終わった時刻と量)。文脈の段に小さな ● で置く (アプリが見た分だけ、24 時間まで)
    readonly List<(double t, string key, bool read, long from, long to)> marks = [];
    const long MarkMaxRange = 6 * 3600;   // これより長い期間では ● が詰まるので出さない

    public void AddMark(string server, int id, bool read, long from, long to, double ts)
    {
        if (ts <= 0) return;
        marks.Add((ts, CtxKey(server, id), read, from, to));
        int old = marks.FindIndex(x => x.t >= ts - 86400);
        if (old > 0) marks.RemoveRange(0, old);
    }

    static string CtxKey(string server, int id) => $"r:{server} 枠{id} 文脈";
    static bool IsCtx(string key) => key.EndsWith(" 文脈");

    // 見た値の無い刻みを、直前に見た値で埋めてよい間隔 (秒)。/api/now は 2 秒ごとだが、タイマーのずれや応答待ちで
    // 2 秒の刻みに 1 つも入らないことがあり、そこで線が切れて欠けて見える。使っている量は段のように動くので前の値のままでよい
    double CtxHoldMax => Math.Max(12, NowIntervalSec * 2.5);
    /// <summary>アプリが /api/now を取りに行く間隔 (秒、上の帯の − ＋)。見た値で埋める間隔の上限に使う</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int NowIntervalSec { get; set; } = 2;

    void FillCtx(HistoryData? d)
    {
        if (d == null || d.T.Count == 0 || ctxSeen.Count == 0) return;
        var index = new Dictionary<long, int>();
        for (int i = 0; i < d.T.Count; i++) index[d.T[i]] = i;
        var sums = new Dictionary<string, (double[] sum, int[] n)>();
        (double[] sum, int[] n) Acc(string k) =>
            sums.TryGetValue(k, out var a) ? a : sums[k] = (new double[d.T.Count], new int[d.T.Count]);
        foreach (var (t, v) in ctxSeen)
        {
            if (!index.TryGetValue((long)t / d.Step * d.Step, out int i)) continue;   // 記録の刻みに入らない所は捨てる
            foreach (var (k, x) in v)
            {
                var a = Acc(k);
                a.sum[i] += x;
                a.n[i]++;
            }
        }
        // 1 つも見ていない刻みは、その刻みの終わりより前に最後に見た値で埋める。
        // ただし前後に見た時刻が CtxHoldMax 秒より空いていたら (アプリを閉じていた・つながらなかった) 埋めずに線を切る
        var hold = new Dictionary<string, double?[]>();
        int j = -1;
        for (int i = 0; i < d.T.Count; i++)
        {
            long end = d.T[i] + d.Step;
            while (j + 1 < ctxSeen.Count && ctxSeen[j + 1].t < end) j++;
            if (j < 0) continue;
            var (pt, pv) = ctxSeen[j];
            double next = j + 1 < ctxSeen.Count ? ctxSeen[j + 1].t : Math.Max(d.To, d.T[i]);   // 右端はいまの時刻まで
            if (next - pt > CtxHoldMax) continue;
            foreach (var (k, x) in pv)
            {
                if (!hold.TryGetValue(k, out var h)) hold[k] = h = new double?[d.T.Count];
                h[i] = x;
                Acc(k);
            }
        }
        foreach (var (k, (sum, n)) in sums)
        {
            if (!d.Series.TryGetValue(k, out var vals)) d.Series[k] = vals = Enumerable.Repeat<double?>(null, d.T.Count).ToList();
            hold.TryGetValue(k, out var h);
            for (int i = 0; i < d.T.Count && i < vals.Count; i++)
                if (vals[i] == null)
                    vals[i] = n[i] > 0 ? Math.Round(sum[i] / n[i], 1) : h?[i];
        }
    }

    // 読み込みの最中の見込みで、右端 (まだ数が増えていない刻み) を埋める。見込みより記録が大きければ記録のまま
    static void ApplyLive(HistoryData? d)
    {
        if (d == null) return;
        foreach (var (key, list) in d.Live)
        {
            if (!d.Series.TryGetValue(key, out var vals)) continue;
            for (int i = 0; i < d.T.Count && i < vals.Count; i++)
            {
                double end = d.T[i] + d.Step, est = 0;
                foreach (var lv in list)   // 読んでいる枠ごとの見込みを足す (枠ごとの速さは GPU を分け合ったあとの値)
                    if (end > lv.From) est += lv.Rate * Math.Min(1, (end - lv.From) / d.Step);   // 読み始めが刻みの途中なら、その割合だけ
                if (est > 0 && (vals[i] ?? 0) < est) vals[i] = Math.Round(est, 1);
            }
        }
    }

    // なめらかにする項目: 電力・使用率・トークン。温度はもともとなめらか、メモリと文脈は段のように動くので均さない
    static readonly HashSet<string> SmoothGroups = ["p", "u", "r"];
    bool SmoothOn => Smooth.Contains(Group) && SmoothGroups.Contains(Group);

    // 平均する幅 (秒): 表示している期間の 1/20 (2 分なら 6 秒、10 分なら 30 秒、1 時間なら 3 分、24 時間なら 72 分)。少なくとも記録 3 つ分
    long SmoothWindow => data == null ? 0 : Math.Max((data.To - data.From) / 20, data.Step * 3L);

    static string SpanText(long sec) => sec < 60 ? $"{sec}秒" : sec < 3600 ? $"{sec / 60.0:0.#}分" : $"{sec / 3600.0:0.#}時間";

    // 描く値。なめらかのときは、その時刻の前後 SmoothWindow / 2 秒にある記録の平均 (記録の無い所・途切れた所はまたがない。端は片側だけの平均)
    List<double?> View(string key, long gap)
    {
        var vals = data!.Series[key];
        if (!SmoothOn || IsCtx(key)) return vals;
        int n = Math.Min(vals.Count, data.T.Count);
        var outv = Enumerable.Repeat<double?>(null, vals.Count).ToList();
        long half = SmoothWindow / 2;
        for (int i = 0; i < n; i++)
        {
            if (vals[i] is null) continue;
            int s = i;   // 途切れていない区間 s〜i
            while (i + 1 < n && vals[i + 1] is double && data.T[i + 1] - data.T[i] <= gap) i++;
            int lo = s, hi = s;
            double sum = 0;
            for (int j = s; j <= i; j++)
            {
                while (hi <= i && data.T[hi] - data.T[j] <= half) sum += vals[hi++]!.Value;
                while (data.T[j] - data.T[lo] > half) sum -= vals[lo++]!.Value;
                outv[j] = Math.Round(sum / (hi - lo), 1);
            }
        }
        return outv;
    }

    public void ShowError(string message) { error = message; Invalidate(); }

    /// <summary>0 でない所だけの平均 (止まっている間を除いた、動いているときの速さ)。▶AVE に使う</summary>
    public static double? NonZeroAverage(IEnumerable<double?> vals)
    {
        var nz = vals.Where(v => v is > 0).Select(v => v!.Value).ToList();
        return nz.Count == 0 ? null : nz.Average();
    }

    public HistoryChart()
    {
        DoubleBuffered = true;
        BackColor = Theme.Back;
    }

    static string NameOf(string key) => key[2..];
    // "V100 (2)" → "V100"、"V100-2" → "V100"（[gpu] labels でカードに付けた名前。2026-10-19）
    static string BaseOf(string name) => Regex.Replace(name, @"( \(\d+\)|-\d+)$", "");
    static int Rank(string name) => name is "全体" or "RAM" ? 0 : name == "CPU" ? 1 : 2;

    List<string> Keys()
    {
        if (data == null) return [];
        var keys = data.Series.Keys.Where(k => k.StartsWith(Group + ":")).ToList();
        if (Group != "r") return keys.OrderBy(k => order.GetValueOrDefault(NameOf(k), int.MaxValue)).ToList();
        // トークンは凡例・マウスを乗せたときの値を段の順 (生成・読込・文脈) に並べる
        return keys.OrderBy(k => k.EndsWith(" 読込") ? 1 : IsCtx(k) ? 2 : 0).ToList();
    }

    /// <summary>部品の名前ごとに、凡例の並び (order) と色 (colors) を全タブ共通で決める。電力・温度・使用率・メモリの全部の記録から名前を集める。
    /// 並び: 全体・RAM → CPU → ほかは記録に現れた順。「V100 (2)」のような同じ型の 2 枚目は、記録に後から現れても 1 枚目のすぐ後ろ。
    /// 色: CPU を先頭 (Theme.Series の 1 色目) にして、あとは並びの順に当てる。全体は白なので数えない</summary>
    void AssignColors(HistoryData? d)
    {
        colors.Clear();
        order.Clear();
        if (d == null) return;
        var names = d.Series.Keys.Where(k => !k.StartsWith("r:")).Select(NameOf).Distinct().ToList();
        var seen = new Dictionary<string, int>();   // 型名 -> 記録に現れた順
        foreach (var n in names) seen.TryAdd(BaseOf(n), seen.Count);
        names = names.OrderBy(Rank).ThenBy(n => seen[BaseOf(n)]).ThenBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
        for (int i = 0; i < names.Count; i++) order[names[i]] = i;
        int c = 0;
        foreach (var n in names.Where(n => n != "全体").OrderBy(n => n == "CPU" ? 0 : 1)) colors[n] = Theme.Series[c++ % Theme.Series.Length];
    }

    Color ColorOf(string key)
    {
        if (key == "p:全体") return Color.White;
        if (key.StartsWith("r:"))
        {
            // トークン: 読込 青・生成 橙・文脈 緑 (サーバーが増えたら次の 3 色)。スマホ版と同じ
            string srv = NameOf(key).Split(' ')[0];
            int si = rServers.IndexOf(srv);
            if (si < 0) { rServers.Add(srv); si = rServers.Count - 1; }
            int kind = key.EndsWith(" 読込") ? 0 : IsCtx(key) ? 2 : 1;
            return Theme.Series[(si * 3 + kind) % Theme.Series.Length];
        }
        string name = NameOf(key);
        if (!colors.TryGetValue(name, out var c)) colors[name] = c = Theme.Series[colors.Count % Theme.Series.Length];
        return c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        buttons.Clear();
        legend.Clear();
        // 上の段: 期間と項目のボタン
        int x = 8;
        foreach (var (key, label) in Ranges)
            x = Button(g, x, label, Range == key, () => { Range = key; SelectionChanged?.Invoke(this, EventArgs.Empty); }) + 2;
        x += 16;
        foreach (var (key, label, _) in Groups)
            x = Button(g, x, label, Group == key, () => { Group = key; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }) + 2;
        if (SmoothGroups.Contains(Group))
            Button(g, x + 14, SmoothOn && data != null ? $"なめらか {SpanText(SmoothWindow)}" : "なめらか", SmoothOn,
                () => { if (!Smooth.Remove(Group)) Smooth.Add(Group); Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); });

        var plot = new Rectangle(PadL, BarH + LegendH + 6, Width - PadL - PadR, Height - BarH - LegendH - 6 - PadB);
        if (plot.Width < 40 || plot.Height < 30) return;
        if (data == null || data.T.Count == 0)
        {
            TextRenderer.DrawText(g, error ?? (data == null ? "読み込み中…" : "まだ記録がありません (1 分ごとに貯まります)"),
                Theme.Ui, plot, error != null ? Theme.Warn : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var keys = Keys();
        // 凡例
        int lx = PadL;
        for (int i = 0; i < keys.Count; i++)
        {
            string name = keys[i][2..];
            var col = ColorOf(keys[i]);
            var sz = TextRenderer.MeasureText(name, Theme.Small);
            var r = new Rectangle(lx, BarH + 2, sz.Width + 18, LegendH - 2);
            bool off = hidden.Contains(keys[i]);
            using (var b = new SolidBrush(off ? Theme.Grid : col)) g.FillRectangle(b, r.X, r.Y + 6, 12, 4);
            TextRenderer.DrawText(g, name, Theme.Small, new Point(r.X + 15, r.Y + 1), off ? Theme.Grid : Theme.Ink);
            string k = keys[i];
            legend.Add((r, k));
            lx = r.Right + 8;
        }

        var unit = Groups.First(x => x.key == Group).unit;
        var shown = keys.Where(k => !hidden.Contains(k)).ToList();
        long t0 = data.From, t1 = data.To;
        float X(long t) => plot.X + (float)((t - t0) / (double)Math.Max(1, t1 - t0) * plot.Width);

        // 段ごとに、目盛り (横線と値・時刻の縦線) と線を描く。目盛りの上限は段に出ている線だけで決める
        using var gridPen = new Pen(Theme.Grid);
        long gap = data.MaxGap > 0 ? data.MaxGap : (long)(data.Step * 2.5);   // 途中で記録が無い所 (null や max_gap より空いた所) は線を切る
        var view = shown.ToDictionary(k => k, k => View(k, gap));
        var panes = Panes(shown, plot);
        foreach (var (pr, pkeys, title) in panes)
        {
            bool ctx = pkeys.Count > 0 && pkeys.All(IsCtx);   // 3 段目 (文脈、k トークン)
            string pu = ctx ? "k" : unit;
            double max = 0;
            foreach (var k in pkeys) foreach (var v in view[k]) if (v is double d && d > max) max = d;
            if (ctx) max = Math.Max(max, ctxLimit);   // 枠の上限まで入るように
            double top = Group == "u" ? 100 : Nice(max * (ctx ? 1.0 : 1.08));
            if (top <= 0) top = 1;
            float Y(double v) => pr.Bottom - (float)(v / top * pr.Height);

            // 横線の数は、上限の頭の数字で決めて目盛りを切りのよい値にする:
            // 2.5・5 は 5 等分 (10・20…50)、1.5・3・6 は 3 等分 (20・40・60)、1・2・4・8 は 4 等分 (低い段は 2 等分)
            double mant = top / Math.Pow(10, Math.Floor(Math.Log10(top)));
            bool Is(params double[] ms) => ms.Any(m => Math.Abs(mant - m) < 1e-9);
            int div = Is(2.5, 5) ? 5 : Is(1.5, 3, 6) ? 3 : pr.Height >= 120 ? 4 : 2;
            for (int i = 0; i <= div; i++)
            {
                // 2 段目からは一番上の値を出さない (上の段の 0 と重なるので)
                if (i == div && pr.Y > plot.Y) continue;   // 上の線は段の枠で描かれる
                double v = top * i / div;
                float y = Y(v);
                g.DrawLine(gridPen, pr.X, y, pr.Right, y);
                TextRenderer.DrawText(g, v.ToString(v >= 10 || v == 0 ? "0" : "0.#") + pu, Theme.Small,
                    new Rectangle(0, (int)y - 8, PadL - 4, 16), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            // 文脈の段は、OpenCode が圧縮を始める高さ (枠の上限 − 予約) に線を引く。
            // OpenCode は「使っている量 ≥ 枠の上限 − 予約 (既定 2 万)」で古いやり取りをまとめ直す
            if (ctx && ctxLimit > 0)
            {
                double compact = Math.Max(0, ctxLimit - CompactReserveK);
                using (var p = new Pen(Color.FromArgb(160, Theme.Warn)) { DashStyle = DashStyle.Dash }) g.DrawLine(p, pr.X, Y(compact), pr.Right, Y(compact));
                string lt = $"圧縮 {compact:0}k";
                var lsz = TextRenderer.MeasureText(lt, Theme.Small);
                var lp = new Point(pr.Right - lsz.Width - 4, (int)Y(compact) - lsz.Height);
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Back))) g.FillRectangle(b, lp.X - 2, lp.Y, lsz.Width + 4, lsz.Height);
                TextRenderer.DrawText(g, lt, Theme.Small, lp, Theme.Warn);
            }
            foreach (var (t, _) in TimeTicks(t0, t1)) g.DrawLine(gridPen, X(t), pr.Y, X(t), pr.Bottom);
            using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, pr);
            if (title != null)   // 後ろを塗って、目盛りや上限の線と重なっても読めるように
            {
                var tsz = TextRenderer.MeasureText(title, Theme.Small);
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Back))) g.FillRectangle(b, pr.X + 1, pr.Y + 1, tsz.Width + 6, tsz.Height + 3);
                TextRenderer.DrawText(g, title, Theme.Small, new Point(pr.X + 4, pr.Y + 3), Theme.Muted);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var k in pkeys)
            {
                using var pen = new Pen(ColorOf(k), k == "p:全体" ? 2f : 1.4f);
                var vals = view[k];
                var pts = new List<PointF>();
                long prev = 0;
                for (int i = 0; i < data.T.Count; i++)
                {
                    if (vals[i] is not double v || (pts.Count > 0 && data.T[i] - prev > gap))
                    {
                        Flush(g, pen, pts);
                        if (vals[i] is not double) continue;
                    }
                    pts.Add(new PointF(X(data.T[i] + data.Step / 2), Y(vals[i]!.Value)));
                    prev = data.T[i];
                }
                Flush(g, pen, pts);
            }
            // 文脈の段に、枠のメーターが光った所 (読込の終わり = 青・生成の終わり = 橙) を目立たない小さな ● で
            if (ctx && t1 - t0 <= MarkMaxRange)
                foreach (var m in marks)
                {
                    if (m.t < t0 || m.t > t1 || !pkeys.Contains(m.key)) continue;
                    using var b = new SolidBrush(Color.FromArgb(150, m.read ? Theme.Series[0] : Theme.Series[1]));
                    g.FillEllipse(b, X((long)m.t) - 2.5f, Y(m.to / 1000.0) - 2.5f, 5, 5);
                }
            g.SmoothingMode = SmoothingMode.None;

            // トークンは、0 でない所だけの平均 (止まっている間を除いた、動いているときの速さ) を軸のところに ▶AVE で出す。
            // なめらかでも元の値で (均すと止まっている間の 0 が小さな値になって平均が下がる)
            if (Group == "r" && !ctx)
                foreach (var k in pkeys)
                {
                    if (NonZeroAverage(data.Series[k]) is not double avg) continue;
                    // 文字が段からはみ出さないように寄せる。段が 16px より低いと Math.Clamp は例外になるので Min・Max で
                    float y = Math.Min(Math.Max(Y(avg), pr.Y + 8), pr.Bottom - 8);
                    var col = ColorOf(k);
                    using (var p = new Pen(Color.FromArgb(110, col)) { DashStyle = DashStyle.Dash }) g.DrawLine(p, pr.X, Y(avg), pr.Right, Y(avg));
                    string text = $"AVE {avg:0.#}{unit}";
                    var sz = TextRenderer.MeasureText(text, Theme.Small);
                    var box = new Rectangle(pr.X + 10, (int)y - sz.Height / 2, sz.Width, sz.Height);
                    using (var b = new SolidBrush(Color.FromArgb(220, Theme.Back))) g.FillRectangle(b, box);
                    using (var b = new SolidBrush(col)) g.FillPolygon(b, [new PointF(pr.X + 1, y - 5), new PointF(pr.X + 9, y), new PointF(pr.X + 1, y + 5)]);
                    TextRenderer.DrawText(g, text, Theme.Small, box.Location, col);
                }
        }
        // 時刻は一番下の段の下に 1 回だけ
        foreach (var (t, label) in TimeTicks(t0, t1))
            TextRenderer.DrawText(g, label, Theme.Small, new Point((int)X(t) - 20, plot.Bottom + 4), Theme.Muted);

        // マウスを乗せた時刻の値
        if (hoverX >= plot.X && hoverX <= plot.Right)
        {
            long th = t0 + (long)((hoverX - plot.X) / (double)plot.Width * (t1 - t0));
            int idx = 0;
            for (int i = 0; i < data.T.Count; i++) if (Math.Abs(data.T[i] + data.Step / 2 - th) < Math.Abs(data.T[idx] + data.Step / 2 - th)) idx = i;
            // 近くに記録が無い所 (記録を始める前など) では値を出さない
            if (Math.Abs(data.T[idx] + data.Step / 2 - th) > gap) return;
            float hx = X(data.T[idx] + data.Step / 2);
            using (var p = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot })
                foreach (var (pr, _, _) in panes) g.DrawLine(p, hx, pr.Y, hx, pr.Bottom);
            var lines = new List<(string, Color)> { (FormatTime(data.T[idx] + data.Step / 2, t1 - t0) + (SmoothOn ? $"  ({SpanText(SmoothWindow)}の平均)" : ""), Theme.Head) };
            foreach (var k in shown)
                if (idx < view[k].Count && view[k][idx] is double v) lines.Add(($"{k[2..]}  {v:0.#}{(IsCtx(k) ? "k" : unit)}", ColorOf(k)));
            // その刻みに入る ● (光った所) の増えた分
            if (Group == "r" && t1 - t0 <= MarkMaxRange)
                foreach (var m in marks.Where(m => m.t >= data.T[idx] && m.t < data.T[idx] + data.Step && shown.Contains(m.key)).TakeLast(4))
                    lines.Add(($"● {(m.read ? "読込" : "生成")} +{(m.to - m.from) / 1000.0:0.0}k ({m.from / 1000.0:0.0}k → {m.to / 1000.0:0.0}k)",
                               m.read ? Theme.Series[0] : Theme.Series[1]));
            int bw = lines.Max(l => TextRenderer.MeasureText(l.Item1, Theme.Small).Width) + 12, bh = lines.Count * 15 + 8;
            int bx = hx + 12 + bw > plot.Right ? (int)hx - 12 - bw : (int)hx + 12;
            var box = new Rectangle(bx, plot.Y + 6, bw, bh);
            using (var b = new SolidBrush(Color.FromArgb(230, Theme.Panel))) g.FillRectangle(b, box);
            using (var p = new Pen(Theme.Edge)) g.DrawRectangle(p, box);
            for (int i = 0; i < lines.Count; i++)
                TextRenderer.DrawText(g, lines[i].Item1, Theme.Small, new Point(box.X + 6, box.Y + 4 + i * 15), lines[i].Item2);
        }
    }

    const int PaneGap = 10;

    /// <summary>グラフの段。トークンは生成 (上・高さ 2/4)・読込 (1/4)・文脈 (1/4) に分け、段ごとの目盛りで描く。
    /// 読込は何百 t/s の山になるので、同じ目盛りだと数十 t/s の生成がつぶれる。文脈は単位が違う (k トークン)。
    /// 凡例で段の線を全部隠すと、残りの段が全体を使う</summary>
    List<(Rectangle r, List<string> keys, string? title)> Panes(List<string> shown, Rectangle plot)
    {
        var parts = new List<(List<string> keys, string? title, int weight)>();
        if (Group == "r")
        {
            var gen = shown.Where(k => !k.EndsWith(" 読込") && !IsCtx(k)).ToList();
            var read = shown.Where(k => k.EndsWith(" 読込")).ToList();
            var ctx = shown.Where(IsCtx).ToList();
            if (gen.Count > 0) parts.Add((gen, "生成", 2));
            if (read.Count > 0) parts.Add((read, "読込", 1));
            if (ctx.Count > 0) parts.Add((ctx, "文脈 (枠が使っている量)", 1));
        }
        if (parts.Count == 0) parts.Add((shown, null, 1));
        if (parts.Count == 1) return [(plot, parts[0].keys, parts[0].title)];

        var list = new List<(Rectangle, List<string>, string?)>();
        int usable = plot.Height - PaneGap * (parts.Count - 1), sum = parts.Sum(p => p.weight), y = plot.Y;
        for (int i = 0; i < parts.Count; i++)
        {
            int h = i == parts.Count - 1 ? plot.Bottom - y : usable * parts[i].weight / sum;
            list.Add((new Rectangle(plot.X, y, plot.Width, h), parts[i].keys, parts[i].title));
            y += h + PaneGap;
        }
        return list;
    }

    static void Flush(Graphics g, Pen pen, List<PointF> pts)
    {
        if (pts.Count >= 2) g.DrawLines(pen, pts.ToArray());
        else if (pts.Count == 1) g.DrawEllipse(pen, pts[0].X - 1, pts[0].Y - 1, 2, 2);
        pts.Clear();
    }

    int Button(Graphics g, int x, string text, bool on, Action act)
    {
        var sz = TextRenderer.MeasureText(text, Theme.Ui);
        var r = new Rectangle(x, 3, sz.Width + 12, BarH - 6);
        using (var b = new SolidBrush(on ? Theme.Accent : Theme.Panel)) g.FillRectangle(b, r);
        using (var p = new Pen(on ? Theme.Good : Theme.Edge)) g.DrawRectangle(p, r);
        TextRenderer.DrawText(g, text, Theme.Ui, r, on ? Theme.Head : Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        buttons.Add((r, act));
        return r.Right;
    }

    internal static double Nice(double v)
    {
        if (v <= 0) return 0;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (m * p >= v) return m * p;   // 刻みを細かくして、上の空きを小さく
        return 10 * p;
    }

    static DateTime Local(long t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

    static string FormatTime(long t, long span) =>
        span <= 600 ? Local(t).ToString("HH:mm:ss") : span <= 86400 ? Local(t).ToString("HH:mm") : Local(t).ToString("M/d HH:mm");

    // 時刻の目盛り: 期間に合わせて切りのよい間隔で
    static IEnumerable<(long, string)> TimeTicks(long t0, long t1)
    {
        long span = t1 - t0;
        long step = span <= 120 ? 20 : span <= 600 ? 60 : span <= 1800 ? 300 : span <= 3600 ? 600 : span <= 3 * 3600 ? 1800 : span <= 6 * 3600 ? 3600 : span <= 86400 ? 4 * 3600 : span <= 7 * 86400 ? 86400 : 5 * 86400;
        long offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;  // 日付の区切りを日本時間の 0 時に
        for (long t = ((t0 + offset) / step + 1) * step - offset; t < t1; t += step)
            yield return (t, step >= 86400 ? Local(t).ToString("M/d") : step < 60 ? Local(t).ToString("HH:mm:ss") : Local(t).ToString("HH:mm"));
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hoverX = e.X; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (r, act) in buttons) if (r.Contains(e.Location)) { act(); Invalidate(); return; }
        foreach (var (r, key) in legend)
            if (r.Contains(e.Location)) { if (!hidden.Remove(key)) hidden.Add(key); Invalidate(); return; }
    }
}
