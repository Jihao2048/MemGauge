using System.Threading;
using System.Windows;
using MemGauge.Services;

namespace MemGauge;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 发一条示例通知，用来验证系统通知是否可用（不启动挂件）
        if (e.Args.Any(a => string.Equals(a, "--notify-test", StringComparison.OrdinalIgnoreCase)))
        {
            Notifications.ReleaseDone(1536UL * 1024 * 1024, MemorySnapshot.Capture());
            Thread.Sleep(3000); // 让通知有机会弹出来（进程秒退的话 toast 会被一起撤销）
            Shutdown();
            return;
        }

        // 只允许跑一个挂件
        _singleInstance = new Mutex(true, @"Local\MemGauge.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
