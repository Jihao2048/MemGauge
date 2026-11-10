using System.Threading;
using System.Windows;
using MemGauge.Services;

namespace MemGauge;

public partial class App : Application
{
    private const string MutexName = @"Local\MemGauge.SingleInstance";

    /// <summary>提权重启时，新进程可能比旧进程退得早，最多等这么久让它接班。</summary>
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(4);

    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diag.Log("UnhandledException: " + args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            Diag.Log("DispatcherUnhandledException: " + args.Exception);
            args.Handled = true;
        };
        // 发一条示例通知，用来验证系统通知是否可用（不启动挂件）
        if (e.Args.Any(a => string.Equals(a, "--notify-test", StringComparison.OrdinalIgnoreCase)))
        {
            Notifications.ReleaseDone(1536UL * 1024 * 1024, MemorySnapshot.Capture());
            Thread.Sleep(3000); // 让通知有机会弹出来（进程秒退的话 toast 会被一起撤销）
            Shutdown();
            return;
        }

        // 提权重启：等旧实例把互斥量放开再接管
        var restarted = e.Args.Any(a => string.Equals(a, "--restarted", StringComparison.OrdinalIgnoreCase));
        if (!TryAcquireSingleInstance(restarted ? RestartGrace : TimeSpan.Zero))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private bool TryAcquireSingleInstance(TimeSpan grace)
    {
        var deadline = DateTime.UtcNow + grace;

        while (true)
        {
            _singleInstance = new Mutex(true, MutexName, out var createdNew);
            if (createdNew) return true;

            _singleInstance.Dispose();
            _singleInstance = null;

            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(120);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
