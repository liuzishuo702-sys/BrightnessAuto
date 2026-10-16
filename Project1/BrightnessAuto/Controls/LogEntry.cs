using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BrightnessAuto.Controls;

/// <summary>一条日志. 警告/错误行用暖橙色, 其余用暗灰色(对应 C++ 版本日志着色).</summary>
public sealed class LogEntry
{
    // 全局当前主题(主窗口切换时更新), 决定普通日志文字色.
    public static bool IsDarkTheme { get; set; } = true;

    public string Text { get; }
    private readonly bool _warn;

    public LogEntry(string text)
    {
        Text = text;
        _warn = text.Contains("[错误]") || text.Contains("[警告]");
    }

    // 每次绑定求值时按当前主题取色: 警告=暖橙(两主题通用),
    // 普通=随主题的暗灰(浅色下加深以保证可读).
    public Brush Brush => new SolidColorBrush(
        _warn
            ? (IsDarkTheme ? Color.FromArgb(255, 240, 170, 90) : Color.FromArgb(255, 200, 90, 20))
            : (IsDarkTheme ? Color.FromArgb(255, 165, 165, 170) : Color.FromArgb(255, 95, 95, 102)));
}
