using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetSpeedTest;

/// <summary>测试开始那一刻的网卡画像。</summary>
sealed class NicSnapshot
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long SpeedBps { get; init; }
    public string? Gateway { get; init; }
    public string? LocalIp { get; init; }
    public List<string> Warnings { get; init; } = new();

    public string SpeedText => SpeedBps switch
    {
        >= 1_000_000_000 => $"{(double)SpeedBps / 1e9:0.##} Gbps",
        > 0 => $"{SpeedBps / 1_000_000} Mbps",
        _ => "未知",
    };
}

/// <summary>界面上「网卡（可多选）」列表里的一行。</summary>
sealed record NicCandidate(
    NetworkInterface Nic, string Name, string Description,
    string? Ip, string? Gateway, long SpeedBps, bool Usable, string Note)
{
    public string Display
    {
        get
        {
            var parts = new List<string> { Name };
            parts.Add(Ip ?? "无 IPv4");
            if (!Usable && !string.IsNullOrEmpty(Note)) parts.Add(Note);
            return string.Join("   ", parts);
        }
    }
}

static class NetInfo
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    private static NetworkInterface[] All() => NetworkInterface.GetAllNetworkInterfaces();

    /// <summary>
    /// 能拿来测速的网卡清单：状态 Up、非环回、有 IPv4 地址。
    /// Usable = 有 IPv4 网关（没网关的网卡根本没有出口，绑上去只会连不上）。
    /// </summary>
    public static List<NicCandidate> ListCandidates()
    {
        var result = new List<NicCandidate>();
        foreach (var nic in All())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = SafeProps(nic);
            var ip = props?.UnicastAddresses
                           .Select(u => u.Address)
                           .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) continue;   // 没有 IPv4 就没法绑源地址

            // ★ GatewayAddresses 里 IPv6 link-local 排在前面，必须过滤
            var gw = props?.GatewayAddresses
                           .Select(g => g.Address)
                           .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            var name = nic.Name ?? "";
            var desc = nic.Description ?? "";
            var usable = gw is not null;

            // 为什么像虚拟网卡（命中哪个词就报哪个词，别一律写成 TAP）
            string? virtualWhy = null;
            if (name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)) virtualWhy = "Hyper-V 虚拟交换机";
            if (virtualWhy is null)
                foreach (var bad in new[] { "TAP", "Hyper-V", "Virtual", "VPN", "Tailscale", "ZeroTier", "WireGuard", "Clash", "Mihomo", "OpenVPN" })
                    if (desc.Contains(bad, StringComparison.OrdinalIgnoreCase)) { virtualWhy = bad; break; }

            var note = (virtualWhy, usable) switch
            {
                (null, true) => "",
                (null, false) => "无网关·测不了",
                (not null, true) => $"虚拟网卡·{virtualWhy}",
                (not null, false) => $"虚拟网卡·{virtualWhy}·无网关",
            };

            result.Add(new NicCandidate(nic, name, desc, ip.ToString(), gw?.ToString(), nic.Speed, usable, note));
        }

        // 可用的排前面，其次按链路速率从高到低
        return result.OrderByDescending(c => c.Usable).ThenByDescending(c => c.SpeedBps).ToList();
    }

    /// <summary>
    /// 按名字把网卡找回来；找不到的忽略。
    /// **保持传进来的顺序** —— 图表的第 N 条线要跟第 N 块网卡对得上。
    /// </summary>
    public static List<NetworkInterface> Resolve(IEnumerable<string> names)
    {
        var byName = All().GroupBy(n => n.Name ?? "", StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = new List<NetworkInterface>();
        foreach (var name in names)
            if (byName.TryGetValue(name, out var nic)) list.Add(nic);
        return list;
    }

    /// <summary>这块网卡的主 IPv4 地址（拿来绑源地址）。</summary>
    public static IPAddress? PrimaryV4(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses
                      .Select(u => u.Address)
                      .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch { return null; }
    }

    /// <summary>承载默认路由的那块网卡。</summary>
    public static NetworkInterface? FindDefaultNic()
    {
        try
        {
            // GetBestInterface 要的是网络字节序的地址（小端机上就是个反过来的 uint）
            var b = IPAddress.Parse("223.5.5.5").GetAddressBytes();
            uint addr = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            if (GetBestInterface(addr, out var idx) == 0 && idx != 0)
            {
                var hit = All().FirstOrDefault(n => V4Index(n) == (int)idx);
                if (hit is not null) return hit;
            }
        }
        catch { /* 掉到下面的兜底 */ }

        // 兜底：状态 Up、非环回、有 IPv4 网关
        return All().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                      && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                      && HasV4Gateway(n));
    }

    public static NicSnapshot Snapshot(NetworkInterface nic)
    {
        var props = SafeProps(nic);

        // ★ GatewayAddresses 里 IPv6 link-local 排在前面，必须过滤，否则会去 ping fe80::
        var gw = props?.GatewayAddresses
                       .Select(g => g.Address)
                       .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var ip = props?.UnicastAddresses
                       .Select(u => u.Address)
                       .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

        var name = nic.Name ?? "";
        var desc = nic.Description ?? "";
        var warn = new List<string>();

        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback)
            warn.Add("这块是隧道/环回网卡 —— 测到的是隧道，不是物理链路");

        foreach (var bad in new[] { "TAP", "Hyper-V", "Virtual", "VPN", "Tailscale", "ZeroTier", "WireGuard", "Clash", "Mihomo", "OpenVPN" })
        {
            if (desc.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                warn.Add($"网卡描述里有「{bad}」—— 像是虚拟/代理网卡，测速会跑到隧道上");
                break;
            }
        }

        if (name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
            warn.Add("这是 Hyper-V 虚拟交换机");

        if (nic.Speed <= 0)
            warn.Add("拿不到链路速率（网线没插上 / 链路没起来）");

        return new NicSnapshot
        {
            Name = name,
            Description = desc,
            SpeedBps = nic.Speed,
            Gateway = gw?.ToString(),
            LocalIp = ip?.ToString(),
            Warnings = warn,
        };
    }

    /// <summary>
    /// 网卡收到的字节总数。用 GetIPStatistics()（该网卡的 IP 层汇总，v4+v6 一起算），
    /// 与 Get-NetAdapterStatistics 的 ReceivedBytes 同源，实测对得上。
    /// </summary>
    public static long RxBytes(NetworkInterface nic)
    {
        try { return nic.GetIPStatistics().BytesReceived; } catch { return 0; }
    }

    /// <summary>网卡发出的字节总数（上传测速用，口径和 RxBytes 对称）。</summary>
    public static long TxBytes(NetworkInterface nic)
    {
        try { return nic.GetIPStatistics().BytesSent; } catch { return 0; }
    }

    /// <summary>收包错误 / 丢弃计数，用来确认物理层干不干净。</summary>
    public static (long Errors, long Discards) RxBad(NetworkInterface nic)
    {
        try
        {
            var s = nic.GetIPStatistics();
            return (s.IncomingPacketsWithErrors, s.IncomingPacketsDiscarded);
        }
        catch { return (0, 0); }
    }

    private static IPInterfaceProperties? SafeProps(NetworkInterface nic)
    {
        try { return nic.GetIPProperties(); } catch { return null; }
    }

    private static int V4Index(NetworkInterface nic)
    {
        try { return nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1; } catch { return -1; }
    }

    private static bool HasV4Gateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                      .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch { return false; }
    }
}
