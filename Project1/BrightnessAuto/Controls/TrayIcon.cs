using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BrightnessAuto.Controls;

/// <summary>基于 Shell_NotifyIcon 的轻量系统托盘图标，不引入第三方依赖。</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 37;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const int GwlpWndProc = -4;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;
    private const uint NimAdd = 0x0000;
    private const uint NimDelete = 0x0002;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint IdShow = 1;
    private const uint IdToggle = 2;
    private const uint IdExit = 3;
    private const uint IdStatus = 4;   // 菜单里的状态行，点击无反应

    private readonly nint _hwnd;
    private readonly Action _showWindow;
    private readonly Action _toggleEngine;
    private readonly Action _exitApplication;
    private readonly Func<bool> _isEngineRunning;
    private readonly Func<(int envLight, int brightness)>? _statusReader;
    private readonly WindowProc _windowProc;
    private readonly uint _taskbarCreatedMessage;
    private nint _oldWindowProc;
    private nint _icon;
    private bool _ownsIcon;
    private bool _visible;
    private bool _disposed;

    public TrayIcon(nint hwnd, Action showWindow, Action toggleEngine,
        Action exitApplication, Func<bool> isEngineRunning,
        Func<(int envLight, int brightness)>? statusReader = null)
    {
        _hwnd = hwnd;
        _showWindow = showWindow;
        _toggleEngine = toggleEngine;
        _exitApplication = exitApplication;
        _isEngineRunning = isEngineRunning;
        _statusReader = statusReader;
        _windowProc = WndProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _oldWindowProc = SetWindowLongPtr(_hwnd, GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_oldWindowProc == 0)
            throw new InvalidOperationException("SetWindowLongPtr 子类化窗口失败, 托盘回调不可用");

        _icon = CreateBrightnessIcon();
        _ownsIcon = _icon != 0;
        if (!_ownsIcon)
        {
            // LoadIcon 返回的是系统共享句柄, 绝不能 DestroyIcon, 所以显式标记为不拥有.
            _icon = LoadIcon(0, (nint)32512); // IDI_APPLICATION
            _ownsIcon = false;
        }
    }

    // 32px ARGB 托盘图标：UWP/Fluent 蓝色圆角方块 + 白色太阳。
    private static nint CreateBrightnessIcon()
    {
        const int size = 32;
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - 16;
                double dy = y + 0.5 - 16;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                uint color = 0;

                // 28px 圆角方块底板，贴近 UWP 应用图标轮廓。
                double qx = Math.Abs(dx) - 10.0;
                double qy = Math.Abs(dy) - 10.0;
                double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0)
                                         + Math.Max(qy, 0) * Math.Max(qy, 0));
                double roundedDistance = outside + Math.Min(Math.Max(qx, qy), 0) - 3.5;
                if (roundedDistance <= 0)
                    color = roundedDistance > -1.0 ? 0xFF62A8EAu : 0xFF2688E5u;

                // 八向白色太阳光芒。
                double axis = Math.Min(Math.Abs(dx), Math.Abs(dy));
                double diagonal = Math.Min(Math.Abs(dx - dy), Math.Abs(dx + dy)) / Math.Sqrt(2);
                if (distance is >= 7.4 and <= 10.8 && Math.Min(axis, diagonal) < 1.05)
                    color = 0xFFFFFFFFu;

                // 中央白色太阳，16px 下仍保持明确轮廓。
                if (distance <= 6.2)
                    color = distance > 5.4 ? 0xFFEAF5FFu : 0xFFFFFFFFu;

                pixels[y * size + x] = unchecked((int)color);
            }
        }

        nint colorBitmap = CreateBitmap(size, size, 1, 32, pixels);
        nint maskBitmap = CreateBitmap(size, size, 1, 1, null);
        if (colorBitmap == 0 || maskBitmap == 0)
        {
            if (colorBitmap != 0) DeleteObject(colorBitmap);
            if (maskBitmap != 0) DeleteObject(maskBitmap);
            return 0;
        }

        try
        {
            // 关键: 32bpp 位图经 CreateIconIndirect 转换时 alpha 常被丢弃,
            // 透明区域会变成黑色方块. 这里把 alpha=0 的像素写进 AND 掩码
            // (1=透明), 保证托盘图标在深色任务栏上也不发黑.
            if (!ApplyTransparencyMask(maskBitmap, pixels, size))
                return 0;

            var info = new IconInfo
            {
                IsIcon = true,
                ColorBitmap = colorBitmap,
                MaskBitmap = maskBitmap
            };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(colorBitmap);
            DeleteObject(maskBitmap);
        }
    }

    /// <summary>按 alpha 通道填充 1bpp AND 掩码: 1=透明, 0=不透明. 行按 4 字节对齐.</summary>
    private static bool ApplyTransparencyMask(nint maskBitmap, int[] pixels, int size)
    {
        int stride = ((size + 31) / 32) * 4;   // 1bpp 每行按 LONG(4 字节) 对齐
        var mask = new byte[stride * size];

        for (int y = 0; y < size; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < size; x++)
            {
                uint alpha = (uint)(pixels[y * size + x] >> 24) & 0xFF;
                if (alpha == 0)
                    mask[rowOffset + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // 最高位 = 最左像素
            }
        }

        return SetBitmapBits(maskBitmap, (uint)mask.Length, mask);
    }

    public bool Show()
    {
        if (_disposed) return false;
        if (_visible) return true;
        var data = CreateData();
        _visible = Shell_NotifyIcon(NimAdd, ref data);
        return _visible;
    }

    public void Hide()
    {
        if (!_visible) return;
        var data = CreateData();
        Shell_NotifyIcon(NimDelete, ref data);
        _visible = false;
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "环境光自动亮度 - 后台运行中"
    };

    private nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == CallbackMessage)
            {
                uint mouseMessage = unchecked((uint)lParam.ToInt64());
                if (mouseMessage == WmLButtonDblClk)
                {
                    _showWindow();
                    return 0;
                }
                if (mouseMessage == WmRButtonUp)
                {
                    ShowContextMenu();
                    return 0;
                }
            }
            else if (message == _taskbarCreatedMessage && _visible)
            {
                _visible = false;
                Show();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIcon] WndProc 异常: {ex.Message}");
        }

        return CallWindowProc(_oldWindowProc, hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, MfString, IdShow, "显示窗口");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, IdToggle,
                _isEngineRunning() ? "停止自动调节" : "开始自动调节");
            if (_statusReader is not null)
            {
                var (envLight, brightness) = _statusReader();
                AppendMenu(menu, MfString, IdStatus,
                    $"环境光 {envLight} | 亮度 {brightness}%");
            }
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, IdExit, "退出程序");
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            uint command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd,
                point.X, point.Y, _hwnd, 0);
            if (command == IdShow) _showWindow();
            else if (command == IdToggle) _toggleEngine();
            else if (command == IdExit) _exitApplication();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_oldWindowProc != 0)
        {
            SetWindowLongPtr(_hwnd, GwlpWndProc, _oldWindowProc);
            _oldWindowProc = 0;
        }
        if (_ownsIcon && _icon != 0 && _icon != 1)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint instance, nint iconName);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref IconInfo iconInfo);
    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel,
        [MarshalAs(UnmanagedType.LPArray)] int[]? bits);
    [DllImport("gdi32.dll")]
    private static extern bool SetBitmapBits(nint bitmap, uint count, [In] byte[] bits);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

}
