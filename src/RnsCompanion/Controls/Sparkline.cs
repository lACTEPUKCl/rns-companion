using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace RnsCompanion.Controls;

/// <summary>
/// Спарклайн онлайна целевого сервера: линия поверх сетки,
/// шкала по числу игроков.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 100 || ActualHeight < 50) return;

        const double left = 34;
        const double right = 6;
        const double top = 4;
        const double bottom = 16;
        var plotWidth = Math.Max(1, ActualWidth - left - right);
        var plotHeight = Math.Max(1, ActualHeight - top - bottom);

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1);
        var axisBrush = new SolidColorBrush(Color.FromArgb(140, 245, 248, 255));

        for (var i = 0; i <= 4; i++)
        {
            var ratio = i / 4d;
            var y = top + plotHeight * ratio;
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
            var value = Maximum * (1 - ratio);
            DrawText(drawingContext, $"{value:0}", axisBrush, 9, new Point(0, y - 6));
        }

        var values = Values?.Cast<object>().Select(Convert.ToDouble).ToArray() ?? Array.Empty<double>();
        if (values.Length < 2) return;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var x = left + i * plotWidth / Math.Max(1, values.Length - 1);
                var normalized = Math.Clamp(values[i] / Math.Max(1, Maximum), 0, 1);
                var point = new Point(x, top + plotHeight - normalized * plotHeight);
                if (i == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();

        // Мягкая заливка под линией.
        var fillGeometry = geometry.Clone();
        using (var context = fillGeometry.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var x = left + i * plotWidth / Math.Max(1, values.Length - 1);
                var normalized = Math.Clamp(values[i] / Math.Max(1, Maximum), 0, 1);
                var point = new Point(x, top + plotHeight - normalized * plotHeight);
                if (i == 0) context.BeginFigure(new Point(left, top + plotHeight), true, true);
                context.LineTo(point, true, false);
                if (i == values.Length - 1)
                    context.LineTo(new Point(x, top + plotHeight), true, false);
            }
        }
        fillGeometry.Freeze();
        var fillBrush = new SolidColorBrush(Color.FromArgb(24, 245, 200, 66));
        fillBrush.Freeze();
        drawingContext.DrawGeometry(fillBrush, null, fillGeometry);
        drawingContext.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private void DrawText(DrawingContext context, string text, Brush brush, double size, Point origin)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, origin);
    }
}
