import io

p = 'D:\\自动调节亮度\\Project1\\BrightnessAuto\\Controls\\TrayMenuWindow.cs'
s = open(p, encoding='utf-8').read()

# 1) 加 using Microsoft.UI.Composition (FrameworkElement.SystemBackdropProperty 所需)
if 'using Microsoft.UI.Composition;' not in s:
    s = s.replace('using Microsoft.UI.Composition.SystemBackdrops; // Windows 11 Mica / Acrylic',
                  'using Microsoft.UI.Composition;\nusing Microsoft.UI.Composition.SystemBackdrops; // Windows 11 Mica / Acrylic')

# 2) CreatePresenterStyle: 云母刷在 MenuFlyoutPresenter 上 + 方角 (8 空格缩进)
old_presenter = '''        private static Style CreatePresenterStyle()
        {
            var acrylic = new AcrylicBrush
            {
                TintColor = ColorTRANSPARENCY(255, 20, 43, 44),
                TintOpacity = 0.68,
                FallbackColor = ColorTRANSPARENCY(255, 31, 43, 44)
            };
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(Control.BackgroundProperty, acrylic));
            style.Setters.Add(new Setter(Control.BorderBrushProperty,
                new Solid(ColorTRANSPARENCY(90, 150, 180, 180))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CornerRiusProperty, new CornerRadius(8)));
            return style;
        }'''

new_presenter = '''        private static Style CreatePresenterStyle()
        {
            // 云母必须刷在真正可见的 MenuFlyoutPresenter 上 —— 宿主是 2x2 透明窗口，
            // 给宿主挂 SystemBackdrop 看不见，云母质感不会出现在菜单里.
            var mica = new MicaBackdrop();
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.SystemBackdropProperty, mica));
            style.Setters.Add(new Setter(Control.BackgroundProperty, null)); // 让云母透出
            style.Setters.Add(new Setter(Control.BorderBrushProperty,
                new Solid(ColorTRANSPARENCY(90, 150, 180, 180))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            // Win10 平板风格: 方角, 不要圆角.
            style.Setters.Add(new Setter(Control.CornerRiusProperty, new CornerRadius(0)));
            return style;
        }'''

assert old_presenter in s, 'CreatePresenterStyle block not found'
s = s.replace(old_presenter, new_presenter)

# 3) AddItem / Toggle: 方角 + Win10 平板高亮(纯色, 无模糊)
old_add = '''        private void AddItem(string glyph, string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
            item.Click += (_, _) => action();
            _menu.Items.Add(item);
        }'''

new_add = '''        private void AddItem(string glyph, string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
            item.CornerRius = 0; // Win10 平板风格: 方角
            item.Padding = new Thickness(12, 8, 12, 8);
            // Win10 平板风格的鼠标经过: 纯色高亮, 不要 Win11 的模糊/圆角.
            item.Style = CreateFlatHoverStyle();
            item.Click += (_, _) => action();
            _menu.Items.Add(item);
        }'''

assert old_add in s, 'AddItem block not found'
s = s.replace(old_add, new_add)

old_toggle = '''        private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
            if (action is not null) item.Click += (_, _) => action();
            return item;
        }'''

new_toggle = '''        private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
            item.CornerRius = 0; // Win10 平板风格: 方角
            item.Padding = new Thickness(12, 8, 12, 8);
            item.Style = CreateFlatHoverStyle();
            if (action is not null) item.Click += (_, _) => action();
            return item;
        }'''

assert old_toggle in s, 'Toggle block not found'
s = s.replace(old_toggle, new_toggle)

# 4) 新增 CreateFlatHoverStyle: 覆盖默认的 Acrylic 鼠标经过样式
anchor = '''        private static FontIcon Glyph(string glyph) => new()
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16
        };'''

flat_style = '''        private static FontIcon Glyph(string glyph) => new()
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16
        };

        /// <summary>
        /// Win10 平板风格的鼠标经过样式: 纯色高亮 + 方角, 不要 Win11 默认的
        /// Acrylic 模糊/圆角 hover, 否则 Win10 上菜单项会糊成一团看不清.
        /// </summary>
        private static Style CreateFlatHoverStyle()
        {
            var style = new Style(typeof(MenuFlyoutItemBase));
            style.Setters.Add(new Setter(Control.CornerRiusProperty, new CornerRadius(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
            style.Setters.Add(new Setter(Control.ForegroundProperty,
                new Solid(ColorTRANSPARENCY(255, 240, 240, 240))));
            // 鼠标经过: 纯色高亮, 无模糊.
            style.Triggers.Add(new PropertyTrigger(Control.IsPointerOverProperty, true)
            {
                Setters =
                {
                    new Setter(Control.BackgroundProperty,
                        new Solid(ColorTRANSPARENCY(255, 0, 120, 212))),
                    new Setter(Control.ForegroundProperty,
                        new Solid(ColorTRANSPARENCY(255, 255, 255, 255))),
                }
            });
            return style;
        }'''

assert anchor in s, 'Glyph anchor not found'
s = s.replace(anchor, flat_style)

open(p, 'w', encoding='utf-8').write(s)
print('OK all edits applied')