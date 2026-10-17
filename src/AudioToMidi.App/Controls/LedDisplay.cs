using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AudioToMidi.App.Controls;

/// <summary>
/// Text on a 14-segment LED display, as on hardware. Unlit segments glow faintly behind the lit ones and
/// the digits lean slightly. Letters, digits and common signs have segment shapes, and other characters
/// (♯, ♭) are drawn as they are in the LED colour. A '.' lights the decimal point of the character before it.
/// </summary>
public sealed class LedDisplay : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<LedDisplay, string?>(nameof(Text));
    public static readonly StyledProperty<double> DigitHeightProperty = AvaloniaProperty.Register<LedDisplay, double>(nameof(DigitHeight), 20);
    public static readonly StyledProperty<int> MinDigitsProperty = AvaloniaProperty.Register<LedDisplay, int>(nameof(MinDigits));
    public static readonly StyledProperty<bool> AlignRightProperty = AvaloniaProperty.Register<LedDisplay, bool>(nameof(AlignRight));
    public static readonly StyledProperty<Thickness> PaddingProperty = AvaloniaProperty.Register<LedDisplay, Thickness>(nameof(Padding), new Thickness(7, 5));
    public static readonly StyledProperty<IBrush?> OnBrushProperty = AvaloniaProperty.Register<LedDisplay, IBrush?>(nameof(OnBrush), Brushes.LimeGreen);
    public static readonly StyledProperty<IBrush?> GhostBrushProperty = AvaloniaProperty.Register<LedDisplay, IBrush?>(nameof(GhostBrush), new SolidColorBrush(Color.Parse("#11261a")));
    public static readonly StyledProperty<IBrush?> BezelBrushProperty = AvaloniaProperty.Register<LedDisplay, IBrush?>(nameof(BezelBrush), new SolidColorBrush(Color.Parse("#0d0e0f")));
    public static readonly StyledProperty<IBrush?> BezelBorderBrushProperty = AvaloniaProperty.Register<LedDisplay, IBrush?>(nameof(BezelBorderBrush), Brushes.Black);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double DigitHeight { get => GetValue(DigitHeightProperty); set => SetValue(DigitHeightProperty, value); }
    /// <summary>The minimum width in characters. Unlit characters pad shorter text.</summary>
    public int MinDigits { get => GetValue(MinDigitsProperty); set => SetValue(MinDigitsProperty, value); }
    public bool AlignRight { get => GetValue(AlignRightProperty); set => SetValue(AlignRightProperty, value); }
    public Thickness Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public IBrush? OnBrush { get => GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public IBrush? GhostBrush { get => GetValue(GhostBrushProperty); set => SetValue(GhostBrushProperty, value); }
    public IBrush? BezelBrush { get => GetValue(BezelBrushProperty); set => SetValue(BezelBrushProperty, value); }
    public IBrush? BezelBorderBrush { get => GetValue(BezelBorderBrushProperty); set => SetValue(BezelBorderBrushProperty, value); }

    static LedDisplay()
    {
        AffectsRender<LedDisplay>(TextProperty, OnBrushProperty, GhostBrushProperty, BezelBrushProperty, BezelBorderBrushProperty, AlignRightProperty);
        AffectsMeasure<LedDisplay>(TextProperty, DigitHeightProperty, MinDigitsProperty, PaddingProperty);
    }

    // Segments: a top, b upper right, c lower right, d bottom, e lower left, f upper left, g left middle,
    // G right middle, h upper-left diagonal, i upper centre, j upper-right diagonal, k lower-left diagonal,
    // l lower centre, m lower-right diagonal.
    static readonly Dictionary<char, string> Shapes = new()
    {
        ['0'] = "abcdefjk", ['1'] = "bcj", ['2'] = "abGged", ['3'] = "abcdG", ['4'] = "bcfgG", ['5'] = "afgGcd",
        ['6'] = "acdefgG", ['7'] = "abc", ['8'] = "abcdefgG", ['9'] = "abcdfgG",
        ['A'] = "abcefgG", ['B'] = "abcdGil", ['C'] = "adef", ['D'] = "abcdil", ['E'] = "adefg", ['F'] = "aefg",
        ['G'] = "acdefG", ['H'] = "bcefgG", ['I'] = "adil", ['J'] = "bcde", ['K'] = "efgjm", ['L'] = "def",
        ['M'] = "bcefhj", ['N'] = "bcefhm", ['O'] = "abcdef", ['P'] = "abefgG", ['Q'] = "abcdefm", ['R'] = "abefgGm",
        ['S'] = "afgGcd", ['T'] = "ail", ['U'] = "bcdef", ['V'] = "efjk", ['W'] = "bcefkm", ['X'] = "hjkm",
        ['Y'] = "hjl", ['Z'] = "adjk",
        // Use lower case where the shape differs, so a minor chord "Em" does not read as "EM".
        ['b'] = "cdefgG", ['c'] = "degG", ['d'] = "bcdegG", ['h'] = "cefgG", ['m'] = "cegGl", ['n'] = "cegG",
        ['o'] = "cdegG", ['r'] = "eg", ['u'] = "cde", ['i'] = "l", ['j'] = "bcd", ['t'] = "defg",
        ['-'] = "gG", ['+'] = "gGil", ['/'] = "jk", ['\\'] = "hm", ['_'] = "d", ['*'] = "ghijklmG", ['°'] = "abfgG",
        ['='] = "dgG", ['\''] = "i", ['"'] = "bi", ['%'] = "cfjk", [' '] = "",
    };

    const string All = "abcdefgGhijklm";
    double CellWidth => DigitHeight * 0.56;
    double Spacing => DigitHeight * 0.2;
    static readonly double Lean = Math.Tan(7 * Math.PI / 180);

    /// <summary>Characters to draw, each with whether a decimal point follows it.</summary>
    List<(char Char, bool Point)> Cells()
    {
        var cells = new List<(char, bool)>();
        foreach (var ch in Text ?? "")
        {
            if (ch == '.' && cells.Count > 0 && !cells[^1].Item2 && cells[^1].Item1 != ':') cells[^1] = (cells[^1].Item1, true);
            else cells.Add((ch, false));
        }
        int pad = MinDigits - cells.Count;
        if (pad > 0)
        {
            var blanks = Enumerable.Repeat((' ', false), pad);
            cells = AlignRight ? [.. blanks, .. cells] : [.. cells, .. blanks];
        }
        return cells;
    }

    double CellWidthOf(char ch) => ch == ':' ? DigitHeight * 0.25 : CellWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var cells = Cells();
        double width = cells.Sum(c => CellWidthOf(c.Char) + Spacing) - (cells.Count > 0 ? Spacing : 0);
        return new Size(width + Padding.Left + Padding.Right + DigitHeight * Lean, DigitHeight + Padding.Top + Padding.Bottom);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(BezelBrush, new Pen(BezelBorderBrush, 1), bounds.Deflate(0.5), 4, 4);
        // Shade the bezel's top edge for a little depth.
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(1, 1, bounds.Width - 2, 2), 1, 1);

        var cells = Cells();
        double contentWidth = cells.Sum(c => CellWidthOf(c.Char) + Spacing) - (cells.Count > 0 ? Spacing : 0) + DigitHeight * Lean;
        double x = AlignRight ? bounds.Width - Padding.Right - contentWidth : Padding.Left;
        double y = (bounds.Height - DigitHeight) / 2;
        foreach (var (ch, point) in cells)
        {
            var origin = new Point(x, y);
            if (ch == ':')
            {
                Dot(ctx, origin, new Point(CellWidthOf(ch) / 2, DigitHeight * 0.32), OnBrush);
                Dot(ctx, origin, new Point(CellWidthOf(ch) / 2, DigitHeight * 0.72), OnBrush);
            }
            else
            {
                foreach (var s in All) DrawSegment(ctx, origin, s, GhostBrush);
                if (Shapes.TryGetValue(ch, out var lit) || Shapes.TryGetValue(char.ToUpperInvariant(ch), out lit))
                    foreach (var s in lit) DrawSegment(ctx, origin, s, OnBrush);
                else
                {
                    // With no segment shape, draw the character itself in the LED colour.
                    var text = Soundboard.Text(ch.ToString(), Soundboard.Label, DigitHeight * 0.95, OnBrush);
                    ctx.DrawText(text, new Point(x + (CellWidth - text.Width) / 2 + DigitHeight * Lean / 2, y + (DigitHeight - text.Height) / 2));
                }
                Dot(ctx, origin, new Point(CellWidth + Spacing / 2, DigitHeight - DigitHeight * 0.06), point ? OnBrush : GhostBrush);
            }
            x += CellWidthOf(ch) + Spacing;
        }
    }

    Point Skew(Point origin, double px, double py) => new(origin.X + px + (DigitHeight - py) * Lean, origin.Y + py);

    void Dot(DrawingContext ctx, Point origin, Point at, IBrush? brush)
    {
        double r = DigitHeight * 0.055;
        ctx.DrawEllipse(brush, null, Skew(origin, at.X, at.Y), r, r);
    }

    void DrawSegment(DrawingContext ctx, Point origin, char segment, IBrush? brush)
    {
        double h = DigitHeight, w = CellWidth, t = h * 0.11, g = h * 0.022, half = t / 2;
        double midY = h / 2, midX = w / 2;
        IReadOnlyList<Point> Horizontal(double x1, double x2, double yc) =>
            [new(x1, yc), new(x1 + half, yc - half), new(x2 - half, yc - half), new(x2, yc), new(x2 - half, yc + half), new(x1 + half, yc + half)];
        IReadOnlyList<Point> Vertical(double xc, double y1, double y2) =>
            [new(xc, y1), new(xc + half, y1 + half), new(xc + half, y2 - half), new(xc, y2), new(xc - half, y2 - half), new(xc - half, y1 + half)];
        IReadOnlyList<Point> Diagonal(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy), nx = -dy / len * t * 0.38, ny = dx / len * t * 0.38;
            return [new(x1 + nx, y1 + ny), new(x2 + nx, y2 + ny), new(x2 - nx, y2 - ny), new(x1 - nx, y1 - ny)];
        }
        var points = segment switch
        {
            'a' => Horizontal(half + g, w - half - g, half),
            'd' => Horizontal(half + g, w - half - g, h - half),
            'g' => Horizontal(half + g, midX - g, midY),
            'G' => Horizontal(midX + g, w - half - g, midY),
            'f' => Vertical(half, half + g, midY - g),
            'e' => Vertical(half, midY + g, h - half - g),
            'b' => Vertical(w - half, half + g, midY - g),
            'c' => Vertical(w - half, midY + g, h - half - g),
            'i' => Vertical(midX, t + g, midY - half - g),
            'l' => Vertical(midX, midY + half + g, h - t - g),
            'h' => Diagonal(t + g * 2, t + g * 2, midX - half - g, midY - half - g),
            'j' => Diagonal(w - t - g * 2, t + g * 2, midX + half + g, midY - half - g),
            'k' => Diagonal(t + g * 2, h - t - g * 2, midX - half - g, midY + half + g),
            'm' => Diagonal(w - t - g * 2, h - t - g * 2, midX + half + g, midY + half + g),
            _ => null,
        };
        if (points is null) return;
        ctx.DrawGeometry(brush, null, Soundboard.Polygon(points.Select(p => Skew(origin, p.X, p.Y)).ToList()));
    }
}
