using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NetSpeedTest;

sealed class TestOptions
{
    public bool TestDownload { get; set; } = true;
    public bool TestUpload { get; set; }
    public bool TestPing { get; set; } = true;

    public int Seconds { get; set; } = 15;
    public int WarmupSeconds { get; set; } = 3;
    public int Threads { get; set; } = 16;
    public double PlanMbps { get; set; } = 1000;
    public string UploadUrl { get; set; } = TrafficEngine.DefaultUploadUrl;
    public List<string> NicNames { get; set; } = new();

    public bool AnyBandwidth => TestDownload || TestUpload;

    public string TestsText => string.Join(" + ", new[]
    {
        TestDownload ? "下载" : null,
        TestUpload ? "上传" : null,
        TestPing ? "延迟" : null,
    }.Where(s => s is not null)!);
}

/// <summary>一块网卡这一轮跑出来的东西。</summary>
sealed class NicRun
{
    public NetworkInterface Nic { get; init; } = null!;
    public NicSnapshot Snapshot { get; init; } = new();

    public List<double> Down { get; } = new();
    public List<double> Up { get; } = new();
    public double DownCold { get; set; }
    public double UpCold { get; set; }
    public long DownNicWindow { get; set; }
    public long UpNicWindow { get; set; }
    public long DownPayloadWindow { get; set; }
    public long UpPayloadWindow { get; set; }
    /// <summary>这块网卡没跑起来的原因（没出口之类）。</summary>
    public string? Note { get; set; }

    public double DownAvg => Down.Count > 0 ? Down.Average() : 0;
    public double UpAvg => Up.Count > 0 ? Up.Average() : 0;
}

sealed class TestReport
{
    public List<NicRun> Nics { get; init; } = new();
    public List<PingStat> IdlePings { get; init; } = new();
    public List<PingStat> LoadedPings { get; init; } = new();
    public bool LoadedWasRun { get; init; }
    public double PlanMbps { get; init; }
    public int WarmupSeconds { get; init; }
    public double MeasureSeconds { get; init; }
    public string TestsText { get; init; } = "";
    public string UploadUrl { get; init; } = "";
    public bool Stopped { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error is null && Nics.Any(n => n.Down.Count > 0 || n.Up.Count > 0);
    public bool AnyDown => Nics.Any(n => n.Down.Count > 0);
    public bool AnyUp => Nics.Any(n => n.Up.Count > 0);

    /// <summary>总下载 = 各网卡稳态均之和。</summary>
    public double TotalDownAvg => Nics.Where(n => n.Down.Count > 0).Sum(n => n.DownAvg);
    public double TotalUpAvg => Nics.Where(n => n.Up.Count > 0).Sum(n => n.UpAvg);
}

/// <summary>
/// 一次完整测试：空闲延迟 → 下载 → 上传（选了才跑），带宽阶段里顺带测满载延迟。
/// 事件都是从后台线程抛出来的，界面那边自己 BeginInvoke 回来。
/// </summary>
sealed class TestRunner
{
    public event Action<string>? Status;
    /// <summary>当前阶段名：「延迟」/「下载」/「上传」。</summary>
    public event Action<string>? Phase;
    public event Action<PingStat>? PingUpdated;
    public event Action<PingStat, bool>? PingDone;      // (结果, 是不是满载那次)
    /// <summary>(第几秒, 每块网卡的瞬时 Mbps, 累计总字节)</summary>
    public event Action<int, double[], long>? SampleTick;

    private List<NicRun> _runs = new();

    public async Task<TestReport> RunAsync(TestOptions opt, CancellationToken external)
    {
        var nics = NetInfo.Resolve(opt.NicNames);
        if (nics.Count == 0)
            return new TestReport { Error = "一块能用的网卡都没选中。", PlanMbps = opt.PlanMbps, TestsText = opt.TestsText };

        _runs = nics.Select(n => new NicRun { Nic = n, Snapshot = NetInfo.Snapshot(n) }).ToList();

        var idlePings = new List<PingStat>();
        var loadedPings = new List<PingStat>();
        var loadedStarted = false;
        var stopped = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = cts.Token;

        try
        {
            // ---- 1. 空闲延迟：必须在拉带宽之前，那时候的 RTT 才是纯线路延迟
            if (opt.TestPing)
            {
                Phase?.Invoke("延迟");
                Status?.Invoke("测空闲延迟（网关 + 公网，约 2 秒）…");
                idlePings = await LatencyTester
                    .MeasureAsync(_runs[0].Snapshot.Gateway, p => PingUpdated?.Invoke(p), ct)
                    .ConfigureAwait(false);
                foreach (var p in idlePings) PingDone?.Invoke(p, false);
            }

            // ---- 2. 下载。满载延迟跟着**第一个**带宽阶段测，所以只启一次。
            if (opt.TestDownload)
            {
                await BandwidthPhaseAsync(opt, TrafficMode.Download, ct,
                    runLoadedPing: opt.TestPing && !loadedStarted,
                    loaded: loadedPings,
                    onLoadedStarted: () => loadedStarted = true).ConfigureAwait(false);
            }

            // ---- 3. 上传
            if (opt.TestUpload)
            {
                await BandwidthPhaseAsync(opt, TrafficMode.Upload, ct,
                    runLoadedPing: opt.TestPing && !loadedStarted,
                    loaded: loadedPings,
                    onLoadedStarted: () => loadedStarted = true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        finally
        {
            cts.Cancel();
        }

        return new TestReport
        {
            Nics = _runs,
            IdlePings = idlePings,
            LoadedPings = loadedPings,
            LoadedWasRun = loadedPings.Count > 0,
            PlanMbps = opt.PlanMbps,
            WarmupSeconds = opt.WarmupSeconds,
            MeasureSeconds = _measuredSeconds,
            TestsText = opt.TestsText,
            UploadUrl = opt.TestUpload ? opt.UploadUrl : "",
            Stopped = stopped,
        };
    }

    private double _measuredSeconds;

    private async Task BandwidthPhaseAsync(
        TestOptions opt, TrafficMode mode, CancellationToken ct,
        bool runLoadedPing, List<PingStat> loaded, Action onLoadedStarted)
    {
        bool isDown = mode == TrafficMode.Download;
        Phase?.Invoke(isDown ? "下载" : "上传");
        Status?.Invoke($"{(isDown ? "下载" : "上传")}：{_runs.Count} 块网卡各起 {opt.Threads} 个线程…");

        // ★ 每个阶段一只自己的 token（挂在总 token 下面）。收工时只掐本阶段的引擎 ——
        //   用共享的那只会把后面的上传阶段一起杀掉。
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var phaseToken = phaseCts.Token;

        var engines = new List<TrafficEngine>();
        Task? all = null;
        Task<List<PingStat>>? loadedTask = null;

        try
        {
            foreach (var run in _runs)
            {
                var engine = new TrafficEngine(NetInfo.PrimaryV4(run.Nic), mode, opt.UploadUrl, "NetSpeedTest/1.1");
                engines.Add(engine);
                engine.Launch(opt.Threads, phaseToken);
            }

            all = Task.WhenAll(engines.Select(e => e.Release()));

            // 预热：建连 + 起流，不计入统计
            if (opt.WarmupSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(opt.WarmupSeconds), ct).ConfigureAwait(false);
                for (int i = 0; i < _runs.Count; i++)
                {
                    double cold = engines[i].PayloadBytes * 8.0 / 1e6 / opt.WarmupSeconds;
                    if (isDown) _runs[i].DownCold = cold; else _runs[i].UpCold = cold;
                }
                Status?.Invoke($"预热完成，正式计时 {opt.Seconds} 秒");
            }

            // 满载延迟：链接已经压满了，这时候测才是"拥堵下的延迟"
            if (runLoadedPing)
            {
                Status?.Invoke("同时在测满载延迟…");
                loadedTask = LatencyTester.MeasureAsync(_runs[0].Snapshot.Gateway, p => PingUpdated?.Invoke(p), ct);
                onLoadedStarted();
            }

            // 逐秒读每块网卡自己的计数器
            var before = _runs.Select(r => Counter(r.Nic, mode)).ToArray();
            var prev = (long[])before.Clone();
            var payloadBefore = engines.Select(e => e.PayloadBytes).ToArray();
            var clock = Stopwatch.StartNew();

            for (int s = 1; s <= opt.Seconds; s++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                var perNic = new double[_runs.Count];
                long total = 0;
                for (int i = 0; i < _runs.Count; i++)
                {
                    long now = Counter(_runs[i].Nic, mode);
                    perNic[i] = (now - prev[i]) * 8.0 / 1e6;
                    total += now - before[i];
                    prev[i] = now;
                }

                for (int i = 0; i < _runs.Count; i++)
                {
                    if (isDown) _runs[i].Down.Add(perNic[i]); else _runs[i].Up.Add(perNic[i]);
                }

                SampleTick?.Invoke(s, perNic, total);
            }

            _measuredSeconds = clock.Elapsed.TotalSeconds;

            for (int i = 0; i < _runs.Count; i++)
            {
                long nicWindow = prev[i] - before[i];
                long payloadWindow = engines[i].PayloadBytes - payloadBefore[i];
                if (isDown)
                {
                    _runs[i].DownNicWindow = nicWindow;
                    _runs[i].DownPayloadWindow = payloadWindow;
                }
                else
                {
                    _runs[i].UpNicWindow = nicWindow;
                    _runs[i].UpPayloadWindow = payloadWindow;
                }

                if (nicWindow < 1_000_000 && payloadWindow <= 0)
                    _runs[i].Note ??= "这块网卡这一个阶段一个字节都没拉到 —— 多半没有独立出口";
            }

            if (loadedTask is not null)
            {
                loaded.AddRange(await loadedTask.ConfigureAwait(false));
                foreach (var p in loaded) PingDone?.Invoke(p, true);
            }
        }
        catch (OperationCanceledException)
        {
            if (loadedTask is not null)
            {
                try { loaded.AddRange(await loadedTask.ConfigureAwait(false)); } catch { }
            }
            throw;
        }
        finally
        {
            phaseCts.Cancel();     // 只掐本阶段；总 token 不动
            if (all is not null) { try { await all.ConfigureAwait(false); } catch { } }
            foreach (var e in engines) e.Dispose();
        }
    }

    private static long Counter(NetworkInterface nic, TrafficMode mode)
        => mode == TrafficMode.Download ? NetInfo.RxBytes(nic) : NetInfo.TxBytes(nic);
}
