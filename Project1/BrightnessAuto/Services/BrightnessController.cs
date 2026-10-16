using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Management;
using Windows.Graphics.Display;

namespace BrightnessAuto.Services;

/// <summary>
/// 屏幕亮度控制. 通过 root\WMI 的 WmiMonitorBrightness 读取当前亮度,
/// 调用 WmiMonitorBrightnessMethods.WmiSetBrightness 设置亮度.
/// 对应命令行/GUI C++ 版本中的 ConnectWmi / GetCurrentBrightness / SetBrightness.
/// 仅在支持软件调节亮度的笔记本(内置显示器)上可用.
/// </summary>
public sealed class BrightnessController : IDisposable
{
    private readonly ManagementScope _scope;
    private bool _connected;
    private ManagementObject? _methodObj;   // 缓存 WmiMonitorBrightnessMethods 单例
    private BrightnessOverride? _systemBrightness;

    /// <summary>亮度过渡时间(秒). 由引擎在每次设置前更新.</summary>
    public int FadeSeconds { get; set; } = 2;

    public BrightnessController()
    {
        // root\WMI 命名空间承载 WmiMonitorBrightness* 类.
        _scope = new ManagementScope(@"root\WMI");
    }

    /// <summary>建立 WMI 连接. 失败(不支持/无权限)时返回 false.</summary>
    public bool Connect()
    {
        try
        {
            // 内置屏存在时禁用虚拟覆盖层，否则它会盖住真实硬件亮度。
            using var laptop = OpenLaptopDisplay();
            if (laptop.IsInvalid)
            {
                var systemBrightness = BrightnessOverride.GetDefaultForSystem();
                if (systemBrightness.IsSupported)
                    _systemBrightness = systemBrightness;
            }
        }
        catch
        {
            _systemBrightness = null;
        }

        try
        {
            _scope.Connect();
            _connected = _scope.IsConnected;
        }
        catch (Exception)
        {
            _connected = false;
        }
        return _systemBrightness is not null || _connected || TryGetDxvaBrightness() >= 0;
    }

    /// <summary>读取当前亮度百分比(0-100), 失败返回 -1.</summary>
    /// <remarks>不缓存实例对象: WmiMonitorBrightness 属性可能不会自动刷新.</remarks>
    public int GetCurrentBrightness()
    {
        var laptop = TryGetLaptopBrightness();
        if (laptop >= 0) return laptop;
        if (_systemBrightness is not null)
        {
            try { return (int)Math.Round(_systemBrightness.BrightnessLevel * 100); }
            catch { _systemBrightness = null; }
        }

        if (!_connected) return TryGetDxvaBrightness();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                _scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var val = obj["CurrentBrightness"];
                    if (val != null)
                        return Convert.ToInt32(val);
                }
            }
        }
        catch (Exception)
        {
            // 某些显示器不暴露 WmiMonitorBrightness, 视为不可读.
        }
        return TryGetDxvaBrightness();
    }

    /// <summary>设置亮度百分比(自动裁剪到 0-100). 返回是否成功.</summary>
    public bool SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        // 内置屏优先走驱动硬件接口，避免 BrightnessOverride 只反映虚拟值。
        if (TrySetLaptopBrightness(percent))
        {
            var actual = TryGetLaptopBrightness();
            if (actual >= 0 && Math.Abs(actual - percent) <= 2) return true;
        }

        if (_systemBrightness is not null)
        {
            try
            {
                if (!_systemBrightness.IsOverrideActive)
                    _systemBrightness.StartOverride();
                _systemBrightness.SetBrightnessLevel(
                    percent / 100.0,
                    DisplayBrightnessOverrideOptions.None);
                var actual = TryGetLaptopBrightness();
                return actual < 0 || Math.Abs(actual - percent) <= 2;
            }
            catch
            {
                try { _systemBrightness.StopOverride(); } catch { }
                _systemBrightness = null;
            }
        }

        if (!_connected) return TrySetLaptopBrightness(percent) || TrySetDxvaBrightness(percent);

        // 优先用缓存的方法对象; 失败时回退到重新查询所有显示器.
        if (_methodObj != null && TryInvokeSetBrightness(_methodObj, percent))
            return true;

        _methodObj?.Dispose();
        _methodObj = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                _scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject obj in searcher.Get())
            {
                // 不同显示器的 WMI 能力不同: 外接/虚拟屏可能返回失败码,
                // 继续尝试下一个, 只缓存真正成功的对象.
                if (TryInvokeSetBrightness(obj, percent))
                {
                    _methodObj = obj;
                    return true;
                }
                obj.Dispose();
            }
        }
        catch (Exception)
        {
            // 设置失败(权限/瞬时错误), 交由调用方记录日志.
        }
        return TrySetLaptopBrightness(percent) || TrySetDxvaBrightness(percent);
    }

    private static bool TrySetLaptopBrightness(int percent)
    {
        using var lcd = OpenLaptopDisplay(); if (lcd.IsInvalid) return false;
        var value = new DISPLAY_BRIGHTNESS { DisplayPolicy = 0, ACBrightness = (byte)Math.Clamp(percent, 0, 100), DCBrightness = (byte)Math.Clamp(percent, 0, 100) };
        return DeviceIoControl(lcd, IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS, ref value, Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
    private static int TryGetLaptopBrightness()
    {
        using var lcd = OpenLaptopDisplay(); if (lcd.IsInvalid) return -1;
        var value = new DISPLAY_BRIGHTNESS();
        return DeviceIoControl(lcd, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS, IntPtr.Zero, 0, ref value, Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), out _, IntPtr.Zero)
            ? Math.Max(value.ACBrightness, value.DCBrightness) : -1;
    }
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenLaptopDisplay() => CreateFile(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS = 0x00230498, IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS = 0x0023049C;
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAY_BRIGHTNESS { public byte DisplayPolicy; public byte ACBrightness; public byte DCBrightness; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint controlCode, ref DISPLAY_BRIGHTNESS input, int inputSize, IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint controlCode, IntPtr input, int inputSize, ref DISPLAY_BRIGHTNESS output, int outputSize, out int bytesReturned, IntPtr overlapped);

    // 部分笔记本驱动不注册 root\\WMI 的 WmiMonitorBrightnessMethods,
    // 改用 Windows DXVA2 物理监视器接口作为 fallback.
    private static int TryGetDxvaBrightness()
    {
        foreach (var monitor in EnumeratePhysicalMonitors())
        {
            try
            {
                if (GetMonitorBrightness(monitor, out _, out var current, out _))
                    return (int)Math.Min(current, 100u);
            }
            finally { DestroyPhysicalMonitor(monitor); }
        }
        return -1;
    }

    private static bool TrySetDxvaBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        foreach (var monitor in EnumeratePhysicalMonitors())
        {
            try
            {
                if (!GetMonitorBrightness(monitor, out _, out _, out var maximum)) continue;
                if (SetMonitorBrightness(monitor, (uint)Math.Round(maximum * percent / 100.0)))
                    return true;
            }
            finally { DestroyPhysicalMonitor(monitor); }
        }
        return false;
    }

    private static IEnumerable<IntPtr> EnumeratePhysicalMonitors()
    {
        var result = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                return true;
            var monitors = new PHYSICAL_MONITOR[count];
            if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                for (int i = 0; i < monitors.Length; i++) result.Add(monitors[i].hPhysicalMonitor);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint newBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    private bool TryInvokeSetBrightness(ManagementObject obj, int percent)
    {
        try
        {
            var inParams = obj.GetMethodParameters("WmiSetBrightness");
            inParams["Timeout"] = (uint)Math.Max(0, FadeSeconds);
            inParams["Brightness"] = (byte)percent;
            using var outParams = obj.InvokeMethod("WmiSetBrightness", inParams, null);
            return outParams?["ReturnValue"] is not null
                && Convert.ToUInt32(outParams["ReturnValue"]) == 0;
        }
        catch (Exception)
        {
            // 缓存对象失效时丢弃, 下次重新查询.
            if (ReferenceEquals(obj, _methodObj))
            {
                _methodObj?.Dispose();
                _methodObj = null;
            }
            return false;
        }
    }

    public void Dispose()
    {
        if (_systemBrightness is not null)
        {
            try { _systemBrightness.StopOverride(); } catch { }
            _systemBrightness = null;
        }
        _methodObj?.Dispose();
        _methodObj = null;
        _connected = false;
    }
}
