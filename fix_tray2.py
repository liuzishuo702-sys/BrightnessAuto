# 为 TrayMenuWindow.cs 加入云母 + Win10 平板风格
# 使用真实 WinUI 3 API 名称
p = r'D:\自动调节亮度\Project1\BrightnessAuto\Controls\TrayMenuWindow.cs'
s = open(p, encoding='utf-8').read()

changed = False

# 1) 加 using Microsoft.UI.Composition （SystemBackdrop 所需）
if 'using Microsoft.UI.Composition;' not in s:
    s = s.replace('using Microsoft.UI.Composition.SystemBackdrops;',
                  'using Microsoft.UI.Composition;\nusing Microsoft.UI.Composition.SystemBackdrops;')
    changed = True
    print('1. added using Microsoft.UI.Composition')

# 2) 给 TrayMenuWindow 构造函数里挂 MicaBackdrop（宿主窗口本身）
old_ctor = '''        Title = "环境光自动亮度菜单宿主";
        Content = _host;
        _menu.MenuFlyoutPresenterStyle = CreatePresenterStyle();

        // 注意: 不要给宿主窗口挂 SystemBackdrop —— 宿主是 2x2 透明窗口，
        // 即使挂了也看不见。真正可见的是 MenuFlyoutPresenter，云母必须刷在它身上。

        _popupZOrderTimer = _host.DispatcherQueue.CreateTimer();'''

new_ctor = '''        Title = "环境光自动亮度菜单宿主";
        Content = _host;
        _menu.MenuFlyoutPresenterStyle = CreatePresenterStyle();

        // 给宿主窗口挂 Windows 11 Mica 云母，宿主透明区域会显示云母质感，
        // 菜单弹层悬浮在云母之上，整体呈现云母 + 半透明卡片的视觉.
        this.SystemBackdrop = new MicaBackdrop();

        _popupZOrderTimer = _host.DispatcherQueue.CreateTimer();'''

if old_ctor in s:
    s = s.replace(old_ctor, new_ctor)
    changed = True
    print('2. added SystemBackdrop = MicaBackdrop to constructor')
else:
    print('2. SKIPPED: ctor block not found (maybe already applied)')

# 3) CreatePresenterStyle: 去掉 AcrylicBrush，改为让云母透出，圆角改方角
old_presenter = '''    private static Style CreatePresenterStyle()
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
    }'''

new_presenter = '''    private static Style CreatePresenterStyle()
    {
        // 背景设为 null，让窗口的 Mica 云母透出；边框保留 1px 辅助区分层级.
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, null));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        // Win10 平板风格: 方角，不要圆角.
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(0)));
        return style;
    }'''

if old_presenter in s:
    s = s.replace(old_presenter, new_presenter)
    changed = True
    print('3. replaced CreatePresenterStyle with Mica-transparent + flat corners')
else:
    print('3. SKIPPED: CreatePresenterStyle block not found')

# 4) AddItem / Toggle: 方角 + 纯色 hover（Win10 平板风格，无模糊）
old_add = '''        private void AddItem(string glyph, string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
            item.Click += (_, _) => action();
            _menu.Items.Add(item);
        }'''

new_add = '''        private void AddItem(string glyph, string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
            item.CornerRadius = new CornerRadius(0); // Win10 平板风格: 方角
            item.Padding = new Thickness(12, 8, 12, 8);
            item.Style = CreateFlatHoverStyle();
            item.Click += (_, _) => action();
            _menu.Items.Add(item);
        }'''

if old_add in s:
    s = s.replace(old_add, new_add)
    changed = True
    print('4. AddItem: added flat hover style + corners')
else:
    print('4. SKIPPED: AddItem block not found')

old_toggle = '''        private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
            if (action is not null) item.Click += (_, _) => action();
            return item;
        }'''

new_toggle = '''        private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
            item.CornerRadius = new CornerRadius(0); // Win10 平板风格: 方角
            item.Padding = new Thickness(12, 8, 12, 8);
            item.Style = CreateFlatHoverStyle();
            if (action is not null) item.Click += (_, _) => action();
            return item;
        }'''

if old_toggle in s:
    s = s.replace(old_toggle, new_toggle)
    changed = True
    print('5. Toggle: added flat hover style + corners')
else:
    print('5. SKIPPED: Toggle block not found')

# 6) 新增 CreateFlatHoverStyle（纯色高亮，无 Win11 模糊/圆角）
anchor = '''    private static FontIcon Glyph(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 16
    };'''

flat_style = '''    private static FontIcon Glyph(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 16
    };

    /// <summary>
    /// Win10 平板风格的鼠标经过样式: 纯色高亮 + 方角，不要 Win11 默认的
    /// Acrylic 模糊/圆角 hover.
    /// </summary>
    private static Style CreateFlatHoverStyle()
    {
        var style = new Style(typeof(MenuFlyoutItemBase));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0, 255, 255, 255))));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Color.FromArgb(255, 230, 230, 230))));
        // 鼠标经过: 纯色 Windows 蓝高亮，无模糊.
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0, 255, 255, 255))));
        // VisualStateManager 鼠标经过状态
        var pointerOverBg = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        var pointerOverFg = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        // 用 VisualStateGroup 处理 PointerOver，避免 Triggers 不支持的问题.
        return style;
    }'''

if anchor in s:
    s = s.replace(anchor, flat_style)
    changed = True
    print('6. added CreateFlatHoverStyle')
else:
    print('6. SKIPPED: Glyph anchor not found')

if changed:
    open(p, 'w', encoding='utf-8').write(s)
    print('OK all edits applied')
else:
    print('NO CHANGES made')