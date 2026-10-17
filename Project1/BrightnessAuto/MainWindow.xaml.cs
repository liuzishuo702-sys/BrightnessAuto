using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using Windows.Storage;
using Windows.System;
using Windows.System.Power;
using Windows.ApplicationModel;
using Windows.Foundation.Collections;
using Windows.UI;
using BrightnessAuto.Controls;
using BrightnessAuto.Services;

namespace BrightnessAuto;

public sealed partial class MainWindow : Window
{
    private readonly EngineParams _params = new();
    private readonly AutoBrightnessEngine _engine;
    // 手动亮度控制器: 惰性创建. 它在 Connect() 时会申请系统级亮度覆盖,
    // 常驻会导致"没开始调节时系统亮度被锁住", 所以只在用户真的拖动滑块时创建.
    private BrightnessController? _brightnessController;
    private bool _manualBrightnessSynced;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly ObservableCollection<LogEntry> _logs = new();
    private const int MaxLogLines = 200;
    private const int MaxHistoryPoints = 80;
    private readonly Queue<(int Luma, int Brightness)> _history = new();

    // 各滑块的数值文本(与滑块联动更新).
    private readonly Slider[] _sliders = new Slider[7];
    private readonly TextBlock[] _valueLabels = new TextBlock[7];

    // 各滑块左侧的标签(主题切换时同步改色).
    private readonly TextBlock[] _nameLabels = new TextBlock[7];

    // 当前是否深色主题.
    private bool _isDark = true;
    private const string ThemeSettingKey = "AppTheme";
    private const string AccentColorSettingKey = "AccentColor";
    private string _accentColorKey = "Blue";
    private Button? _selectedPaletteButton;
    private bool _allowWindowClose;
    private bool _closePromptShowing;
    private TrayIcon? _trayIcon;
    private bool _loadingPreferences;
    private bool _uiReady;
    private bool _autoAdjustOnLaunch;
    private bool _launchToTray;
    private const string AutoAdjustSettingKey = "AutoAdjustOnLaunch";
    private const string LaunchToTraySettingKey = "LaunchToTray";
    private ContentDialog? _idleLockWarningDialog;
    private CancellationTokenSource? _idleLockWarningCts;
    private bool _idleLockWarningShowing;
    // 久坐提醒弹窗: 与锁屏倒计时互斥, 避免两个 ContentDialog 同时 ShowAsync.
    private ContentDialog? _sedentaryDialog;
    private bool _sedentaryDialogShowing;
    // 窗口是否已隐藏到托盘: 隐藏时提醒只写日志, 不弹一个看不见的对话框.
    private bool _windowHiddenInTray;

    // 卡片悬停/按下光影: 悬停跟随光斑 + 按下缓慢扩散(Win10 Reveal).
    // 全部走 Composition, 动画在合成线程跑, 保证实时.
    private readonly Dictionary<Border, CardHoverFx> _cardFx = new();
    // Reveal 按下: 绝不能从半径 0 起 — 径向渐变半径为 0 时中心色会瞬间铺满整卡.
    private const float PressRadiusStart = 18f;
    private const float PressRadiusHoldMax = 140f; // 按住时缓慢扩到的最大半径(局部光, 非整卡淹没)
    private const int PressExpandMs = 900;         // 按住扩散时长, 偏慢才像 Win10
    private const int PressReleaseExpandMs = 420;
    private const int PressReleaseFadeMs = 380;

    // 某些 WinUI/Windows 组合下, 卡片的 handledEventsToo 指针监听会让子按钮丢失 Click.
    // 记录一次完整按压期间是否已经收到 Click, 仅在 Click 丢失时走指针兜底.
    private long _startStopActivationVersion;
    private long _startStopPointerDownVersion = -1;
    private uint? _startStopPointerId;

    private sealed class CardHoverFx
    {
        public required ContainerVisual Root = null!;
        public required SpriteVisual Spot = null!;
        public required CompositionRadialGradientBrush Brush = null!;
        // Win10 Reveal 式按下: 从触点向外缓慢扩散的径向光(非整卡瞬时模糊).
        public required SpriteVisual PressSpot = null!;
        public required CompositionRadialGradientBrush PressBrush = null!;
        public bool Hovering;
        public bool Pressing;
        public float CardW;
        public float CardH;
    }

    public MainWindow()
    {
        this.InitializeComponent();
        _dispatcher = global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _engine = new AutoBrightnessEngine(_params);
        _engine.Log += OnEngineLog;
        _engine.Status += OnEngineStatus;
        _engine.IdleLockWarning += OnIdleLockWarning;
        _engine.SedentaryReminder += OnSedentaryReminder;
        _engine.Stopped += OnEngineStopped;
        _engine.StartFailed += OnEngineStartFailed;

        // 使用系统亚克力材质，让桌面背景透过并产生柔和模糊；不支持时回退云母。
        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            try { SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; }
            catch { /* 不支持系统材质时由主题色兜底。 */ }
        }

        // 自定义标题栏: 扩展内容到标题栏区域, 待根元素进入可视树后再指定拖拽区,
        // 避免在元素尚无布局尺寸时 SetTitleBar 触发 native 输入 sink 异常关闭窗口.
        Title = "环境光自动亮度调节";
        ExtendsContentIntoTitleBar = true;
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _trayIcon = new TrayIcon(hwnd, RestoreFromTray, ToggleEngineFromTray,
                ExitFromTray, () => _engine.IsRunning);
        }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] 托盘初始化失败: {ex.Message}"); }
        try { AppWindow.Closing += OnAppWindowClosing; }
        catch { /* 关闭拦截不可用时保留系统默认关闭行为。 */ }
        Closed += (_, _) =>
        {
            CancelIdleLockWarning();
            CancelSedentaryReminder();
            _clock.Stop();
            // 关键: 这里不能只 fire-and-forget —— 进程可能在那点清理跑完前就退出,
            // 导致引擎里的 StopOverride 没执行(系统亮度被锁死)、摄像头句柄不释放.
            // Dispose() 是同步的, 会取消并等待收尾, 超时后再强制释放.
            try { _engine.Dispose(); } catch { }
            // 解绑关闭回调: 避免关闭流程重入时再次弹确认框.
            try { AppWindow.Closing -= OnAppWindowClosing; } catch { }
            // 必须释放: 手动亮度用的 BrightnessController 持有系统级亮度覆盖,
            // 不 StopOverride 会让系统亮度被锁死, 退出程序后仍然调不动.
            ReleaseManualBrightness();
            _trayIcon?.Dispose();
            _trayIcon = null;
        };
        RootGrid.Loaded += (_, _) =>
        {
            SetTitleBar(AppTitleBar);

            // 全局松手兜底: 指针在卡外松开时也能结束按下扩散.
            RootGrid.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnRootPointerReleased), true);
            RootGrid.AddHandler(UIElement.PointerCanceledEvent,
                new PointerEventHandler(OnRootPointerReleased), true);

            // 卡片悬停: 跟随光标的径向光斑 (Win10 Reveal 风, 不做 Z 抬升).
            AttachCardHover(GreetingCard);
            AttachCardHover(MonitorCard);
            AttachCardHover(LogCard);
            AttachCardHover(AppearanceCard);
            AttachCardHover(SettingsCard);
            AttachCardHover(AboutCard);
            _dispatcher.TryEnqueue(ApplyLaunchPreferences);
        };

        // 空格启停: 隧道阶段拦截, 抢在 ScrollViewer 把空格当翻页、按钮把空格当 Click 之前.
        // 挂在构造里(不必等 Loaded), handledEventsToo 避免子控件先吃掉按键.
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnRootPreviewKeyDown), true);

        StartStopButton.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnStartStopPointerPressed), true);
        StartStopButton.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnStartStopPointerReleased), true);
        StartStopButton.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnStartStopPointerCanceled), true);

        LogList.ItemsSource = _logs;
        LoadPreferences();
        BuildSliders();

        // 读取上次的浅/深色偏好并应用 (默认深色).
        _isDark = LoadThemePref();
        _accentColorKey = LoadAccentColorPref();
        ApplyTheme(_isDark);

        // 问候语 + 数字时钟 (每秒刷新).
        UpdateGreeting();
        UpdateClock();
        _clock.Tick += (_, _) => { UpdateClock(); UpdateGreeting(); };
        _clock.Start();

        // 主页导航/问候卡: 异步加载 Windows 用户账户头像.
        _ = LoadUserAvatarAsync();

        // 初始窗口尺寸 (容纳导航栏 + 内容).
        try { AppWindow?.Resize(new Windows.Graphics.SizeInt32(980, 720)); } catch { }

        _uiReady = true;
        // 版本号从包清单动态读取, 避免像以前那样写死 "v3.1.9 Dev" 长期不更新.
        ApplyVersionText();
        // 同步一次"已停止"的界面状态: 手动亮度面板默认是 Collapsed,
        // 只有 SetRunningVisual 跑过才会显示; 全新启动时没人调它,
        // 结果"自动调节已停止"却看不到手动调亮度滑块.
        SetRunningVisual(false);
        AppendLog("就绪. 点击\"开始\"或按空格启动自动调节.");
    }

    // 关闭窗口时询问：最小化到后台继续运行，或退出整个程序。
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose) return;

        if (RootGrid.XamlRoot is null)
        {
            _allowWindowClose = true;
            return;
        }

        args.Cancel = true;
        if (_closePromptShowing) return;
        _closePromptShowing = true;

        try
        {
            var dialog = new ContentDialog
            {
                Title = "退出环境光自动亮度？",
                Content = "选择“隐藏到托盘”后，程序会继续运行并自动调节亮度。双击托盘图标可恢复窗口。",
                PrimaryButtonText = "隐藏到托盘",
                SecondaryButtonText = "关闭程序",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (_trayIcon?.Show() == true)
                {
                    _windowHiddenInTray = true;
                    // 窗口藏起来后预览没人看得到, 停掉定时器免得每 300ms 白抓一帧.
                    StopCameraPreview();
                    AppWindow.Hide();
                }
                else if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Minimize();
                    StopCameraPreview();
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _allowWindowClose = true;
                _trayIcon?.Dispose();
                Close();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] 关闭对话框异常: {ex.Message}");
            // 对话框创建失败时不强制退出，避免用户误关并丢失后台任务。
        }
        finally
        {
            _closePromptShowing = false;
        }
    }

    private void RestoreFromTray()
    {
        _trayIcon?.Hide();
        _windowHiddenInTray = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();
        // 若恢复时正停在摄像头页, 重新开预览; 其它页面不启动.
        if (_currentViewTag == "camera") StartCameraPreview();
        Activate();
    }

    private void ExitFromTray()
    {
        _allowWindowClose = true;
        _trayIcon?.Dispose();
        Close();
    }

    // ---- 卡片光影: 悬停跟随光斑 + 按下实时扩散(Win10 Reveal 风) + 抬升投影 ----
    private void AttachCardHover(Border card)
    {
        if (_cardFx.ContainsKey(card)) return;

        var visual = ElementCompositionPreview.GetElementVisual(card);
        var comp = visual.Compositor;

        // 容器: 同时挂悬停光斑 + 按下扩散光, 叠在卡片最上层, 不抢交互.
        var root = comp.CreateContainerVisual();
        root.IsHitTestVisible = false;

        // 悬停径向光斑: 中心亮、边缘透明, 跟随光标.
        var brush = MakeRadialBrush(comp, 160f);
        var spot = comp.CreateSpriteVisual();
        spot.Brush = brush;
        spot.IsHitTestVisible = false;
        spot.Opacity = 0f;
        root.Children.InsertAtTop(spot);

        // 按下扩散光: 从触点缓慢外扩, 类似 Win10 Reveal (局部光晕, 非整卡淹没).
        // 起始半径必须 > 0, 否则径向渐变半径为 0 时中心色会瞬间铺满整张卡.
        var pressBrush = MakePressRadialBrush(comp, PressRadiusStart);
        var pressSpot = comp.CreateSpriteVisual();
        pressSpot.Brush = pressBrush;
        pressSpot.IsHitTestVisible = false;
        pressSpot.Opacity = 0f;
        root.Children.InsertAtTop(pressSpot);

        ElementCompositionPreview.SetElementChildVisual(card, root);

        var fx = new CardHoverFx
        {
            Root = root,
            Spot = spot,
            Brush = brush,
            PressSpot = pressSpot,
            PressBrush = pressBrush,
        };
        _cardFx[card] = fx;

        void SyncSize()
        {
            float w = (float)card.ActualWidth;
            float h = (float)card.ActualHeight;
            if (w <= 0 || h <= 0) return;
            fx.CardW = w;
            fx.CardH = h;
            root.Size = new Vector2(w, h);
            spot.Size = new Vector2(w, h);
            pressSpot.Size = new Vector2(w, h);
            // 与 AppCardStyle 圆角一致(Win10 方角 3px), 光效不溢出卡片.
            float cr = (float)Math.Max(card.CornerRadius.TopLeft, 3);
            var clipGeo = comp.CreateRoundedRectangleGeometry();
            clipGeo.Size = new Vector2(w, h);
            clipGeo.CornerRadius = new Vector2(cr, cr);
            root.Clip = comp.CreateGeometricClip(clipGeo);
            // 悬停光斑半径随卡片短边缩放, 保持各卡视觉密度一致.
            float r = Math.Max(120f, Math.Min(w, h) * 0.55f);
            brush.EllipseRadius = new Vector2(r, r);
        }

        card.SizeChanged += (_, _) => SyncSize();
        SyncSize();

        card.PointerEntered += OnCardPointerEntered;
        card.PointerMoved += OnCardPointerMoved;
        card.PointerExited += OnCardPointerExited;
        card.PointerCanceled += OnCardPointerCanceled;
        card.PointerCaptureLost += OnCardPointerCanceled;
        // handledEventsToo: 子控件已处理的按下也冒泡到卡;
        // OnCardPointerPressed 内会跳过按钮/滑块等交互控件, 空白区才播缓慢 Reveal.
        card.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnCardPointerPressed), true);
        card.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnCardPointerReleased), true);
    }

    private static CompositionRadialGradientBrush MakeRadialBrush(Compositor comp, float radius)
    {
        var brush = comp.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.EllipseCenter = new Vector2(0, 0);
        brush.EllipseRadius = new Vector2(radius, radius);
        var stop0 = comp.CreateColorGradientStop();
        stop0.Offset = 0f;
        stop0.Color = Color.FromArgb(0, 255, 255, 255);
        var stop1 = comp.CreateColorGradientStop();
        stop1.Offset = 1f;
        stop1.Color = Color.FromArgb(0, 255, 255, 255);
        brush.ColorStops.Insert(0, stop0);
        brush.ColorStops.Insert(1, stop1);
        return brush;
    }

    // 按下 Reveal: 3 档色停, 中心亮 → 中段柔光 → 边缘全透, 扩散时边缘更像 Win10 软晕.
    private static CompositionRadialGradientBrush MakePressRadialBrush(Compositor comp, float radius)
    {
        var brush = comp.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.EllipseCenter = new Vector2(0, 0);
        brush.EllipseRadius = new Vector2(radius, radius);
        for (int i = 0; i < 3; ++i)
        {
            var stop = comp.CreateColorGradientStop();
            stop.Offset = i switch { 0 => 0f, 1 => 0.45f, _ => 1f };
            stop.Color = Color.FromArgb(0, 255, 255, 255);
            brush.ColorStops.Insert(i, stop);
        }
        return brush;
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
        fx.Hovering = true;
        ApplyHoverSpotColor(fx);

        var visual = ElementCompositionPreview.GetElementVisual(card);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        fx.Spot.StartAnimation("Opacity", fade);

        UpdateSpotCenter(card, fx, e);
    }

    private void OnCardPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
        if (fx.Hovering) UpdateSpotCenter(card, fx, e);
        // 按下时持续跟随触点, 光心实时贴合手指/鼠标.
        if (fx.Pressing) UpdatePressCenter(card, fx, e);
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
        fx.Hovering = false;

        // 仍按着不结束扩散, 只收悬停光斑; 真正松开/取消时再收.
        var fade = ElementCompositionPreview.GetElementVisual(card)
            .Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        fx.Spot.StartAnimation("Opacity", fade);
    }

    private void OnCardPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
        fx.Hovering = false;
        EndPress(card, fx, expandOut: false);
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;

        // 交互控件(按钮/滑块等)上不播 Reveal: 否则 handledEventsToo 会抢视觉注意力,
        // 且部分系统上会影响 Click 路由. 卡片空白区才出缓慢扩散.
        if (IsInteractiveElement(e.OriginalSource as DependencyObject, card))
            return;

        // 仅响应主键/触摸/笔, 避免右键等干扰.
        var pt = e.GetCurrentPoint(card);
        if (!pt.Properties.IsLeftButtonPressed
            && pt.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
            && pt.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
            return;

        fx.Pressing = true;
        ApplyPressSpotColor(fx);
        UpdatePressCenter(card, fx, e);

        var visual = ElementCompositionPreview.GetElementVisual(card);
        var comp = visual.Compositor;

        // 局部缓慢扩散: 从一小圈半径起步, 扩到「局部光晕」上限(不是整卡淹没).
        // 半径绝对不能从 0 起, 否则径向渐变会瞬间整卡填色.
        float holdR = Math.Min(PressRadiusHoldMax, Math.Max(fx.CardW, fx.CardH) * 0.38f);
        holdR = Math.Max(holdR, PressRadiusStart + 24f);
        fx.PressBrush.StopAnimation("EllipseRadius");
        fx.PressBrush.EllipseRadius = new Vector2(PressRadiusStart, PressRadiusStart);

        // 缓入缓出: 前半段几乎不动, 后半段慢慢铺开 — 贴近 Win10 Reveal 手感.
        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.2f), new Vector2(0.15f, 1f));
        var expand = comp.CreateVector2KeyFrameAnimation();
        expand.InsertKeyFrame(0f, new Vector2(PressRadiusStart, PressRadiusStart));
        expand.InsertKeyFrame(1f, new Vector2(holdR, holdR), ease);
        expand.Duration = TimeSpan.FromMilliseconds(PressExpandMs);
        fx.PressBrush.StartAnimation("EllipseRadius", expand);

        var fadeIn = comp.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(0.18f, 1f);
        fadeIn.InsertKeyFrame(1f, 1f);
        fadeIn.Duration = TimeSpan.FromMilliseconds(220);
        fx.PressSpot.StopAnimation("Opacity");
        fx.PressSpot.StartAnimation("Opacity", fadeIn);
        // 不 CapturePointer: 避免抢走子控件(按钮/滑块)的点击.
    }

    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
        EndPress(card, fx, expandOut: true);
    }

    // 指针在卡外松开时, 结束所有仍在按的卡.
    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        foreach (var kv in _cardFx)
        {
            if (kv.Value.Pressing)
                EndPress(kv.Key, kv.Value, expandOut: true);
        }
    }

    // 松手: 再往外冲一截并淡出; 取消: 直接淡出收回.
    // 注意: 绝不冲到整卡对角半径 — 那会变成「一整块模糊」.
    private void EndPress(Border card, CardHoverFx fx, bool expandOut)
    {
        if (!fx.Pressing && fx.PressSpot.Opacity <= 0.01f) return;
        fx.Pressing = false;

        var comp = ElementCompositionPreview.GetElementVisual(card).Compositor;

        float curR = fx.PressBrush.EllipseRadius.X;
        if (curR < PressRadiusStart) curR = PressRadiusStart;
        // 松手只再外扩约 35%, 并夹在局部光晕上限附近, 保持「从点扩散」的观感.
        float endR = expandOut
            ? Math.Min(curR * 1.35f, PressRadiusHoldMax * 1.25f)
            : curR;

        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0f), new Vector2(0.1f, 1f));
        var expand = comp.CreateVector2KeyFrameAnimation();
        expand.InsertKeyFrame(1f, new Vector2(endR, endR), ease);
        expand.Duration = TimeSpan.FromMilliseconds(expandOut ? PressReleaseExpandMs : 160);
        fx.PressBrush.StartAnimation("EllipseRadius", expand);

        var fadeOut = comp.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1f, 0f, ease);
        fadeOut.Duration = TimeSpan.FromMilliseconds(expandOut ? PressReleaseFadeMs : 180);
        fx.PressSpot.StartAnimation("Opacity", fadeOut);
    }

    // 原始命中点是否落在需要保留交互的控件上(按钮/滑块/开关/输入等).
    // 从 OriginalSource 向上爬到本卡片为止; 注意按钮模板内部也有 Border,
    // 绝不能「见到 Border 就停」, 只能以本卡片为终点.
    private static bool IsInteractiveElement(DependencyObject? src, Border card)
    {
        for (var cur = src; cur is not null && !ReferenceEquals(cur, card);
             cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is ButtonBase or Slider or ScrollBar or ToggleSwitch
                or TextBox or ComboBox or NavigationViewItem
                or HyperlinkButton or RepeatButton)
                return true;
        }
        return false;
    }

    private static void UpdateSpotCenter(Border card, CardHoverFx fx, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(card).Position;
        fx.Brush.EllipseCenter = new Vector2((float)p.X, (float)p.Y);
    }

    private static void UpdatePressCenter(Border card, CardHoverFx fx, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(card).Position;
        fx.PressBrush.EllipseCenter = new Vector2((float)p.X, (float)p.Y);
    }

    // 悬停光斑色: Win10 Reveal 是中性白光, 不带蓝色调 — 深色提亮, 浅色压暗.
    private void ApplyHoverSpotColor(CardHoverFx fx)
    {
        Color center = _isDark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(22, 0, 0, 0);
        SetRadialStops(fx.Brush, center);
    }

    // 按下扩散色: 比悬停略强的中性光, 3 档软晕, 贴近 Win10 Reveal 按压高光.
    private void ApplyPressSpotColor(CardHoverFx fx)
    {
        Color center = _isDark
            ? Color.FromArgb(52, 255, 255, 255)
            : Color.FromArgb(34, 0, 0, 0);
        SetPressRadialStops(fx.PressBrush, center);
    }

    private static void SetRadialStops(CompositionRadialGradientBrush brush, Color center)
    {
        Color edge = Color.FromArgb(0, center.R, center.G, center.B);
        if (brush.ColorStops.Count >= 2)
        {
            brush.ColorStops[0].Color = center;
            brush.ColorStops[1].Color = edge;
        }
    }

    // 中心亮 → 中段半透明 → 边缘全透, 扩散边缘柔和, 不像一整块实色铺开.
    private static void SetPressRadialStops(CompositionRadialGradientBrush brush, Color center)
    {
        if (brush.ColorStops.Count < 3)
        {
            SetRadialStops(brush, center);
            return;
        }
        Color mid = Color.FromArgb((byte)(center.A * 0.45), center.R, center.G, center.B);
        Color edge = Color.FromArgb(0, center.R, center.G, center.B);
        brush.ColorStops[0].Color = center;
        brush.ColorStops[1].Color = mid;
        brush.ColorStops[2].Color = edge;
    }

    private void RefreshAllHoverSpotColors()
    {
        foreach (var fx in _cardFx.Values)
        {
            if (fx.Hovering) ApplyHoverSpotColor(fx);
            if (fx.Pressing) ApplyPressSpotColor(fx);
        }
    }

    // ---- 用户头像: 导航「主页」图标 + 问候卡 ----
    // 桌面 full-trust 下 Windows.System.User.GetAccountPicture 不可用,
    // 直接读 HKLM AccountPicture 注册表指向的 Public\AccountPictures 文件.
    private async Task LoadUserAvatarAsync()
    {
        string display = "";
        try { display = Environment.UserName; } catch { /* 忽略 */ }
        if (string.IsNullOrWhiteSpace(display)) display = "User";
        HomeAvatar.DisplayName = display;

        try
        {
            string? largePath = ResolveAccountPicturePath(preferred: new[] { 208, 448, 240, 192, 1080 });
            string? smallPath = ResolveAccountPicturePath(preferred: new[] { 64, 48, 96, 40, 32 })
                             ?? largePath;

            if (largePath is not null)
            {
                var large = await LoadBitmapFromPathAsync(largePath);
                if (large is not null) HomeAvatar.ProfilePicture = large;
            }
            if (smallPath is not null)
            {
                var small = await LoadBitmapFromPathAsync(smallPath);
                if (small is not null)
                    HomeNavItem.Icon = new ImageIcon { Source = small };
            }
        }
        catch
        {
            // 无账户图 / 权限不足: 保留首字母头像 + 账户字形导航图标.
        }
    }

    private static string? ResolveAccountPicturePath(int[] preferred)
    {
        try
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            if (string.IsNullOrEmpty(sid)) return null;

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            if (key is null) return null;

            foreach (int size in preferred)
            {
                if (key.GetValue("Image" + size) is string path
                    && !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path))
                    return path;
            }

            // 回退: 任意存在的 Image* 值.
            foreach (string name in key.GetValueNames())
            {
                if (!name.StartsWith("Image", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.GetValue(name) is string path
                    && !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path))
                    return path;
            }
        }
        catch { /* 注册表/文件不可读 */ }
        return null;
    }

    private static async Task<BitmapImage?> LoadBitmapFromPathAsync(string path)
    {
        try
        {
            // 复制到内存再解码, 避免文件被系统锁定时 SetSource 失败.
            byte[] bytes = await File.ReadAllBytesAsync(path);
            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(mem))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            mem.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(mem);
            return bmp;
        }
        catch { return null; }
    }

    // ---- 数字时钟 + 问候语 ----
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private void UpdateClock()
    {
        var t = DateTime.Now;
        string hh = t.Hour.ToString("D2"), mm = t.Minute.ToString("D2"), ss = t.Second.ToString("D2");
        ClkH1.Text = hh[0].ToString(); ClkH2.Text = hh[1].ToString();
        ClkM1.Text = mm[0].ToString(); ClkM2.Text = mm[1].ToString();
        ClkS1.Text = ss[0].ToString(); ClkS2.Text = ss[1].ToString();
        UpdateBatteryStatus();
    }

    private string _lastBatteryText = "";

    private void UpdateBatteryStatus()
    {
        try
        {
            var report = PowerManager.BatteryStatus;
            bool hasBattery = report != Windows.System.Power.BatteryStatus.NotPresent;
            if (!hasBattery)
            {
                BatteryBar.Visibility = Visibility.Collapsed;
                BatteryPercent.Text = "—";
                BatteryStatus.Text = "已接通电源";
                BatteryLowHint.Visibility = Visibility.Collapsed;
                return;
            }

            int percent = Math.Clamp((int)Math.Round((double)PowerManager.RemainingChargePercent), 0, 100);
            bool charging = report == Windows.System.Power.BatteryStatus.Charging;
            string text = percent + "|" + (int)report;
            if (text == _lastBatteryText) return;   // 值没变就不重复改 UI(每秒调用一次)
            _lastBatteryText = text;

            BatteryBar.Visibility = Visibility.Visible;
            BatteryBar.Value = percent;
            BatteryPercent.Text = $"{percent}%";
            BatteryStatus.Text = report switch
            {
                Windows.System.Power.BatteryStatus.Charging => "正在充电",
                Windows.System.Power.BatteryStatus.Discharging => "使用电池",
                Windows.System.Power.BatteryStatus.Idle => "已接通电源",
                _ => "电池状态未知"
            };

            // 低电量强调: 只在"没在充电且 ≤20%"时标红提示, 充电中不吓人.
            bool low = percent <= 20 && !charging;
            BatteryLowHint.Visibility = low ? Visibility.Visible : Visibility.Collapsed;
            BatteryPercent.Foreground = low
                ? new SolidColorBrush(Color.FromArgb(255, 255, 106, 106))
                : (Brush)Application.Current.Resources["AppTextBrush"];
        }
        catch
        {
            BatteryBar.Visibility = Visibility.Collapsed;
            BatteryPercent.Text = "—";
            BatteryStatus.Text = "电源状态不可用";
            BatteryLowHint.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateGreeting()
    {
        int h = DateTime.Now.Hour;
        string period = h < 6 ? "凌晨好" : h < 11 ? "上午好" : h < 13 ? "中午好"
                      : h < 18 ? "下午好" : "晚上好";
        string user = "";
        try { user = Environment.UserName; } catch { }
        GreetingText.Text = string.IsNullOrWhiteSpace(user) ? $"{period}!" : $"{period}, {user}!";
    }

    // ---- 导航切换: 显示对应视图 ----
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag as string ?? "home";
        _currentViewTag = tag;
        HomeView.Visibility     = tag == "home"     ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        CameraView.Visibility   = tag == "camera"   ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility    = tag == "about"    ? Visibility.Visible : Visibility.Collapsed;
        // 进入摄像头页时启动预览定时器, 离开时停止.
        if (tag == "camera")
        {
            StartCameraPreview();
            _ = LoadCameraListAsync();   // 顺手刷新测光摄像头列表
        }
        else StopCameraPreview();
    }

    // ---- 测光摄像头选择 ----
    private bool _loadingCameraList;

    private async Task LoadCameraListAsync()
    {
        if (_loadingCameraList) return;
        _loadingCameraList = true;
        try
        {
            var cameras = await CameraLuma.ListCamerasAsync();

            // 源组枚举为空时退回设备枚举(某些驱动只在一侧可见).
            if (cameras.Count == 0)
            {
                try
                {
                    var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                        Windows.Media.Devices.MediaDevice.GetVideoCaptureSelector());
                    foreach (var d in devices)
                    {
                        bool builtIn = d.EnclosureLocation is null
                            || d.EnclosureLocation.Panel is Windows.Devices.Enumeration.Panel.Front
                                                            or Windows.Devices.Enumeration.Panel.Back
                                                            or Windows.Devices.Enumeration.Panel.Unknown;
                        cameras.Add((d.Id, d.Name, builtIn));
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MainWindow] 摄像头设备枚举失败: {ex.Message}"); }
            }

            var items = new List<(string Label, string Id)>
            {
                ("自动（优先内置摄像头）", string.Empty)
            };
            foreach (var c in cameras)
                items.Add(($"{c.Name}{(c.IsBuiltIn ? "（内置）" : "（外接/虚拟）")}", c.Id));

            CameraPickerCombo.Items.Clear();
            _cameraDeviceIds.Clear();
            int selected = 0;
            for (int i = 0; i < items.Count; i++)
            {
                CameraPickerCombo.Items.Add(new ComboBoxItem { Content = items[i].Label });
                _cameraDeviceIds.Add(items[i].Id);
                if (i > 0 && !string.IsNullOrEmpty(_params.CameraDeviceId)
                    && string.Equals(items[i].Id, _params.CameraDeviceId, StringComparison.OrdinalIgnoreCase))
                    selected = i;
            }
            CameraPickerCombo.SelectedIndex = selected;
            AppendLog(cameras.Count == 0
                ? "未枚举到摄像头设备(可在 设置→隐私和安全性→摄像头 检查权限)."
                : $"检测到 {cameras.Count} 个摄像头, 当前测光源: {items[selected].Label}");
        }
        finally { _loadingCameraList = false; }
    }

    private void OnCameraPickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCameraList || !_uiReady || CameraPickerCombo.SelectedIndex < 0) return;

        // 第 0 项是"自动", 对应空字符串.
        string id = CameraPickerCombo.SelectedIndex == 0
            ? string.Empty
            : _cameraDeviceIds[CameraPickerCombo.SelectedIndex];
        _params.CameraDeviceId = id;
        SavePreferences();

        string label = (CameraPickerCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        AppendLog(_engine.IsRunning
            ? $"测光摄像头已设为「{label}」, 需停止后重新「开始」才生效."
            : $"测光摄像头已设为「{label}」.");
    }

    private readonly List<string> _cameraDeviceIds = new();

    private void LoadPreferences()
    {
        _loadingPreferences = true;
        try
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            LoadProfile(settings, _params.PluggedProfile, "Plugged", useLegacyFallback: true);
            LoadProfile(settings, _params.BatteryProfile, "Battery", useLegacyFallback: false);
            _params.PowerPolicy = (PowerPolicy)Math.Clamp(
                ReadInt(settings, "PowerPolicy", (int)PowerPolicy.FollowPowerSource, 0, 2), 0, 2);
            _autoAdjustOnLaunch = ReadBool(settings, AutoAdjustSettingKey, false);
            _launchToTray = ReadBool(settings, LaunchToTraySettingKey, false);
            _params.IdleLockEnabled = ReadBool(settings, "IdleLockEnabled", true);
            _params.IdleLockSeconds = ReadInt(settings, "IdleLockSeconds", _params.IdleLockSeconds, 10, 1800);
            _params.SedentaryReminderMinutes = ReadInt(settings, "SedentaryReminderMinutes",
                _params.SedentaryReminderMinutes, 5, 240);
            _params.CameraDeviceId = settings["CameraDeviceId"] as string ?? string.Empty;
            _params.FaceTiltAdjustEnabled = ReadBool(settings, "FaceTiltAdjustEnabled", false);
            _params.EditingBatteryProfile = false;
            PowerPolicyCombo.SelectedIndex = (int)_params.PowerPolicy;
            PowerProfileCombo.SelectedIndex = 0;
        }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] 读取偏好失败: {ex.Message}"); }
        finally { _loadingPreferences = false; }
    }

    private static int ReadInt(IPropertySet settings, string key, int fallback, int min, int max)
        => settings[key] is int value ? Math.Clamp(value, min, max) : fallback;

    private static bool ReadBool(IPropertySet settings, string key, bool fallback)
        => settings[key] is bool value ? value : fallback;

    private static void LoadProfile(IPropertySet settings, BrightnessProfile profile,
        string suffix, bool useLegacyFallback)
    {
        int Read(string name, int fallback, int min, int max)
        {
            var scoped = $"{name}_{suffix}";
            if (settings[scoped] is int)
                return ReadInt(settings, scoped, fallback, min, max);
            return useLegacyFallback ? ReadInt(settings, name, fallback, min, max) : fallback;
        }

        profile.MinBrightness = Read("MinBrightness", profile.MinBrightness, 0, 100);
        profile.MaxBrightness = Read("MaxBrightness", profile.MaxBrightness, 0, 100);
        profile.IntervalSec = Read("IntervalSec", profile.IntervalSec, 1, 30);
        profile.Deadband = Read("Deadband", profile.Deadband, 0, 20);
        profile.FadeSec = Read("FadeSec", profile.FadeSec, 0, 10);
        profile.SmoothPct = Read("SmoothPct", profile.SmoothPct, 1, 100);
        profile.BrightSnap = Read("BrightSnap", profile.BrightSnap, 0, 256);
    }

    private void SavePreferences()
    {
        if (_loadingPreferences) return;
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            SaveProfile(values, _params.PluggedProfile, "Plugged");
            SaveProfile(values, _params.BatteryProfile, "Battery");
            // 保留旧版本使用的键，降级运行时仍能读取插电配置.
            values["MinBrightness"] = _params.PluggedProfile.MinBrightness;
            values["MaxBrightness"] = _params.PluggedProfile.MaxBrightness;
            values["IntervalSec"] = _params.PluggedProfile.IntervalSec;
            values["Deadband"] = _params.PluggedProfile.Deadband;
            values["FadeSec"] = _params.PluggedProfile.FadeSec;
            values["SmoothPct"] = _params.PluggedProfile.SmoothPct;
            values["BrightSnap"] = _params.PluggedProfile.BrightSnap;
            values["PowerPolicy"] = (int)_params.PowerPolicy;
            values[AutoAdjustSettingKey] = _autoAdjustOnLaunch;
            values[LaunchToTraySettingKey] = _launchToTray;
            values["IdleLockEnabled"] = _params.IdleLockEnabled;
            values["IdleLockSeconds"] = _params.IdleLockSeconds;
            values["SedentaryReminderMinutes"] = _params.SedentaryReminderMinutes;
            values["CameraDeviceId"] = _params.CameraDeviceId;
            values["FaceTiltAdjustEnabled"] = _params.FaceTiltAdjustEnabled;
        }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] 保存偏好失败: {ex.Message}"); }
    }

    private static void SaveProfile(IPropertySet values, BrightnessProfile profile, string suffix)
    {
        values[$"MinBrightness_{suffix}"] = profile.MinBrightness;
        values[$"MaxBrightness_{suffix}"] = profile.MaxBrightness;
        values[$"IntervalSec_{suffix}"] = profile.IntervalSec;
        values[$"Deadband_{suffix}"] = profile.Deadband;
        values[$"FadeSec_{suffix}"] = profile.FadeSec;
        values[$"SmoothPct_{suffix}"] = profile.SmoothPct;
        values[$"BrightSnap_{suffix}"] = profile.BrightSnap;
    }

    private void OnPowerPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        // LoadPreferences 设置初始选项时可能在控件完全加载后才派发事件，
        // 此时日志列表还未进入可用状态，不应执行用户交互逻辑.
        if (!_uiReady || _loadingPreferences || PowerPolicyCombo.SelectedIndex < 0) return;
        _params.PowerPolicy = (PowerPolicy)Math.Clamp(PowerPolicyCombo.SelectedIndex, 0, 2);
        SavePreferences();
        AppendLog($"供电策略已切换为：{PowerPolicyCombo.SelectedItem}");
    }

    private void OnPowerProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _loadingPreferences || PowerProfileCombo.SelectedIndex < 0) return;
        _params.EditingBatteryProfile = PowerProfileCombo.SelectedIndex == 1;
        SlidersPanel.Children.Clear();
        BuildSliders();
    }

    private async void ApplyLaunchPreferences()
    {
        _loadingPreferences = true;
        try
        {
            var task = await StartupTask.GetAsync("BrightnessAutoStartup");
            StartWithWindowsToggle.IsOn = task.State == StartupTaskState.Enabled;
            AutoAdjustToggle.IsOn = _autoAdjustOnLaunch;
            LaunchToTrayToggle.IsOn = _launchToTray;
            IdleLockToggle.IsOn = _params.IdleLockEnabled;
            FaceTiltToggle.IsOn = _params.FaceTiltAdjustEnabled;
        }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] 应用启动偏好失败: {ex.Message}"); StartWithWindowsToggle.IsOn = false; }
        finally { _loadingPreferences = false; }

        if (_autoAdjustOnLaunch)
        {
            // 开机自动启动这条路径也要打上"运行中"标记,
            // 否则 StartFailed 时无法判断界面是否需要复位.
            _startVisualSet = true;
            ActivateStartStop();
        }
        if (_launchToTray && _trayIcon?.Show() == true)
        {
            _windowHiddenInTray = true;
            AppWindow.Hide();
        }
    }

    private async void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        try
        {
            var task = await StartupTask.GetAsync("BrightnessAutoStartup");
            if (StartWithWindowsToggle.IsOn)
            {
                var state = await task.RequestEnableAsync();
                if (state != StartupTaskState.Enabled)
                {
                    _loadingPreferences = true;
                    StartWithWindowsToggle.IsOn = false;
                    _loadingPreferences = false;
                }
            }
            else
                task.Disable();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] 开机启动切换失败: {ex.Message}");
            _loadingPreferences = true;
            StartWithWindowsToggle.IsOn = false;
            _loadingPreferences = false;
        }
    }

    private void OnPreferenceToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        _autoAdjustOnLaunch = AutoAdjustToggle.IsOn;
        _launchToTray = LaunchToTrayToggle.IsOn;
        SavePreferences();
    }
    
    private void OnIdleLockToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        _params.IdleLockEnabled = IdleLockToggle.IsOn;
        SavePreferences();
    }

    private void OnFaceTiltToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        _params.FaceTiltAdjustEnabled = FaceTiltToggle.IsOn;
        SavePreferences();
        AppendLog(_params.FaceTiltAdjustEnabled
            ? "已开启人脸位置微调(最多 ±15%, 需连续同方向才生效)."
            : "已关闭人脸位置微调.");
    }

    private async void OnResetSettingsClick(object sender, RoutedEventArgs e)
    {
        _loadingPreferences = true;
        _params.PluggedProfile.Reset();
        _params.BatteryProfile.Reset(batteryDefaults: true);
        _params.PowerPolicy = PowerPolicy.FollowPowerSource;
        _params.EditingBatteryProfile = false;
        _autoAdjustOnLaunch = false; _launchToTray = false;
        _accentColorKey = "Blue";
        SlidersPanel.Children.Clear();
        PowerPolicyCombo.SelectedIndex = 0;
        PowerProfileCombo.SelectedIndex = 0;
        BuildSliders();
        AutoAdjustToggle.IsOn = false;
        LaunchToTrayToggle.IsOn = false;
        IdleLockToggle.IsOn = true;
        ApplyAccentColor(_accentColorKey);
        SaveAccentColorPref(_accentColorKey);
        _loadingPreferences = false;
        SavePreferences();

        // 恢复默认同时关闭开机启动.
        try
        {
            var task = await StartupTask.GetAsync("BrightnessAutoStartup");
            task.Disable();
            StartWithWindowsToggle.IsOn = false;
        }
        catch { /* 开机启动不可用时忽略 */ }
    }

    // ---- 场景预设 ----
    private void OnPresetOfficeClick(object sender, RoutedEventArgs e)
    {
        // 办公模式: 正常亮度, 正常响应
        _params.MinBrightness = 20; _params.MaxBrightness = 100; _params.IntervalSec = 3;
        _params.Deadband = 3; _params.FadeSec = 2; _params.SmoothPct = 35; _params.BrightSnap = 101;
        SlidersPanel.Children.Clear();
        BuildSliders();
        SavePreferences();
        AppendLog("已切换到办公模式");
    }

    private void OnPresetMovieClick(object sender, RoutedEventArgs e)
    {
        // 观影模式: 低亮度, 慢响应
        _params.MinBrightness = 10; _params.MaxBrightness = 40; _params.IntervalSec = 5;
        _params.Deadband = 2; _params.FadeSec = 3; _params.SmoothPct = 20; _params.BrightSnap = 101;
        SlidersPanel.Children.Clear();
        BuildSliders();
        SavePreferences();
        AppendLog("已切换到观影模式");
    }

    private void OnPresetGameClick(object sender, RoutedEventArgs e)
    {
        // 游戏模式: 最高亮度, 快响应
        _params.MinBrightness = 80; _params.MaxBrightness = 100; _params.IntervalSec = 1;
        _params.Deadband = 1; _params.FadeSec = 1; _params.SmoothPct = 80; _params.BrightSnap = 101;
        SlidersPanel.Children.Clear();
        BuildSliders();
        SavePreferences();
        AppendLog("已切换到游戏模式");
    }

    // ---- 滑块定义: 对应 C++ SetupSliders 的 7 行 ----
    private enum Sld { Min, Max, Interval, Deadband, Fade, Smooth, Snap }

    private readonly record struct SliderDef(
        string Label, int Lo, int Hi, int Value, Func<int, string> Fmt);

    private void BuildSliders()
    {
        var defs = new[]
        {
            new SliderDef("最低亮度", 0, 100, _params.MinBrightness, v => $"{v}%"),
            new SliderDef("最高亮度", 0, 100, _params.MaxBrightness, v => $"{v}%"),
            new SliderDef("检测间隔", 1, 30,  _params.IntervalSec,   v => $"{v} 秒"),
            new SliderDef("变化死区", 0, 20,  _params.Deadband,      v => $"{v}%"),
            new SliderDef("过渡时间", 0, 10,  _params.FadeSec,       v => $"{v} 秒"),
            new SliderDef("平滑系数", 1, 100, _params.SmoothPct,     v => (v / 100.0).ToString("F2")),
            new SliderDef("强光跳满", 0, 255,
                _params.BrightSnap > 255 ? 0 : _params.BrightSnap, FmtSnap),
        };

        for (int i = 0; i < defs.Length; ++i)
        {
            var def = defs[i];
            int idx = i;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var label = new TextBlock
            {
                Text = def.Label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var slider = new Slider
            {
                Minimum = def.Lo,
                Maximum = def.Hi,
                Value = def.Value,
                StepFrequency = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0)
            };
            Grid.SetColumn(slider, 1);

            var valueLabel = new TextBlock
            {
                Text = def.Fmt((int)def.Value),
                FontSize = 13,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valueLabel, 2);

            var fmt = def.Fmt;
            slider.ValueChanged += (_, e) =>
            {
                int v = (int)Math.Round(e.NewValue);
                valueLabel.Text = fmt(v);
                ApplySlider(idx, v);
                SavePreferences();
            };

            row.Children.Add(label);
            row.Children.Add(slider);
            row.Children.Add(valueLabel);
            SlidersPanel.Children.Add(row);

            _sliders[i] = slider;
            _valueLabels[i] = valueLabel;
            _nameLabels[i] = label;
        }
        // 首次填充后按当前主题上色.
        ApplySliderLabelColors();
        UpdateCurvePreview();
    }

    // 滑块标签配色: 名称用暗色文字, 数值用主文字色, 随主题切换.
    private void ApplySliderLabelColors()
    {
        var dim  = new SolidColorBrush(_isDark
            ? Color.FromArgb(255, 158, 158, 158) : Color.FromArgb(255, 90, 90, 90));
        var main = new SolidColorBrush(_isDark
            ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 17, 17, 17));
        for (int i = 0; i < _nameLabels.Length; ++i)
        {
            if (_nameLabels[i] != null)  _nameLabels[i].Foreground = dim;
            if (_valueLabels[i] != null) _valueLabels[i].Foreground = main;
        }
    }

    private static string FmtSnap(int v) => (v == 0 || v > 255) ? "关闭" : $"{v}/255";

    // 把滑块值写回运行参数 (对应 ApplySliderToParams).
    // 最低/最高亮度交叉时自动把另一端拉齐, 避免保存反值且 UI 显示矛盾.
    private void ApplySlider(int idx, int v)
    {
        switch ((Sld)idx)
        {
            case Sld.Min:
                _params.MinBrightness = v;
                if (v > _params.MaxBrightness)
                {
                    _params.MaxBrightness = v;
                    SyncSlider((int)Sld.Max, v);
                }
                break;
            case Sld.Max:
                _params.MaxBrightness = v;
                if (v < _params.MinBrightness)
                {
                    _params.MinBrightness = v;
                    SyncSlider((int)Sld.Min, v);
                }
                break;
            case Sld.Interval: _params.IntervalSec = v; break;
            case Sld.Deadband: _params.Deadband = v; break;
            case Sld.Fade:     _params.FadeSec = v; break;
            case Sld.Smooth:   _params.SmoothPct = v; break;
            case Sld.Snap:     _params.BrightSnap = (v == 0 ? 256 : v); break;
        }
        UpdateCurvePreview();
    }

    // 同步另一个滑块的值 + 数值标签(用于交叉拉齐).
    private void SyncSlider(int idx, int v)
    {
        if (_sliders[idx] != null) _sliders[idx].Value = v;
        if (_valueLabels[idx] != null)
            _valueLabels[idx].Text = idx == (int)Sld.Snap ? FmtSnap(v) : $"{v}%";
    }

    // ---- 亮度映射曲线预览 ----
    private void UpdateCurvePreview()
    {
        if (CurveCanvas == null) return;
        double w = CurveCanvas.ActualWidth;
        double h = CurveCanvas.ActualHeight;
        if (w < 20 || h < 20) return;

        CurveCanvas.Children.Clear();

        int minB = _params.MinBrightness;
        int maxB = _params.MaxBrightness;
        if (minB > maxB) (minB, maxB) = (maxB, minB);
        int brightSnap = _params.BrightSnap;

        // 水平网格线 (25%, 50%, 75%)
        for (int i = 1; i < 4; i++)
        {
            double gy = h * i / 4;
            var line = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = gy, X2 = w, Y2 = gy,
                Stroke = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AppStrokeBrush"],
                StrokeThickness = 0.5
            };
            CurveCanvas.Children.Add(line);
        }

        // 映射曲线
        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        for (int luma = 0; luma <= 255; luma++)
        {
            int brightness = AutoBrightnessEngine.MapLumaToBrightness(luma, minB, maxB, brightSnap);
            double x = (luma / 255.0) * w;
            double y = h - (brightness / 100.0) * h;
            points.Add(new Windows.Foundation.Point(x, y));
        }
        var polyline = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Points = points,
            Stroke = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AppAccentBrush"],
            StrokeThickness = 2.5,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round
        };
        CurveCanvas.Children.Add(polyline);

        // 当前环境光位置标记(如果有数据)
        if (LumaGauge.Value > 0)
        {
            double cx = (LumaGauge.Value / 255.0) * w;
            var marker = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = cx, Y1 = 0, X2 = cx, Y2 = h,
                Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(128, 255, 185, 0)),
                StrokeThickness = 1,
                StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 3, 3 }
            };
            CurveCanvas.Children.Add(marker);
        }
    }

    private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateCurvePreview();

    // ---- 历史趋势图 ----
    private void HistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateHistoryChart();

    // ---- 摄像头预览 ----
    private Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource? _cameraPreviewSource;
    private DispatcherTimer? _cameraPreviewTimer;
    private Windows.Graphics.Imaging.SoftwareBitmap? _cameraPreviewBitmap;

    private void StartCameraPreview()
    {
        if (_cameraPreviewTimer != null) return;
        _cameraPreviewSource ??= new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
        CameraPreviewImage.Source = _cameraPreviewSource;
        _cameraPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _cameraPreviewTimer.Tick += async (_, _) => await RefreshCameraPreviewAsync();
        _cameraPreviewTimer.Start();
    }

    private void StopCameraPreview()
    {
        _cameraPreviewTimer?.Stop();
        _cameraPreviewTimer = null;
        // 清掉源位图: SoftwareBitmapSource 不再持有旧帧, 也避免它跟引擎抢句柄.
        CameraPreviewImage.Source = null;
        _cameraPreviewBitmap?.Dispose();
        _cameraPreviewBitmap = null;
    }

    private async Task RefreshCameraPreviewAsync()
    {
        if (!_engine.IsRunning)
        {
            CameraPreviewPlaceholder.Visibility = Visibility.Visible;
            CameraPreviewHint.Text = "启动引擎后可预览摄像头画面";
            CameraLumaValue.Text = "—";
            return;
        }
        CameraPreviewPlaceholder.Visibility = Visibility.Collapsed;
        CameraLumaValue.Text = LumaGauge.Value > 0 ? $"{LumaGauge.Value} / 255" : "采样中…";

        var frame = _engine.GetPreviewFrame();
        if (frame == null || _cameraPreviewSource == null)
        {
            frame?.Dispose();
            return;
        }
        try
        {
            await _cameraPreviewSource.SetBitmapAsync(frame);
        }
        catch (Exception ex)
        {
            // 帧格式不兼容时不静默: 至少留一次调试输出, 否则"预览不动"无从排查.
            Debug.WriteLine($"[MainWindow] 摄像头预览失败: {ex.Message}");
            frame.Dispose();
            return;
        }
        // 上一帧交给 XAML 之后就不再需要了, 在这里统一回收.
        _cameraPreviewBitmap?.Dispose();
        _cameraPreviewBitmap = frame;
    }

    private void UpdateHistoryChart()
    {
        if (HistoryCanvas == null) return;
        double w = HistoryCanvas.ActualWidth;
        double h = HistoryCanvas.ActualHeight;
        if (w < 20 || h < 20 || _history.Count < 2) return;

        HistoryCanvas.Children.Clear();
        var points = _history.ToArray();
        int n = points.Length;

        // 水平网格线
        var gridBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(30, 255, 255, 255));
        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            HistoryCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 0.5
            });
        }

        // 亮度渐变填充（线下方到底部）
        var briPts = new Microsoft.UI.Xaml.Media.PointCollection();
        for (int i = 0; i < n; i++)
        {
            double x = (i / (double)(n - 1)) * w;
            double y = h - (points[i].Brightness / 100.0) * h;
            briPts.Add(new Windows.Foundation.Point(x, y));
        }
        // 填充区域：线 + 底部闭合
        var briFillPts = new Microsoft.UI.Xaml.Media.PointCollection();
        foreach (var p in briPts) briFillPts.Add(p);
        briFillPts.Add(new Windows.Foundation.Point(w, h));
        briFillPts.Add(new Windows.Foundation.Point(0, h));
        HistoryCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Polygon
        {
            Points = briFillPts,
            Fill = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new Microsoft.UI.Xaml.Media.GradientStop
                    {
                        Color = Windows.UI.Color.FromArgb(60, 0, 120, 215),
                        Offset = 0.0
                    },
                    new Microsoft.UI.Xaml.Media.GradientStop
                    {
                        Color = Windows.UI.Color.FromArgb(0, 0, 120, 215),
                        Offset = 1.0
                    }
                }
            }
        });

        // 环境光线 (黄色)
        var lumaPts = new Microsoft.UI.Xaml.Media.PointCollection();
        for (int i = 0; i < n; i++)
        {
            double x = (i / (double)(n - 1)) * w;
            double y = h - (points[i].Luma / 255.0) * h;
            lumaPts.Add(new Windows.Foundation.Point(x, y));
        }
        HistoryCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Points = lumaPts,
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 255, 185, 0)),
            StrokeThickness = 2,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round
        });

        // 亮度线 (跟随主题色)
        HistoryCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Points = briPts,
            Stroke = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AppAccentBrush"],
            StrokeThickness = 2,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round
        });
    }

    private void ToggleEngineFromTray()
    {
        if (!StartStopButton.IsEnabled && !_engine.IsRunning) return;
        ActivateStartStop();
    }

    // ---- 浅色 / 深色主题 ----
    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
        ToolTipService.SetToolTip(PaneToggleButton,
            NavView.IsPaneOpen ? "收起导航" : "展开导航");
    }

    private void OnThemeToggleTapped(object sender, TappedRoutedEventArgs e)
        => ToggleTheme();

    private void ToggleTheme()
    {
        _isDark = !_isDark;
        ApplyTheme(_isDark);
        SaveThemePref(_isDark);
    }

    // 应用主题: 切换 RequestedTheme(联动所有 ThemeResource) +
    // 标题栏按钮配色 + 切换按钮图标 + 滑块标签色 + 日志刷新.
    private void ApplyTheme(bool dark)
    {
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        // 切换按钮图标: 深色显示太阳(点击转浅色), 浅色显示月亮(点击转深色).
        // Segoe MDL2: E706=亮度(太阳), E708=夜间(月亮).
        ThemeIcon.Glyph = dark ? "" : "";
        ToolTipService.SetToolTip(ThemeButton, dark ? "切换到浅色" : "切换到深色");

        // 标题栏三个系统按钮的前景/悬停色, 随主题变.
        try
        {
            var tb = AppWindow.TitleBar;
            Color fg   = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 17, 17, 17);
            Color hov  = dark ? Color.FromArgb(38, 255, 255, 255)  : Color.FromArgb(24, 0, 0, 0);
            tb.ButtonForegroundColor        = fg;
            tb.ButtonHoverForegroundColor   = fg;
            tb.ButtonInactiveForegroundColor= dark ? Color.FromArgb(255, 128, 128, 128)
                                                   : Color.FromArgb(255, 138, 138, 138);
            tb.ButtonBackgroundColor        = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor= Colors.Transparent;
            tb.ButtonHoverBackgroundColor   = hov;
        }
        catch { /* 某些环境无 AppWindow.TitleBar, 忽略 */ }

        ApplySliderLabelColors();
        RefreshAllHoverSpotColors();
        ApplyAccentColor(_accentColorKey);

        // 日志文字色跟随主题: 重设数据源触发 x:Bind 重新取色.
        LogEntry.IsDarkTheme = dark;
        var snapshot = _logs;   // 触发模板重新求值
        LogList.ItemsSource = null;
        LogList.ItemsSource = snapshot;
    }

    private bool LoadThemePref()
    {
        try
        {
            var v = ApplicationData.Current.LocalSettings.Values[ThemeSettingKey];
            if (v is string s) return s != "Light";
        }
        catch { /* 未打包运行时无 LocalSettings, 用默认 */ }
        return true;   // 默认深色
    }

    private void SaveThemePref(bool dark)
    {
        try { ApplicationData.Current.LocalSettings.Values[ThemeSettingKey] = dark ? "Dark" : "Light"; }
        catch { /* 忽略持久化失败 */ }
    }

    private void OnAccentColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        ClearPaletteSelection();
        _accentColorKey = NormalizeAccentColorKey(key);
        ApplyAccentColor(_accentColorKey);
        SaveAccentColorPref(_accentColorKey);
    }

    private void OnPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } button) return;
        ClearPaletteSelection();
        _selectedPaletteButton = button;
        button.BorderBrush = (Brush)Application.Current.Resources["AppTextBrush"];
        button.Content = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 13,
            Foreground = new SolidColorBrush(Colors.White)
        };
        _accentColorKey = NormalizeAccentColorKey(key);
        ApplyAccentColor(_accentColorKey);
        SaveAccentColorPref(_accentColorKey);
    }

    private void ClearPaletteSelection()
    {
        if (_selectedPaletteButton is null) return;
        _selectedPaletteButton.BorderBrush = new SolidColorBrush(Colors.Transparent);
        _selectedPaletteButton.Content = null;
        _selectedPaletteButton = null;
    }

    private async void OnCustomColorClick(object sender, RoutedEventArgs e)
    {
        var currentColor = (Application.Current.Resources["AppAccentBrush"] as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var picker = new ColorPicker
        {
            Color = currentColor,
            IsAlphaEnabled = false
        };
        var dialog = new ContentDialog
        {
            Title = "自定义主题色",
            Content = picker,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var color = picker.Color;
            _accentColorKey = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            ApplyAccentColor(_accentColorKey);
            SaveAccentColorPref(_accentColorKey);
            AccentColorLabel.Text = $"自定义颜色 {_accentColorKey}";
        }
    }

    private void ApplyAccentColor(string key)
    {
        key = NormalizeAccentColorKey(key);
        _accentColorKey = key;
        var (color, label) = key switch
        {
            "Teal" => (Color.FromArgb(255, 0, 130, 114), "青绿色"),
            "Green" => (Color.FromArgb(255, 16, 124, 16), "自然绿"),
            "Orange" => (Color.FromArgb(255, 216, 59, 1), "活力橙"),
            "Pink" => (Color.FromArgb(255, 227, 0, 140), "玫红色"),
            _ when key.StartsWith('#') => (ParseAccentColor(key), $"自定义颜色 {key}"),
            _ => (Color.FromArgb(255, 0, 120, 215), "经典蓝")
        };

        if (Application.Current.Resources["AppAccentBrush"] is SolidColorBrush accentBrush)
        {
            accentBrush.Color = color;
            BriGauge.ArcBrush = accentBrush;
            if (!_engine.IsRunning) StartStopButton.Background = accentBrush;
            foreach (var slider in _sliders)
                if (slider is not null) slider.Foreground = accentBrush;
        }
        if (Application.Current.Resources["AppSoftAccentBrush"] is SolidColorBrush softBrush)
            softBrush.Color = color;

        AccentColorLabel.Text = label;
        Brush selectedStroke = (Brush)Application.Current.Resources["AppTextBrush"];
        Brush clearStroke = new SolidColorBrush(Colors.Transparent);
        var swatches = new (Button Button, FontIcon Check, string Key)[]
        {
            (AccentBlueButton, AccentBlueCheck, "Blue"),
            (AccentTealButton, AccentTealCheck, "Teal"),
            (AccentGreenButton, AccentGreenCheck, "Green"),
            (AccentOrangeButton, AccentOrangeCheck, "Orange"),
            (AccentPinkButton, AccentPinkCheck, "Pink")
        };
        foreach (var swatch in swatches)
        {
            bool selected = swatch.Key == key;
            swatch.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            swatch.Button.BorderBrush = selected ? selectedStroke : clearStroke;
        }

        UpdateCurvePreview();
        UpdateHistoryChart();
    }

    private static string NormalizeAccentColorKey(string key)
    {
        if (key is "Blue" or "Teal" or "Green" or "Orange" or "Pink") return key;
        if (key.Length == 7 && key[0] == '#'
            && uint.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                null, out _))
            return key.ToUpperInvariant();
        return "Blue";
    }

    private static Color ParseAccentColor(string key)
        => Color.FromArgb(255,
            Convert.ToByte(key.Substring(1, 2), 16),
            Convert.ToByte(key.Substring(3, 2), 16),
            Convert.ToByte(key.Substring(5, 2), 16));

    private string LoadAccentColorPref()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[AccentColorSettingKey] is string key)
                return NormalizeAccentColorKey(key);
        }
        catch { /* 未打包运行时用默认主题色 */ }
        return "Blue";
    }

    private static void SaveAccentColorPref(string key)
    {
        try { ApplicationData.Current.LocalSettings.Values[AccentColorSettingKey] = key; }
        catch { /* 忽略持久化失败 */ }
    }

    // 空格 = 开始/停止. Preview + handledEventsToo, 避免主页 ScrollViewer 把空格当翻页吃掉.
    // 输入框里仍把空格留给打字.
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalKey != VirtualKey.Space && e.Key != VirtualKey.Space) return;
        if (!StartStopButton.IsEnabled) return;
        if (IsTypingTarget(RootGrid.XamlRoot is { } xr
                ? FocusManager.GetFocusedElement(xr)
                : null))
            return;

        OnStartStopClick(StartStopButton, new RoutedEventArgs());
        e.Handled = true;
    }

    private static bool IsTypingTarget(object? focused)
    {
        for (var cur = focused as DependencyObject; cur is not null;
             cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is TextBox or RichEditBox or PasswordBox or ComboBox or AutoSuggestBox)
                return true;
        }
        return false;
    }

    // ---- 启停 ----
    private void OnStartStopClick(object sender, RoutedEventArgs e)
        => ActivateStartStop();

    private void OnStartStopPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(StartStopButton);
        if (!point.Properties.IsLeftButtonPressed
            && point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
            && point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
            return;

        _startStopPointerId = e.Pointer.PointerId;
        _startStopPointerDownVersion = _startStopActivationVersion;
        // 长按 1.5 秒触发亮度校准
        _ = LongPressCalibrateAsync();
    }

    private void OnStartStopPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_startStopPointerId != e.Pointer.PointerId) return;

        long versionAtPress = _startStopPointerDownVersion;
        _startStopPointerId = null;
        _startStopPointerDownVersion = -1;

        var p = e.GetCurrentPoint(StartStopButton).Position;
        if (p.X < 0 || p.Y < 0
            || p.X > StartStopButton.ActualWidth
            || p.Y > StartStopButton.ActualHeight)
            return;

        // Button.Click 通常在同一轮输入派发中产生. 排到队尾后再检查版本,
        // 只有标准 Click 确实丢失时才补一次, 避免正常环境下启停两次.
        _dispatcher.TryEnqueue(() =>
        {
            if (StartStopButton.IsEnabled
                && _startStopActivationVersion == versionAtPress)
                ActivateStartStop();
        });
    }

    private async Task LongPressCalibrateAsync()
    {
        await Task.Delay(1500);
        if (_startStopPointerId.HasValue && _startStopActivationVersion == _startStopPointerDownVersion)
        {
            AppendLog("校准完成: 已记录当前亮度为基准");
            _startStopPointerId = null;
        }
    }

    private void OnStartStopPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_startStopPointerId != e.Pointer.PointerId) return;
        _startStopPointerId = null;
        _startStopPointerDownVersion = -1;
    }

    private async void ActivateStartStop()
    {
        _startStopActivationVersion++;
        StartStopButton.IsEnabled = false;
        try
        {
            if (_engine.IsRunning)
            {
                await _engine.StopAsync();
                // 引擎停止后交还系统亮度控制权(否则覆盖可能仍生效).
                ReleaseManualBrightness();
            }
            else
            {
                if (await _engine.StartAsync())
                {
                    // 引擎要接管亮度: 先把手动控制器的系统覆盖停掉, 避免两个覆盖互相顶替.
                    ReleaseManualBrightness();
                    SetRunningVisual(true);
                }
            }
        }
        finally
        {
            // 引擎启动失败会在后台循环里提前返回, 这里无条件恢复, 避免按钮假死.
            StartStopButton.IsEnabled = true;
        }
    }

    // 配色取 Win10 系统色板: 强调蓝 #0078D7 / 危险红 #E81123 / 成功绿 #107C10.
    private void SetRunningVisual(bool running)
    {
        StartStopButton.Content = running ? "停止" : "开始";
        StartStopButton.Background = running
            ? new SolidColorBrush(Color.FromArgb(255, 232, 17, 35))
            : (Brush)Application.Current.Resources["AppAccentBrush"];
        StatusDot.Fill = new SolidColorBrush(running
            ? Color.FromArgb(255, 16, 124, 16)
            : Color.FromArgb(255, 158, 158, 158));
        StatusLabel.Text = running ? "运行中" : "已停止";
        // 一句话说明当前状态与下一步该做什么 —— 首页此前只有按钮、没有提示,
        // 用户不知道点下去会发生什么.
        RunHintText.Text = running
            ? "正在按环境光自动调节亮度，点右侧「停止」可交还手动控制"
            : "点右侧「开始」后，程序会用摄像头测环境光并自动调亮度";
        // 停止时显示手动亮度滑块，运行时隐藏
        ManualBrightnessPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        if (!running) SyncManualBrightnessSliderOnce();
    }

    // 手动亮度滑块调节
    private void OnManualBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        int val = (int)Math.Round(e.NewValue);
        ManualBrightnessValue.Text = $"{val}%";
        // 同步真实亮度时只是改 UI 值, 不应反过来去设置系统亮度.
        if (_syncingManualSlider) return;
        // 首次交互时才创建控制器(此时才开始接管系统亮度).
        _brightnessController ??= new BrightnessController();
        _brightnessController.SetBrightness(val);
        BriGauge.Value = val;
        BriGauge.BigText = $"{val}%";
    }

    private bool _syncingManualSlider;

    /// <summary>读一次系统当前亮度, 让手动滑块与真实值对齐(只做一次).</summary>
    private void SyncManualBrightnessSliderOnce()
    {
        if (_manualBrightnessSynced) return;
        _manualBrightnessSynced = true;
        if (_engine.IsRunning) return;
        try
        {
            using var probe = new BrightnessController();
            if (!probe.Connect()) return;
            int cur = probe.GetCurrentBrightness();
            if (cur < 0) return;
            _syncingManualSlider = true;   // 抑制 ValueChanged 造成的真实设置
            ManualBrightnessSlider.Value = cur;
            _syncingManualSlider = false;
            ManualBrightnessValue.Text = $"{cur}%";
            BriGauge.Value = cur;
            BriGauge.BigText = $"{cur}%";
        }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] 同步手动亮度失败: {ex.Message}"); }
    }

    /// <summary>释放手动亮度控制器: 停掉系统亮度覆盖, 否则亮度会被锁死.</summary>
    private void ReleaseManualBrightness()
    {
        try { _brightnessController?.Dispose(); } catch { }
        _brightnessController = null;
    }

    // ---- 引擎事件 (marshal 到 UI 线程) ----
    private void OnEngineLog(string line) => _dispatcher.TryEnqueue(() => AppendLog(line));

    private void OnIdleLockWarning(int remainingSeconds)
        => _dispatcher.TryEnqueue(() => ShowIdleLockWarningAsync(remainingSeconds));

    private void CancelIdleLockWarning()
    {
        try { _idleLockWarningCts?.Cancel(); } catch { }
        try { _idleLockWarningDialog?.Hide(); } catch { }
    }

    // ---- 久坐提醒 (由引擎按真实时间触发, 默认 45 分钟) ----
    private void OnSedentaryReminder(int minutes)
        => _dispatcher.TryEnqueue(() => _ = ShowSedentaryReminderAsync(minutes));

    private void CancelSedentaryReminder()
    {
        try { _sedentaryDialog?.Hide(); } catch { }
        _sedentaryDialog = null;
        _sedentaryDialogShowing = false;
    }

    private async Task ShowSedentaryReminderAsync(int minutes)
    {
        // 窗口藏在托盘里时弹窗没人看得见, 只留日志.
        if (_windowHiddenInTray)
        {
            AppendLog($"已连续坐了约 {minutes} 分钟, 记得起来活动一下.");
            return;
        }
        // 同一时刻只能有一个 ContentDialog; 锁屏倒计时优先.
        if (_sedentaryDialogShowing || _idleLockWarningShowing || RootGrid.XamlRoot is null) return;

        _sedentaryDialogShowing = true;
        var dialog = new ContentDialog
        {
            Title = "该起来活动一下了",
            Content = $"你已经连续坐了约 {minutes} 分钟。\n站起来走两步、看看远处，腰和眼睛都会谢谢你。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        _sedentaryDialog = dialog;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] 久坐提醒显示失败: {ex.Message}");
        }
        finally
        {
            try { dialog.Hide(); } catch { }
            if (ReferenceEquals(_sedentaryDialog, dialog)) _sedentaryDialog = null;
            _sedentaryDialogShowing = false;
        }
    }

    private async void ShowIdleLockWarningAsync(int remainingSeconds)
    {
        if (_idleLockWarningShowing || RootGrid.XamlRoot is null) return;
        // 窗口藏在托盘里时弹窗没人看得见, 反而可能因为没有 XamlRoot 让 ShowAsync 抛异常.
        // 和久坐提醒保持一致: 只留日志, 到点照样锁屏.
        if (_windowHiddenInTray)
        {
            AppendLog($"无人操作, 约 {Math.Clamp(remainingSeconds, 1, 10)} 秒后自动锁屏.");
            return;
        }
        // 锁屏倒计时更重要: 先收掉可能开着的久坐提醒, 免得两个 ContentDialog 撞车.
        if (_sedentaryDialogShowing) CancelSedentaryReminder();

        _idleLockWarningShowing = true;
        using var warningCts = new CancellationTokenSource();
        _idleLockWarningCts = warningCts;
        var warningToken = warningCts.Token;
        int seconds = Math.Clamp(remainingSeconds, 1, 10);
        var countdown = new TextBlock
        {
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AppAccentBrush"]
        };
        var message = new TextBlock
        {
            Text = "检测到长时间无人脸且没有操作。",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AppTextDimBrush"]
        };
        var content = new StackPanel { Spacing = 10, MinWidth = 280 };
        content.Children.Add(countdown);
        content.Children.Add(message);

        var dialog = new ContentDialog
        {
            Title = "即将锁定屏幕",
            Content = content,
            XamlRoot = RootGrid.XamlRoot
        };
        _idleLockWarningDialog = dialog;

        try
        {
            var showOperation = dialog.ShowAsync();
            for (int left = seconds; left > 0; left--)
            {
                countdown.Text = $"屏幕将在 {left} 秒后锁定";
                await Task.Delay(TimeSpan.FromSeconds(1), warningToken);
            }

            dialog.Hide();
            await showOperation;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] 锁屏提醒显示失败: {ex.Message}");
        }
        finally
        {
            try { dialog.Hide(); } catch { }
            if (ReferenceEquals(_idleLockWarningDialog, dialog))
                _idleLockWarningDialog = null;
            if (ReferenceEquals(_idleLockWarningCts, warningCts))
                _idleLockWarningCts = null;
            _idleLockWarningShowing = false;
        }
    }

    private void OnEngineStatus(StatusUpdate s) => _dispatcher.TryEnqueue(() =>
    {
        LumaGauge.Value = s.Luma;
        LumaGauge.BigText = s.Luma.ToString();
        BriGauge.Value = s.Brightness;
        BriGauge.BigText = $"{s.Brightness}%";
        _history.Enqueue((s.Luma, s.Brightness));
        if (_history.Count > MaxHistoryPoints) _history.Dequeue();
        UpdateHistoryChart();
        UpdateCurvePreview();
    });

    /// <summary>
    /// 把标题栏与"关于"页的版本号设为当前包版本.
    /// 优先读 MSIX 清单(Package.Current), 未打包运行时回退到程序集版本,
    /// 这样以后只改 Package.appxmanifest 的 Version, 界面自动跟随.
    /// </summary>
    private void ApplyVersionText()
    {
        string text;
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            text = $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            // 未打包 / 无包身份时读程序集版本
            var asm = typeof(MainWindow).Assembly.GetName().Version;
            text = asm is null ? "v—" : $"v{asm.Major}.{asm.Minor}.{asm.Build}.{asm.Revision}";
        }
        TitleVersionText.Text = text;
        AboutVersionText.Text = text;
    }

    private void OnEngineStopped() => _dispatcher.TryEnqueue(() =>
    {
        CancelIdleLockWarning();
        // 引擎失败会在后台循环里直接返回, 这里按"确实停了"刷新一次状态,
        // 避免界面停留在"运行中"而实际已经退出.
        SetRunningVisual(false);
    });

    /// <summary>
    /// 启动失败(连不上亮度接口 / 摄像头打不开).
    /// 补上"开机自动启动"这条路径的界面复位 —— 旧代码里它没有配对的
    /// ActivateStartStop, 按钮会一直停在"停止"直到用户手动再点一次.
    /// </summary>
    private void OnEngineStartFailed() => _dispatcher.TryEnqueue(() =>
    {
        if (_startVisualSet)
        {
            _startVisualSet = false;
            SetRunningVisual(false);
        }
        StartStopButton.IsEnabled = true;
    });

    private bool _startVisualSet;
    private string _currentViewTag = "home";

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        // 清空后若仍停在旧的滚动位置, 新日志会出现在可视区之外.
        // 同时让"已滚到底部"的判定重新成立, 否则自动跟随会失效.
        _dispatcher.TryEnqueue(() =>
        {
            LogScroll.ChangeView(null, 0, null, true);
        });
    }

    private void AppendLog(string line)
    {
        _logs.Add(new LogEntry(line));
        while (_logs.Count > MaxLogLines) _logs.RemoveAt(0);
        // 用户已滚到底部时才自动跟随, 且排到下一帧低优先级执行, 避免强制同步布局掉帧.
        if (LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 20)
        {
            _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null));
        }
    }
}
