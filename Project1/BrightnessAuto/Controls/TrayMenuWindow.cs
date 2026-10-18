using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

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

    public TrayMenuWindow(Action showWindow, Action toggleEngine, Action exitApplication,
        Func<bool> isEngineRunning, Func<(int envLight, int brightness)>? statusReader,
        Func<(bool dim, bool lockScreen, bool sedentary)>? togglesReader,
        Action? toggleDimWhenAway, Action? toggleLockWhenAway, Action? toggleSedentary)
    {
        Title = "环境光自动亮度菜单宿主";
        Content = _host;

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
        _menu.Closed += (_, _) => Close();

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

    public void ShowAt(int x, int y)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        nint style = GetWindowLongPtr(hwnd, -20); // GWL_EXSTYLE
        SetWindowLongPtr(hwnd, -20, (style & ~(nint)0x00040000) | (nint)0x00000080);

        var display = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        int hostX = Math.Clamp(x, work.X, work.X + work.Width - 2);
        int hostY = Math.Clamp(y, work.Y, work.Y + work.Height - 2);
        AppWindow.MoveAndResize(new RectInt32(hostX, hostY, 2, 2));

        _showPending = true;
        Activate();
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
                Position = new Point(1, 1),
                Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
                ShowMode = FlyoutShowMode.Standard
            });
        });
    }

    public new void Close()
    {
        if (_closed) return;
        _closed = true;
        base.Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
