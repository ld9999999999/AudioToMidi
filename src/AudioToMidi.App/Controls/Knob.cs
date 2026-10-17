using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace AudioToMidi.App.Controls;

/// <summary>
/// A rotary knob: its value above, the dial with an arc showing the setting, its label below. Drag up or
/// down to turn it (hold Shift for fine steps), scroll, or use the arrow keys, and double-click to reset it.
/// </summary>
public sealed class Knob : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Knob, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Maximum), 1);
    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<Knob, double>(nameof(DefaultValue), double.NaN);
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<Knob, double>(nameof(Step), double.NaN);
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<Knob, string?>(nameof(Label));
    public static readonly StyledProperty<string> FormatProperty = AvaloniaProperty.Register<Knob, string>(nameof(Format), "{0:0.00}");
    public static readonly StyledProperty<string?> ValueTextProperty = AvaloniaProperty.Register<Knob, string?>(nameof(ValueText));
    public static readonly StyledProperty<double> DialSizeProperty = AvaloniaProperty.Register<Knob, double>(nameof(DialSize), 52);
    public static readonly StyledProperty<bool> IsBipolarProperty = AvaloniaProperty.Register<Knob, bool>(nameof(IsBipolar));
    public static readonly StyledProperty<bool> ShowValueProperty = AvaloniaProperty.Register<Knob, bool>(nameof(ShowValue), true);

    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(AccentBrush), Brushes.LimeGreen);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(TrackBrush), Brushes.DimGray);
    public static readonly StyledProperty<IBrush?> RimBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(RimBrush), Brushes.Black);
    public static readonly StyledProperty<IBrush?> PointerBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(PointerBrush), Brushes.Black);
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(LabelBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> ValueBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(ValueBrush), Brushes.White);
    public static readonly StyledProperty<Color> FaceHighlightProperty = AvaloniaProperty.Register<Knob, Color>(nameof(FaceHighlight), Color.Parse("#efe9db"));
    public static readonly StyledProperty<Color> FaceColorProperty = AvaloniaProperty.Register<Knob, Color>(nameof(FaceColor), Color.Parse("#bfb7a7"));
    public static readonly StyledProperty<Color> FaceShadowProperty = AvaloniaProperty.Register<Knob, Color>(nameof(FaceShadow), Color.Parse("#7c7566"));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>The value a double-click resets to. NaN turns the reset off.</summary>
    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    /// <summary>The step for one wheel notch or arrow key press. NaN means a hundredth of the range.</summary>
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>Composite format for the value shown above the dial, e.g. "{0:0} ms".</summary>
    public string Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    /// <summary>Text to show in place of the formatted value, e.g. a note name.</summary>
    public string? ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public double DialSize { get => GetValue(DialSizeProperty); set => SetValue(DialSizeProperty, value); }
    /// <summary>Grow the arc from the middle, as for pan or balance.</summary>
    public bool IsBipolar { get => GetValue(IsBipolarProperty); set => SetValue(IsBipolarProperty, value); }
    public bool ShowValue { get => GetValue(ShowValueProperty); set => SetValue(ShowValueProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? RimBrush { get => GetValue(RimBrushProperty); set => SetValue(RimBrushProperty, value); }
    public IBrush? PointerBrush { get => GetValue(PointerBrushProperty); set => SetValue(PointerBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public Color FaceHighlight { get => GetValue(FaceHighlightProperty); set => SetValue(FaceHighlightProperty, value); }
    public Color FaceColor { get => GetValue(FaceColorProperty); set => SetValue(FaceColorProperty, value); }
    public Color FaceShadow { get => GetValue(FaceShadowProperty); set => SetValue(FaceShadowProperty, value); }

    const double TextHeight = 15;
    double? _dragStartY, _dragStartValue;

    static Knob()
    {
        AffectsRender<Knob>(ValueProperty, MinimumProperty, MaximumProperty, LabelProperty, FormatProperty, ValueTextProperty,
            IsBipolarProperty, ShowValueProperty, AccentBrushProperty, TrackBrushProperty, RimBrushProperty, PointerBrushProperty,
            LabelBrushProperty, ValueBrushProperty, FaceHighlightProperty, FaceColorProperty, FaceShadowProperty);
        AffectsMeasure<Knob>(DialSizeProperty, LabelProperty, ShowValueProperty);
        FocusableProperty.OverrideDefaultValue<Knob>(true);
        CursorProperty.OverrideDefaultValue<Knob>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    double Fraction => Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
    double StepSize => double.IsNaN(Step) ? (Maximum - Minimum) / 100 : Step;

    string DisplayText => ValueText ?? string.Format(CultureInfo.CurrentCulture, Format, Value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double labelWidth = Label is null ? 0 : Soundboard.Text(Label, Soundboard.Label, 11, null).Width;
        double top = ShowValue ? TextHeight : 0, bottom = Label is null ? 0 : TextHeight;
        return new Size(Math.Max(DialSize + 8, labelWidth + 6), top + DialSize + bottom);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, top = ShowValue ? TextHeight : 0;
        double scale = DialSize / 64;
        var centre = new Point(w / 2, top + DialSize / 2);

        if (ShowValue)
        {
            var text = Soundboard.Text(DisplayText, Soundboard.Mono, 11.5, IsFocused || _dragStartY is not null ? AccentBrush : ValueBrush);
            ctx.DrawText(text, new Point((w - text.Width) / 2, (TextHeight - text.Height) / 2));
        }

        // Draw the arc for the whole travel, then the setting from the minimum, or from the middle when bipolar.
        double angle = -135 + 270 * Fraction;
        double radius = 28 * scale, stroke = Math.Max(2, 3.5 * scale);
        ctx.DrawGeometry(null, new Pen(TrackBrush, stroke, lineCap: PenLineCap.Round), Soundboard.Arc(centre, radius, -135, 135));
        double from = IsBipolar ? Math.Min(0, angle) : -135, to = IsBipolar ? Math.Max(0, angle) : angle;
        if (to - from > 1) ctx.DrawGeometry(null, new Pen(AccentBrush, stroke, lineCap: PenLineCap.Round), Soundboard.Arc(centre, radius, from, to));

        // Draw the body: a shadow below, the rim, the shaded face and the pointer dot.
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, centre + new Vector(0, 2 * scale), 21 * scale, 21 * scale);
        ctx.DrawEllipse(RimBrush, null, centre, 21 * scale, 21 * scale);
        ctx.DrawEllipse(Soundboard.KnobFace(FaceHighlight, FaceColor, FaceShadow), null, centre, 18.5 * scale, 18.5 * scale);
        ctx.DrawEllipse(PointerBrush, null, Soundboard.OnCircle(centre, 12 * scale, angle), 2.6 * scale, 2.6 * scale);

        if (Label is { } label)
        {
            var text = Soundboard.Text(label, Soundboard.Label, 11, LabelBrush);
            ctx.DrawText(text, new Point((w - text.Width) / 2, top + DialSize + (TextHeight - text.Height) / 2 + 1));
        }
    }

    void SetClamped(double value) => SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2 && !double.IsNaN(DefaultValue))
        {
            SetClamped(DefaultValue);
            e.Handled = true;
            return;
        }
        _dragStartY = e.GetPosition(this).Y;
        _dragStartValue = Value;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStartY is not { } startY || _dragStartValue is not { } startValue) return;
        // 200 px covers the whole range, and Shift slows that to a quarter.
        double pixels = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 800 : 200;
        SetClamped(startValue + (startY - e.GetPosition(this).Y) / pixels * (Maximum - Minimum));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStartY = _dragStartValue = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        SetClamped(Value + Math.Sign(e.Delta.Y) * StepSize * fine);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        switch (e.Key)
        {
            case Key.Up or Key.Right: SetClamped(Value + StepSize * fine); e.Handled = true; break;
            case Key.Down or Key.Left: SetClamped(Value - StepSize * fine); e.Handled = true; break;
            case Key.Home: SetClamped(Minimum); e.Handled = true; break;
            case Key.End: SetClamped(Maximum); e.Handled = true; break;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }
}
