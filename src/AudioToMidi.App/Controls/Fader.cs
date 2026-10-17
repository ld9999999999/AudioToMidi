using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace AudioToMidi.App.Controls;

/// <summary>
/// A vertical channel fader with a dB scale. <see cref="Value"/> is the cap's position, 0 (bottom) to 1
/// (top); <see cref="Gain"/> turns it into a level: 0 dB at about 70% of the travel, +6 dB at the top.
/// Drag the cap (Shift for fine), scroll, or double-click to go back to 0 dB.
/// </summary>
public sealed class Fader : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Fader, double>(nameof(Value), UnityPosition, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(TrackBrush), Brushes.Black);
    public static readonly StyledProperty<IBrush?> CapBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(CapBrush), Brushes.Beige);
    public static readonly StyledProperty<IBrush?> CapLineBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(CapLineBrush), Brushes.DimGray);
    public static readonly StyledProperty<IBrush?> TickBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(TickBrush), Brushes.Gray);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? CapBrush { get => GetValue(CapBrushProperty); set => SetValue(CapBrushProperty, value); }
    public IBrush? CapLineBrush { get => GetValue(CapLineBrushProperty); set => SetValue(CapLineBrushProperty, value); }
    public IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }

    /// <summary>The position of 0 dB.</summary>
    public const double UnityPosition = 0.70710678;

    /// <summary>Linear gain for a cap position: 2p², so 0 dB at √½ and +6 dB at the top.</summary>
    public static double Gain(double position) => 2 * position * position;

    /// <summary>The level in dB as text, e.g. "-6.0", "+2.5" or "-∞".</summary>
    public static string DbText(double position)
    {
        double gain = Gain(position);
        if (gain < 1e-4) return "-∞";
        double db = Math.Round(20 * Math.Log10(gain), 1);
        if (db == 0) return "0.0";
        return (db > 0 ? "+" : "") + db.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    const double CapHeight = 14, ScaleWidth = 18;
    static readonly (string Label, double Db)[] Marks = [("+6", 6), ("0", 0), ("-6", -6), ("-12", -12), ("-24", -24), ("-∞", double.NegativeInfinity)];
    double? _grab;

    static Fader()
    {
        AffectsRender<Fader>(ValueProperty, TrackBrushProperty, CapBrushProperty, CapLineBrushProperty, TickBrushProperty);
        FocusableProperty.OverrideDefaultValue<Fader>(true);
        CursorProperty.OverrideDefaultValue<Fader>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    static double PositionOf(double db) => double.IsNegativeInfinity(db) ? 0 : Math.Sqrt(Math.Pow(10, db / 20) / 2);

    double Travel => Math.Max(1, Bounds.Height - CapHeight);
    double CapTop(double value) => (1 - Math.Clamp(value, 0, 1)) * Travel;

    protected override Size MeasureOverride(Size availableSize) => new(ScaleWidth + 26, 120);

    public override void Render(DrawingContext ctx)
    {
        double trackX = ScaleWidth + 13;
        foreach (var (label, db) in Marks)
        {
            double y = CapTop(PositionOf(db)) + CapHeight / 2;
            var text = Soundboard.Text(label, Soundboard.Label, 9, TickBrush);
            ctx.DrawText(text, new Point(ScaleWidth - text.Width, y - text.Height / 2));
            ctx.DrawLine(new Pen(TickBrush, 1), new Point(ScaleWidth + 3, y), new Point(ScaleWidth + 7, y));
        }
        ctx.DrawRectangle(TrackBrush, null, new Rect(trackX - 2, CapHeight / 2, 4, Travel), 2, 2);

        double top = CapTop(Value);
        var cap = new Rect(trackX - 11, top, 22, CapHeight);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, cap.Translate(new Vector(0, 2)), 2, 2);
        ctx.DrawRectangle(CapBrush, null, cap, 2, 2);
        ctx.DrawLine(new Pen(CapLineBrush, 2), new Point(cap.Left + 2, cap.Center.Y), new Point(cap.Right - 2, cap.Center.Y));
    }

    void SetClamped(double value) => SetCurrentValue(ValueProperty, Math.Clamp(value, 0, 1));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2) { SetClamped(UnityPosition); e.Handled = true; return; }
        double y = e.GetPosition(this).Y, top = CapTop(Value);
        // Grab the cap where it was clicked. A click on the track moves the cap there first.
        if (y < top || y > top + CapHeight) SetClamped(1 - (y - CapHeight / 2) / Travel);
        _grab = e.GetPosition(this).Y - CapTop(Value);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_grab is not { } grab) return;
        double target = 1 - (e.GetPosition(this).Y - grab) / Travel;
        SetClamped(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Value + (target - Value) * 0.25 : target);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _grab = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetClamped(Value + Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.005 : 0.02));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Up or Key.Right) { SetClamped(Value + 0.02); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Left) { SetClamped(Value - 0.02); e.Handled = true; }
    }
}
