using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace TradeDemo.Wpf.Controls;

public sealed class RateGraphControl : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IEnumerable),
        typeof(RateGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentRateTextProperty = DependencyProperty.Register(
        nameof(CurrentRateText),
        typeof(string),
        typeof(RateGraphControl),
        new FrameworkPropertyMetadata("0 msg/s", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Samples
    {
        get => (IEnumerable?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public string CurrentRateText
    {
        get => (string)GetValue(CurrentRateTextProperty);
        set => SetValue(CurrentRateTextProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var background = new SolidColorBrush(Color.FromRgb(17, 24, 39));
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 49, 66)), 1);
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(16, 185, 129)), 2);
        var textBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));

        drawingContext.DrawRectangle(background, null, new Rect(0, 0, width, height));

        for (var i = 0; i <= 4; i++)
        {
            var y = height / 4 * i;
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        var values = Samples?.Cast<object>()
            .Select(Convert.ToDouble)
            .ToArray() ?? [];

        if (values.Length > 1)
        {
            var maxRate = Math.Max(100, values.Max());
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var x = width / Math.Max(1, values.Length - 1) * i;
                    var y = height - values[i] / maxRate * height;
                    if (i == 0)
                    {
                        context.BeginFigure(new Point(x, y), false, false);
                    }
                    else
                    {
                        context.LineTo(new Point(x, y), true, false);
                    }
                }
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, linePen, geometry);

            var fill = new StreamGeometry();
            using (var context = fill.Open())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var x = width / Math.Max(1, values.Length - 1) * i;
                    var y = height - values[i] / maxRate * height;
                    if (i == 0)
                    {
                        context.BeginFigure(new Point(x, height), true, true);
                        context.LineTo(new Point(x, y), true, false);
                    }
                    else
                    {
                        context.LineTo(new Point(x, y), true, false);
                    }
                }

                context.LineTo(new Point(width, height), true, false);
            }
            fill.Freeze();
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(32, 16, 185, 129)), null, fill);
        }

        var formattedText = new FormattedText(
            CurrentRateText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            12,
            textBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(formattedText, new Point(8, 5));
    }
}
