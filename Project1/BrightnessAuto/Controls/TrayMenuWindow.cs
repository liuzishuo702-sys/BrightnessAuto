using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace BrightnessAuto.Controls;

/// <summary>
/// Tiny invisible host for the tray MenuFlyout. The visible popup is the
/// system XAML menu, so acrylic, hover and submenus follow the OS behavior.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private readonly Grid _host = new();
    private readonly MenuFlyout _menu = new() { ShouldConstrainToRootBounds = false };
    private bool _loaded;
    private bool _showPending;
    private bool _closed;
    private int _zOrderTicks;
    private readonly HashSet<nint> _windowsBeforeMenu = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _popupZOrderTimer;

    public TrayMenuWindow(Action showWindow, Action toggleEngine, Action exitApplication,
        Func<bool> isEngineRunning, Func<(int envLight, int brightness)>? statusReader,
        Func<(bool dim, bool lockScreen, bool sedentary)>? togglesReader,
        Action? toggleDimWhenAway, Action? toggleLockWhenAway, Action? toggleSedentary)
    {
        Title = "环境光自动亮度菜单宿主";
        Content = _host;
        _menu.MenuFlyoutPresenterStyle = CreatePresenterStyle();

        _popupZOrderTimer = _host.DispatcherQueue.CreateTimer();
        _popupZOrderTimer.Interval = TimeSpan.FromMilliseconds(40);
        _popupZOrderTimer.Tick += (_, _) =>
        {
            ElevateMenuPopups();
            // 菜单弹层只需在刚出现的几百毫秒内提权，没必要一直每 40ms 枚举全系统窗口.
            if (++_zOrderTicks >= 8)
            {
                _popupZOrderTimer.Stop();
                _zOrderTicks = 0;
            }
        };

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        _host.Loaded += (_, _) =>
        {
            _loaded = true;
            if (_showPending) OpenMenu();
        };
        _menu.Opened += (_, _) =>
        {
            ElevateMenuPopups();
            _popupZOrderTimer.Start();
        };
        _menu.Closed += (_, _) =>
        {
            _popupZOrderTimer.Stop();
            Close();
        };

        AddItem("\uE737", "显示窗口", showWindow);
        _menu.Items.Add(new MenuFlyoutSeparator());

        if (statusReader is not null)
        {
            var (light, brightness) = statusReader();
            string status = light < 0 || brightness < 0
                ? (isEngineRunning() ? "环境光  测量中…" : "环境光  —   |   亮度  —")
                : $"环境光  {light}   |   亮度  {brightness}%";
            _menu.Items.Add(new MenuFlyoutItem
            {
                Text = status,
                IsEnabled = false,
                Icon = Glyph("\uE706")
            });
            _menu.Items.Add(new MenuFlyoutSeparator());
        }

        AddItem("\uE706", isEngineRunning() ? "停止自动调节" : "开始自动调节", toggleEngine);

        if (togglesReader is not null)
        {
            var (dim, lockScreen, sedentary) = togglesReader();
            var submenu = new MenuFlyoutSubItem
            {
                Text = "无人检测",
                Icon = Glyph("\uE7B3")
            };
            submenu.Items.Add(Toggle("无人时降低亮度", dim, toggleDimWhenAway));
            submenu.Items.Add(Toggle("无人时自动锁屏", lockScreen, toggleLockWhenAway));
            submenu.Items.Add(new MenuFlyoutSeparator());
            submenu.Items.Add(Toggle("久坐提醒", sedentary, toggleSedentary));
            _menu.Items.Add(submenu);
        }

        _menu.Items.Add(new MenuFlyoutSeparator());
        AddItem("\uE7E8", "退出程序", exitApplication);
    }

    private void AddItem(string glyph, string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }

    private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }

    private static FontIcon Glyph(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 16
    };

    private static Style CreatePresenterStyle()
    {
        var acrylic = new AcrylicBrush
        {
            TintColor = Color.FromArgb(255, 20, 43, 44),
            TintOpacity = 0.68,
            FallbackColor = Color.FromArgb(255, 31, 43, 44)
        };
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, acrylic));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(90, 150, 180, 180))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        return style;
    }

    public void ShowAt(int left, int top, int right, int bottom, int pointerX, int pointerY)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        nint style = GetWindowLongPtr(hwnd, -20); // GWL_EXSTYLE
        SetWindowLongPtr(hwnd, -20,
            (style & ~(nint)0x00040000) | (nint)0x00000080 | (nint)0x00080000);
        SetLayeredWindowAttributes(hwnd, 0, 0, 0x00000002); // 宿主完全透明，菜单弹层不受影响

        var display = DisplayArea.GetFromPoint(new PointInt32(pointerX, pointerY), DisplayAreaFallback.Primary);
        var work = display.WorkArea;

        // 托盘图标通常在工作区外的任务栏里。像 TranslucentTB 一样把宿主矩形
        // 延伸 1px 进入工作区，让系统弹层能在正确的一侧选择位置。
        int workRight = work.X + work.Width;
        int workBottom = work.Y + work.Height;
        if (left >= workRight) left = workRight - 1;
        if (right <= work.X) right = work.X + 1;
        if (top >= workBottom) top = workBottom - 1;
        if (bottom <= work.Y) bottom = work.Y + 1;

        int width = Math.Max(2, right - left);
        int height = Math.Max(2, bottom - top);
        AppWindow.MoveAndResize(new RectInt32(left, top, width, height));

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        _menuPosition = new Point(
            Math.Clamp((pointerX - left) / scale, 0, width / scale),
            Math.Clamp((pointerY - top) / scale, 0, height / scale));

        _showPending = true;
        SnapshotCurrentWindows();
        Activate();
        SetForegroundWindow(hwnd);
        if (_loaded) OpenMenu();
    }

    private void OpenMenu()
    {
        if (_closed || !_showPending) return;
        _showPending = false;
        _host.DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _menu.ShowAt(_host, new FlyoutShowOptions
            {
                Position = _menuPosition,
                Placement = FlyoutPlacementMode.Auto,
                ShowMode = FlyoutShowMode.Standard
            });
        });
    }

    private Point _menuPosition = new(1, 1);

    private void SnapshotCurrentWindows()
    {
        _windowsBeforeMenu.Clear();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == (uint)Environment.ProcessId)
                _windowsBeforeMenu.Add(window);
            return true;
        }, 0);
    }

    private void ElevateMenuPopups()
    {
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId != (uint)Environment.ProcessId ||
                _windowsBeforeMenu.Contains(window) || !IsWindowVisible(window))
                return true;

            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (className.ToString().Contains("Popup", StringComparison.OrdinalIgnoreCase))
                SetWindowPos(window, (nint)(-1), 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010); // TOPMOST, 不移动、不改尺寸、不抢焦点
            return true;
        }, 0);
    }

    public new void Close()
    {
        if (_closed) return;
        _closed = true;
        // 关键: 必须在这里停掉 40ms 定时器.
        // 只依赖 _menu.Closed 是不够的 —— 外部(下一次右键)直接 Close() 时
        // MenuFlyout 的 Closed 不保证触发, 定时器会一直跑 EnumWindows,
        // 右键 N 次就累积 N 个 40ms 定时器, 最终把 UI 线程拖死(AppHang).
        try { _popupZOrderTimer.Stop(); } catch { }
        try { _menu.Hide(); } catch { }
        base.Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y,
        int width, int height, uint flags);
}
