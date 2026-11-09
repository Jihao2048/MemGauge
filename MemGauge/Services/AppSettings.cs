using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemGauge.Services;

public enum AppTheme
{
    /// <summary>跟随 Windows 的应用主题。</summary>
    Auto,
    Dark,
    Light
}

/// <summary>挂件的持久化配置，存放在 %APPDATA%\MemGauge\settings.json。</summary>
public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool SnapToEdge { get; set; } = true;
    public bool AutoCollapse { get; set; } = true;
    public bool Notify { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.Auto;
    public bool Topmost { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemGauge");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时静默回到默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写不进去就算了，不影响使用
        }
    }
}
