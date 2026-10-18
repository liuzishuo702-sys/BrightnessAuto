using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace BrightnessAuto.Controls;

internal sealed class TrayMenuWindow : Window
{
    private const int MenuWidth = 256;
    private const int RowHeight = 40;
    private readonly SolidColorBrush _text = Brush(238, 241, 243);
    private readonly SolidColorBrush _muted = Brush(163, 175, 180);
    private bool _shown;
    private bool _closed;
    private bool _submenuOpen;

    public TrayMenuWindow(Action showWindow, Action toggleEngine, Action exitApplication,
        Func<bool> isEngineRunning, Func<(int envLight, int brightness)>? statusReader,
        Func<(bool dim, bool lockScreen, bool sedentary)>? togglesReader,
        Action? toggleDimWhenAway, Action? toggleLockWhenAway, Action? toggleSedentary)
    {
        Title = "环境光自动亮度菜单";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(Item("\uE737", "显示窗口", () => Run(showWindow)));
        panel.Children.Add(Separator());

        if (statusReader is not null)
        {
            var (light, brightness) = statusReader();
            string status = light < 0 || brightness < 0
                ? (isEngineRunning() ? "环境光 测量中…" : "环境光 —  |  亮度 —")
                : $"环境光 {light}  |  亮度 {brightness}%";
            panel.Children.Add(new TextBlock
            {
                Text = status,
                Foreground = _muted,
                FontSize = 12,
                Margin = new Thickness(42, 9, 8, 9),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(Separator());
        }

        panel.Children.Add(Item("\uE706", isEngineRunning() ? "停止自动调节" : "开始自动调节",
            () => Run(toggleEngine)));

        if (togglesReader is not null)
        {
            var (dim, lockScreen, sedentary) = togglesReader();
            var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop };
            flyout.Items.Add(Toggle("无人时降低亮度", dim, toggleDimWhenAway));
            flyout.Items.Add(Toggle("无人时自动锁屏", lockScreen, toggleLockWhenAway));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Toggle("久坐提醒", sedentary, toggleSedentary));
            flyout.Opening += (_, _) => _submenuOpen = true;
            flyout.Closed += (_, _) =>
            {
                _submenuOpen = false;
                if (_shown && !_closed && GetForegroundWindow() != WinRT.Interop.WindowNative.GetWindowHandle(this))
                    Close();
            };
            var submenu = Item("\uE7B3", "无人检测", () => { }, true);
            submenu.Flyout = flyout;
            panel.Children.Add(submenu);
        }

        panel.Children.Add(Separator());
        panel.Children.Add(Item("\uE7E8", "退出程序", () => Run(exitApplication)));

        Content = new Border
        {
            Background = Brush(31, 44, 49),
            BorderBrush = Brush(82, 101, 105),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Child = panel,
            RequestedTheme = ElementTheme.Dark
        };
        Activated += (_, args) =>
        {
            if (_shown && !_submenuOpen && args.WindowActivationState == WindowActivationState.Deactivated)
                Close();
        };
    }

    private ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
        item.Click += (_, _) => { if (action is not null) Run(action); };
        return item;
    }

    private Button Item(string glyph, string label, Action action, bool hasSubmenu = false)
    {
        var grid = new Grid { Width = MenuWidth - 22, Height = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = _text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = label,
            Foreground = _text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        if (hasSubmenu)
        {
            var arrow = new FontIcon
            {
                Glyph = "\uE76C",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = _muted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
        }
        var button = new Button
        {
            Content = grid,
            Width = MenuWidth - 12,
            Height = RowHeight,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(4)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Margin = new Thickness(7, 5, 7, 5),
        Background = Brush(73, 89, 94)
    };

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromArgb(255, red, green, blue));

    private void Run(Action action)
    {
        Close();
        action();
    }

    public void ShowAt(int x, int y)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        nint style = GetWindowLongPtr(hwnd, -20); // GWL_EXSTYLE
        SetWindowLongPtr(hwnd, -20, (style & ~(nint)0x00040000) | (nint)0x00000080);
        int cornerPreference = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, sizeof(int));
        int dpi = (int)GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int width = (int)Math.Ceiling(MenuWidth * scale);
        var root = (Border)Content;
        root.Measure(new Windows.Foundation.Size(MenuWidth, 1000));
        int height = (int)Math.Ceiling(root.DesiredSize.Height * scale);
        var display = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        int left = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
        int top = Math.Clamp(y - height, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
        Activate();
        _shown = true;
    }

    public new void Close()
    {
        if (_closed) return;
        _closed = true;
        base.Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
