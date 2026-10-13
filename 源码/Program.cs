using System.Runtime.InteropServices;
using System.Text;

namespace NetSpeedTest;

static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--cli" or "-c" or "--console" or "--help" or "-h" or "/?"))
            return Cli.Run(args);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        InstallCrashLog();

        // --history 直接开到历史窗口（界面上也有「历史记录」按钮）
        Application.Run(args.Any(a => a is "--history") ? new HistoryForm() : new MainForm());
        return 0;
    }

    /// <summary>崩溃了至少留一份可查的日志在 exe 旁边，而不是只弹个"Parameter is not valid"。</summary>
    private static void InstallCrashLog()
    {
        Application.ThreadException += (_, e) => LogCrash("界面线程", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("后台线程", e.ExceptionObject as Exception);
    }

    public static void LogCrash(string where, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "错误日志.txt");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(path, text, new UTF8Encoding(true));
        }
        catch { }
    }
}

/// <summary>
/// 无界面模式：同样的引擎，结果打到 stdout。方便脚本调用，也方便自动化验证。
/// 用法：网速测试.exe --cli --seconds 15 --threads 16 --out 报告.txt
/// </summary>
static class Cli
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint FileTypeUnknown = 0x0000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static bool _attached;

    public static int Run(string[] args)
    {
        bool haveStdout = EnsureStdout();
        try
        {
            var utf8 = new UTF8Encoding(false);
            Console.OutputEncoding = utf8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch { /* 没有标准输出也不能影响测试本身 */ }

        var options = new TestOptions
        {
            TestDownload = true,
            TestUpload = false,
            TestPing = true,
        };
        string? outFile = null;
        var nics = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";

            switch (a)
            {
                case "--cli" or "-c" or "--console": break;
                case "--seconds" or "-s": if (int.TryParse(Next(), out var s)) options.Seconds = s; break;
                case "--threads" or "-t": if (int.TryParse(Next(), out var t)) options.Threads = t; break;
                case "--warmup" or "-w": if (int.TryParse(Next(), out var w)) options.WarmupSeconds = w; break;
                case "--plan" or "-p": if (double.TryParse(Next(), out var p)) options.PlanMbps = p; break;
                case "--download": options.TestDownload = true; break;
                case "--no-download": options.TestDownload = false; break;
                case "--upload": options.TestUpload = true; break;
                case "--ping": options.TestPing = true; break;
                case "--no-ping": options.TestPing = false; break;
                case "--upload-url": options.UploadUrl = Next(); break;
                case "--nic": nics.Add(Next()); break;
                case "--list-nics": PrintNics(); if (_attached) FreeConsole(); return 0;
                case "--out" or "-o": outFile = Next(); break;
                case "--help" or "-h" or "/?":
                    PrintHelp();
                    if (_attached) FreeConsole();
                    return 0;
            }
        }

        if (!options.AnyBandwidth && !options.TestPing)
        {
            Console.WriteLine("× 什么都没选：至少要有下载 / 上传 / 延迟 里的一个。");
            if (_attached) FreeConsole();
            return 2;
        }

        if (nics.Count == 0)
        {
            var def = NetInfo.FindDefaultNic();
            if (def?.Name is not null) nics.Add(def.Name);
        }
        options.NicNames = nics;

        options.Seconds = Math.Clamp(options.Seconds, 3, 600);
        options.Threads = Math.Clamp(options.Threads, 1, 256);
        options.WarmupSeconds = Math.Clamp(options.WarmupSeconds, 0, 30);

        var runner = new TestRunner();
        runner.Status += s => Console.WriteLine("  " + s);
        runner.Phase += p => Console.WriteLine($"  ── {p} ──");
        runner.SampleTick += (second, perNic, _) =>
        {
            double total = perNic.Sum();
            string detail = perNic.Length > 1
                ? "   各网卡 " + string.Join(" / ", perNic.Select(v => v.ToString("N0")))
                : "";
            Console.WriteLine($"  {second,3}s  {total,7:N0} Mbps  {Bar(total)}{detail}");
        };

        TestReport report;
        try
        {
            report = runner.RunAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report = new TestReport { Error = "意外错误：" + ex.Message, PlanMbps = options.PlanMbps };
        }

        var text = Verdict.BuildText(report);
        Console.WriteLine();
        Console.Write(text);
        Console.Out.Flush();

        if (outFile is not null)
        {
            try { File.WriteAllText(outFile, text, new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("  （写报告文件失败：" + ex.Message + "）"); }
        }
        else if (!haveStdout)
        {
            // 连 stdout 都拿不到（比如被计划任务静默启动）—— 兜底落一个文件，不然白跑了
            try
            {
                var fallback = Path.Combine(AppContext.BaseDirectory, "网速测试-报告.txt");
                File.WriteAllText(fallback, text, new UTF8Encoding(true));
            }
            catch { }
        }

        if (_attached) FreeConsole();
        return report.Ok ? 0 : 1;
    }

    private static string Bar(double mbps) => new('#', (int)Math.Clamp(mbps / 30, 0, 50));

    private static void PrintNics()
    {
        Console.WriteLine("网卡清单（* = 有网关，能拿来测）：");
        foreach (var c in NetInfo.ListCandidates())
        {
            string speed = c.SpeedBps >= 1_000_000_000
                ? $"{(double)c.SpeedBps / 1e9:0.##} Gbps"
                : $"{c.SpeedBps / 1_000_000} Mbps";
            Console.WriteLine($"  {(c.Usable ? "*" : " ")} {c.Name,-24} {c.Ip,-16} 链路 {speed,-9} {c.Note}");
        }
        Console.WriteLine();
        Console.WriteLine("  用 --nic \"网卡名\" 指定（可写多次）。");
    }

    /// <summary>
    /// WinExe 子系统的进程默认没有控制台。先看 stdout 句柄是不是已经有效
    /// （管道 / 文件重定向都算有效）—— 有效就千万别 AttachConsole，
    /// attach 之后输出会跑去 console 而不是管道，`| grep` 就抓不到了。
    /// </summary>
    private static bool EnsureStdout()
    {
        static bool Usable()
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            return GetFileType(handle) != FileTypeUnknown;
        }

        if (Usable()) return true;

        if (!AttachConsole(AttachParentProcess)) return false;
        _attached = true;
        return Usable();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("网速测试 —— 下载 / 上传 / 延迟，数据不落盘");
        Console.WriteLine();
        Console.WriteLine("  网速测试.exe                              打开界面");
        Console.WriteLine("  网速测试.exe --cli [选项]                 无界面跑一次，结果打到屏幕");
        Console.WriteLine("  网速测试.exe --cli --list-nics            看看有哪些网卡能选");
        Console.WriteLine();
        Console.WriteLine("  测什么（默认：下载 + 延迟）");
        Console.WriteLine("      --download          测下载（默认开）");
        Console.WriteLine("      --no-download       不测下载");
        Console.WriteLine("      --upload            测上传");
        Console.WriteLine("      --ping / --no-ping  测 / 不测延迟（默认开）");
        Console.WriteLine("      --upload-url <地址> 上传落点，默认 Cloudflare");
        Console.WriteLine();
        Console.WriteLine("  怎么测");
        Console.WriteLine("  -s, --seconds <秒>      计时时长，默认 15");
        Console.WriteLine("  -t, --threads <n>       每块网卡的并发线程，默认 = 逻辑处理器数");
        Console.WriteLine("  -w, --warmup <秒>       预热秒数（建连用，不算进统计），默认 3，0 = 关");
        Console.WriteLine("  -p, --plan <Mbps>       宽带套餐，默认 1000，用来算「跑到了几成」");
        Console.WriteLine("      --nic \"网卡名\"       指定网卡，可写多次（多块 = 同时测）");
        Console.WriteLine();
        Console.WriteLine("  -o, --out <文件>        把报告另存一份到文件");
        Console.WriteLine();
    }
}
