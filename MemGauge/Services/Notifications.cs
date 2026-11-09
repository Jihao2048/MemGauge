using System.IO;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MemGauge.Services;

/// <summary>释放结果走 Windows 系统通知（操作中心里的那种 Toast）。</summary>
public static class Notifications
{
    public static void ReleaseDone(ulong freedBytes, MemorySnapshot after)
    {
        var title = freedBytes >= 1024UL * 1024UL
            ? $"已释放 {ByteFormat.Human(freedBytes)}"
            : "内存已经比较干净了";

        Show(title, $"可用 {ByteFormat.CompactGb(after.AvailableGb)} · 已用 {ByteFormat.CompactGb(after.UsedGb)}");
    }

    public static void ReleaseFailed(string message) => Show("内存释放失败", message);

    private static void Show(string title, string body)
    {
        try
        {
            // 注册失败也照样尝试：只要这个 AUMID 之前登记过，通知照样能弹
            ToastRegistrar.EnsureRegistered();

            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text>" +
                $"<text>{Escape(body)}</text>" +
                "</binding></visual></toast>");

            ToastNotificationManager
                .CreateToastNotifier(ToastRegistrar.Aumid)
                .Show(new ToastNotification(xml));
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemGauge");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "notification-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
            // 连日志都写不了就彻底算了
        }
    }
}
