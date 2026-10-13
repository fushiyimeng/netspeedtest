using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetSpeedTest;

/// <summary>一次测试在历史里的摘要。完整报告原文也一起存，方便回头查。</summary>
sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Nics { get; set; } = "";
    public string Tests { get; set; } = "";
    public double DownAvg { get; set; }
    public double UpAvg { get; set; }
    public double IdlePingMs { get; set; }
    public double LoadedPingMs { get; set; }
    public string Report { get; set; } = "";

    [JsonIgnore]
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore]
    public string DownText => DownAvg > 0 ? $"{DownAvg:N0}" : "—";
    [JsonIgnore]
    public string UpText => UpAvg > 0 ? $"{UpAvg:N0}" : "—";
    [JsonIgnore]
    public string IdlePingText => IdlePingMs > 0 ? $"{Verdict.Ms(IdlePingMs)}" : "—";
    [JsonIgnore]
    public string LoadedPingText => LoadedPingMs > 0 ? $"{Verdict.Ms(LoadedPingMs)}" : "—";
}

/// <summary>历史记录，一行一条 JSON（JSONL），就放在 exe 旁边。</summary>
static class HistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    // ★ 单文件发布下 Assembly.Location 是空串，只能用 AppContext.BaseDirectory
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "历史记录.jsonl");

    public static void Append(HistoryEntry entry)
    {
        try
        {
            File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Options) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { /* 写不进去就算了，别因为记不了账把测试搞崩 */ }
    }

    /// <summary>倒着读（最新的在最前面）。坏行直接跳过。</summary>
    public static List<HistoryEntry> LoadAll()
    {
        var list = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<HistoryEntry>(line, Options);
                    if (e is not null) list.Add(e);
                }
                catch { }
            }
        }
        catch { }
        list.Reverse();
        return list;
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
