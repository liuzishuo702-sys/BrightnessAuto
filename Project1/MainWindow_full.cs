Read "C:\Users\刘\Desktop\自动调节亮度\Project1\BrightnessAuto\MainWindow.xaml.cs":

     1	using System;
     2	using System.Collections.Generic;
     3	using System.Collections.ObjectModel;
     4	using System.IO;
     5	using System.Numerics;
     6	using System.Security.Principal;
     7	using System.Threading.Tasks;
     8	using Microsoft.UI;
     9	using Microsoft.UI.Composition;
    10	using Microsoft.UI.Composition.SystemBackdrops;
    11	using Microsoft.UI.Dispatching;
    12	using Microsoft.UI.Xaml;
    13	using Microsoft.UI.Xaml.Controls;
    14	using Microsoft.UI.Xaml.Controls.Primitives;
    15	using Microsoft.UI.Xaml.Hosting;
    16	using Microsoft.UI.Xaml.Input;
    17	using Microsoft.UI.Xaml.Media;
    18	using Microsoft.UI.Xaml.Media.Imaging;
    19	using Microsoft.UI.Windowing;
    20	using Microsoft.Win32;
    21	using Windows.Storage;
    22	using Windows.System;
    23	using Windows.ApplicationModel;
    24	using Windows.Foundation.Collections;
    25	using Windows.UI;
    26	using BrightnessAuto.Controls;
    27	using BrightnessAuto.Services;
    28	
    29	namespace BrightnessAuto;
    30	
    31	public sealed partial class MainWindow : Window
    32	{
    33	    private readonly EngineParams _params = new();
    34	    private readonly AutoBrightnessEngine _engine;
    35	    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    36	    private readonly ObservableCollection<LogEntry> _logs = new();
    37	    private const int MaxLogLines = 200;
    38	
    39	    // 各滑块的数值文本(与滑块联动更新).
    40	    private readonly Slider[] _sliders = new Slider[7];
    41	    private readonly TextBlock[] _valueLabels = new TextBlock[7];
    42	
    43	    // 各滑块左侧的标签(主题切换时同步改色).
    44	    private readonly TextBlock[] _nameLabels = new TextBlock[7];
    45	
    46	    // 当前是否深色主题.
    47	    private bool _isDark = true;
    48	    private const string ThemeSettingKey = "AppTheme";
    49	    private bool _allowWindowClose;
    50	    private bool _closePromptShowing;
    51	    private TrayIcon? _trayIcon;
    52	    private bool _loadingPreferences;
    53	    private bool _autoAdjustOnLaunch;
    54	    private bool _launchToTray;
    55	    private const string AutoAdjustSettingKey = "AutoAdjustOnLaunch";
    56	    private const string LaunchToTraySettingKey = "LaunchToTray";
    57	
    58	    // 卡片悬停/按下光影: 悬停跟随光斑 + 按下缓慢扩散(Win10 Reveal).
    59	    // 全部走 Composition, 动画在合成线程跑, 保证实时.
    60	    private readonly Dictionary<Border, CardHoverFx> _cardFx = new();
    61	    // Reveal 按下: 绝不能从半径 0 起 — 径向渐变半径为 0 时中心色会瞬间铺满整卡.
    62	    private const float PressRadiusStart = 18f;
    63	    private const float PressRadiusHoldMax = 140f; // 按住时缓慢扩到的最大半径(局部光, 非整卡淹没)
    64	    private const int PressExpandMs = 900;         // 按住扩散时长, 偏慢才像 Win10
    65	    private const int PressReleaseExpandMs = 420;
    66	    private const int PressReleaseFadeMs = 380;
    67	
    68	    // 某些 WinUI/Windows 组合下, 卡片的 handledEventsToo 指针监听会让子按钮丢失 Click.
    69	    // 记录一次完整按压期间是否已经收到 Click, 仅在 Click 丢失时走指针兜底.
    70	    private long _startStopActivationVersion;
    71	    private long _startStopPointerDownVersion = -1;
    72	    private uint? _startStopPointerId;
    73	
    74	    private sealed class CardHoverFx
    75	    {
    76	        public required ContainerVisual Root = null!;
    77	        public required SpriteVisual Spot = null!;
    78	        public required CompositionRadialGradientBrush Brush = null!;
    79	        // Win10 Reveal 式按下: 从触点向外缓慢扩散的径向光(非整卡瞬时模糊).
    80	        public required SpriteVisual PressSpot = null!;
    81	        public required CompositionRadialGradientBrush PressBrush = null!;
    82	        public bool Hovering;
    83	        public bool Pressing;
    84	        public float CardW;
    85	        public float CardH;
    86	    }
    87	
    88	    public MainWindow()
    89	    {
    90	        this.InitializeComponent();
    91	        _dispatcher = global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    92	
    93	        _engine = new AutoBrightnessEngine(_params);
    94	        _engine.Log += OnEngineLog;
    95	        _engine.Status += OnEngineStatus;
    96	        _engine.Stopped += OnEngineStopped;
    97	
    98	        // Windows 11 使用 BaseAlt 云母形成稳定的桌面色温；Windows 10 回退亚克力。
    99	        // 控件层仍保留方角、细描边和 Reveal，形成 Win11 材质 + Win10 交互的混合风格。
   100	        try
   101	        {
   102	            SystemBackdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
   103	                ? new MicaBackdrop { Kind = MicaKind.BaseAlt }
   104	                : new DesktopAcrylicBackdrop();
   105	        }
   106	        catch
   107	        {
   108	            try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
   109	            catch { /* 不支持系统材质时由主题色兜底。 */ }
   110	        }
   111	
   112	        // 自定义标题栏: 扩展内容到标题栏区域, 待根元素进入可视树后再指定拖拽区,
   113	        // 避免在元素尚无布局尺寸时 SetTitleBar 触发 native 输入 sink 异常关闭窗口.
   114	        Title = "环境光自动亮度调节";
   115	        ExtendsContentIntoTitleBar = true;
   116	        try
   117	        {
   118	            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
   119	            _trayIcon = new TrayIcon(hwnd, RestoreFromTray, ToggleEngineFromTray,
   120	                ExitFromTray, () => _engine.IsRunning);
   121	        }
   122	        catch { /* 托盘初始化失败时仍可正常使用窗口。 */ }
   123	        try { AppWindow.Closing += OnAppWindowClosing; }
   124	        catch { /* 关闭拦截不可用时保留系统默认关闭行为。 */ }
   125	        Closed += (_, _) =>
   126	        {
   127	            _clock.Stop();
   128	            _engine.Stop();
   129	            _trayIcon?.Dispose();
   130	            _trayIcon = null;
   131	        };
   132	        RootGrid.Loaded += (_, _) =>
   133	        {
   134	            SetTitleBar(AppTitleBar);
   135	
   136	            // 全局松手兜底: 指针在卡外松开时也能结束按下扩散.
   137	            RootGrid.AddHandler(UIElement.PointerReleasedEvent,
   138	                new PointerEventHandler(OnRootPointerReleased), true);
   139	            RootGrid.AddHandler(UIElement.PointerCanceledEvent,
   140	                new PointerEventHandler(OnRootPointerReleased), true);
   141	
   142	            // 卡片悬停: 跟随光标的径向光斑 (Win10 Reveal 风, 不做 Z 抬升).
   143	            AttachCardHover(GreetingCard);
   144	            AttachCardHover(MonitorCard);
   145	            AttachCardHover(LogCard);
   146	            AttachCardHover(SettingsCard);
   147	             AttachCardHover(AboutCard);
   148	             _dispatcher.TryEnqueue(ApplyLaunchPreferences);
   149	        };
   150	
   151	        // 空格启停: 隧道阶段拦截, 抢在 ScrollViewer 把空格当翻页、按钮把空格当 Click 之前.
   152	        // 挂在构造里(不必等 Loaded), handledEventsToo 避免子控件先吃掉按键.
   153	        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent,
   154	            new KeyEventHandler(OnRootPreviewKeyDown), true);
   155	
   156	        StartStopButton.AddHandler(UIElement.PointerPressedEvent,
   157	            new PointerEventHandler(OnStartStopPointerPressed), true);
   158	        StartStopButton.AddHandler(UIElement.PointerReleasedEvent,
   159	            new PointerEventHandler(OnStartStopPointerReleased), true);
   160	        StartStopButton.AddHandler(UIElement.PointerCanceledEvent,
   161	            new PointerEventHandler(OnStartStopPointerCanceled), true);
   162	
   163	        LogList.ItemsSource = _logs;
   164	        LoadPreferences();
   165	        BuildSliders();
   166	
   167	        // 读取上次的浅/深色偏好并应用 (默认深色).
   168	        _isDark = LoadThemePref();
   169	        ApplyTheme(_isDark);
   170	
   171	        // 问候语 + 数字时钟 (每秒刷新).
   172	        UpdateGreeting();
   173	        UpdateClock();
   174	        _clock.Tick += (_, _) => { UpdateClock(); UpdateGreeting(); };
   175	        _clock.Start();
   176	
   177	        // 主页导航/问候卡: 异步加载 Windows 用户账户头像.
   178	        _ = LoadUserAvatarAsync();
   179	
   180	        // 初始窗口尺寸 (容纳导航栏 + 内容).
   181	        try { AppWindow?.Resize(new Windows.Graphics.SizeInt32(980, 720)); } catch { }
   182	
   183	        AppendLog("就绪. 点击\"开始\"或按空格启动自动调节.");
   184	    }
   185	
   186	    // 关闭窗口时询问：最小化到后台继续运行，或退出整个程序。
   187	    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
   188	    {
   189	        if (_allowWindowClose) return;
   190	
   191	        args.Cancel = true;
   192	        if (_closePromptShowing) return;
   193	        _closePromptShowing = true;
   194	
   195	        try
   196	        {
   197	            var dialog = new ContentDialog
   198	            {
   199	                Title = "退出环境光自动亮度？",
   200	                Content = "选择“隐藏到托盘”后，程序会继续运行并自动调节亮度。双击托盘图标可恢复窗口。",
   201	                PrimaryButtonText = "隐藏到托盘",
   202	                SecondaryButtonText = "关闭程序",
   203	                CloseButtonText = "取消",
   204	                DefaultButton = ContentDialogButton.Primary,
   205	                XamlRoot = RootGrid.XamlRoot
   206	            };
   207	
   208	            var result = await dialog.ShowAsync();
   209	            if (result == ContentDialogResult.Primary)
   210	            {
   211	                if (_trayIcon?.Show() == true)
   212	                {
   213	                    AppWindow.Hide();
   214	                }
   215	                else if (AppWindow.Presenter is OverlappedPresenter presenter)
   216	                {
   217	                    presenter.Minimize();
   218	                }
   219	            }
   220	            else if (result == ContentDialogResult.Secondary)
   221	            {
   222	                _allowWindowClose = true;
   223	                _trayIcon?.Dispose();
   224	                Close();
   225	            }
   226	        }
   227	        catch
   228	        {
   229	            // 对话框创建失败时不强制退出，避免用户误关并丢失后台任务。
   230	        }
   231	        finally
   232	        {
   233	            _closePromptShowing = false;
   234	        }
   235	    }
   236	
   237	    private void RestoreFromTray()
   238	    {
   239	        _trayIcon?.Hide();
   240	        AppWindow.Show();
   241	        if (AppWindow.Presenter is OverlappedPresenter presenter)
   242	            presenter.Restore();
   243	        Activate();
   244	    }
   245	
   246	    private void ExitFromTray()
   247	    {
   248	        _allowWindowClose = true;
   249	        _trayIcon?.Dispose();
   250	        Close();
   251	    }
   252	
   253	    // ---- 卡片光影: 悬停跟随光斑 + 按下实时扩散(Win10 Reveal 风) + 抬升投影 ----
   254	    private void AttachCardHover(Border card)
   255	    {
   256	        if (_cardFx.ContainsKey(card)) return;
   257	
   258	        var visual = ElementCompositionPreview.GetElementVisual(card);
   259	        var comp = visual.Compositor;
   260	
   261	        // 容器: 同时挂悬停光斑 + 按下扩散光, 叠在卡片最上层, 不抢交互.
   262	        var root = comp.CreateContainerVisual();
   263	        root.IsHitTestVisible = false;
   264	
   265	        // 悬停径向光斑: 中心亮、边缘透明, 跟随光标.
   266	        var brush = MakeRadialBrush(comp, 160f);
   267	        var spot = comp.CreateSpriteVisual();
   268	        spot.Brush = brush;
   269	        spot.IsHitTestVisible = false;
   270	        spot.Opacity = 0f;
   271	        root.Children.InsertAtTop(spot);
   272	
   273	        // 按下扩散光: 从触点缓慢外扩, 类似 Win10 Reveal (局部光晕, 非整卡淹没).
   274	        // 起始半径必须 > 0, 否则径向渐变半径为 0 时中心色会瞬间铺满整张卡.
   275	        var pressBrush = MakePressRadialBrush(comp, PressRadiusStart);
   276	        var pressSpot = comp.CreateSpriteVisual();
   277	        pressSpot.Brush = pressBrush;
   278	        pressSpot.IsHitTestVisible = false;
   279	        pressSpot.Opacity = 0f;
   280	        root.Children.InsertAtTop(pressSpot);
   281	
   282	        ElementCompositionPreview.SetElementChildVisual(card, root);
   283	
   284	        var fx = new CardHoverFx
   285	        {
   286	            Root = root,
   287	            Spot = spot,
   288	            Brush = brush,
   289	            PressSpot = pressSpot,
   290	            PressBrush = pressBrush,
   291	        };
   292	        _cardFx[card] = fx;
   293	
   294	        void SyncSize()
   295	        {
   296	            float w = (float)card.ActualWidth;
   297	            float h = (float)card.ActualHeight;
   298	            if (w <= 0 || h <= 0) return;
   299	            fx.CardW = w;
   300	            fx.CardH = h;
   301	            root.Size = new Vector2(w, h);
   302	            spot.Size = new Vector2(w, h);
   303	            pressSpot.Size = new Vector2(w, h);
   304	            // 与 AppCardStyle 圆角一致(Win10 方角 3px), 光效不溢出卡片.
   305	            float cr = (float)Math.Max(card.CornerRadius.TopLeft, 3);
   306	            var clipGeo = comp.CreateRoundedRectangleGeometry();
   307	            clipGeo.Size = new Vector2(w, h);
   308	            clipGeo.CornerRadius = new Vector2(cr, cr);
   309	            root.Clip = comp.CreateGeometricClip(clipGeo);
   310	            // 悬停光斑半径随卡片短边缩放, 保持各卡视觉密度一致.
   311	            float r = Math.Max(120f, Math.Min(w, h) * 0.55f);
   312	            brush.EllipseRadius = new Vector2(r, r);
   313	        }
   314	
   315	        card.SizeChanged += (_, _) => SyncSize();
   316	        SyncSize();
   317	
   318	        card.PointerEntered += OnCardPointerEntered;
   319	        card.PointerMoved += OnCardPointerMoved;
   320	        card.PointerExited += OnCardPointerExited;
   321	        card.PointerCanceled += OnCardPointerCanceled;
   322	        card.PointerCaptureLost += OnCardPointerCanceled;
   323	        // handledEventsToo: 子控件已处理的按下也冒泡到卡;
   324	        // OnCardPointerPressed 内会跳过按钮/滑块等交互控件, 空白区才播缓慢 Reveal.
   325	        card.AddHandler(UIElement.PointerPressedEvent,
   326	            new PointerEventHandler(OnCardPointerPressed), true);
   327	        card.AddHandler(UIElement.PointerReleasedEvent,
   328	            new PointerEventHandler(OnCardPointerReleased), true);
   329	    }
   330	
   331	    private static CompositionRadialGradientBrush MakeRadialBrush(Compositor comp, float radius)
   332	    {
   333	        var brush = comp.CreateRadialGradientBrush();
   334	        brush.MappingMode = CompositionMappingMode.Absolute;
   335	        brush.EllipseCenter = new Vector2(0, 0);
   336	        brush.EllipseRadius = new Vector2(radius, radius);
   337	        var stop0 = comp.CreateColorGradientStop();
   338	        stop0.Offset = 0f;
   339	        stop0.Color = Color.FromArgb(0, 255, 255, 255);
   340	        var stop1 = comp.CreateColorGradientStop();
   341	        stop1.Offset = 1f;
   342	        stop1.Color = Color.FromArgb(0, 255, 255, 255);
   343	        brush.ColorStops.Insert(0, stop0);
   344	        brush.ColorStops.Insert(1, stop1);
   345	        return brush;
   346	    }
   347	
   348	    // 按下 Reveal: 3 档色停, 中心亮 → 中段柔光 → 边缘全透, 扩散时边缘更像 Win10 软晕.
   349	    private static CompositionRadialGradientBrush MakePressRadialBrush(Compositor comp, float radius)
   350	    {
   351	        var brush = comp.CreateRadialGradientBrush();
   352	        brush.MappingMode = CompositionMappingMode.Absolute;
   353	        brush.EllipseCenter = new Vector2(0, 0);
   354	        brush.EllipseRadius = new Vector2(radius, radius);
   355	        for (int i = 0; i < 3; ++i)
   356	        {
   357	            var stop = comp.CreateColorGradientStop();
   358	            stop.Offset = i switch { 0 => 0f, 1 => 0.45f, _ => 1f };
   359	            stop.Color = Color.FromArgb(0, 255, 255, 255);
   360	            brush.ColorStops.Insert(i, stop);
   361	        }
   362	        return brush;
   363	    }
   364	
   365	    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
   366	    {
   367	        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
   368	        fx.Hovering = true;
   369	        ApplyHoverSpotColor(fx);
   370	
   371	        var visual = ElementCompositionPreview.GetElementVisual(card);
   372	        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
   373	        fade.InsertKeyFrame(1f, 1f);
   374	        fade.Duration = TimeSpan.FromMilliseconds(160);
   375	        fx.Spot.StartAnimation("Opacity", fade);
   376	
   377	        UpdateSpotCenter(card, fx, e);
   378	    }
   379	
   380	    private void OnCardPointerMoved(object sender, PointerRoutedEventArgs e)
   381	    {
   382	        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
   383	        if (fx.Hovering) UpdateSpotCenter(card, fx, e);
   384	        // 按下时持续跟随触点, 光心实时贴合手指/鼠标.
   385	        if (fx.Pressing) UpdatePressCenter(card, fx, e);
   386	    }
   387	
   388	    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
   389	    {
   390	        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
   391	        fx.Hovering = false;
   392	
   393	        // 仍按着不结束扩散, 只收悬停光斑; 真正松开/取消时再收.
   394	        var fade = ElementCompositionPreview.GetElementVisual(card)
   395	            .Compositor.CreateScalarKeyFrameAnimation();
   396	        fade.InsertKeyFrame(1f, 0f);
   397	        fade.Duration = TimeSpan.FromMilliseconds(200);
   398	        fx.Spot.StartAnimation("Opacity", fade);
   399	    }
   400	
   401	    private void OnCardPointerCanceled(object sender, PointerRoutedEventArgs e)
   402	    {
   403	        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
   404	        fx.Hovering = false;
   405	        EndPress(card, fx, expandOut: false);
   406	    }
   407	
   408	    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
   409	    {
   410	        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
   411	
   412	        // 交互控件(按钮/滑块等)上不播 Reveal: 否则 handledEventsToo 会抢视觉注意力,
   413	        // 且部分系统上会影响 Click 路由. 卡片空白区才出缓慢扩散.
   414	        if (IsInteractiveElement(e.OriginalSource as DependencyObject, card))
   415	            return;
   416	
   417	        // 仅响应主键/触摸/笔, 避免右键等干扰.
   418	        var pt = e.GetCurrentPoint(card);
   419	        if (!pt.Properties.IsLeftButtonPressed
   420	            && pt.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
   421	            && pt.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
   422	            return;
   423	
   424	        fx.Pressing = true;
   425	        ApplyPressSpotColor(fx);
   426	        UpdatePressCenter(card, fx, e);
   427	
   428	        var visual = ElementCompositionPreview.GetElementVisual(card);
   429	        var comp = visual.Compositor;
   430	
   431	        // 局部缓慢扩散: 从一小圈半径起步, 扩到「局部光晕」上限(不是整卡淹没).
   432	        // 半径绝对不能从 0 起, 否则径向渐变会瞬间整卡填色.
   433	        float holdR = Math.Min(PressRadiusHoldMax, Math.Max(fx.CardW, fx.CardH) * 0.38f);
   434	        holdR = Math.Max(holdR, PressRadiusStart + 24f);
   435	        fx.PressBrush.StopAnimation("EllipseRadius");
   436	        fx.PressBrush.EllipseRadius = new Vector2(PressRadiusStart, PressRadiusStart);
   437	
   438	        // 缓入缓出: 前半段几乎不动, 后半段慢慢铺开 — 贴近 Win10 Reveal 手感.
   439	        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.2f), new Vector2(0.15f, 1f));
   440	        var expand = comp.CreateVector2KeyFrameAnimation();
   441	        expand.InsertKeyFrame(0f, new Vector2(PressRadiusStart, PressRadiusStart));
   442	        expand.InsertKeyFrame(1f, new Vector2(holdR, holdR), ease);
   443	        expand.Duration = TimeSpan.FromMilliseconds(PressExpandMs);
   444	        fx.PressBrush.StartAnimation("EllipseRadius", expand);
   445	
   446	        var fadeIn = comp.CreateScalarKeyFrameAnimation();
   447	        fadeIn.InsertKeyFrame(0f, 0f);
   448	        fadeIn.InsertKeyFrame(0.18f, 1f);
   449	        fadeIn.InsertKeyFrame(1f, 1f);
   450	        fadeIn.Duration = TimeSpan.FromMilliseconds(220);
   451	        fx.PressSpot.StopAnimation("Opacity");
   452	        fx.PressSpot.StartAnimation("Opacity", fadeIn);
   453	        // 不 CapturePointer: 避免抢走子控件(按钮/滑块)的点击.
   454	    }
   455	
   456	    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
   457	    {
   458	        if (sender is not Border card || !_cardFx.TryGetValue(card, out var fx)) return;
   459	        EndPress(card, fx, expandOut: true);
   460	    }
   461	
   462	    // 指针在卡外松开时, 结束所有仍在按的卡.
   463	    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
   464	    {
   465	        foreach (var kv in _cardFx)
   466	        {
   467	            if (kv.Value.Pressing)
   468	                EndPress(kv.Key, kv.Value, expandOut: true);
   469	        }
   470	    }
   471	
   472	    // 松手: 再往外冲一截并淡出; 取消: 直接淡出收回.
   473	    // 注意: 绝不冲到整卡对角半径 — 那会变成「一整块模糊」.
   474	    private void EndPress(Border card, CardHoverFx fx, bool expandOut)
   475	    {
   476	        if (!fx.Pressing && fx.PressSpot.Opacity <= 0.01f) return;
   477	        fx.Pressing = false;
   478	
   479	        var comp = ElementCompositionPreview.GetElementVisual(card).Compositor;
   480	
   481	        float curR = fx.PressBrush.EllipseRadius.X;
   482	        if (curR < PressRadiusStart) curR = PressRadiusStart;
   483	        // 松手只再外扩约 35%, 并夹在局部光晕上限附近, 保持「从点扩散」的观感.
   484	        float endR = expandOut
   485	            ? Math.Min(curR * 1.35f, PressRadiusHoldMax * 1.25f)
   486	            : curR;
   487	
   488	        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0f), new Vector2(0.1f, 1f));
   489	        var expand = comp.CreateVector2KeyFrameAnimation();
   490	        expand.InsertKeyFrame(1f, new Vector2(endR, endR), ease);
   491	        expand.Duration = TimeSpan.FromMilliseconds(expandOut ? PressReleaseExpandMs : 160);
   492	        fx.PressBrush.StartAnimation("EllipseRadius", expand);
   493	
   494	        var fadeOut = comp.CreateScalarKeyFrameAnimation();
   495	        fadeOut.InsertKeyFrame(1f, 0f, ease);
   496	        fadeOut.Duration = TimeSpan.FromMilliseconds(expandOut ? PressReleaseFadeMs : 180);
   497	        fx.PressSpot.StartAnimation("Opacity", fadeOut);
   498	    }
   499	
   500	    // 原始命中点是否落在需要保留交互的控件上(按钮/滑块/开关/输入等).
   501	    // 从 OriginalSource 向上爬到本卡片为止; 注意按钮模板内部也有 Border,
   502	    // 绝不能「见到 Border 就停」, 只能以本卡片为终点.
   503	    private static bool IsInteractiveElement(DependencyObject? src, Border card)
   504	    {
   505	        for (var cur = src; cur is not null && !ReferenceEquals(cur, card);
   506	             cur = VisualTreeHelper.GetParent(cur))
   507	        {
   508	            if (cur is ButtonBase or Slider or ScrollBar or ToggleSwitch
   509	                or TextBox or ComboBox or NavigationViewItem
   510	                or HyperlinkButton or RepeatButton)
   511	                return true;
   512	        }
   513	        return false;
   514	    }
   515	
   516	    private static void UpdateSpotCenter(Border card, CardHoverFx fx, PointerRoutedEventArgs e)
   517	    {
   518	        var p = e.GetCurrentPoint(card).Position;
   519	        fx.Brush.EllipseCenter = new Vector2((float)p.X, (float)p.Y);
   520	    }
   521	
   522	    private static void UpdatePressCenter(Border card, CardHoverFx fx, PointerRoutedEventArgs e)
   523	    {
   524	        var p = e.GetCurrentPoint(card).Position;
   525	        fx.PressBrush.EllipseCenter = new Vector2((float)p.X, (float)p.Y);
   526	    }
   527	
   528	    // 悬停光斑色: Win10 Reveal 是中性白光, 不带蓝色调 — 深色提亮, 浅色压暗.
   529	    private void ApplyHoverSpotColor(CardHoverFx fx)
   530	    {
   531	        Color center = _isDark
   532	            ? Color.FromArgb(30, 255, 255, 255)
   533	            : Color.FromArgb(22, 0, 0, 0);
   534	        SetRadialStops(fx.Brush, center);
   535	    }
   536	
   537	    // 按下扩散色: 比悬停略强的中性光, 3 档软晕, 贴近 Win10 Reveal 按压高光.
   538	    private void ApplyPressSpotColor(CardHoverFx fx)
   539	    {
   540	        Color center = _isDark
   541	            ? Color.FromArgb(52, 255, 255, 255)
   542	            : Color.FromArgb(34, 0, 0, 0);
   543	        SetPressRadialStops(fx.PressBrush, center);
   544	    }
   545	
   546	    private static void SetRadialStops(CompositionRadialGradientBrush brush, Color center)
   547	    {
   548	        Color edge = Color.FromArgb(0, center.R, center.G, center.B);
   549	        if (brush.ColorStops.Count >= 2)
   550	        {
   551	            brush.ColorStops[0].Color = center;
   552	            brush.ColorStops[1].Color = edge;
   553	        }
   554	    }
   555	
   556	    // 中心亮 → 中段半透明 → 边缘全透, 扩散边缘柔和, 不像一整块实色铺开.
   557	    private static void SetPressRadialStops(CompositionRadialGradientBrush brush, Color center)
   558	    {
   559	        if (brush.ColorStops.Count < 3)
   560	        {
   561	            SetRadialStops(brush, center);
   562	            return;
   563	        }
   564	        Color mid = Color.FromArgb((byte)(center.A * 0.45), center.R, center.G, center.B);
   565	        Color edge = Color.FromArgb(0, center.R, center.G, center.B);
   566	        brush.ColorStops[0].Color = center;
   567	        brush.ColorStops[1].Color = mid;
   568	        brush.ColorStops[2].Color = edge;
   569	    }
   570	
   571	    private void RefreshAllHoverSpotColors()
   572	    {
   573	        foreach (var fx in _cardFx.Values)
   574	        {
   575	            if (fx.Hovering) ApplyHoverSpotColor(fx);
   576	            if (fx.Pressing) ApplyPressSpotColor(fx);
   577	        }
   578	    }
   579	
   580	    // ---- 用户头像: 导航「主页」图标 + 问候卡 ----
   581	    // 桌面 full-trust 下 Windows.System.User.GetAccountPicture 不可用,
   582	    // 直接读 HKLM AccountPicture 注册表指向的 Public\AccountPictures 文件.
   583	    private async Task LoadUserAvatarAsync()
   584	    {
   585	        string display = "";
   586	        try { display = Environment.UserName; } catch { /* 忽略 */ }
   587	        if (string.IsNullOrWhiteSpace(display)) display = "User";
   588	        HomeAvatar.DisplayName = display;
   589	
   590	        try
   591	        {
   592	            string? largePath = ResolveAccountPicturePath(preferred: new[] { 208, 448, 240, 192, 1080 });
   593	            string? smallPath = ResolveAccountPicturePath(preferred: new[] { 64, 48, 96, 40, 32 })
   594	                             ?? largePath;
   595	
   596	            if (largePath is not null)
   597	            {
   598	                var large = await LoadBitmapFromPathAsync(largePath);
   599	                if (large is not null) HomeAvatar.ProfilePicture = large;
   600	            }
   601	            if (smallPath is not null)
   602	            {
   603	                var small = await LoadBitmapFromPathAsync(smallPath);
   604	                if (small is not null)
   605	                    HomeNavItem.Icon = new ImageIcon { Source = small };
   606	            }
   607	        }
   608	        catch
   609	        {
   610	            // 无账户图 / 权限不足: 保留首字母头像 + 账户字形导航图标.
   611	        }
   612	    }
   613	
   614	    private static string? ResolveAccountPicturePath(int[] preferred)
   615	    {
   616	        try
   617	        {
   618	            string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
   619	            if (string.IsNullOrEmpty(sid)) return null;
   620	
   621	            using var key = Registry.LocalMachine.OpenSubKey(
   622	                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
   623	            if (key is null) return null;
   624	
   625	            foreach (int size in preferred)
   626	            {
   627	                if (key.GetValue("Image" + size) is string path
   628	                    && !string.IsNullOrWhiteSpace(path)
   629	                    && File.Exists(path))
   630	                    return path;
   631	            }
   632	
   633	            // 回退: 任意存在的 Image* 值.
   634	            foreach (string name in key.GetValueNames())
   635	            {
   636	                if (!name.StartsWith("Image", StringComparison.OrdinalIgnoreCase)) continue;
   637	                if (key.GetValue(name) is string path
   638	                    && !string.IsNullOrWhiteSpace(path)
   639	                    && File.Exists(path))
   640	                    return path;
   641	            }
   642	        }
   643	        catch { /* 注册表/文件不可读 */ }
   644	        return null;
   645	    }
   646	
   647	    private static async Task<BitmapImage?> LoadBitmapFromPathAsync(string path)
   648	    {
   649	        try
   650	        {
   651	            // 复制到内存再解码, 避免文件被系统锁定时 SetSource 失败.
   652	            byte[] bytes = await File.ReadAllBytesAsync(path);
   653	            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
   654	            using (var writer = new Windows.Storage.Streams.DataWriter(mem))
   655	            {
   656	                writer.WriteBytes(bytes);
   657	                await writer.StoreAsync();
   658	                writer.DetachStream();
   659	            }
   660	            mem.Seek(0);
   661	            var bmp = new BitmapImage();
   662	            await bmp.SetSourceAsync(mem);
   663	            return bmp;
   664	        }
   665	        catch { return null; }
   666	    }
   667	
   668	    // ---- 数字时钟 + 问候语 ----
   669	    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
   670	
   671	    private void UpdateClock()
   672	    {
   673	        var t = DateTime.Now;
   674	        string hh = t.Hour.ToString("D2"), mm = t.Minute.ToString("D2"), ss = t.Second.ToString("D2");
   675	        ClkH1.Text = hh[0].ToString(); ClkH2.Text = hh[1].ToString();
   676	        ClkM1.Text = mm[0].ToString(); ClkM2.Text = mm[1].ToString();
   677	        ClkS1.Text = ss[0].ToString(); ClkS2.Text = ss[1].ToString();
   678	    }
   679	
   680	    private void UpdateGreeting()
   681	    {
   682	        int h = DateTime.Now.Hour;
   683	        string period = h < 6 ? "凌晨好" : h < 11 ? "上午好" : h < 13 ? "中午好"
   684	                      : h < 18 ? "下午好" : "晚上好";
   685	        string user = "";
   686	        try { user = Environment.UserName; } catch { }
   687	        GreetingText.Text = string.IsNullOrWhiteSpace(user) ? $"{period}!" : $"{period}, {user}!";
   688	    }
   689	
   690	    // ---- 导航切换: 显示对应视图 ----
   691	    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
   692	    {
   693	        if (args.SelectedItem is not NavigationViewItem item) return;
   694	        string tag = item.Tag as string ?? "home";
   695	        HomeView.Visibility     = tag == "home"     ? Visibility.Visible : Visibility.Collapsed;
   696	        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
   697	        AboutView.Visibility    = tag == "about"    ? Visibility.Visible : Visibility.Collapsed;
   698	    }
   699	
   700	    private void LoadPreferences()
   701	    {
   702	        _loadingPreferences = true;
   703	        try
   704	        {
   705	            var settings = ApplicationData.Current.LocalSettings.Values;
   706	            _params.MinBrightness = ReadInt(settings, "MinBrightness", 10, 0, 100);
   707	            _params.MaxBrightness = ReadInt(settings, "MaxBrightness", 100, 0, 100);
   708	            _params.IntervalSec = ReadInt(settings, "IntervalSec", 3, 1, 30);
   709	            _params.Deadband = ReadInt(settings, "Deadband", 3, 0, 20);
   710	            _params.FadeSec = ReadInt(settings, "FadeSec", 2, 0, 10);
   711	            _params.SmoothPct = ReadInt(settings, "SmoothPct", 35, 1, 100);
   712	            _params.BrightSnap = ReadInt(settings, "BrightSnap", 101, 0, 256);
   713	            _autoAdjustOnLaunch = ReadBool(settings, AutoAdjustSettingKey, false);
   714	            _launchToTray = ReadBool(settings, LaunchToTraySettingKey, false);
   715	        }
   716	        catch { }
   717	        finally { _loadingPreferences = false; }
   718	    }
   719	
   720	    private static int ReadInt(IPropertySet settings, string key, int fallback, int min, int max)
   721	        => settings[key] is int value ? Math.Clamp(value, min, max) : fallback;
   722	
   723	    private static bool ReadBool(IPropertySet settings, string key, bool fallback)
   724	        => settings[key] is bool value ? value : fallback;
   725	
   726	    private void SavePreferences()
   727	    {
   728	        if (_loadingPreferences) return;
   729	        try
   730	        {
   731	            var values = ApplicationData.Current.LocalSettings.Values;
   732	            values["MinBrightness"] = _params.MinBrightness;
   733	            values["MaxBrightness"] = _params.MaxBrightness;
   734	            values["IntervalSec"] = _params.IntervalSec;
   735	            values["Deadband"] = _params.Deadband;
   736	            values["FadeSec"] = _params.FadeSec;
   737	            values["SmoothPct"] = _params.SmoothPct;
   738	            values["BrightSnap"] = _params.BrightSnap;
   739	            values[AutoAdjustSettingKey] = _autoAdjustOnLaunch;
   740	            values[LaunchToTraySettingKey] = _launchToTray;
   741	        }
   742	        catch { }
   743	    }
   744	
   745	    private async void ApplyLaunchPreferences()
   746	    {
   747	        _loadingPreferences = true;
   748	        try
   749	        {
   750	            var task = await StartupTask.GetAsync("BrightnessAutoStartup");
   751	            StartWithWindowsToggle.IsOn = task.State == StartupTaskState.Enabled;
   752	            AutoAdjustToggle.IsOn = _autoAdjustOnLaunch;
   753	            LaunchToTrayToggle.IsOn = _launchToTray;
   754	        }
   755	        catch { StartWithWindowsToggle.IsOn = false; }
   756	        finally { _loadingPreferences = false; }
   757	
   758	        if (_autoAdjustOnLaunch) ActivateStartStop();
   759	        if (_launchToTray && _trayIcon?.Show() == true)
   760	            AppWindow.Hide();
   761	    }
   762	
   763	    private async void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
   764	    {
   765	        if (_loadingPreferences) return;
   766	        try
   767	        {
   768	            var task = await StartupTask.GetAsync("BrightnessAutoStartup");
   769	            if (StartWithWindowsToggle.IsOn)
   770	            {
   771	                var state = await task.RequestEnableAsync();
   772	                if (state != StartupTaskState.Enabled)
   773	                {
   774	                    _loadingPreferences = true;
   775	                    StartWithWindowsToggle.IsOn = false;
   776	                    _loadingPreferences = false;
   777	                }
   778	            }
   779	            else
   780	                task.Disable();
   781	        }
   782	        catch
   783	        {
   784	            _loadingPreferences = true;
   785	            StartWithWindowsToggle.IsOn = false;
   786	            _loadingPreferences = false;
   787	        }
   788	    }
   789	
   790	    private void OnPreferenceToggled(object sender, RoutedEventArgs e)
   791	    {
   792	        if (_loadingPreferences) return;
   793	        _autoAdjustOnLaunch = AutoAdjustToggle.IsOn;
   794	        _launchToTray = LaunchToTrayToggle.IsOn;
   795	        SavePreferences();
   796	    }
   797	
   798	    private void OnResetSettingsClick(object sender, RoutedEventArgs e)
   799	    {
   800	        _loadingPreferences = true;
   801	        _params.MinBrightness = 10; _params.MaxBrightness = 100; _params.IntervalSec = 3;
   802	        _params.Deadband = 3; _params.FadeSec = 2; _params.SmoothPct = 35; _params.BrightSnap = 101;
   803	        _autoAdjustOnLaunch = false; _launchToTray = false;
   804	        _loadingPreferences = false;
   805	        SlidersPanel.Children.Clear();
   806	        BuildSliders();
   807	        AutoAdjustToggle.IsOn = false;
   808	        LaunchToTrayToggle.IsOn = false;
   809	        SavePreferences();
   810	    }
   811	
   812	    // ---- 滑块定义: 对应 C++ SetupSliders 的 7 行 ----
   813	    private enum Sld { Min, Max, Interval, Deadband, Fade, Smooth, Snap }
   814	
   815	    private readonly record struct SliderDef(
   816	        string Label, int Lo, int Hi, int Value, Func<int, string> Fmt);
   817	
   818	    private void BuildSliders()
   819	    {
   820	        var defs = new[]
   821	        {
   822	            new SliderDef("最低亮度", 0, 100, _params.MinBrightness, v => $"{v}%"),
   823	            new SliderDef("最高亮度", 0, 100, _params.MaxBrightness, v => $"{v}%"),
   824	            new SliderDef("检测间隔", 1, 30,  _params.IntervalSec,   v => $"{v} 秒"),
   825	            new SliderDef("变化死区", 0, 20,  _params.Deadband,      v => $"{v}%"),
   826	            new SliderDef("过渡时间", 0, 10,  _params.FadeSec,       v => $"{v} 秒"),
   827	            new SliderDef("平滑系数", 1, 100, _params.SmoothPct,     v => (v / 100.0).ToString("F2")),
   828	            new SliderDef("强光跳满", 0, 255,
   829	                _params.BrightSnap > 255 ? 0 : _params.BrightSnap, FmtSnap),
   830	        };
   831	
   832	        for (int i = 0; i < defs.Length; ++i)
   833	        {
   834	            var def = defs[i];
   835	            int idx = i;
   836	
   837	            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
   838	            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
   839	            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   840	            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
   841	
   842	            var label = new TextBlock
   843	            {
   844	                Text = def.Label,
   845	                FontSize = 13,
   846	                VerticalAlignment = VerticalAlignment.Center
   847	            };
   848	            Grid.SetColumn(label, 0);
   849	
   850	            var slider = new Slider
   851	            {
   852	                Minimum = def.Lo,
   853	                Maximum = def.Hi,
   854	                Value = def.Value,
   855	                StepFrequency = 1,
   856	                VerticalAlignment = VerticalAlignment.Center,
   857	                Margin = new Thickness(4, 0, 8, 0)
   858	            };
   859	            Grid.SetColumn(slider, 1);
   860	
   861	            var valueLabel = new TextBlock
   862	            {
   863	                Text = def.Fmt((int)def.Value),
   864	                FontSize = 13,
   865	                TextAlignment = TextAlignment.Right,
   866	                VerticalAlignment = VerticalAlignment.Center
   867	            };
   868	            Grid.SetColumn(valueLabel, 2);
   869	
   870	            var fmt = def.Fmt;
   871	            slider.ValueChanged += (_, e) =>
   872	            {
   873	                int v = (int)Math.Round(e.NewValue);
   874	                valueLabel.Text = fmt(v);
   875	                ApplySlider(idx, v);
   876	                SavePreferences();
   877	            };
   878	
   879	            row.Children.Add(label);
   880	            row.Children.Add(slider);
   881	            row.Children.Add(valueLabel);
   882	            SlidersPanel.Children.Add(row);
   883	
   884	            _sliders[i] = slider;
   885	            _valueLabels[i] = valueLabel;
   886	            _nameLabels[i] = label;
   887	        }
   888	        // 首次填充后按当前主题上色.
   889	        ApplySliderLabelColors();
   890	    }
   891	
   892	    // 滑块标签配色: 名称用暗色文字, 数值用主文字色, 随主题切换.
   893	    private void ApplySliderLabelColors()
   894	    {
   895	        var dim  = new SolidColorBrush(_isDark
   896	            ? Color.FromArgb(255, 158, 158, 158) : Color.FromArgb(255, 90, 90, 90));
   897	        var main = new SolidColorBrush(_isDark
   898	            ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 17, 17, 17));
   899	        for (int i = 0; i < _nameLabels.Length; ++i)
   900	        {
   901	            if (_nameLabels[i] != null)  _nameLabels[i].Foreground = dim;
   902	            if (_valueLabels[i] != null) _valueLabels[i].Foreground = main;
   903	        }
   904	    }
   905	
   906	    private static string FmtSnap(int v) => (v == 0 || v > 255) ? "关闭" : $"{v}/255";
   907	
   908	    // 把滑块值写回运行参数 (对应 ApplySliderToParams).
   909	    private void ApplySlider(int idx, int v)
   910	    {
   911	        switch ((Sld)idx)
   912	        {
   913	            case Sld.Min:      _params.MinBrightness = v; break;
   914	            case Sld.Max:      _params.MaxBrightness = v; break;
   915	            case Sld.Interval: _params.IntervalSec = v; break;
   916	            case Sld.Deadband: _params.Deadband = v; break;
   917	            case Sld.Fade:     _params.FadeSec = v; break;
   918	            case Sld.Smooth:   _params.SmoothPct = v; break;
   919	            case Sld.Snap:     _params.BrightSnap = (v == 0 ? 256 : v); break;
   920	        }
   921	    }
   922	
   923	    private void ToggleEngineFromTray()
   924	    {
   925	        if (!StartStopButton.IsEnabled && !_engine.IsRunning) return;
   926	        ActivateStartStop();
   927	    }
   928	
   929	    // ---- 浅色 / 深色主题 ----
   930	    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
   931	    {
   932	        NavView.IsPaneOpen = !NavView.IsPaneOpen;
   933	        ToolTipService.SetToolTip(PaneToggleButton,
   934	            NavView.IsPaneOpen ? "收起导航" : "展开导航");
   935	    }
   936	
   937	    private void OnThemeToggleTapped(object sender, TappedRoutedEventArgs e)
   938	        => ToggleTheme();
   939	
   940	    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
   941	        => ToggleTheme();
   942	
   943	    private void ToggleTheme()
   944	    {
   945	        _isDark = !_isDark;
   946	        ApplyTheme(_isDark);
   947	        SaveThemePref(_isDark);
   948	    }
   949	
   950	    // 应用主题: 切换 RequestedTheme(联动所有 ThemeResource) +
   951	    // 标题栏按钮配色 + 切换按钮图标 + 滑块标签色 + 日志刷新.
   952	    private void ApplyTheme(bool dark)
   953	    {
   954	        if (RootGrid.XamlRoot is not null || true)
   955	            RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
   956	
   957	        // 切换按钮图标: 深色显示太阳(点击转浅色), 浅色显示月亮(点击转深色).
   958	        // Segoe MDL2: E706=亮度(太阳), E708=夜间(月亮).
   959	        ThemeIcon.Glyph = dark ? "" : "";
   960	        ToolTipService.SetToolTip(ThemeButton, dark ? "切换到浅色" : "切换到深色");
   961	
   962	        // 标题栏三个系统按钮的前景/悬停色, 随主题变.
   963	        try
   964	        {
   965	            var tb = AppWindow.TitleBar;
   966	            Color fg   = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 17, 17, 17);
   967	            Color hov  = dark ? Color.FromArgb(38, 255, 255, 255)  : Color.FromArgb(24, 0, 0, 0);
   968	            tb.ButtonForegroundColor        = fg;
   969	            tb.ButtonHoverForegroundColor   = fg;
   970	            tb.ButtonInactiveForegroundColor= dark ? Color.FromArgb(255, 128, 128, 128)
   971	                                                   : Color.FromArgb(255, 138, 138, 138);
   972	            tb.ButtonBackgroundColor        = Colors.Transparent;
   973	            tb.ButtonInactiveBackgroundColor= Colors.Transparent;
   974	            tb.ButtonHoverBackgroundColor   = hov;
   975	        }
   976	        catch { /* 某些环境无 AppWindow.TitleBar, 忽略 */ }
   977	
   978	        ApplySliderLabelColors();
   979	        RefreshAllHoverSpotColors();
   980	
   981	        // 日志文字色跟随主题: 重设数据源触发 x:Bind 重新取色.
   982	        LogEntry.IsDarkTheme = dark;
   983	        var snapshot = _logs;   // 触发模板重新求值
   984	        LogList.ItemsSource = null;
   985	        LogList.ItemsSource = snapshot;
   986	    }
   987	
   988	    private bool LoadThemePref()
   989	    {
   990	        try
   991	        {
   992	            var v = ApplicationData.Current.LocalSettings.Values[ThemeSettingKey];
   993	            if (v is string s) return s != "Light";
   994	        }
   995	        catch { /* 未打包运行时无 LocalSettings, 用默认 */ }
   996	        return true;   // 默认深色
   997	    }
   998	
   999	    private void SaveThemePref(bool dark)
  1000	    {
  1001	        try { ApplicationData.Current.LocalSettings.Values[ThemeSettingKey] = dark ? "Dark" : "Light"; }
  1002	        catch { /* 忽略持久化失败 */ }
  1003	    }
  1004	
  1005	    // 空格 = 开始/停止. Preview + handledEventsToo, 避免主页 ScrollViewer 把空格当翻页吃掉.
  1006	    // 输入框里仍把空格留给打字.
  1007	    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
  1008	    {
  1009	        if (e.OriginalKey != VirtualKey.Space && e.Key != VirtualKey.Space) return;
  1010	        if (!StartStopButton.IsEnabled) return;
  1011	        if (IsTypingTarget(RootGrid.XamlRoot is { } xr
  1012	                ? FocusManager.GetFocusedElement(xr)
  1013	                : null))
  1014	            return;
  1015	
  1016	        OnStartStopClick(StartStopButton, new RoutedEventArgs());
  1017	        e.Handled = true;
  1018	    }
  1019	
  1020	    private static bool IsTypingTarget(object? focused)
  1021	    {
  1022	        for (var cur = focused as DependencyObject; cur is not null;
  1023	             cur = VisualTreeHelper.GetParent(cur))
  1024	        {
  1025	            if (cur is TextBox or RichEditBox or PasswordBox or ComboBox or AutoSuggestBox)
  1026	                return true;
  1027	        }
  1028	        return false;
  1029	    }
  1030	
  1031	    // ---- 启停 ----
  1032	    private void OnStartStopClick(object sender, RoutedEventArgs e)
  1033	        => ActivateStartStop();
  1034	
  1035	    private void OnStartStopPointerPressed(object sender, PointerRoutedEventArgs e)
  1036	    {
  1037	        var point = e.GetCurrentPoint(StartStopButton);
  1038	        if (!point.Properties.IsLeftButtonPressed
  1039	            && point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
  1040	            && point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen)
  1041	            return;
  1042	
  1043	        _startStopPointerId = e.Pointer.PointerId;
  1044	        _startStopPointerDownVersion = _startStopActivationVersion;
  1045	    }
  1046	
  1047	    private void OnStartStopPointerReleased(object sender, PointerRoutedEventArgs e)
  1048	    {
  1049	        if (_startStopPointerId != e.Pointer.PointerId) return;
  1050	
  1051	        long versionAtPress = _startStopPointerDownVersion;
  1052	        _startStopPointerId = null;
  1053	        _startStopPointerDownVersion = -1;
  1054	
  1055	        var p = e.GetCurrentPoint(StartStopButton).Position;
  1056	        if (p.X < 0 || p.Y < 0
  1057	            || p.X > StartStopButton.ActualWidth
  1058	            || p.Y > StartStopButton.ActualHeight)
  1059	            return;
  1060	
  1061	        // Button.Click 通常在同一轮输入派发中产生. 排到队尾后再检查版本,
  1062	        // 只有标准 Click 确实丢失时才补一次, 避免正常环境下启停两次.
  1063	        _dispatcher.TryEnqueue(() =>
  1064	        {
  1065	            if (StartStopButton.IsEnabled
  1066	                && _startStopActivationVersion == versionAtPress)
  1067	                ActivateStartStop();
  1068	        });
  1069	    }
  1070	
  1071	    private void OnStartStopPointerCanceled(object sender, PointerRoutedEventArgs e)
  1072	    {
  1073	        if (_startStopPointerId != e.Pointer.PointerId) return;
  1074	        _startStopPointerId = null;
  1075	        _startStopPointerDownVersion = -1;
  1076	    }
  1077	
  1078	    private void ActivateStartStop()
  1079	    {
  1080	        _startStopActivationVersion++;
  1081	
  1082	        if (_engine.IsRunning)
  1083	        {
  1084	            _engine.Stop();
  1085	            StartStopButton.IsEnabled = false;   // 等待 Stopped 事件恢复
  1086	        }
  1087	        else
  1088	        {
  1089	            _engine.Start();
  1090	            SetRunningVisual(true);
  1091	        }
  1092	    }
  1093	
  1094	    // 配色取 Win10 系统色板: 强调蓝 #0078D7 / 危险红 #E81123 / 成功绿 #107C10.
  1095	    private void SetRunningVisual(bool running)
  1096	    {
  1097	        StartStopButton.Content = running ? "停止" : "开始";
  1098	        StartStopButton.Background = new SolidColorBrush(running
  1099	            ? Color.FromArgb(255, 232, 17, 35)     // 停止红
  1100	            : Color.FromArgb(255, 0, 120, 215));   // 开始蓝
  1101	        StatusDot.Fill = new SolidColorBrush(running
  1102	            ? Color.FromArgb(255, 16, 124, 16)
  1103	            : Color.FromArgb(255, 158, 158, 158));
  1104	        StatusLabel.Text = running ? "运行中" : "已停止";
  1105	    }
  1106	
  1107	    // ---- 引擎事件 (marshal 到 UI 线程) ----
  1108	    private void OnEngineLog(string line) => _dispatcher.TryEnqueue(() => AppendLog(line));
  1109	
  1110	    private void OnEngineStatus(StatusUpdate s) => _dispatcher.TryEnqueue(() =>
  1111	    {
  1112	        LumaGauge.Value = s.Luma;
  1113	        LumaGauge.BigText = s.Luma.ToString();
  1114	        BriGauge.Value = s.Brightness;
  1115	        BriGauge.BigText = $"{s.Brightness}%";
  1116	    });
  1117	
  1118	    private void OnEngineStopped() => _dispatcher.TryEnqueue(() =>
  1119	    {
  1120	        SetRunningVisual(false);
  1121	        StartStopButton.IsEnabled = true;
  1122	    });
  1123	
  1124	    private void AppendLog(string line)
  1125	    {
  1126	        _logs.Add(new LogEntry(line));
  1127	        while (_logs.Count > MaxLogLines) _logs.RemoveAt(0);
  1128	        LogScroll.UpdateLayout();
  1129	        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
  1130	    }
  1131	}
