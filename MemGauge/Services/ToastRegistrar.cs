using System.IO;
using System.Runtime.InteropServices;

namespace MemGauge.Services;

/// <summary>
/// 非打包的 Win32 程序要发系统 Toast，必须先有一个带 AppUserModelID 的开始菜单快捷方式。
/// 这里用 IShellLink + IPropertyStore 自己写一个，比依赖第三方兼容层可靠。
/// </summary>
internal static class ToastRegistrar
{
    internal const string Aumid = "MemGauge.Widget";

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>PKEY_AppUserModel_ID，{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5</summary>
    private static readonly PropertyKey AppUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    /// <summary>PKEY_AppUserModel_ToastActivatorCLSID，同一 fmtid，pid 26</summary>
    private static readonly PropertyKey ToastActivatorKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 26
    };

    /// <summary>固定的 Toast 激活器 CLSID（点通知时由它拉起本程序）。</summary>
    private static readonly Guid ToastActivatorClsid = new("6C3D9C2B-3F1E-4B7A-9F0E-1C2D3E4F5A6B");

    private static bool _registered;
    private static readonly object Gate = new();

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>确保快捷方式存在且指向当前 exe。</summary>
    public static bool EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return true;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\内存挂件.lnk");

            WriteAppUserModelId(exe);

            // 已经注册好就直接用：重写快捷方式会被资源管理器占用而报 E_ACCESSDENIED
            if (IsShortcutCurrent(shortcutPath, exe))
            {
                _registered = true;
                return true;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
                CreateShortcut(exe, shortcutPath);

                _registered = true;

                // 让 shell 立刻重新扫描开始菜单，否则刚建的快捷方式要等一会儿才被通知平台认到
                SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(150);

                return true;
            }
            catch (Exception ex)
            {
                Log(ex, "register");
                return false;
            }
        }
    }

    private static void CreateShortcut(string exe, string shortcutPath)
    {
        try
        {
            SaveShortcut(exe, shortcutPath);
        }
        catch (UnauthorizedAccessException)
        {
            // 文件被占用：删掉再建一次
            try
            {
                File.Delete(shortcutPath);
                SaveShortcut(exe, shortcutPath);
            }
            catch (Exception ex)
            {
                Log(ex, "register-retry");
                throw;
            }
        }
    }

    private static void SaveShortcut(string exe, string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetArguments(string.Empty);
            link.SetIconLocation(exe, 0);

            var store = (IPropertyStore)link;
            SetStringProperty(store, AppUserModelIdKey, Aumid);
            SetClsidProperty(store, ToastActivatorKey, ToastActivatorClsid);

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// 快捷方式是否已经注册过（只校验 AUMID 是否写在文件里）。
    /// 不比对 exe 路径：安全软件常锁住开始菜单里的 lnk，改不动；
    /// 而通知平台认的是 AUMID，指向哪份 exe 都能正常弹。
    /// </summary>
    private static bool IsShortcutCurrent(string shortcutPath, string exe)
    {
        try
        {
            if (!File.Exists(shortcutPath)) return false;

            var bytes = File.ReadAllBytes(shortcutPath);
            return Contains(bytes, Aumid);
        }
        catch
        {
            return false;
        }
    }

    private static bool Contains(byte[] haystack, string text)
    {
        var pattern = System.Text.Encoding.Unicode.GetBytes(text);
        if (pattern.Length == 0 || haystack.Length < pattern.Length) return false;

        for (var i = 0; i <= haystack.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (haystack[i + j] == pattern[j]) continue;
                match = false;
                break;
            }
            if (match) return true;
        }
        return false;
    }

    private static void SetStringProperty(IPropertyStore store, PropertyKey key, string value)
    {
        var prop = new PropVariant
        {
            VariantType = 31, // VT_LPWSTR
            PointerValue = Marshal.StringToCoTaskMemUni(value)
        };
        try
        {
            var k = key;
            store.SetValue(ref k, ref prop);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(prop.PointerValue);
        }
    }

    private static void SetClsidProperty(IPropertyStore store, PropertyKey key, Guid value)
    {
        var memory = Marshal.AllocCoTaskMem(16);
        try
        {
            Marshal.StructureToPtr(value, memory, false);
            var prop = new PropVariant { VariantType = 72 /* VT_CLSID */, PointerValue = memory };
            var k = key;
            store.SetValue(ref k, ref prop);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    private static void WriteAppUserModelId(string exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{Aumid}");
            if (key is not null)
            {
                key.SetValue("DisplayName", "内存挂件", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("IconUri", exe, Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
                key.SetValue("CustomActivator", "{" + ToastActivatorClsid.ToString().ToUpperInvariant() + "}",
                    Microsoft.Win32.RegistryValueKind.String);
            }

            var clsidPath = $@"Software\Classes\CLSID\{{{ToastActivatorClsid.ToString().ToUpperInvariant()}}}\LocalServer32";
            using var server = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(clsidPath);
            server?.SetValue(string.Empty, $"\"{exe}\" -ToastActivated", Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // 写不进注册表不影响通知本身
        }
    }

    internal static void Log(Exception ex, string stage)
    {        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemGauge");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "notification-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
            // 忽略
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr PointerValue;
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink
    {
    }
}
