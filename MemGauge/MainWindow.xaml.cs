using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MemGauge.Interop;
using MemGauge.Services;

namespace MemGauge;

public partial class MainWindow : Window
{
    private enum UiState
    {
        Idle,
        Working,
        Result
    }

    private enum DockSide
    {
        None,
        Left,
        Right,
        Top,
        Bottom
    }

    private const double RingSize = 72;
    private const double EdgePadding = 8;
    private const double WindowSize = RingSize + EdgePadding * 2; // 88

    private const double BarLength = 88;
    private const double BarThickness = 14;
    private const double BarWindowLong = BarLength + EdgePadding * 2;   // 96
    private const double BarWindowShort = BarThickness + EdgePadding * 2; // 28

    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMilliseconds(3200);

    // linear-gradient(90deg, #FFD1DC, #A6E0CF, #87CEEB)
    private static readonly Color[] GradientColors =
    {
        Color.FromRgb(0xFF, 0xD1, 0xDC),
        Color.FromRgb(0xA6, 0xE0, 0xCF),
        Color.FromRgb(0x87, 0xCE, 0xEB)
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _startupTimer;

    private UiState _state = UiState.Idle;
    private DockSide _dock = DockSide.None;

    private bool _collapsed;
    private bool _pendingClick;
    private double _ringLeft;
    private double _ringTop;
    private DispatcherTimer? _boundsTimer;

    private bool _dragging;
    private bool _moved;
    private int _cursorDownX;
    private int _cursorDownY;
    private double _ringLeftDown;
    private double _ringTopDown;

    private Brush _textPrimary = Brushes.Black;
    private Brush _trackBrush = Brushes.LightGray;

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshLive();

        _collapseTimer = new DispatcherTimer { Interval = CollapseDelay };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            TryCollapse();
        };

        _startupTimer = new DispatcherTimer { Interval = StartupDelay };
        _startupTimer.Tick += (_, _) =>
        {
            _startupTimer.Stop();
            TryCollapse();
        };

        Root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
        Root.MouseMove += Root_MouseMove;
        Root.MouseLeftButtonUp += Root_MouseLeftButtonUp;
        Root.MouseEnter += Root_MouseEnter;
        Root.MouseLeave += Root_MouseLeave;

        BarClip.SizeChanged += (_, _) => UpdateBarFill();

        Loaded += OnLoaded;
        ContentRendered += OnFirstRendered;
        Closing += (_, _) => SaveState();
    }

    // ---------------------------------------------------------------- 初始化

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = _settings.Topmost;
        ApplyTheme();
        SyncMenu();

        var work = SystemParameters.WorkArea;
        _ringLeft = Left = work.Right - WindowSize;
        _ringTop = Top = work.Top + (work.Height - WindowSize) / 2;

        PlayEntrance();
        RefreshLive();
        _refreshTimer.Start();

        // 后台补一次通知注册：安装包/其他程序可能覆盖掉带 AUMID 的开始菜单快捷方式
        Task.Run(() => ToastRegistrar.EnsureRegistered());
    }

    /// <summary>
    /// 首次渲染完成后再定位：此时窗口 DPI 才最终确定，
    /// 在 Loaded 阶段取 DPI 会拿到 96，缩放屏上位置会跑偏。
    /// </summary>
    private void OnFirstRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRendered;

        RestorePosition();
        _ringLeft = Left;
        _ringTop = Top;

        if (_settings.SnapToEdge)
            SnapToNearestEdge(animate: false);
        else
            _dock = DockSide.None;

        _startupTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && _settings.Theme == AppTheme.Auto)
            Dispatcher.BeginInvoke(new Action(ApplyTheme), DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    private void PlayEntrance()
    {
        RingHost.Opacity = 0;
        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RingHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    // ---------------------------------------------------------------- 渐变与主题

    private static void AddStops(GradientBrush brush)
    {
        for (var i = 0; i < GradientColors.Length; i++)
        {
            var offset = GradientColors.Length == 1 ? 0 : (double)i / (GradientColors.Length - 1);
            brush.GradientStops.Add(new GradientStop(GradientColors[i], offset));
        }
    }

    /// <summary>给环形用：绝对坐标横跨整个 72px 直径，颜色按位置分布。</summary>
    private static LinearGradientBrush RingGradient()
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(RingSize, 0)
        };
        AddStops(brush);
        brush.Freeze();
        return brush;
    }

    /// <summary>给细条用：横向条 90deg，纵向条 180deg（顺着长边走）。</summary>
    private static LinearGradientBrush BarGradient(bool horizontal)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = horizontal ? new Point(0, 0.5) : new Point(0.5, 0),
            EndPoint = horizontal ? new Point(1, 0.5) : new Point(0.5, 1)
        };
        AddStops(brush);
        brush.Freeze();
        return brush;
    }

    private bool IsDarkEffective => _settings.Theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => !SystemTheme.IsLight
    };

    private void ApplyTheme()
    {
        if (IsDarkEffective)
        {
            _textPrimary = Brush("#F3F5F9");
            _trackBrush = Brush("#2AFFFFFF");
        }
        else
        {
            _textPrimary = Brush("#171A1F");
            _trackBrush = Brush("#1F000000");
        }

        Gauge.TrackBrush = _trackBrush;
        Gauge.ValueBrush = RingGradient();
        TxtPercent.Foreground = _textPrimary;

        Bar.BorderBrush = Brush(IsDarkEffective ? "#8A8A8A" : "#9E9E9E");
        Bar.Background = Brush(IsDarkEffective ? "#1AFFFFFF" : "#0F000000");
        UpdateBarFill();

        SyncMenu();
        RefreshLive();
    }

    private void SyncMenu()
    {
        MenuCollapse.IsChecked = _settings.AutoCollapse;
        MenuNotify.IsChecked = _settings.Notify;
        MenuSnap.IsChecked = _settings.SnapToEdge;
        MenuTopmost.IsChecked = _settings.Topmost;
        MenuFollow.IsChecked = _settings.Theme == AppTheme.Auto;
        MenuDark.IsChecked = IsDarkEffective;
        MenuAutoStart.IsChecked = AutoStart.IsEnabled;

        MenuElevate.Header = Elevation.IsElevated ? "已是管理员权限" : "以管理员身份重启（清除待机内存更彻底）";
        MenuElevate.IsEnabled = !Elevation.IsElevated;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- 位置与吸附

    private void RestorePosition()
    {
        var work = WorkAreaDip();

        if (_settings.Left is { } savedLeft && _settings.Top is { } savedTop)
        {
            var rect = new Rect(savedLeft, savedTop, WindowSize, WindowSize);
            if (work.IntersectsWith(rect) && rect.Left < work.Right - 20 && rect.Right > work.Left + 20)
            {
                Left = savedLeft;
                Top = savedTop;
                return;
            }
        }

        Left = work.Right - WindowSize;
        Top = work.Top + (work.Height - WindowSize) / 2;
    }

    private Rect WorkAreaDip()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;

        return new Rect(
            info.rcWork.Left / scale,
            info.rcWork.Top / scale,
            (info.rcWork.Right - info.rcWork.Left) / scale,
            (info.rcWork.Bottom - info.rcWork.Top) / scale);
    }

    private void SnapToNearestEdge(bool animate = true)
    {
        var work = WorkAreaDip();

        double ClampLeft(double value) => Math.Clamp(value, work.Left, Math.Max(work.Left, work.Right - WindowSize));
        double ClampTop(double value) => Math.Clamp(value, work.Top, Math.Max(work.Top, work.Bottom - WindowSize));

        if (!_settings.SnapToEdge)
        {
            _dock = DockSide.None;
            MoveTo(ClampLeft(_ringLeft), ClampTop(_ringTop), animate);
            return;
        }

        var dLeft = Math.Abs(_ringLeft - work.Left);
        var dRight = Math.Abs(work.Right - (_ringLeft + WindowSize));
        var dTop = Math.Abs(_ringTop - work.Top);
        var dBottom = Math.Abs(work.Bottom - (_ringTop + WindowSize));
        var nearest = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        double targetLeft, targetTop;

        if (nearest > 120)
        {
            // 拖到屏幕中间就不强吸，只保证不出界
            _dock = DockSide.None;
            targetLeft = ClampLeft(_ringLeft);
            targetTop = ClampTop(_ringTop);
        }
        else if (nearest == dLeft)
        {
            _dock = DockSide.Left;
            targetLeft = work.Left;
            targetTop = ClampTop(_ringTop);
        }
        else if (nearest == dRight)
        {
            _dock = DockSide.Right;
            targetLeft = work.Right - WindowSize;
            targetTop = ClampTop(_ringTop);
        }
        else if (nearest == dTop)
        {
            _dock = DockSide.Top;
            targetTop = work.Top;
            targetLeft = ClampLeft(_ringLeft);
        }
        else
        {
            _dock = DockSide.Bottom;
            targetTop = work.Bottom - WindowSize;
            targetLeft = ClampLeft(_ringLeft);
        }

        MoveTo(targetLeft, targetTop, animate);
    }

    private void MoveTo(double left, double top, bool animate)
    {
        _boundsTimer?.Stop();
        _boundsTimer = null;
        _ringLeft = left;
        _ringTop = top;

        if (!animate)
        {
            Left = left;
            Top = top;
            return;
        }

        Animate(LeftProperty, left);
        Animate(TopProperty, top);
    }

    private void Animate(DependencyProperty property, double target)
    {
        var from = (double)GetValue(property);
        if (double.IsNaN(from) || Math.Abs(from - target) < 0.4)
        {
            SetCurrentValue(property, target);
            return;
        }

        SetCurrentValue(property, target);
        BeginAnimation(property, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    /// <summary>
    /// 同时改宽高和位置。
    /// 不能用 BeginAnimation：窗口尺寸改动会把 HWND 的 WM_SIZE 写回 Width/Height，
    /// 动画值会被抹掉（宽度变了、高度没变）。只能自己按时间插值。
    /// </summary>
    private void AnimateBounds(double width, double height, double left, double top)
    {
        _boundsTimer?.Stop();
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        var startWidth = double.IsNaN(Width) ? ActualWidth : Width;
        var startHeight = double.IsNaN(Height) ? ActualHeight : Height;
        var startLeft = Left;
        var startTop = Top;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const double durationMs = 190;

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(12)
        };
        _boundsTimer = timer;

        void Apply(double progress)
        {
            SetCurrentValue(WidthProperty, startWidth + (width - startWidth) * progress);
            SetCurrentValue(HeightProperty, startHeight + (height - startHeight) * progress);
            SetCurrentValue(LeftProperty, startLeft + (left - startLeft) * progress);
            SetCurrentValue(TopProperty, startTop + (top - startTop) * progress);
        }

        timer.Tick += (_, _) =>
        {
            var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / durationMs);
            Apply(1 - Math.Pow(1 - progress, 3)); // easeOutCubic

            if (progress < 1) return;

            timer.Stop();
            _boundsTimer = null;
            SetCurrentValue(WidthProperty, width);
            SetCurrentValue(HeightProperty, height);
            SetCurrentValue(LeftProperty, left);
            SetCurrentValue(TopProperty, top);
        };

        timer.Start();
    }

    // ---------------------------------------------------------------- 收缩 / 展开

    /// <summary>左右停靠时细条竖着，其余横着。</summary>
    private bool BarIsVertical => _dock is DockSide.Left or DockSide.Right;

    private void TryCollapse()
    {
        if (_collapsed || !_settings.AutoCollapse) return;
        if (_state == UiState.Working || _dragging || Menu.IsOpen || Root.IsMouseOver) return;

        _collapsed = true;

        var vertical = BarIsVertical;
        Bar.Width = vertical ? BarThickness : BarLength;
        Bar.Height = vertical ? BarLength : BarThickness;
        BarFill.Background = BarGradient(horizontal: !vertical);

        RingHost.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Visible;
        Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        var width = vertical ? BarWindowShort : BarWindowLong;
        var height = vertical ? BarWindowLong : BarWindowShort;
        var (left, top) = BarPosition(width, height);
        AnimateBounds(width, height, left, top);
        UpdateBarFill();
    }

    /// <summary>
    /// 细条是固定长度的轨道：渐变始终按整条铺满，这里只裁剪出「占用率」那一段。
    /// 竖条从底部往上填，横条从右往左填 —— 保证 50% 时露出来的是蓝 + 半个绿。
    /// </summary>
    private void UpdateBarFill()
    {
        if (!_collapsed) return;

        var width = BarClip.ActualWidth;
        var height = BarClip.ActualHeight;
        if (width <= 1 || height <= 1) return;

        var percent = Math.Clamp(MemorySnapshot.Capture().UsedPercent / 100.0, 0, 1);
        var radius = Math.Min(width, height) / 2;

        if (BarIsVertical)
        {
            var fill = Math.Max(3, height * percent);
            BarClip.Clip = new RectangleGeometry(new Rect(0, height - fill, width, fill), radius, radius);
        }
        else
        {
            var fill = Math.Max(3, width * percent);
            BarClip.Clip = new RectangleGeometry(new Rect(width - fill, 0, fill, height), radius, radius);
        }
    }

    private (double Left, double Top) BarPosition(double width, double height)
    {
        var work = WorkAreaDip();
        var centerX = _ringLeft + WindowSize / 2;
        var centerY = _ringTop + WindowSize / 2;

        double left = centerX - width / 2;
        double top = centerY - height / 2;

        switch (_dock)
        {
            case DockSide.Right:
                left = work.Right - width;
                break;
            case DockSide.Left:
                left = work.Left;
                break;
            case DockSide.Top:
                top = work.Top;
                break;
            case DockSide.Bottom:
                top = work.Bottom - height;
                break;
        }

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        return (left, top);
    }

    private void Expand()
    {
        if (!_collapsed) return;
        _collapsed = false;

        Bar.BeginAnimation(OpacityProperty, null);
        Bar.Opacity = 0;
        Bar.Visibility = Visibility.Collapsed;
        RingHost.Visibility = Visibility.Visible;

        AnimateBounds(WindowSize, WindowSize, _ringLeft, _ringTop);
    }

    // ---------------------------------------------------------------- 悬停 / 拖动 / 点击

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        RefreshLive();          // 看之前先刷一次，避免看到陈旧数值
        if (_collapsed) Expand();
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging || _state == UiState.Working) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;

        if (_collapsed)
        {
            _pendingClick = true;
            return;
        }

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        _boundsTimer?.Stop();
        _boundsTimer = null;

        NativeMethods.GetCursorPos(out var cursor);
        _cursorDownX = cursor.X;
        _cursorDownY = cursor.Y;
        _ringLeftDown = _ringLeft;
        _ringTopDown = _ringTop;
        _moved = false;
        _dragging = true;

        Root.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        NativeMethods.GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);

        var dx = (cursor.X - _cursorDownX) / dpi.DpiScaleX;
        var dy = (cursor.Y - _cursorDownY) / dpi.DpiScaleY;

        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        _moved = true;

        _ringLeft = _ringLeftDown + dx;
        _ringTop = _ringTopDown + dy;

        SetCurrentValue(LeftProperty, _ringLeft);
        SetCurrentValue(TopProperty, _ringTop);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingClick)
        {
            _pendingClick = false;
            Expand();
            StartRelease();
            return;
        }

        if (!_dragging) return;

        _dragging = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();

        if (_moved)
        {
            SnapToNearestEdge();
            SaveState();
        }
        else
        {
            StartRelease();
        }
    }

    // ---------------------------------------------------------------- 释放流程

    private async void StartRelease()
    {
        if (_state == UiState.Working) return;

        _state = UiState.Working;
        MenuRelease.IsEnabled = false;
        _collapseTimer.Stop();
        _startupTimer.Stop();

        Gauge.IsIndeterminate = true;
        TxtPercent.Visibility = Visibility.Collapsed;

        try
        {
            var result = await Task.Run(() => MemoryReleaser.Release());

            ShowResult(result);

            if (_settings.Notify)
                Notifications.ReleaseDone(result.FreedBytes, result.After);
        }
        catch (Exception ex)
        {
            _state = UiState.Idle;
            if (_settings.Notify) Notifications.ReleaseFailed(ex.Message);
        }
        finally
        {
            Gauge.IsIndeterminate = false;
            TxtPercent.Visibility = Visibility.Visible;
            MenuRelease.IsEnabled = true;
            RefreshLive();

            if (!Root.IsMouseOver) _collapseTimer.Start();
        }
    }

    private void ShowResult(ReleaseResult result)
    {
        _state = UiState.Result;
        Gauge.Value = result.After.UsedPercent;
        TxtPercent.Text = $"{result.After.UsedPercent:0}%";

        ToolTip = $"上次释放 {ByteFormat.Human(result.FreedBytes)}（修剪 {result.TrimmedProcesses} 个进程）\n" +
                  $"可用 {ByteFormat.CompactGb(result.After.AvailableGb)} · 已用 {ByteFormat.CompactGb(result.After.UsedGb)}\n" +
                  "左键点击再次释放";
    }

    private void RefreshLive()
    {
        var snapshot = MemorySnapshot.Capture();

        // 定时器如果被意外停掉（休眠恢复、被别处 Stop 等），这里顺手拉起来
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();

        Gauge.Value = snapshot.UsedPercent;
        TxtPercent.Text = $"{snapshot.UsedPercent:0}%";

        RingTip.Content = $"可用 {ByteFormat.CompactGb(snapshot.AvailableGb)} · 已用 {ByteFormat.CompactGb(snapshot.UsedGb)}\n左键点击释放内存";

        if (_collapsed) UpdateBarFill();
    }

    // ---------------------------------------------------------------- 右键菜单

    private void MenuRelease_Click(object sender, RoutedEventArgs e) => StartRelease();

    private void MenuCollapse_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoCollapse = MenuCollapse.IsChecked;
        _settings.Save();

        if (!_settings.AutoCollapse) Expand();
        else if (!Root.IsMouseOver) _collapseTimer.Start();
    }

    private void MenuNotify_Click(object sender, RoutedEventArgs e)
    {
        _settings.Notify = MenuNotify.IsChecked;
        _settings.Save();
    }

    private void MenuSnap_Click(object sender, RoutedEventArgs e)
    {
        _settings.SnapToEdge = MenuSnap.IsChecked;
        _settings.Save();
        if (_settings.SnapToEdge) SnapToNearestEdge();
        else _dock = DockSide.None;
    }

    private void MenuTopmost_Click(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = MenuTopmost.IsChecked;
        Topmost = _settings.Topmost;
        _settings.Save();
    }

    private void MenuFollow_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = MenuFollow.IsChecked
            ? AppTheme.Auto
            : IsDarkEffective ? AppTheme.Dark : AppTheme.Light;
        _settings.Save();
        ApplyTheme();
    }

    private void MenuDark_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = MenuDark.IsChecked ? AppTheme.Dark : AppTheme.Light;
        _settings.Save();
        ApplyTheme();
    }

    private void MenuAutoStart_Click(object sender, RoutedEventArgs e)
    {
        AutoStart.Set(MenuAutoStart.IsChecked);
        MenuAutoStart.IsChecked = AutoStart.IsEnabled;
    }

    private void MenuElevate_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.RestartElevated())
            Close();
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveState()
    {
        _boundsTimer?.Stop();
        _boundsTimer = null;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        _settings.Left = _ringLeft;
        _settings.Top = _ringTop;
        _settings.Save();
        _collapseTimer.Stop();
        _startupTimer.Stop();
    }
}
