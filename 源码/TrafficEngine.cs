using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NetSpeedTest;

enum TrafficMode { Download, Upload }

/// <summary>
/// 拉流 / 打流的引擎，一次只服务**一块网卡**。
///
/// 全部跑在本进程里 —— 关窗口即断，不存在老 curl 方案那种
/// "父进程被杀掉了，curl 子进程还在后台继续吃带宽"的孤儿进程问题。
/// 下载的数据读进 buffer 就扔，**绝不落盘**。
///
/// 多网卡靠 <see cref="SocketsHttpHandler.ConnectCallback"/> 把源地址绑到指定网卡的
/// IPv4 地址上 —— 绑了源地址，路由就会被内核锁到那块网卡的出口去。
/// </summary>
sealed class TrafficEngine : IDisposable
{
    /// <summary>8 台不同主机上的镜像，避开单站限速。挑 ISO 是因为它够大、够稳。</summary>
    public static readonly string[] Sources =
    {
        "https://mirrors.tuna.tsinghua.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirrors.ustc.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirrors.bfsu.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirror.sjtu.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirrors.zju.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirrors.cernet.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirrors.huaweicloud.com/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
        "https://mirrors.aliyun.com/ubuntu-releases/24.04/ubuntu-24.04.4-desktop-amd64.iso",
    };

    /// <summary>默认的上传落点。走国际线路，数字偏保守；设置里可以换成自己的国内地址。</summary>
    public const string DefaultUploadUrl = "https://speed.cloudflare.com/__up";

    private const int ChunkSize = 512 * 1024;   // 打流用的分片
    private const int UploadPerRequest = 8 * 1024 * 1024;

    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly TrafficMode _mode;
    private readonly string _uploadUrl;
    private readonly byte[] _chunk;             // 上传用的零数据，所有 worker 共用

    private long _payload;
    private TaskCompletionSource? _gate;
    private Task[] _workers = Array.Empty<Task>();

    public TrafficEngine(IPAddress? localAddress, TrafficMode mode, string uploadUrl, string userAgent)
    {
        _mode = mode;
        _uploadUrl = uploadUrl;
        _chunk = new byte[ChunkSize];

        _handler = new SocketsHttpHandler
        {
            UseProxy = false,                            // ★ 必须：否则可能走系统代理/PAC，测的就不是这条线
            Proxy = null,
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),    // ★ 默认无限；某个镜像 SYN 黑洞会永久挂住
            MaxConnectionsPerServer = 64,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = false,
        };

        if (localAddress is not null)
            _handler.ConnectCallback = (ctx, ct) => ConnectViaAsync(localAddress, ctx, ct);

        _http = new HttpClient(_handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,          // ★ 默认 100 秒，长测速会被提前掐断
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>实际读到的字节数（下载）/ 实际写出去的字节数（上传）。都不含协议头。</summary>
    public long PayloadBytes => Interlocked.Read(ref _payload);

    /// <summary>起 N 个 worker，全部停在屏障上等放行。</summary>
    public void Launch(int threads, CancellationToken ct)
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workers = new Task[threads];
        for (int i = 0; i < threads; i++)
        {
            int index = i;
            _workers[i] = Task.Run(() => WorkerAsync(index, ct), ct);
        }
    }

    /// <summary>放行（所有线程同时起跑）；返回的 Task 在全部 worker 退出后完成。</summary>
    public Task Release()
    {
        _gate?.TrySetResult();
        return Task.WhenAll(_workers);
    }

    private async Task WorkerAsync(int index, CancellationToken ct)
    {
        var gate = _gate;
        if (gate is not null) await gate.Task.ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_mode == TrafficMode.Download) await PullOnceAsync(index, ct).ConfigureAwait(false);
                else await PushOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // 某个源恰好抽风（403 / 超时 / 断流）：歇一下重来，别让整个测试塌掉
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task PullOnceAsync(int index, CancellationToken ct)
    {
        var url = Sources[index % Sources.Length];
        using var req = new HttpRequestMessage(HttpMethod.Get, url)
        {
            // ★ 固定 HTTP/1.1：协商到 h2 的话 N 个请求会被多路复用到同一条 TCP 上，
            //   「每个线程一条独立连接」这个前提就不成立了
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                Interlocked.Add(ref _payload, n);   // 读出来就扔，不落盘
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PushOnceAsync(CancellationToken ct)
    {
        // 每写一段就往计数器里加一段 —— 等请求返回才记账的话，跑一半被停掉的那些请求
        // 一个字节都不算，上传的"实际吞吐"会少一大截
        using var content = new ZeroContent(_chunk, UploadPerRequest, n => Interlocked.Add(ref _payload, n));
        using var resp = await _http.PostAsync(_uploadUrl, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>把源地址绑到指定网卡的 IP 上，再连目标。绑了以后内核只能走那块网卡的出口。</summary>
    private static async ValueTask<Stream> ConnectViaAsync(IPAddress local, SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        IPAddress[] targets;
        if (IPAddress.TryParse(ctx.DnsEndPoint.Host, out var literal)) targets = new[] { literal };
        else targets = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);

        Exception? last = null;
        foreach (var target in targets)
        {
            if (target.AddressFamily != AddressFamily.InterNetwork) continue;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(target, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();     // 连失败的 socket 不能重用，每个地址新建一个
                last = ex;
            }
        }

        throw last ?? new IOException("解析不出可用的目标地址");
    }

    public void Dispose()
    {
        _http.Dispose();     // 关掉底层 socket，连接立刻断
        _handler.Dispose();
    }

    /// <summary>
    /// 固定长度的全零上传体。用自定义 HttpContent 而不是 ByteArrayContent：
    /// 后者要真分配 8MB 数组（16 线程就是 128MB），这个只重复写同一块 512KB 的缓冲区。
    /// </summary>
    private sealed class ZeroContent : HttpContent
    {
        private readonly byte[] _chunk;
        private readonly long _total;
        private readonly Action<int> _onWritten;

        public ZeroContent(byte[] chunk, long total, Action<int> onWritten)
        {
            _chunk = chunk;
            _total = total;
            _onWritten = onWritten;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            long left = _total;
            while (left > 0)
            {
                int n = (int)Math.Min(_chunk.Length, left);
                await stream.WriteAsync(_chunk.AsMemory(0, n)).ConfigureAwait(false);
                _onWritten(n);
                left -= n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _total;     // 有长度就不会退化成 chunked，省一层开销
            return true;
        }
    }
}
