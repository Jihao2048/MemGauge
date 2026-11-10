using System.Diagnostics;
using System.Security.Principal;
using MemGauge.Interop;

namespace MemGauge.Services;

/// <summary>一次内存释放的结果。</summary>
public sealed class ReleaseResult
{
    public ulong FreedBytes { get; init; }
    public MemorySnapshot After { get; init; }
    public int TrimmedProcesses { get; init; }
    public int SkippedProcesses { get; init; }
    public bool PurgedStandbyList { get; init; }
    public bool Elevated { get; init; }

    /// <summary>过程说明，用于界面上补充一句人话。</summary>
    public string Note =>
        PurgedStandbyList ? "已清理待机内存" :
        Elevation.IsElevated ? "待机列表清理失败" : "普通权限，效果受限";
}

/// <summary>
/// 内存释放：先把所有进程的工作集页推回待机列表，再清空待机列表。
/// 对应 PCL「内存优化」的那套做法（PCL 的实现未开源，这里是按同样行为自行实现）。
/// </summary>
public static class MemoryReleaser
{
    private static readonly string[] SkipProcessNames =
    {
        "System", "Idle", "Registry", "Memory Compression", "MemCompression", "Secure System"
    };

    public static ReleaseResult Release(Action<string>? report = null)
    {
        var before = MemorySnapshot.Capture();
        var elevated = Elevation.IsElevated;

        // SeProfileSingleProcessPrivilege：清空待机列表必需
        // SeIncreaseQuotaPrivilege：调整工作集/系统配额时可能用到
        NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege");
        NativeMethods.EnablePrivilege("SeIncreaseQuotaPrivilege");

        report?.Invoke("整理工作集…");
        var (trimmed, skipped) = TrimAllWorkingSets();

        report?.Invoke("清理待机内存…");
        var purged = PurgeStandbyList();

        // 内存回收是异步的，采样一小段时间取可用内存峰值
        report?.Invoke("统计结果…");
        var peak = before.Available;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1100)
        {
            Thread.Sleep(110);
            var sample = MemorySnapshot.Capture();
            if (sample.Available > peak) peak = sample.Available;
        }

        var after = MemorySnapshot.Capture();
        return new ReleaseResult
        {
            FreedBytes = peak > before.Available ? peak - before.Available : 0,
            After = after,
            TrimmedProcesses = trimmed,
            SkippedProcesses = skipped,
            PurgedStandbyList = purged,
            Elevated = elevated
        };
    }

    private static (int Trimmed, int Skipped) TrimAllWorkingSets()
    {
        int trimmed = 0, skipped = 0;
        var self = Environment.ProcessId;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return (0, 0);
        }

        foreach (var process in processes)
        {
            using (process)
            {
                if (process.Id == self) continue; // 自己留着，否则界面会卡一下

                if (SkipProcessNames.Any(n => string.Equals(n, process.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                var handle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                    false, process.Id);

                if (handle == IntPtr.Zero)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // -1 / -1：把工作集整体交给系统回收
                    if (NativeMethods.SetProcessWorkingSetSizeEx(handle, new IntPtr(-1), new IntPtr(-1), 0))
                        trimmed++;
                    else
                        skipped++;
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
        }

        return (trimmed, skipped);
    }

    private static bool PurgeStandbyList()
    {
        var command = NativeMethods.MemoryPurgeStandbyList;
        var status = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return status == 0; // 0 = STATUS_SUCCESS，负数为 NTSTATUS 错误
    }
}

/// <summary>权限相关的小工具。</summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>以管理员身份重新启动自己（会弹 UAC）。</summary>
    public static bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--restarted", // 告诉新实例：旧实例正在退出，等一下互斥量
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch
        {
            return false; // 用户点了"否"
        }
    }
}
