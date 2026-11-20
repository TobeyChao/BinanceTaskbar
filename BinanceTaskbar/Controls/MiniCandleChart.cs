using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using BinanceTaskbar.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace BinanceTaskbar.Controls;

public sealed class MiniCandleChart : FrameworkElement
{
    public static readonly DependencyProperty CandlesProperty = DependencyProperty.Register(
        nameof(Candles),
        typeof(IReadOnlyList<KlinePoint>),
        typeof(MiniCandleChart),
        new FrameworkPropertyMetadata(null, OnCandlesChanged));

    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(
        nameof(UpBrush),
        typeof(Brush),
        typeof(MiniCandleChart),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(
        nameof(DownBrush),
        typeof(Brush),
        typeof(MiniCandleChart),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<KlinePoint>? Candles
    {
        get => (IReadOnlyList<KlinePoint>?)GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    public Brush UpBrush
    {
        get => (Brush)GetValue(UpBrushProperty);
        set => SetValue(UpBrushProperty, value);
    }

    public Brush DownBrush
    {
        get => (Brush)GetValue(DownBrushProperty);
        set => SetValue(DownBrushProperty, value);
    }

    public MiniCandleChart()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    private static void OnCandlesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var chart = (MiniCandleChart)dependencyObject;

        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= chart.Candles_OnCollectionChanged;
        }

        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += chart.Candles_OnCollectionChanged;
        }

        chart.InvalidateVisual();
    }

    private void Candles_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var candles = Candles;
        if (candles is null || candles.Count == 0 || ActualWidth <= 2 || ActualHeight <= 2)
        {
            return;
        }

        var minimum = candles.Min(candle => candle.Low);
        var maximum = candles.Max(candle => candle.High);
        var range = maximum - minimum;
        if (range <= 0)
        {
            return;
        }

        const double topPadding = 2;
        const double bottomPadding = 2;
        var plotHeight = Math.Max(1, ActualHeight - topPadding - bottomPadding);
        var slotWidth = ActualWidth / candles.Count;
        var bodyWidth = Math.Clamp(slotWidth * 0.58, 1.1, 3.6);
        var wickPen = new Pen(Brushes.Gray, 0.8);

        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            var x = slotWidth * (index + 0.5);
            var highY = topPadding + (double)((maximum - candle.High) / range) * plotHeight;
            var lowY = topPadding + (double)((maximum - candle.Low) / range) * plotHeight;
            var openY = topPadding + (double)((maximum - candle.Open) / range) * plotHeight;
            var closeY = topPadding + (double)((maximum - candle.Close) / range) * plotHeight;
            var brush = candle.Close >= candle.Open ? UpBrush : DownBrush;

            wickPen.Brush = brush;
            drawingContext.DrawLine(wickPen, new Point(x, highY), new Point(x, lowY));

            var bodyTop = Math.Min(openY, closeY);
            var bodyHeight = Math.Max(1, Math.Abs(openY - closeY));
            drawingContext.DrawRectangle(
                brush,
                null,
                new Rect(x - bodyWidth / 2, bodyTop, bodyWidth, bodyHeight));
        }
    }
}

