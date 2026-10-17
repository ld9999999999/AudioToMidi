using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.App.Controls;

/// <summary>
/// A vertical slider with two handles for a range of MIDI pitches (A0 to C8), marked at every C. Drag to
/// move the handle nearest the click, scroll to move it by a semitone, or double-click to reset.
/// </summary>
public sealed class PitchRangeSlider : Control
{
    public static readonly StyledProperty<decimal> LowProperty =
        AvaloniaProperty.Register<PitchRangeSlider, decimal>(nameof(Low), 21, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<decimal> HighProperty =
        AvaloniaProperty.Register<PitchRangeSlider, decimal>(nameof(High), 108, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<PitchRangeSlider, IBrush?>(nameof(TrackBrush), Brushes.Black);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<PitchRangeSlider, IBrush?>(nameof(AccentBrush), Brushes.LimeGreen);
    public static readonly StyledProperty<IBrush?> CapBrushProperty = AvaloniaProperty.Register<PitchRangeSlider, IBrush?>(nameof(CapBrush), Brushes.Beige);
    public static readonly StyledProperty<IBrush?> TickBrushProperty = AvaloniaProperty.Register<PitchRangeSlider, IBrush?>(nameof(TickBrush), Brushes.Gray);

    public decimal Low { get => GetValue(LowProperty); set => SetValue(LowProperty, value); }
    public decimal High { get => GetValue(HighProperty); set => SetValue(HighProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? CapBrush { get => GetValue(CapBrushProperty); set => SetValue(CapBrushProperty, value); }
    public IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }

    const int Lowest = 21, Highest = 108;
    const double CapHeight = 10, TrackX = 16;
    bool? _draggingHigh;

    static PitchRangeSlider()
    {
        AffectsRender<PitchRangeSlider>(LowProperty, HighProperty, TrackBrushProperty, AccentBrushProperty, CapBrushProperty, TickBrushProperty);
        CursorProperty.OverrideDefaultValue<PitchRangeSlider>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    double Travel => Math.Max(1, Bounds.Height - CapHeight);
    double YOf(double pitch) => (1 - (pitch - Lowest) / (Highest - Lowest)) * Travel;
    decimal PitchAt(double y) => Math.Clamp((decimal)Math.Round(Lowest + (1 - (y - CapHeight / 2) / Travel) * (Highest - Lowest)), Lowest, Highest);

    protected override Size MeasureOverride(Size availableSize) => new(64, 140);

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(TrackBrush, null, new Rect(TrackX - 3, CapHeight / 2, 6, Travel), 3, 3);
        double hiY = YOf((double)High), loY = YOf((double)Low);
        ctx.DrawRectangle(AccentBrush, null, new Rect(TrackX - 3, hiY + CapHeight / 2, 6, Math.Max(0, loY - hiY)), 3, 3);

        for (int p = 24; p <= Highest; p += 12)
        {
            double y = YOf(p) + CapHeight / 2;
            ctx.DrawLine(new Pen(TickBrush, 1), new Point(TrackX + 10, y), new Point(TrackX + (p % 24 == 0 ? 17 : 14), y));
            if (p % 24 == 0)
            {
                var text = Soundboard.Text(DetectedNote.PitchName(p), Soundboard.Mono, 9.5, TickBrush);
                ctx.DrawText(text, new Point(TrackX + 20, y - text.Height / 2));
            }
        }
        foreach (double y in new[] { hiY, loY })
        {
            var cap = new Rect(TrackX - 11, y, 22, CapHeight);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, cap.Translate(new Vector(0, 2)), 2, 2);
            ctx.DrawRectangle(CapBrush, null, cap, 2, 2);
            ctx.DrawRectangle(AccentBrush, null, new Rect(cap.Right - 3, cap.Top, 3, cap.Height), 1, 1);
        }
    }

    bool NearerIsHigh(double y) => Math.Abs(y - YOf((double)High) - CapHeight / 2) <= Math.Abs(y - YOf((double)Low) - CapHeight / 2);

    void Move(bool high, decimal pitch)
    {
        if (high) SetCurrentValue(HighProperty, Math.Max(pitch, Low + 1));
        else SetCurrentValue(LowProperty, Math.Min(pitch, High - 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            SetCurrentValue(LowProperty, (decimal)Lowest);
            SetCurrentValue(HighProperty, (decimal)Highest);
            e.Handled = true;
            return;
        }
        double y = e.GetPosition(this).Y;
        _draggingHigh = NearerIsHigh(y);
        Move(_draggingHigh.Value, PitchAt(y));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_draggingHigh is { } high) Move(high, PitchAt(e.GetPosition(this).Y));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _draggingHigh = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        bool high = NearerIsHigh(e.GetPosition(this).Y);
        Move(high, (high ? High : Low) + Math.Sign(e.Delta.Y));
        e.Handled = true;
    }
}
