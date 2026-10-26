using System.Windows;
using System.Windows.Controls;

namespace SubReel.Launcher.App.DesignSystem;

/// <summary>Arranges tiles in equal-width columns and shrinks them to the available viewport.</summary>
public sealed class ResponsiveTilePanel : Panel
{
    public static readonly DependencyProperty PreferredTileWidthProperty = DependencyProperty.Register(
        nameof(PreferredTileWidth), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(340d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalGapProperty = DependencyProperty.Register(
        nameof(HorizontalGap), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty VerticalGapProperty = DependencyProperty.Register(
        nameof(VerticalGap), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double PreferredTileWidth
    {
        get => (double)GetValue(PreferredTileWidthProperty);
        set => SetValue(PreferredTileWidthProperty, value);
    }

    public double HorizontalGap
    {
        get => (double)GetValue(HorizontalGapProperty);
        set => SetValue(HorizontalGapProperty, value);
    }

    public double VerticalGap
    {
        get => (double)GetValue(VerticalGapProperty);
        set => SetValue(VerticalGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0) return new Size();

        var columns = GetColumnCount(availableSize.Width);
        var tileWidth = GetTileWidth(availableSize.Width, columns);
        var rowCount = (InternalChildren.Count + columns - 1) / columns;
        var rowHeights = new double[rowCount];

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(tileWidth, availableSize.Height));
            var row = i / columns;
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
        }

        var width = double.IsInfinity(availableSize.Width)
            ? columns * tileWidth + (columns - 1) * HorizontalGap
            : availableSize.Width;
        return new Size(width, rowHeights.Sum() + Math.Max(0, rowCount - 1) * VerticalGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;

        var columns = GetColumnCount(finalSize.Width);
        var tileWidth = GetTileWidth(finalSize.Width, columns);
        var rowHeights = new double[(InternalChildren.Count + columns - 1) / columns];
        for (var i = 0; i < InternalChildren.Count; i++)
            rowHeights[i / columns] = Math.Max(rowHeights[i / columns], InternalChildren[i].DesiredSize.Height);

        var rowTops = new double[rowHeights.Length];
        for (var row = 1; row < rowHeights.Length; row++)
            rowTops[row] = rowTops[row - 1] + rowHeights[row - 1] + VerticalGap;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var left = column * (tileWidth + HorizontalGap);
            InternalChildren[i].Arrange(new Rect(left, rowTops[row], tileWidth, rowHeights[row]));
        }

        return finalSize;
    }

    private int GetColumnCount(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        return Math.Max(1, (int)Math.Floor((width + HorizontalGap) / (Math.Max(1, PreferredTileWidth) + HorizontalGap)));
    }

    private double GetTileWidth(double width, int columns) => double.IsInfinity(width)
        ? PreferredTileWidth
        : Math.Max(1, (width - (columns - 1) * HorizontalGap) / columns);
}
