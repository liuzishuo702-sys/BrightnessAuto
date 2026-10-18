using System;
using System.Collections.Generic;
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
    private const uint MfPopup = 0x0010;
    private const uint MfChecked = 0x0008;
    private const uint MfGrayed = 0x0001;
    private const uint MfByCommand = 0x0000;
    private const uint IdShow = 1;
    private const uint IdToggle = 2;
    private const uint IdExit = 3;
    private const uint IdStatus = 4;   // 菜单里的状态行，点击无反应
    private const uint IdDimWhenAway = 5;
    private const uint IdLockWhenAway = 6;
    private const uint IdSedentary = 7;

    // Segoe MDL2 Assets 图标字符. 用单色位图挂到菜单项上,
    // Windows 会把它当遮罩, 自动按菜单文字色绘制 —— 深浅主题都自适应.
    private const string GlyphWindow = "\uE737";   // FullScreen / 窗口
    private const string GlyphSun = "\uE706";      // Brightness 太阳
    private const string GlyphPower = "\uE7E8";    // PowerButton
    private const string GlyphBell = "\uEA8F";     // Ringer / 提醒
    private const string GlyphEye = "\uE7B3";      // RedEye / 无人检测

    private readonly nint _hwnd;
    private readonly Action _showWindow;
    private readonly Action _toggleEngine;
    private readonly Action _exitApplication;
    private readonly Func<bool> _isEngineRunning;
    private readonly Func<(int envLight, int brightness)>? _statusReader;
    private readonly Func<(bool dim, bool lockScreen, bool sedentary)>? _togglesReader;
    private readonly Action? _toggleDimWhenAway;
    private readonly Action? _toggleLockWhenAway;
    private readonly Action? _toggleSedentary;
    private readonly WindowProc _windowProc;
    private readonly uint _taskbarCreatedMessage;
    private nint _oldWindowProc;
    private nint _icon;
    private bool _ownsIcon;
    private bool _visible;
    private bool _disposed;
    private TrayMenuWindow? _menuWindow;

    public TrayIcon(nint hwnd, Action showWindow, Action toggleEngine,
        Action exitApplication, Func<bool> isEngineRunning,
        Func<(int envLight, int brightness)>? statusReader = null,
        Func<(bool dim, bool lockScreen, bool sedentary)>? togglesReader = null,
        Action? toggleDimWhenAway = null,
        Action? toggleLockWhenAway = null,
        Action? toggleSedentary = null)
    {
        _hwnd = hwnd;
        _showWindow = showWindow;
        _toggleEngine = toggleEngine;
        _exitApplication = exitApplication;
        _isEngineRunning = isEngineRunning;
        _statusReader = statusReader;
        _togglesReader = togglesReader;
        _toggleDimWhenAway = toggleDimWhenAway;
        _toggleLockWhenAway = toggleLockWhenAway;
        _toggleSedentary = toggleSedentary;
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
        try
        {
            _menuWindow?.Close();
            _menuWindow = new TrayMenuWindow(
                _showWindow, _toggleEngine, _exitApplication, _isEngineRunning,
                _statusReader, _togglesReader, _toggleDimWhenAway,
                _toggleLockWhenAway, _toggleSedentary);
            GetCursorPos(out var point);
            var iconBounds = GetIconBounds(point);
            _menuWindow.ShowAt(iconBounds.Left, iconBounds.Top,
                iconBounds.Right, iconBounds.Bottom, point.X, point.Y);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIcon] 自定义菜单失败，回退系统菜单: {ex}");
            _menuWindow?.Close();
            _menuWindow = null;
            ShowNativeContextMenu();
        }
    }

    private Rect GetIconBounds(Point fallbackPoint)
    {
        var id = new NotifyIconIdentifier
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            hWnd = _hwnd,
            uID = 1
        };
        return Shell_NotifyIconGetRect(ref id, out var bounds) == 0
            ? bounds
            : new Rect
            {
                Left = fallbackPoint.X,
                Top = fallbackPoint.Y,
                Right = fallbackPoint.X + 2,
                Bottom = fallbackPoint.Y + 2
            };
    }

    private void ShowNativeContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        var glyphs = new List<nint>();   // 每个菜单项的单色图标位图, 用完统一销毁
        nint subnet = 0;
        try
        {
            // 1) 显示窗口
            AppendMenu(menu, MfString, IdShow, "显示窗口");
            SetItemGlyph(menu, IdShow, GlyphWindow, glyphs);

            AppendMenu(menu, MfSeparator, 0, null);

            // 2) 状态行: 置灰不可点, 只作信息展示
            if (_statusReader is not null)
            {
                var (envLight, brightness) = _statusReader();
                // -1 是"还没有数据"的哨兵值: 引擎刚启动, 摄像头还没出第一帧.
                string text = envLight < 0 || brightness < 0
                    ? (_isEngineRunning() ? "环境光 测量中…" : "环境光 — | 亮度 —")
                    : $"环境光 {envLight} | 亮度 {brightness}%";
                AppendMenu(menu, MfString | MfGrayed, IdStatus, text);
            }

            AppendMenu(menu, MfSeparator, 0, null);

            // 3) 开始/停止
            AppendMenu(menu, MfString, IdToggle,
                _isEngineRunning() ? "停止自动调节" : "开始自动调节");
            SetItemGlyph(menu, IdToggle, GlyphSun, glyphs);

            // 4) 无人检测子菜单(带勾选状态)
            if (_togglesReader is not null)
            {
                var (dim, lockScreen, sedentary) = _togglesReader();
                subnet = CreatePopupMenu();
                AppendMenu(subnet, MfString | (dim ? MfChecked : 0), IdDimWhenAway, "无人时降低亮度");
                SetItemGlyph(subnet, IdDimWhenAway, GlyphEye, glyphs);
                AppendMenu(subnet, MfString | (lockScreen ? MfChecked : 0), IdLockWhenAway, "无人时自动锁屏");
                SetItemGlyph(subnet, IdLockWhenAway, GlyphPower, glyphs);
                AppendMenu(subnet, MfSeparator, 0, null);
                AppendMenu(subnet, MfString | (sedentary ? MfChecked : 0), IdSedentary, "久坐提醒");
                SetItemGlyph(subnet, IdSedentary, GlyphBell, glyphs);
                AppendMenu(menu, MfPopup, (uint)subnet, "无人检测");
            }

            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, IdExit, "退出程序");
            SetItemGlyph(menu, IdExit, GlyphPower, glyphs);

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            uint command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd,
                point.X, point.Y, _hwnd, 0);

            switch (command)
            {
                case IdShow: _showWindow(); break;
                case IdToggle: _toggleEngine(); break;
                case IdExit: _exitApplication(); break;
                case IdDimWhenAway: _toggleDimWhenAway?.Invoke(); break;
                case IdLockWhenAway: _toggleLockWhenAway?.Invoke(); break;
                case IdSedentary: _toggleSedentary?.Invoke(); break;
            }
        }
        finally
        {
            // 子菜单: DestroyMenu 父菜单时会一并销毁, 这里只需在未挂上时单独清理.
            if (subnet != 0) { /* 已挂到父菜单, 交给 DestroyMenu(menu) */ }
            DestroyMenu(menu);
            foreach (var h in glyphs) DeleteObject(h);
        }
    }

    /// <summary>
    /// 给菜单项挂一个 Segoe MDL2 字形图标.
    /// 用<b>单色</b>位图: Windows 把它当遮罩, 用菜单文字色绘制前景,
    /// 深浅主题自动适配, 不需要自己判断主题。
    /// </summary>
    private static void SetItemGlyph(nint menu, uint id, string glyph, List<nint> keepAlive)
    {
        try
        {
            var hbmp = CreateGlyphBitmap(glyph, 16);
            if (hbmp == 0) return;
            if (SetMenuItemBitmaps(menu, id, MfByCommand, hbmp, hbmp))
                keepAlive.Add(hbmp);
            else
                DeleteObject(hbmp);
        }
        catch { /* 图标失败不影响菜单可用 */ }
    }

    private static nint CreateGlyphBitmap(string glyph, int size)
    {
        nint dc = CreateCompatibleDC(0);
        if (dc == 0) return 0;
        // 单色位图: 1 = 用菜单文字色绘制, 0 = 透明.
        nint bmp = CreateMonoBitmap(size, size, 1, 1, null);
        nint oldBmp = 0, oldFont = 0;
        nint font = 0;
        try
        {
            if (bmp == 0) return 0;
            oldBmp = SelectObject(dc, bmp);

            // 背景刷成 0(透明), 字形刷成 1(前景色).
            var rect = new Rect { Left = 0, Top = 0, Right = size, Bottom = size };
            PatBlt(dc, 0, 0, size, size, Blackness);
            SetBkMode(dc, Transparent);
            SetTextColor(dc, 0x00FFFFFF);   // 单色 DC 里非黑即白: 白=1

            font = CreateFont(-size, 0, 0, 0, 400, 0, 0, 0, DefaultCharset,
                OutTtPrecis, ClipDefaultPrecis, DefaultQuality, DefaultPitch,
                "Segoe MDL2 Assets");
            if (font == 0) return 0;
            oldFont = SelectObject(dc, font);

            DrawText(dc, glyph, -1, ref rect,
                DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);

            SelectObject(dc, oldFont);
            oldFont = 0;
            SelectObject(dc, oldBmp);
            oldBmp = 0;
            return bmp;
        }
        finally
        {
            if (oldFont != 0) SelectObject(dc, oldFont);
            if (oldBmp != 0) SelectObject(dc, oldBmp);
            if (font != 0) DeleteObject(font);
            DeleteDC(dc);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menuWindow?.Close();
        _menuWindow = null;
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
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

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
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);
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

    // ---- 菜单图标(单色位图)相关 ----
    private const uint Blackness = 0x00000042;
    private const int Transparent = 1;
    private const uint DefaultCharset = 1;
    private const uint OutTtPrecis = 0;
    private const uint ClipDefaultPrecis = 0;
    private const uint DefaultQuality = 0;
    private const uint DefaultPitch = 0;
    private const uint DtCenter = 0x0001;
    private const uint DtVCenter = 0x0004;
    private const uint DtSingleLine = 0x0020;
    private const uint DtNoPrefix = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetMenuItemBitmaps(nint menu, uint item, uint flags, nint uncheckedBmp, nint checkBmp);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll", EntryPoint = "CreateBitmap")]
    private static extern nint CreateMonoBitmap(int width, int height, uint planes, uint bitsPerPixel,
        [In] byte[]? bits);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(nint hdc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFont(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(nint hdc, string text, int count, ref Rect rect, uint flags);

}
