p = r'D:\自动调节亮度\Project1\BrightnessAuto\Controls\TrayMenuWindow.cs'
s = open(p, encoding='utf-8').read()

# AddItem / Toggle: 方角 + 纯色 hover（Win10 平板风格，无模糊）
old_add = '''private void AddItem(string glyph, string label, Action action)
{
    var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
    item.Click += (_, _) => action();
    _menu.Items.Add(item);
}'''

new_add = '''private void AddItem(string glyph, string label, Action action)
{
    var item = new MenuFlyoutItem { Text = label, Icon = Glyph(glyph) };
    item.CornerRadius = new CornerRadius(0); // Win10 平板风格: 方角
    item.Padding = new Thickness(12, 8, 12, 8);
    item.Style = CreateFlatHoverStyle();
    item.Click += (_, _) => action();
    _menu.Items.Add(item);
}'''

assert old_add in s, 'AddItem block not found'
s = s.replace(old_add, new_add)
print('AddItem done')

old_toggle = '''private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
{
    var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
    if (action is not null) item.Click += (_, _) => action();
    return item;
}'''

new_toggle = '''private static ToggleMenuFlyoutItem Toggle(string label, bool enabled, Action? action)
{
    var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = enabled };
    item.CornerRadius = new CornerRadius(0); // Win10 平板风格: 方角
    item.Padding = new Thickness(12, 8, 12, 8);
    item.Style = CreateFlatHoverStyle();
    if (action is not null) item.Click += (_, _) => action();
    return item;
}'''

assert old_toggle in s, 'Toggle block not found'
s = s.replace(old_toggle, new_toggle)
print('Toggle done')

open(p, 'w', encoding='utf-8').write(s)
print('OK')