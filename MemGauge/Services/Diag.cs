using System.IO;

namespace MemGauge.Services;

/// <summary>临时诊断日志：记录刷新节拍和未处理异常，定位"数值不刷新"。</summary>
internal static class Diag
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemGauge", "diag.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
        }
        catch
        {
            // 忽略
        }
    }
}
