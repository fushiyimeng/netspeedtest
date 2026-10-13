using System.Text;

namespace NetSpeedTest;

enum VerdictLevel { Neutral, Good, Warning, Critical }

sealed class Stats
{
    public int Count { get; init; }
    public double Avg { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double P10 { get; init; }
    public double P50 { get; init; }
    public double P90 { get; init; }

    public static readonly Stats Empty = new();
}

static class Verdict
{
    /// <summary>毫秒显示。局域网网关就是亚毫秒往返，整数毫秒只能落到 0，写成 &lt;1 才不误导。</summary>
    public static string Ms(double v) => v < 0.5 ? "<1" : v.ToString("0.0");

    public static Stats StatsOf(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return Stats.Empty;
        var sorted = values.OrderBy(x => x).ToArray();
        return new Stats
        {
            Count = values.Count,
            Avg = values.Average(),
            Min = sorted[0],
            Max = sorted[^1],
            P10 = Pct(sorted, 0.10),
            P50 = Pct(sorted, 0.50),
            P90 = Pct(sorted, 0.90),
        };
    }

    private static double Pct(double[] sorted, double p)
        => sorted.Length == 0 ? 0 : sorted[(int)Math.Floor((sorted.Length - 1) * p)];

    /// <summary>判读逻辑照搬老脚本 net-test.ps1（套餐百分比 + 平均/峰值比），只是现在按"合计"算。</summary>
    public static (string Text, VerdictLevel Level) Judge(TestReport r)
    {
        var sb = new StringBuilder();

        if (r.Error is not null) return ("× " + r.Error, VerdictLevel.Critical);
        if (!r.AnyDown && !r.AnyUp) return ("这次没测吞吐，只有延迟。", VerdictLevel.Neutral);

        var level = VerdictLevel.Good;

        // 下载：按合计稳态均判套餐百分比
        if (r.AnyDown)
        {
            var all = r.Nics.SelectMany(n => n.Down).ToList();
            var st = StatsOf(all);
            double avg = r.TotalDownAvg, peak = st.Max;
            double sag = peak > 0 ? avg / peak : 1;

            if (r.PlanMbps > 0)
            {
                double ratio = avg / r.PlanMbps;
                sb.AppendLine($"下载合计 {avg:N0} Mbps = 套餐的 {ratio:P0}");
                if (ratio >= 1.0) sb.AppendLine("✓ 跑满甚至有富余（运营商给了余量）—— 这条线没问题。");
                else if (ratio >= 0.9) sb.AppendLine("✓ 基本跑满套餐 —— 正常。");
                else if (ratio >= 0.7) { sb.AppendLine("⚠ 只有套餐的七成上下 —— 有东西在拖后腿，值得查。"); level = VerdictLevel.Warning; }
                else { sb.AppendLine("× 连套餐的一半都不到 —— 确实有问题。"); level = VerdictLevel.Critical; }
            }
            else if (peak < 950)
            {
                sb.AppendLine("⚠ 峰值连 1G 都没到 —— 瓶颈可能更靠前（网口 / 网线 / 链路模式）。");
                level = VerdictLevel.Warning;
            }

            if (sag < 0.6) { sb.AppendLine($"⚠ 但稳不住：平均只有峰值的 {sag:P0}，锯齿很厉害 —— 上游在丢包/停顿。"); if (level == VerdictLevel.Good) level = VerdictLevel.Warning; }
            else if (sag < 0.8) { sb.AppendLine($"⚠ 抖动偏大：平均是峰值的 {sag:P0}。"); if (level == VerdictLevel.Good) level = VerdictLevel.Warning; }
            else sb.AppendLine($"✓ 而且很稳：平均是峰值的 {sag:P0}。");
        }

        if (r.AnyUp && r.UploadUrl.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("· 上传走的是 Cloudflare 国际线路，数字比你真实的上传带宽保守 —— 想准就把落点换成国内的。");

        // 多网卡：两块网卡如果挂在同一个路由器上，合计不会超过那条线的总带宽
        if (r.Nics.Count > 1)
            sb.AppendLine("· 多网卡合计：几块网卡如果走的是同一个上游，合计不会超过那条线的总带宽，只是把同一条管子分了。");

        // 满载延迟 vs 空闲延迟 —— 这就是 bufferbloat
        if (r.LoadedPings.Count > 0 && r.IdlePings.Count > 0)
        {
            var idleGw = r.IdlePings.FirstOrDefault(p => p.Label == "网关");
            var loadGw = r.LoadedPings.FirstOrDefault(p => p.Label == "网关");
            if (idleGw is not null && loadGw is not null && idleGw.Received > 0 && loadGw.Received > 0)
            {
                double growth = loadGw.AvgMs - idleGw.AvgMs;
                if (growth >= 50)
                {
                    sb.AppendLine($"⚠ 满载时网关延迟从 {Ms(idleGw.AvgMs)} ms 涨到 {Ms(loadGw.AvgMs)} ms —— 光猫/路由器在排队（bufferbloat），别人打游戏时你会卡。");
                    if (level == VerdictLevel.Good) level = VerdictLevel.Warning;
                }
                else if (growth >= 15)
                {
                    sb.AppendLine($"· 满载时延迟涨了 {growth:N0} ms（{Ms(idleGw.AvgMs)} → {Ms(loadGw.AvgMs)} ms），有点排队，但不算严重。");
                }
                else
                {
                    sb.AppendLine($"✓ 满载时延迟几乎没涨（{Ms(idleGw.AvgMs)} → {Ms(loadGw.AvgMs)} ms）—— 光猫/路由器扛得住满载。");
                }
            }
        }

        var (errors, discards) = r.Nics.Count > 0 ? NetInfo.RxBad(r.Nics[0].Nic) : (0L, 0L);
        if (errors > 0 || discards > 0)
            sb.AppendLine($"⚠ 网卡收包错误 {errors} / 丢弃 {discards} —— 物理层不干净，先查网线。");

        return (sb.ToString().TrimEnd(), level);
    }

    /// <summary>
    /// 整份报告文本 —— CLI 输出和界面的「复制结果」共用。
    /// 界面自己有一块判读面板，所以它传 includeVerdict: false，免得同一段话显示两遍；
    /// 存进历史的报告用 true，这样历史条目是自包含的。
    /// </summary>
    public static string BuildText(TestReport r, bool includeVerdict = true)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"网速测试 · {r.TestsText} · 下载的数据不落盘");
        sb.AppendLine();

        sb.AppendLine(Field("网卡", $"{r.Nics.Count} 块，每块各起独立并发"));
        foreach (var n in r.Nics)
        {
            var s = n.Snapshot;
            sb.AppendLine(Field("", $"{s.Name}   {s.LocalIp ?? "?"}   链路 {s.SpeedText}   网关 {s.Gateway ?? "无"}"));
            if (!string.IsNullOrEmpty(s.Description))
                sb.AppendLine(Field("", $"    {s.Description}"));
            foreach (var w in s.Warnings) sb.AppendLine(Field("⚠", w));
            if (n.Note is not null) sb.AppendLine(Field("⚠", n.Note));
        }

        if (r.Error is not null)
        {
            sb.AppendLine();
            sb.AppendLine("× " + r.Error);
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine($"---------------- 吞吐（计时 {r.MeasureSeconds:0.0} 秒） ----------------");

        if (r.AnyDown) AppendThroughput(sb, r, "下载", r.Nics.Where(n => n.Down.Count > 0).ToList(),
            n => n.Down, n => n.DownCold, n => n.DownNicWindow, n => n.DownPayloadWindow, r.TotalDownAvg);

        if (r.AnyUp)
        {
            AppendThroughput(sb, r, "上传", r.Nics.Where(n => n.Up.Count > 0).ToList(),
                n => n.Up, n => n.UpCold, n => n.UpNicWindow, n => n.UpPayloadWindow, r.TotalUpAvg);
            sb.AppendLine(Field("落点", r.UploadUrl));
        }

        if (r.IdlePings.Count > 0 || r.LoadedPings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("---------------- 延迟 ----------------");
            AppendPings(sb, "空闲", r.IdlePings);
            if (r.LoadedPings.Count > 0) AppendPings(sb, "满载", r.LoadedPings);
        }

        if (r.Stopped) sb.AppendLine(Field("", "（这次是中途停掉的）"));

        if (includeVerdict)
        {
            sb.AppendLine();
            sb.AppendLine("---------------- 判读 ----------------");
            var (text, _) = Judge(r);
            foreach (var line in text.Split('\n')) sb.AppendLine("  " + line.TrimEnd());
        }

        return sb.ToString();
    }

    private static void AppendThroughput(
        StringBuilder sb, TestReport r, string title, List<NicRun> runs,
        Func<NicRun, List<double>> samples, Func<NicRun, double> cold,
        Func<NicRun, long> nicBytes, Func<NicRun, long> payloadBytes, double totalAvg)
    {
        sb.AppendLine();
        sb.AppendLine($"  【{title}】");

        double coldAvg = runs.Sum(cold);
        if (coldAvg > 0)
            sb.AppendLine(Field("  冷启动均", $"{coldAvg:N0} Mbps   （预热 {r.WarmupSeconds} 秒，不计入下面的统计）", 13));

        foreach (var n in runs)
        {
            var st = StatsOf(samples(n));
            sb.AppendLine(Field("  " + Trim(n.Snapshot.Name, 10),
                $"稳态均 {st.Avg,6:N0}   最低 {st.Min,6:N0}   最高 {st.Max,6:N0}   P10 {st.P10:N0} / P50 {st.P50:N0} / P90 {st.P90:N0}",
                13));
        }

        if (runs.Count > 1)
            sb.AppendLine(Field("  ── 合计", $"{totalAvg:N0} Mbps   （{runs.Count} 块网卡相加）", 13));
        else
            sb.AppendLine(Field("  稳态均", $"{totalAvg:N0} Mbps", 13));

        sb.AppendLine(Field(title == "下载" ? "  一次拉了" : "  一次发出",
            $"{runs.Sum(nicBytes) / 1e9:N2} GB（网卡计数口径）", 13));

        long payload = runs.Sum(payloadBytes);
        if (payload > 0)
            sb.AppendLine(Field("  实际吞吐", $"{payload / 1e9:N2} GB（差额是收工时还压在收发缓冲里没走完的那部分）", 13));

        if (r.MeasureSeconds > 0)
        {
            double nicMbps = runs.Sum(nicBytes) * 8.0 / 1e6 / r.MeasureSeconds;
            double gap = nicMbps > 0 ? (totalAvg - nicMbps) / nicMbps * 100 : 0;
            sb.AppendLine(Field("  自洽核对", $"网卡差值 {nicMbps:N0} vs 逐秒采样 {totalAvg:N0} Mbps（差 {gap:+0.0;-0.0;0}%）", 13));
        }
    }

    private static void AppendPings(StringBuilder sb, string when, List<PingStat> pings)
    {
        for (int i = 0; i < pings.Count; i++)
            sb.AppendLine(Field(i == 0 ? when : "", pings[i].Summary));
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>按显示宽度补空格（CJK 算两格），让左列能对齐。</summary>
    private static string Field(string label, string value, int width = 11)
    {
        int display = 0;
        foreach (var ch in label) display += ch >= 0x1100 && (ch <= 0x115F || ch >= 0x2E80) ? 2 : 1;
        return label + new string(' ', Math.Max(1, width - display)) + value;
    }

    public static Color LevelColor(VerdictLevel level) => level switch
    {
        VerdictLevel.Good => Color.FromArgb(0x0c, 0xa3, 0x0c),
        VerdictLevel.Warning => Color.FromArgb(0xfa, 0xb2, 0x19),
        VerdictLevel.Critical => Color.FromArgb(0xd0, 0x3b, 0x3b),
        _ => Color.FromArgb(0x89, 0x87, 0x81),
    };
}
