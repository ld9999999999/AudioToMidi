using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace AudioToMidi.App.Controls;

/// <summary>Fonts and drawing helpers shared by the soundboard controls (knobs, LED displays, faders, meters).</summary>
static class Soundboard
{
    /// <summary>A condensed sans-serif font for labels, as on hardware panels. The first one installed is used.</summary>
    public static readonly FontFamily LabelFamily = new("Avenir Next Condensed, Bahnschrift SemiCondensed, DIN Condensed, Arial Narrow, sans-serif");
    public static readonly Typeface Label = new(LabelFamily, FontStyle.Normal, FontWeight.DemiBold);
    public static readonly Typeface Mono = new(new FontFamily("SF Mono, Menlo, Consolas, monospace"));

    public static FormattedText Text(string text, Typeface typeface, double size, IBrush? brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    /// <summary>A point on a circle; angle in degrees clockwise from straight up.</summary>
    public static Point OnCircle(Point centre, double radius, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Sin(a), centre.Y - radius * Math.Cos(a));
    }

    /// <summary>A clockwise arc between two angles (degrees clockwise from up).</summary>
    public static StreamGeometry Arc(Point centre, double radius, double from, double to)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(OnCircle(centre, radius, from), false);
        ctx.ArcTo(OnCircle(centre, radius, to), new Size(radius, radius), 0, to - from > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return geometry;
    }

    public static StreamGeometry Polygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true);
        for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i]);
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>Shaded knob face: lit from the upper left.</summary>
    public static IBrush KnobFace(Color highlight, Color body, Color shadow) => new RadialGradientBrush
    {
        Center = new RelativePoint(0.38, 0.30, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.38, 0.30, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.78, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.78, RelativeUnit.Relative),
        GradientStops = [new GradientStop(highlight, 0), new GradientStop(body, 0.55), new GradientStop(shadow, 1)],
    };
}
