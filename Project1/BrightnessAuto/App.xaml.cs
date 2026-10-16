using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;

namespace BrightnessAuto;

/// <summary>
/// 应用入口. 创建并激活主窗口.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // 未处理异常写到 %LocalAppData%\BrightnessAuto\crash.log, 便于排查闪退.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 自动启动 LightBulb 色温调节（如果没在运行）
        try
        {
            string lightBulbPath = @"C:\Program Files (x86)\LightBulb\LightBulb.exe";
            if (File.Exists(lightBulbPath) && Process.GetProcessesByName("LightBulb").Length == 0)
            {
                Process.Start(new ProcessStartInfo { FileName = lightBulbPath, UseShellExecute = true });
            }
        }
        catch { /* 不影响主程序 */ }

        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI", e.Exception);
        e.Handled = false; // 仍按致命处理, 但已落盘
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Domain", e.ExceptionObject as Exception);
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrightnessAuto");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "crash.log");
            string body = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            File.AppendAllText(path, body);
            Debug.WriteLine(body);
        }
        catch { /* 日志失败不影响进程退出路径 */ }
    }
}
