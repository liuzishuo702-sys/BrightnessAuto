p = r'D:\自动调节亮度\Project1\BrightnessAuto\Controls\TrayMenuWindow.cs'
s = open(p, encoding='utf-8').read()

old = '''    private void AddItem(string glyph, string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }'''

new = '''    private void AddItem(string glyph, string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
        item.CornerRadius = new CornerRadius(0);
        item.Padding = new Thickness(12, 8, 12, 8);
        item.Style = CreateFlatHoverStyle();
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }'''
assert old in s
s = s.replace(old, new)

old2 = '''    private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }'''

new2 = '''    private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
        item.CornerRadius = new CornerRadius(0);
        item.Padding = new Thickness(12, 8, 12, 8);
        item.Style = CreateFlatHoverStyle();
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }'''
assert old2 in s
s = s.replace(old2, new2)

open(p,'w',encoding='utf-8').write(s)
print('ok')