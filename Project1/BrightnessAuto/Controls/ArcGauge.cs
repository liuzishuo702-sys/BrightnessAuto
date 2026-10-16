using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace BrightnessAuto.Controls;

/// <summary>
/// 270° 环形仪表. 对应 C++ 版本的 DrawGauge:
/// 背景轨道 + 前景弧(按 Value/Maximum 填充), 中心大数字 + 下方标签.
/// 内部用 DispatcherTimer 平滑逼近目标值(对应 WM_TIMER 动画).
/// 代码构建视觉树(Canvas + 两条 Path + 两个 TextBlock), 随尺寸自适应重绘.
/// </summary>
public sealed class ArcGauge : ContentControl
{
    private const double StartAngle = 135.0;   // 起始角(度), 与 C++ 一致
    private const double SweepTotal = 270.0;   // 总扫掠角(度)

    private readonly Canvas _canvas = new();
    private readonly Path _trackPath = new();
    private readonly Path _valuePath = new();
    private readonly TextBlock _bigText = new();
    private readonly TextBlock _labelText = new();

    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _displayValue;   // 当前显示值(平滑逼近 Value)

    public ArcGauge()
    {
        // 现代观感: 圆头端点，更柔和。
        _trackPath.StrokeStartLineCap = PenLineCap.Round;
        _trackPath.StrokeEndLineCap = PenLineCap.Round;
        _valuePath.StrokeStartLineCap = PenLineCap.Round;
        _valuePath.StrokeEndLineCap = PenLineCap.Round;

        _bigText.HorizontalAlignment = HorizontalAlignment.Center;
        _bigText.TextAlignment = TextAlignment.Center;
        _bigText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        _labelText.HorizontalAlignment = HorizontalAlignment.Center;
        _labelText.TextAlignment = TextAlignment.Center;

        _canvas.Children.Add(_trackPath);
        _canvas.Children.Add(_valuePath);
        _canvas.Children.Add(_bigText);
        _canvas.Children.Add(_labelText);

        // ContentControl 承载视觉树; 关闭默认内边距, 让 Canvas 铺满.
        Content = _canvas;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        SizeChanged += (_, _) => Redraw();
        _anim.Tick += OnAnimTick;
        Loaded += (_, _) => Redraw();
    }

    #region Dependency Properties

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge),
        new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ArcGauge),
        new PropertyMetadata(100.0, OnVisualChanged));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(ArcGauge),
        new PropertyMetadata(new SolidColorBrush(Color.FromArgb(255, 90, 190, 255)), OnVisualChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ArcGauge),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty BigTextProperty = DependencyProperty.Register(
        nameof(BigText), typeof(string), typeof(ArcGauge),
        new PropertyMetadata("0", OnTextChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ArcGauge),
        new PropertyMetadata("", OnTextChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public string BigText { get => (string)GetValue(BigTextProperty); set => SetValue(BigTextProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (ArcGauge)d;
        // Value 改变时启动平滑动画; 其它可视属性直接重绘.
        if (e.Property == ValueProperty)
        {
            if (!g._anim.IsEnabled) g._anim.Start();
        }
        else g.Redraw();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (ArcGauge)d;
        g._bigText.Text = g.BigText;
        g._labelText.Text = g.Label;
    }

    #endregion

    private void OnAnimTick(object? sender, object e)
    {
        // 平滑逼近: 与 C++ 相同的 0.18 步进 + 0.3 阈值.
        double d = Value - _displayValue;
        if (Math.Abs(d) < 0.3)
        {
            _displayValue = Value;
            _anim.Stop();
        }
        else _displayValue += d * 0.18;
        Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        _canvas.Width = w;
        _canvas.Height = h;

        double cx = w / 2.0, cy = h / 2.0;
        double radius = Math.Min(w, h) / 2.0 - 12;
        if (radius <= 0) return;
        double thick = radius * 0.11;

        _trackPath.StrokeThickness = thick;
        _valuePath.StrokeThickness = thick;
        // 轨道色跟随主题: 由 XAML 用 {ThemeResource AppGaugeTrackBrush} 赋值.
        _trackPath.Stroke = TrackBrush ?? new SolidColorBrush(Color.FromArgb(255, 58, 58, 61));
        _valuePath.Stroke = ArcBrush;

        // 背景整弧.
        _trackPath.Data = BuildArc(cx, cy, radius, StartAngle, SweepTotal);

        // 前景弧: 按显示值比例.
        double frac = Maximum > 0 ? Math.Clamp(_displayValue / Maximum, 0.0, 1.0) : 0.0;
        _valuePath.Data = frac > 0.001
            ? BuildArc(cx, cy, radius, StartAngle, SweepTotal * frac)
            : null;

        // 中心大数字.
        _bigText.FontSize = radius * 0.42;
        _bigText.Measure(new Size(w, h));
        Canvas.SetLeft(_bigText, cx - _bigText.DesiredSize.Width / 2.0);
        Canvas.SetTop(_bigText, cy - _bigText.DesiredSize.Height / 2.0 - radius * 0.08);

        // 下方标签.
        _labelText.FontSize = radius * 0.16;
        _labelText.Measure(new Size(w, h));
        Canvas.SetLeft(_labelText, cx - _labelText.DesiredSize.Width / 2.0);
        Canvas.SetTop(_labelText, cy + radius * 0.36);
    }

    /// <summary>构建一段圆弧几何(角度制, 顺时针, 0°在右, 90°在下).</summary>
    private static Geometry BuildArc(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        var fig = new PathFigure { StartPoint = PointOnCircle(cx, cy, r, startDeg), IsClosed = false };
        var seg = new ArcSegment
        {
            Point = PointOnCircle(cx, cy, r, startDeg + sweepDeg),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepDeg > 180.0
        };
        fig.Segments.Add(seg);
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private static Point PointOnCircle(double cx, double cy, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}
