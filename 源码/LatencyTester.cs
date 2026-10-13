using System.Net.NetworkInformation;

namespace NetSpeedTest;

/// <summary>一次延迟测试的结果。</summary>
sealed record PingStat(string Label, string Host, int Sent, int Received, double MinMs, double AvgMs, double MaxMs)
{
    public double LossPct => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;

    // 局域网网关的往返时间就是亚毫秒，RoundtripTime 是整数毫秒，只能显示成 <1
    public string Summary => Received == 0
        ? $"{Label} {Host}   全丢（{Sent} 个包一个没回）"
        : $"{Label} {Host}   平均 {Verdict.Ms(AvgMs)} ms   最小 {Verdict.Ms(MinMs)}   最大 {Verdict.Ms(MaxMs)}   丢包 {LossPct:0}%";

    /// <summary>界面上那一行用的短版本。</summary>
    public string Short => Received == 0
        ? $"{Label} {Host}   全丢"
        : $"{Label} {Host}   {Verdict.Ms(AvgMs)} ms   丢包 {LossPct:0}%";
}

static class LatencyTester
{
    public const int Count = 20;        // 每目标发 20 个包
    private const int GapMs = 100;      // 包间隔
    private const int TimeoutMs = 1000; // 单包超时

    /// <summary>网关和公网两个目标并发，各自内部串行发包。</summary>
    public static async Task<List<PingStat>> MeasureAsync(string? gateway, Action<PingStat>? onUpdate, CancellationToken ct)
    {
        var targets = new List<(string Label, string Host)>();
        if (!string.IsNullOrWhiteSpace(gateway)) targets.Add(("网关", gateway!));
        targets.Add(("公网", "223.5.5.5"));

        var results = await Task.WhenAll(targets.Select(t => OneAsync(t.Label, t.Host, onUpdate, ct)))
                                .ConfigureAwait(false);
        return results.ToList();
    }

    private static async Task<PingStat> OneAsync(string label, string host, Action<PingStat>? onUpdate, CancellationToken ct)
    {
        // ★ Ping 的实例成员不保证线程安全，一个目标一个实例
        using var ping = new Ping();
        var options = new PingOptions(64, true);
        var payload = new byte[32];

        int sent = 0, received = 0;
        double min = double.MaxValue, max = 0, sum = 0;

        for (int i = 0; i < Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            sent++;
            try
            {
                var reply = await ping
                    .SendPingAsync(host, TimeSpan.FromMilliseconds(TimeoutMs), payload, options, ct)
                    .ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    double rtt = reply.RoundtripTime;
                    sum += rtt;
                    if (rtt < min) min = rtt;
                    if (rtt > max) max = rtt;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* PingException（解析不了 / 无路由）也算丢包，不能让整个测试炸掉 */ }

            onUpdate?.Invoke(new PingStat(label, host, sent, received,
                min == double.MaxValue ? 0 : min,
                received == 0 ? 0 : sum / received,
                max));

            if (i < Count - 1) await Task.Delay(GapMs, ct).ConfigureAwait(false);
        }

        return new PingStat(label, host, sent, received,
            min == double.MaxValue ? 0 : min,
            received == 0 ? 0 : sum / received,
            max);
    }
}
