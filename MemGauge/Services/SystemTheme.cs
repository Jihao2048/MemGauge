using Microsoft.Win32;

namespace MemGauge.Services;

/// <summary>读取 Windows 的应用主题（设置 → 个性化 → 颜色 → 选择默认应用模式）。</summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统当前是否使用浅色应用主题。读不到时按浅色处理。</summary>
    public static bool IsLight
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue("AppsUseLightTheme") is int value)
                    return value != 0;
            }
            catch
            {
                // 忽略，退回浅色
            }
            return true;
        }
    }
}
