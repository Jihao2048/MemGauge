using MemGauge.Interop;

namespace MemGauge.Services;

/// <summary>某一时刻的物理内存快照（字节）。</summary>
public readonly record struct MemorySnapshot(ulong Total, ulong Available)
{
    public ulong Used => Total > Available ? Total - Available : 0;

    public double UsedPercent => Total == 0 ? 0 : (double)Used / Total * 100.0;

    public double AvailableGb => Available / 1024.0 / 1024.0 / 1024.0;

    public double UsedGb => Used / 1024.0 / 1024.0 / 1024.0;

    public static MemorySnapshot Capture()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? new MemorySnapshot(status.ullTotalPhys, status.ullAvailPhys)
            : default;
    }
}

/// <summary>字节数的易读格式化。</summary>
public static class ByteFormat
{
    /// <summary>自动选择单位，例如 "1.24 GB"、"860 MB"。</summary>
    public static string Human(ulong bytes)
    {
        const double kb = 1024.0, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0} MB";
        if (bytes >= kb) return $"{bytes / kb:0} KB";
        return $"{bytes} B";
    }

    /// <summary>用于"已用 / 可用"栏目的紧凑写法，例如 "5.8G"。</summary>
    public static string CompactGb(double gigabytes) => gigabytes >= 10 ? $"{gigabytes:0.0}G" : $"{gigabytes:0.00}G";
}
