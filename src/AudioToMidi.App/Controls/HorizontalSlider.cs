using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace AudioToMidi.App.Controls;

/// <summary>
/// A horizontal slider in the soundboard style: its value above, a groove with a fader cap, its label
/// below. Drag the cap (hold Shift for fine steps) or click the groove, scroll, or use the arrow keys, and
/// double-click to reset it. On a logarithmic scale, 100% to 200% is as far as 200% to 400%.
/// </summary>
public sealed class HorizontalSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<HorizontalSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<HorizontalSlider, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<HorizontalSlider, double>(nameof(Maximum), 1);
    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<HorizontalSlider, double>(nameof(DefaultValue), double.NaN);
    public static readonly StyledProperty<bool> IsLogarithmicProperty = AvaloniaProperty.Register<HorizontalSlider, bool>(nameof(IsLogarithmic));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<HorizontalSlider, string?>(nameof(Label));
    public static readonly StyledProperty<string> FormatProperty = AvaloniaProperty.Register<HorizontalSlider, string>(nameof(Format), "{0:0.00}");

    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<HorizontalSlider, IBrush?>(nameof(AccentBrush), Brushes.LimeGreen);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<HorizontalSlider, IBrush?>(nameof(TrackBrush), Brushes.Black);
    public static readonly StyledProperty<IBrush?> CapBrushProperty = AvaloniaProperty.Register<HorizontalSlider, IBrush?>(nameof(CapBrush), Brushes.Beige);
    public static readonly StyledProperty<IBrush?> CapLineBrushProperty = AvaloniaProperty.Register<HorizontalSlider, IBrush?>(nameof(CapLineBrush), Brushes.DimGray);
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<HorizontalSlider, IBrush?>(nameof(LabelBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> ValueBrushProperty = AvaloniaProperty.Register<HorizontalSlider, IBrush?>(nameof(ValueBrush), Brushes.White);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>The value a double-click resets to. NaN turns the reset off.</summary>
    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    /// <summary>Equal distances are equal ratios (needs a positive <see cref="Minimum"/>).</summary>
    public bool IsLogarithmic { get => GetValue(IsLogarithmicProperty); set => SetValue(IsLogarithmicProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>Composite format for the value shown above the groove, e.g. "{0:0}%".</summary>
    public string Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? CapBrush { get => GetValue(CapBrushProperty); set => SetValue(CapBrushProperty, value); }
    public IBrush? CapLineBrush { get => GetValue(CapLineBrushProperty); set => SetValue(CapLineBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    const double TextHeight = 15, GrooveHeight = 24, CapWidth = 12, CapHeight = 20, Step = 0.04;
    double? _grab;

    static HorizontalSlider()
    {
        AffectsRender<HorizontalSlider>(ValueProperty, MinimumProperty, MaximumProperty, IsLogarithmicProperty, LabelProperty, FormatProperty,
            AccentBrushProperty, TrackBrushProperty, CapBrushProperty, CapLineBrushProperty, LabelBrushProperty, ValueBrushProperty);
        AffectsMeasure<HorizontalSlider>(LabelProperty);
        FocusableProperty.OverrideDefaultValue<HorizontalSlider>(true);
        CursorProperty.OverrideDefaultValue<HorizontalSlider>(new Cursor(StandardCursorType.SizeWestEast));
    }

    bool Logarithmic => IsLogarithmic && Minimum > 0 && Maximum > Minimum;

    /// <summary>Where a value sits along the groove, 0..1.</summary>
    double FractionOf(double value)
    {
        if (Maximum <= Minimum) return 0;
        double f = Logarithmic ? Math.Log(value / Minimum) / Math.Log(Maximum / Minimum) : (value - Minimum) / (Maximum - Minimum);
        return Math.Clamp(double.IsNaN(f) ? 0 : f, 0, 1);
    }

    double ValueAt(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        return Logarithmic ? Minimum * Math.Pow(Maximum / Minimum, fraction) : Minimum + fraction * (Maximum - Minimum);
    }

    double Travel => Math.Max(1, Bounds.Width - CapWidth);
    double CapLeft(double value) => FractionOf(value) * Travel;

    protected override Size MeasureOverride(Size availableSize)
    {
        double labelWidth = Label is null ? 0 : Soundboard.Text(Label, Soundboard.Label, 11, null).Width;
        double width = double.IsInfinity(availableSize.Width) ? 110 : Math.Min(availableSize.Width, 110);
        return new Size(Math.Max(width, labelWidth + 6), TextHeight + GrooveHeight + (Label is null ? 0 : TextHeight));
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        var value = Soundboard.Text(string.Format(CultureInfo.CurrentCulture, Format, Value), Soundboard.Mono, 11.5,
            IsFocused || _grab is not null ? AccentBrush : ValueBrush);
        ctx.DrawText(value, new Point((w - value.Width) / 2, (TextHeight - value.Height) / 2));

        // The groove, lit from the start up to the cap.
        double y = TextHeight + GrooveHeight / 2, left = CapLeft(Value);
        ctx.DrawRectangle(TrackBrush, null, new Rect(CapWidth / 2, y - 2, Travel, 4), 2, 2);
        if (left > 1) ctx.DrawRectangle(AccentBrush, null, new Rect(CapWidth / 2, y - 1.5, left, 3), 1.5, 1.5);

        var cap = new Rect(left, y - CapHeight / 2, CapWidth, CapHeight);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, cap.Translate(new Vector(0, 2)), 2, 2);
        ctx.DrawRectangle(CapBrush, null, cap, 2, 2);
        ctx.DrawLine(new Pen(CapLineBrush, 2), new Point(cap.Center.X, cap.Top + 3), new Point(cap.Center.X, cap.Bottom - 3));

        if (Label is { } label)
        {
            var text = Soundboard.Text(label, Soundboard.Label, 11, LabelBrush);
            ctx.DrawText(text, new Point((w - text.Width) / 2, TextHeight + GrooveHeight + (TextHeight - text.Height) / 2 + 1));
        }
    }

    /// <summary>
    /// Keep dragging, scrolling and key presses within <see cref="Minimum"/> and <see cref="Maximum"/>. A value
    /// set past them in code, such as a zoom the scroll wheel took further, shows as it is with the cap at the end.
    /// </summary>
    void SetClamped(double value) => SetCurrentValue(ValueProperty, Math.Clamp(value, Math.Min(Minimum, Maximum), Math.Max(Minimum, Maximum)));
    void SetFraction(double fraction) => SetClamped(ValueAt(fraction));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2 && !double.IsNaN(DefaultValue)) { SetClamped(DefaultValue); e.Handled = true; return; }
        double x = e.GetPosition(this).X, left = CapLeft(Value);
        // Grab the cap where it was clicked. A click on the groove moves the cap there first.
        if (x < left || x > left + CapWidth) SetFraction((x - CapWidth / 2) / Travel);
        _grab = x - CapLeft(Value);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_grab is not { } grab) return;
        double current = FractionOf(Value), target = (e.GetPosition(this).X - grab) / Travel;
        SetFraction(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? current + (target - current) * 0.25 : target);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _grab = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (!(delta > 0 && Value >= Maximum)) // already past the end: scrolling up leaves it there
            SetFraction(FractionOf(Value) + Math.Sign(delta) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Step / 4 : Step));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Up or Key.Right) { if (Value < Maximum) SetFraction(FractionOf(Value) + Step); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Left) { SetFraction(FractionOf(Value) - Step); e.Handled = true; }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }
}
