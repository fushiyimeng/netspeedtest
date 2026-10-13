using System.Text.Json;

namespace NetSpeedTest;

/// <summary>上次用的参数，存在 exe 旁边的 设置.json。</summary>
sealed class AppSettings
{
    public bool TestDownload { get; set; } = true;
    public bool TestUpload { get; set; } = true;
    public bool TestPing { get; set; } = true;

    public int Seconds { get; set; } = 15;
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount, 4, 64);
    public int WarmupSeconds { get; set; } = 3;
    public double PlanMbps { get; set; } = 1000;

    /// <summary>上传落点。想换成国内的地址，改这里就行（界面上也能改）。</summary>
    public string UploadUrl { get; set; } = TrafficEngine.DefaultUploadUrl;

    /// <summary>上次勾选的网卡名字。</summary>
    public List<string> SelectedNics { get; set; } = new();

    // ★ 单文件发布下 Assembly.Location 返回空字符串，只能用 AppContext.BaseDirectory
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "设置.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 读不动就用默认值，不值得打扰用户 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 只读目录之类，静默放弃 */ }
    }
}
