using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AudioToMidi.App.Controls;

/// <summary>
/// A vertical LED level meter: green, then amber, then red at the top. <see cref="Level"/> is a linear
/// peak (1 = full scale); the segments span -48 to +3 dB.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));
    public static readonly StyledProperty<int> SegmentsProperty = AvaloniaProperty.Register<LevelMeter, int>(nameof(Segments), 15);
    public static readonly StyledProperty<IBrush?> GreenBrushProperty = AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(GreenBrush), Brushes.LimeGreen);
    public static readonly StyledProperty<IBrush?> AmberBrushProperty = AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(AmberBrush), Brushes.Orange);
    public static readonly StyledProperty<IBrush?> RedBrushProperty = AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(RedBrush), Brushes.Red);
    public static readonly StyledProperty<IBrush?> OffBrushProperty = AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(OffBrush), Brushes.DimGray);

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public int Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IBrush? GreenBrush { get => GetValue(GreenBrushProperty); set => SetValue(GreenBrushProperty, value); }
    public IBrush? AmberBrush { get => GetValue(AmberBrushProperty); set => SetValue(AmberBrushProperty, value); }
    public IBrush? RedBrush { get => GetValue(RedBrushProperty); set => SetValue(RedBrushProperty, value); }
    public IBrush? OffBrush { get => GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    static LevelMeter() =>
        AffectsRender<LevelMeter>(LevelProperty, SegmentsProperty, GreenBrushProperty, AmberBrushProperty, RedBrushProperty, OffBrushProperty);

    /// <summary>The share of the meter to light for a linear peak level.</summary>
    public static double Fill(double level) => level <= 1e-5 ? 0 : Math.Clamp((20 * Math.Log10(level) + 48) / 51, 0, 1);

    protected override Size MeasureOverride(Size availableSize) => new(6, 100);

    public override void Render(DrawingContext ctx)
    {
        int n = Math.Max(1, Segments);
        double gap = 2, height = (Bounds.Height - gap * (n - 1)) / n, lit = Fill(Level) * n;
        for (int k = 0; k < n; k++)
        {
            var brush = k >= lit ? OffBrush : k >= n - 2 ? RedBrush : k >= n - 5 ? AmberBrush : GreenBrush;
            double y = Bounds.Height - (k + 1) * height - k * gap;
            ctx.DrawRectangle(brush, null, new Rect(0, y, Bounds.Width, height), 1, 1);
        }
    }
}
