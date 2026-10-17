using Avalonia;
using Avalonia.Controls;

namespace AudioToMidi.App.Controls;

/// <summary>
/// One row of children spread across the whole width: each widens from <see cref="ItemMinWidth"/> up to
/// <see cref="ItemMaxWidth"/>, then the gaps between them grow, so the first sits at the left edge and the
/// last at the right. The row only asks for <see cref="ItemMinWidth"/> per child, so inside a ScrollViewer
/// it fills the viewport and scrolls only when even that doesn't fit.
/// </summary>
public sealed class SpreadPanel : Panel
{
    public static readonly StyledProperty<double> ItemMinWidthProperty = AvaloniaProperty.Register<SpreadPanel, double>(nameof(ItemMinWidth), 72);
    public static readonly StyledProperty<double> ItemMaxWidthProperty = AvaloniaProperty.Register<SpreadPanel, double>(nameof(ItemMaxWidth), double.PositiveInfinity);
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<SpreadPanel, double>(nameof(Spacing), 5);

    public double ItemMinWidth { get => GetValue(ItemMinWidthProperty); set => SetValue(ItemMinWidthProperty, value); }
    public double ItemMaxWidth { get => GetValue(ItemMaxWidthProperty); set => SetValue(ItemMaxWidthProperty, value); }
    /// <summary>The smallest gap between children.</summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    static SpreadPanel() => AffectsMeasure<SpreadPanel>(ItemMinWidthProperty, ItemMaxWidthProperty, SpacingProperty);

    double _arrangedWidth, _measuredItemWidth;

    double ItemWidth(double rowWidth, int count) =>
        Math.Clamp((rowWidth - (count - 1) * Spacing) / count, ItemMinWidth, Math.Max(ItemMinWidth, ItemMaxWidth));

    protected override Size MeasureOverride(Size availableSize)
    {
        // Measure at the width the children will get, so centred text keeps its full width.
        // In a ScrollViewer the width is unlimited, so use the last arranged width.
        int count = Children.Count(c => c.IsVisible);
        if (count == 0) return default;
        double row = double.IsFinite(availableSize.Width) ? availableSize.Width : _arrangedWidth;
        _measuredItemWidth = ItemWidth(row, count);
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(_measuredItemWidth, availableSize.Height));
            if (child.IsVisible) height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(count * ItemMinWidth + (count - 1) * Spacing, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        int n = visible.Count;
        if (n == 0) return finalSize;
        _arrangedWidth = finalSize.Width;
        double width = ItemWidth(finalSize.Width, n);
        if (Math.Abs(width - _measuredItemWidth) > 0.5) InvalidateMeasure(); // measured for a different width, so measure again
        double gap = n == 1 ? 0 : Math.Max(Spacing, (finalSize.Width - n * width) / (n - 1));
        double x = n == 1 ? (finalSize.Width - width) / 2 : 0;
        foreach (var child in visible)
        {
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + gap;
        }
        return finalSize;
    }
}
