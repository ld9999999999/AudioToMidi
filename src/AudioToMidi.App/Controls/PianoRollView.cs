using System.Globalization;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AudioToMidi.App.Controls;

/// <summary>A chord symbol over a stretch of time, for the piano roll's chord row.</summary>
public sealed record ChordLabel(double Start, double End, string Text);

/// <summary>One part's row in the pattern lane: a cell for each repeat of each of its bar patterns.</summary>
public sealed record PatternLane(string Label, IReadOnlyList<PatternCell> Cells);

/// <summary>A repeat of a bar pattern, from <see cref="Start"/> to <see cref="End"/> (seconds).</summary>
/// <param name="Pattern">Which of the part's patterns: 0 is A, 1 is B...</param>
public readonly record struct PatternCell(double Start, double End, int Pattern)
{
    public string Letter => Pattern < 26 ? ((char)('A' + Pattern)).ToString() : (Pattern + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A piano roll with a waveform lane, a bar and chord ruler, and a drum lane when there are drum hits.
/// Scroll with the wheel, zoom around the cursor with Ctrl/Cmd and the wheel (or set <see cref="Zoom"/>),
/// drag to pan, and double-click to fit the whole file.
/// </summary>
public sealed class PianoRollView : Control
{
    public static readonly StyledProperty<IReadOnlyList<DetectedNote>?> NotesProperty =
        AvaloniaProperty.Register<PianoRollView, IReadOnlyList<DetectedNote>?>(nameof(Notes));
    public static readonly StyledProperty<float[]?> WaveformProperty =
        AvaloniaProperty.Register<PianoRollView, float[]?>(nameof(Waveform));
    public static readonly StyledProperty<float[]?> RemovedProperty =
        AvaloniaProperty.Register<PianoRollView, float[]?>(nameof(Removed));
    public static readonly StyledProperty<int> SampleRateProperty =
        AvaloniaProperty.Register<PianoRollView, int>(nameof(SampleRate), 22050);
    public static readonly StyledProperty<BarGrid?> GridProperty =
        AvaloniaProperty.Register<PianoRollView, BarGrid?>(nameof(Grid));
    public static readonly StyledProperty<bool> ColorByTrackProperty =
        AvaloniaProperty.Register<PianoRollView, bool>(nameof(ColorByTrack));
    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<PianoRollView, double>(nameof(Playhead), -1);
    public static readonly StyledProperty<double> RangeStartProperty =
        AvaloniaProperty.Register<PianoRollView, double>(nameof(RangeStart), double.NaN, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> RangeEndProperty =
        AvaloniaProperty.Register<PianoRollView, double>(nameof(RangeEnd), double.NaN, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<IReadOnlyCollection<int>?> SoundingKeysProperty =
        AvaloniaProperty.Register<PianoRollView, IReadOnlyCollection<int>?>(nameof(SoundingKeys));
    public static readonly StyledProperty<IReadOnlyList<ChordLabel>?> ChordsProperty =
        AvaloniaProperty.Register<PianoRollView, IReadOnlyList<ChordLabel>?>(nameof(Chords));
    public static readonly StyledProperty<IReadOnlyList<DetectedNote>?> DrumsProperty =
        AvaloniaProperty.Register<PianoRollView, IReadOnlyList<DetectedNote>?>(nameof(Drums));
    public static readonly StyledProperty<IReadOnlyList<PatternLane>?> PatternsProperty =
        AvaloniaProperty.Register<PianoRollView, IReadOnlyList<PatternLane>?>(nameof(Patterns));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PianoRollView, double>(nameof(Zoom), MinZoom, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (o, z) => double.IsNaN(z) ? MinZoom : Math.Clamp(z, MinZoom, ((PianoRollView)o).MaxZoom));

    /// <summary>Timeline length in percent of the view: 100 fits the whole file.</summary>
    public const double MinZoom = 100;

    /// <summary>The closest zoom: two seconds across the view, and at least 400% (four views long).</summary>
    double MaxZoom => Math.Max(400, _duration / 2 * MinZoom);

    public IReadOnlyList<DetectedNote>? Notes { get => GetValue(NotesProperty); set => SetValue(NotesProperty, value); }
    /// <summary>Original audio, drawn grey.</summary>
    public float[]? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    /// <summary>The percussive part that was filtered out, drawn on top in orange.</summary>
    public float[]? Removed { get => GetValue(RemovedProperty); set => SetValue(RemovedProperty, value); }
    public int SampleRate { get => GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }
    /// <summary>Beat and bar lines. Without it the roll shows a seconds grid.</summary>
    public BarGrid? Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }
    /// <summary>Colour notes by <see cref="DetectedNote.Track"/> (brightness still shows velocity).</summary>
    public bool ColorByTrack { get => GetValue(ColorByTrackProperty); set => SetValue(ColorByTrackProperty, value); }
    /// <summary>Selected start/stop points (seconds); NaN when nothing is selected. Drag on the waveform to set.</summary>
    public double RangeStart { get => GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }
    public double RangeEnd { get => GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }
    bool HasRange => !double.IsNaN(RangeStart) && !double.IsNaN(RangeEnd) && RangeEnd > RangeStart;

    /// <summary>Chord symbols, shown in a row under the bar numbers.</summary>
    public IReadOnlyList<ChordLabel>? Chords { get => GetValue(ChordsProperty); set => SetValue(ChordsProperty, value); }

    /// <summary>Drum hits as General MIDI drum notes, shown in a lane of their own above the notes.</summary>
    public IReadOnlyList<DetectedNote>? Drums { get => GetValue(DrumsProperty); set => SetValue(DrumsProperty, value); }

    /// <summary>How long the timeline is against the view, in percent: <see cref="MinZoom"/> up to two seconds across the view.</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>
    /// Bar patterns, a row per part under the drum lane, each bar lettered by the pattern it repeats. Clicking
    /// a repeat selects its bars as the start/stop range (to loop it) and raises <see cref="ClipClicked"/>.
    /// </summary>
    public IReadOnlyList<PatternLane>? Patterns { get => GetValue(PatternsProperty); set => SetValue(PatternsProperty, value); }

    /// <summary>A pattern's repeat was clicked: its lane, its pattern, and whether with the right button.</summary>
    public event Action<int, int, bool>? ClipClicked;

    /// <summary>Keys lit on the keyboard strip (notes the player is sounding).</summary>
    public IReadOnlyCollection<int>? SoundingKeys { get => GetValue(SoundingKeysProperty); set => SetValue(SoundingKeysProperty, value); }

    /// <summary>Playback position in seconds; negative hides it.</summary>
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }

    /// <summary>A piano key or a note was pressed: pitch and velocity (0..1).</summary>
    public event Action<int, float>? KeyPressed;
    public event Action<int>? KeyReleased;

    const double KeyboardWidth = 44;
    const double WaveHeight = 64;
    /// <summary>Bar numbers, and a second row for chord symbols when there are any.</summary>
    double RulerHeight => Chords is { Count: > 0 } ? 34 : 18;
    const double DrumRowHeight = 9;
    double DrumLaneTop => WaveHeight + RulerHeight;
    double DrumLaneHeight => Drums is { Count: > 0 } ? DrumRows.Length * DrumRowHeight + 2 : 0;
    const double PatternRowHeight = 13;
    double PatternLaneTop => DrumLaneTop + DrumLaneHeight;
    double PatternLaneHeight => Patterns is { Count: > 0 } lanes ? lanes.Count * PatternRowHeight + 2 : 0;

    /// <summary>Pattern colours by letter, cycling.</summary>
    static readonly IBrush[] PatternBrushes = new[] { 0x5cd68a, 0xf2a23c, 0x4a9df2, 0xe86fb4, 0xb48cf2, 0xf2d45c, 0x7fd6c8, 0xff6b6b }
        .Select(c => (IBrush)new ImmutableSolidColorBrush(Color.FromArgb(0x70, (byte)(c >> 16), (byte)(c >> 8), (byte)c))).ToArray();
    static readonly IPen SelectedClipPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff)), 1.5);
    (int Lane, int Pattern)? _selectedClip;

    /// <summary>The drum lane's rows, top to bottom, with their colours (quiet to loud).</summary>
    static readonly (string Label, IBrush[] Brushes)[] DrumRows =
    [
        ("KICK", Shades(Color.FromRgb(0xff, 0x6b, 0x6b))),
        ("SNARE", Shades(Color.FromRgb(0xf2, 0xa2, 0x3c))),
        ("HAT", Shades(Color.FromRgb(0xf2, 0xd4, 0x5c))),
        ("OPEN", Shades(Color.FromRgb(0x7f, 0xd6, 0xc8))),
    ];

    static IBrush[] Shades(Color c) => Enumerable.Range(0, 4).Select(level =>
    {
        double f = 0.5 + 0.5 * level / 3.0;
        return (IBrush)new ImmutableSolidColorBrush(Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f)));
    }).ToArray();

    /// <summary>A General MIDI drum note's row: kick, snare, closed hi-hat (and anything else), open hi-hat.</summary>
    static int DrumRow(int pitch) => pitch switch { 35 or 36 => 0, >= 37 and <= 40 => 1, 46 => 3, _ => 2 };
    const int MinVisibleSemitones = 24;

    static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(0x1b, 0x1d, 0x22));
    static readonly IBrush BlackKeyRow = new ImmutableSolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1c));
    static readonly IBrush LaneBackground = new ImmutableSolidColorBrush(Color.FromRgb(0x14, 0x15, 0x19));
    static readonly IBrush WhiteKey = new ImmutableSolidColorBrush(Color.FromRgb(0xe6, 0xe6, 0xe6));
    static readonly IBrush BlackKey = new ImmutableSolidColorBrush(Color.FromRgb(0x30, 0x32, 0x38));
    static readonly IBrush WaveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x6b, 0x72, 0x80));
    static readonly IBrush RemovedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xc0, 0xf0, 0x8a, 0x3c));
    static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x9a, 0xa0, 0xaa));
    static readonly IBrush ChordBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xe8, 0xc8, 0x6a));
    static readonly IBrush KeyLabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
    static readonly IBrush OutsideRange = new ImmutableSolidColorBrush(Color.FromArgb(0xc0, 0x05, 0x06, 0x08));
    static readonly IBrush RangeBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x5c, 0xd6, 0x8a));
    static readonly IPen RangePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x5c, 0xd6, 0x8a)), 1.5);
    static readonly IBrush PressedKey = new ImmutableSolidColorBrush(Color.FromRgb(0x4a, 0x9d, 0xf2));
    static readonly IBrush HoverBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xe0, 0x2a, 0x2d, 0x34));
    static readonly IPen RowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2d)), 1);
    static readonly IPen OctavePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x34, 0x38, 0x40)), 1);
    static readonly IPen BeatPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x2a, 0x2d, 0x34)), 1);
    static readonly IPen BarPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x45, 0x4a, 0x55)), 1);
    static readonly IPen NoteOutline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 1);
    static readonly IPen BendPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xe0, 0xff, 0xff, 0xff)), 1.5);
    static readonly IPen PlayheadPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff)), 1.5);
    static readonly Typeface LabelFace = new(FontFamily.Default);

    double _scrollSeconds;
    double _duration = 10;
    Point? _dragStart;
    double _dragScrollStart;
    Point? _hover;
    int _heldKey = -1;            // key held on the keyboard strip
    int _auditioning = -1;        // note being played after a click in the roll
    Point? _pressPoint;
    enum RangeDrag { None, Start, End, New }
    RangeDrag _rangeDrag;
    double _rangeAnchor;
    CancellationTokenSource? _auditionCts;
    WaveSummary? _wave, _removed;

    static PianoRollView()
    {
        AffectsRender<PianoRollView>(NotesProperty, WaveformProperty, RemovedProperty, GridProperty, PlayheadProperty, ColorByTrackProperty,
            RangeStartProperty, RangeEndProperty, SoundingKeysProperty, ChordsProperty, DrumsProperty, ZoomProperty, PatternsProperty);
        FocusableProperty.OverrideDefaultValue<PianoRollView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PianoRollView>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WaveformProperty || change.Property == NotesProperty || change.Property == SampleRateProperty)
        {
            _duration = Waveform is { Length: > 0 } w ? (double)w.Length / SampleRate
                : Notes is { Count: > 0 } n ? n.Max(x => x.End) : 10;
            if (change.Property == WaveformProperty)
            {
                _wave = Waveform is { } wave ? new WaveSummary(wave) : null;
                FitAll(); // a new file starts with all of it in view
            }
            CoerceValue(ZoomProperty); // the closest zoom depends on the length
        }
        else if (change.Property == ZoomProperty)
        {
            // Keep the middle of the view where it is (Ctrl + wheel then moves it under the cursor).
            double before = _duration / RollWidth * MinZoom / change.GetOldValue<double>();
            _scrollSeconds += RollWidth * (before - SecondsPerPixel) / 2;
            ClampScroll();
        }
        else if (change.Property == RemovedProperty)
            _removed = Removed is { } r ? new WaveSummary(r) : null;
        else if (change.Property == PatternsProperty)
            _selectedClip = null;
        else if (change.Property == PlayheadProperty && Playhead >= 0 && _dragStart is null)
        {
            // Page along with playback when the playhead leaves the view.
            double visible = RollWidth * SecondsPerPixel;
            if (Playhead < _scrollSeconds || Playhead > _scrollSeconds + visible * 0.95)
                _scrollSeconds = Math.Max(0, Playhead - visible * 0.1);
        }
    }

    double Duration => _duration;

    /// <summary>Seconds of the file per pixel of the roll: all of it at 100%, a quarter at 400%.</summary>
    double SecondsPerPixel => Math.Max(1e-5, _duration / RollWidth * MinZoom / Zoom);

    double RollLeft => KeyboardWidth;
    double RollWidth => Math.Max(1, Bounds.Width - KeyboardWidth);
    double RollTop => WaveHeight + RulerHeight + DrumLaneHeight + PatternLaneHeight;
    double RollHeight => Math.Max(1, Bounds.Height - RollTop);

    double TimeToX(double t) => RollLeft + (t - _scrollSeconds) / SecondsPerPixel;
    double XToTime(double x) => _scrollSeconds + (x - RollLeft) * SecondsPerPixel;

    (int Low, int High) PitchRange()
    {
        int low = 48, high = 84;
        if (Notes is { Count: > 0 } notes)
        {
            low = notes.Min(n => n.Pitch) - 2;
            high = notes.Max(n => n.Pitch) + 2;
        }
        while (high - low + 1 < MinVisibleSemitones) { low--; high++; }
        return (Math.Max(0, low), Math.Min(127, high));
    }

    /// <summary>Pitch under a y coordinate in the roll/keyboard area, or -1 above it.</summary>
    int PitchAt(double y)
    {
        if (y < RollTop) return -1;
        var (low, high) = PitchRange();
        double rowHeight = RollHeight / (high - low + 1);
        return Math.Clamp(high - (int)Math.Floor((y - RollTop) / rowHeight), low, high);
    }

    /// <summary>Like a real key: pressing further in (to the right) plays louder.</summary>
    static float VelocityAt(double x) => (float)Math.Clamp(0.35 + 0.65 * x / KeyboardWidth, 0.2, 1);

    void PressKey(int pitch, float velocity)
    {
        _heldKey = pitch;
        KeyPressed?.Invoke(pitch, velocity);
        InvalidateVisual();
    }

    void ReleaseKey()
    {
        if (_heldKey < 0) return;
        KeyReleased?.Invoke(_heldKey);
        _heldKey = -1;
        InvalidateVisual();
    }

    /// <summary>Play a note from the roll for its length, up to 2 s.</summary>
    async void Audition(DetectedNote note)
    {
        _auditionCts?.Cancel();
        if (_auditioning >= 0) KeyReleased?.Invoke(_auditioning);
        var cts = new CancellationTokenSource();
        _auditionCts = cts;
        _auditioning = note.Pitch;
        KeyPressed?.Invoke(note.Pitch, Math.Max(0.3f, note.Velocity / 127f));
        InvalidateVisual();
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(note.Duration, 0.15, 2.0)), cts.Token); }
        catch (OperationCanceledException) { return; }
        KeyReleased?.Invoke(note.Pitch);
        _auditioning = -1;
        InvalidateVisual();
    }

    public void FitAll()
    {
        SetCurrentValue(ZoomProperty, MinZoom);
        _scrollSeconds = 0;
        InvalidateVisual();
    }

    void ClampScroll()
    {
        double visible = RollWidth * SecondsPerPixel;
        _scrollSeconds = Math.Clamp(_scrollSeconds, 0, Math.Max(0, Duration - visible * 0.5));
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Background, bounds);
        if (bounds.Width < KeyboardWidth + 10 || bounds.Height < RollTop + 10) return;

        var (low, high) = PitchRange();
        int rows = high - low + 1;
        double rowHeight = RollHeight / rows;
        double PitchToY(int p) => RollTop + (high - p) * rowHeight;

        // Pitch rows
        for (int p = low; p <= high; p++)
        {
            double y = PitchToY(p);
            if (IsBlack(p)) ctx.FillRectangle(BlackKeyRow, new Rect(RollLeft, y, RollWidth, rowHeight));
            ctx.DrawLine(p % 12 == 0 ? OctavePen : RowPen, new Point(RollLeft, y + rowHeight), new Point(bounds.Width, y + rowHeight));
        }

        if (DrumLaneHeight > 0) ctx.FillRectangle(LaneBackground, new Rect(RollLeft, DrumLaneTop, RollWidth, DrumLaneHeight));
        if (PatternLaneHeight > 0) ctx.FillRectangle(LaneBackground, new Rect(RollLeft, PatternLaneTop, RollWidth, PatternLaneHeight));
        DrawGrid(ctx, bounds);
        DrawDrums(ctx, bounds);
        DrawPatterns(ctx, bounds);
        DrawChords(ctx, bounds);
        DrawWaveLane(ctx);

        // Notes
        if (Notes is { } notes)
        {
            using (ctx.PushClip(new Rect(RollLeft, RollTop, RollWidth, RollHeight)))
            {
                double t0 = _scrollSeconds, t1 = XToTime(bounds.Width);
                foreach (var n in notes)
                {
                    if (n.End < t0 || n.Start > t1 || n.Pitch < low || n.Pitch > high) continue;
                    double x0 = TimeToX(n.Start), x1 = Math.Max(x0 + 2, TimeToX(n.End));
                    var rect = new Rect(x0, PitchToY(n.Pitch) + 1, x1 - x0, Math.Max(1, rowHeight - 2));
                    var brush = ColorByTrack ? TrackBrush(n.Track, n.Velocity) : VelocityBrush(n.Velocity);
                    ctx.DrawRectangle(brush, NoteOutline, rect, 2, 2);
                    if (n.Bends is { Count: > 0 } bends) DrawBends(ctx, n, bends, x1, PitchToY(n.Pitch) + rowHeight / 2, rowHeight);
                }
            }
        }

        DrawRange(ctx, bounds);
        DrawKeyboard(ctx, low, high, rowHeight, PitchToY);
        if (DrumLaneHeight > 0)
            for (int row = 0; row < DrumRows.Length; row++)
                DrawLabel(ctx, DrumRows[row].Label, new Point(4, DrumLaneTop + row * DrumRowHeight - 1), LabelBrush, 7.5);
        if (Patterns is { Count: > 0 } patternLanes)
            for (int lane = 0; lane < patternLanes.Count; lane++)
                DrawLabel(ctx, patternLanes[lane].Label, new Point(4, PatternLaneTop + lane * PatternRowHeight + 1), LabelBrush, 7.5);

        if (Playhead >= 0)
        {
            double x = TimeToX(Playhead);
            if (x >= RollLeft) ctx.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        DrawHover(ctx, low, high, rowHeight);
    }

    /// <summary>A note's bends: a line along its pitch, stepping up and down by rows (a semitone each).</summary>
    void DrawBends(DrawingContext ctx, DetectedNote note, IReadOnlyList<PitchBend> bends, double x1, double centreY, double rowHeight)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            double y(float semitones) => centreY - semitones * rowHeight;
            g.BeginFigure(new Point(TimeToX(note.Start), y(bends[0].Time <= note.Start ? bends[0].Semitones : 0)), false);
            for (int i = 0; i < bends.Count; i++)
            {
                double x = Math.Clamp(TimeToX(bends[i].Time), TimeToX(note.Start), x1);
                g.LineTo(new Point(x, y(i > 0 ? bends[i - 1].Semitones : bends[0].Semitones)));
                g.LineTo(new Point(x, y(bends[i].Semitones)));
            }
            g.LineTo(new Point(x1, y(bends[^1].Semitones)));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, BendPen, geometry);
    }

    void DrawGrid(DrawingContext ctx, Rect bounds)
    {
        double t0 = _scrollSeconds, t1 = XToTime(bounds.Width);
        if (Grid is { } grid)
        {
            double beat = grid.BeatSeconds;
            int perBar = grid.BeatsPerBar;
            double pxPerBeat = beat / SecondsPerPixel;
            double pxPerBar = pxPerBeat * perBar;
            // Draw every beat when there is room, otherwise every bar, otherwise every n-th bar.
            int step = pxPerBeat >= 8 ? 1 : pxPerBar >= 8 ? perBar : (int)Math.Ceiling(32 / pxPerBar) * perBar;
            int labelEvery = Math.Max(1, (int)Math.Ceiling(40 / pxPerBar));
            long first = (long)Math.Floor(grid.ToBeats(t0));
            first -= ((first % step) + step) % step;
            for (long b = first; ; b += step)
            {
                double t = grid.ToSeconds(b);
                if (t > t1) break;
                if (t < 0) continue;
                double x = TimeToX(t);
                bool isBar = b % perBar == 0;
                ctx.DrawLine(isBar ? BarPen : BeatPen, new Point(x, DrumLaneTop), new Point(x, bounds.Height));
                long bar = b / perBar + 1;
                if (isBar && bar >= 1 && (bar - 1) % labelEvery == 0 && x >= RollLeft) // skip labels that would sit half under the keyboard
                    DrawLabel(ctx, bar.ToString(CultureInfo.InvariantCulture), new Point(x + 3, WaveHeight + 2), LabelBrush);
            }
        }
        else
        {
            double stepSeconds = NiceStep(80 * SecondsPerPixel);
            for (double t = Math.Floor(t0 / stepSeconds) * stepSeconds; t <= t1; t += stepSeconds)
            {
                double x = TimeToX(t);
                ctx.DrawLine(BeatPen, new Point(x, DrumLaneTop), new Point(x, bounds.Height));
                DrawLabel(ctx, FormatTime(t), new Point(x + 3, WaveHeight + 2), LabelBrush);
            }
        }
    }

    /// <summary>Each hit as a tick in its drum's row, brighter for louder hits.</summary>
    void DrawDrums(DrawingContext ctx, Rect bounds)
    {
        if (Drums is not { Count: > 0 } drums) return;
        double t0 = XToTime(RollLeft - 2), t1 = XToTime(bounds.Width + 2);
        using (ctx.PushClip(new Rect(RollLeft, DrumLaneTop, RollWidth, DrumLaneHeight)))
            foreach (var hit in drums)
            {
                if (hit.Start < t0 || hit.Start > t1) continue;
                int row = DrumRow(hit.Pitch);
                double x = TimeToX(hit.Start), y = DrumLaneTop + 1 + row * DrumRowHeight;
                ctx.FillRectangle(DrumRows[row].Brushes[Math.Clamp(hit.Velocity, 0, 127) * 4 / 128], new Rect(x - 1, y + 1, 3, DrumRowHeight - 2));
            }
        ctx.DrawLine(RowPen, new Point(RollLeft, DrumLaneTop + DrumLaneHeight - 0.5), new Point(bounds.Width, DrumLaneTop + DrumLaneHeight - 0.5));
    }

    /// <summary>Each part's repeats as lettered cells, the selected clip's outlined wherever it repeats.</summary>
    void DrawPatterns(DrawingContext ctx, Rect bounds)
    {
        if (Patterns is not { Count: > 0 } lanes) return;
        double t0 = XToTime(RollLeft), t1 = XToTime(bounds.Width);
        using (ctx.PushClip(new Rect(RollLeft, PatternLaneTop, RollWidth, PatternLaneHeight)))
            for (int lane = 0; lane < lanes.Count; lane++)
                foreach (var cell in lanes[lane].Cells)
                {
                    if (cell.End < t0 || cell.Start > t1) continue;
                    double x0 = TimeToX(cell.Start), x1 = TimeToX(cell.End), y = PatternLaneTop + 1 + lane * PatternRowHeight;
                    var rect = new Rect(x0 + 1, y + 1, Math.Max(1, x1 - x0 - 2), PatternRowHeight - 2);
                    bool selected = _selectedClip == (lane, cell.Pattern);
                    ctx.DrawRectangle(PatternBrushes[cell.Pattern % PatternBrushes.Length], selected ? SelectedClipPen : null, rect, 2, 2);
                    if (rect.Width >= 10) DrawLabel(ctx, cell.Letter, new Point(rect.X + 3, y), WhiteKey, 9);
                }
        ctx.DrawLine(RowPen, new Point(RollLeft, PatternLaneTop + PatternLaneHeight - 0.5), new Point(bounds.Width, PatternLaneTop + PatternLaneHeight - 0.5));
    }

    /// <summary>The pattern lane and repeat under a point, if any.</summary>
    (int Lane, PatternCell Cell)? PatternAt(Point pos)
    {
        if (Patterns is not { Count: > 0 } lanes || pos.X < RollLeft || pos.Y < PatternLaneTop || pos.Y >= PatternLaneTop + PatternLaneHeight) return null;
        int lane = Math.Clamp((int)((pos.Y - PatternLaneTop - 1) / PatternRowHeight), 0, lanes.Count - 1);
        double t = XToTime(pos.X);
        foreach (var cell in lanes[lane].Cells)
            if (t >= cell.Start && t < cell.End) return (lane, cell);
        return null;
    }

    /// <summary>The drum hit drawn under a point in the drum lane, if any.</summary>
    DetectedNote? DrumAt(Point pos)
    {
        if (Drums is not { Count: > 0 } drums || pos.X < RollLeft || pos.Y < DrumLaneTop || pos.Y >= RollTop) return null;
        int row = Math.Clamp((int)((pos.Y - DrumLaneTop - 1) / DrumRowHeight), 0, DrumRows.Length - 1);
        DetectedNote? best = null;
        double nearest = 5; // pixels
        foreach (var hit in drums)
        {
            double dx = Math.Abs(TimeToX(hit.Start) - pos.X);
            if (DrumRow(hit.Pitch) == row && dx <= nearest) { best = hit; nearest = dx; }
        }
        return best;
    }

    void DrawRange(DrawingContext ctx, Rect bounds)
    {
        if (!HasRange) return;
        double x0 = Math.Max(RollLeft, TimeToX(RangeStart)), x1 = Math.Min(bounds.Width, TimeToX(RangeEnd));
        using (ctx.PushClip(new Rect(RollLeft, 0, RollWidth, bounds.Height)))
        {
            if (x0 > RollLeft) ctx.FillRectangle(OutsideRange, new Rect(RollLeft, 0, x0 - RollLeft, bounds.Height));
            if (x1 < bounds.Width) ctx.FillRectangle(OutsideRange, new Rect(x1, 0, bounds.Width - x1, bounds.Height));
            foreach (var t in new[] { RangeStart, RangeEnd })
            {
                double x = TimeToX(t);
                ctx.DrawLine(RangePen, new Point(x, 0), new Point(x, bounds.Height));
                ctx.FillRectangle(RangeBrush, new Rect(x - 4, 0, 8, 10)); // grab handle
            }
        }
    }

    /// <summary>Time for a range edge: snapped to the nearest beat unless Alt is held.</summary>
    double RangeTime(double x, KeyModifiers modifiers)
    {
        double t = Math.Clamp(XToTime(x), 0, Duration);
        if (Grid is { } g && !modifiers.HasFlag(KeyModifiers.Alt))
            t = Math.Clamp(g.ToSeconds(Math.Round(g.ToBeats(t))), 0, Duration);
        return t;
    }

    void SetRange(double a, double b)
    {
        SetCurrentValue(RangeStartProperty, Math.Min(a, b));
        SetCurrentValue(RangeEndProperty, Math.Max(a, b));
    }

    void DrawWaveLane(DrawingContext ctx)
    {
        var lane = new Rect(RollLeft, 0, RollWidth, WaveHeight);
        ctx.FillRectangle(LaneBackground, lane);
        double mid = WaveHeight / 2, scale = WaveHeight / 2 - 2;

        void DrawSummary(WaveSummary? wave, IBrush brush)
        {
            if (wave is null) return;
            double samplesPerPixel = SecondsPerPixel * SampleRate;
            for (int px = 0; px < (int)RollWidth; px++)
            {
                long s0 = (long)((_scrollSeconds + px * SecondsPerPixel) * SampleRate);
                var (mn, mx) = wave.Range(s0, s0 + Math.Max(1, (long)samplesPerPixel));
                if (mx < mn) continue;
                double x = RollLeft + px;
                ctx.FillRectangle(brush, new Rect(x, mid - mx * scale, 1, Math.Max(1, (mx - mn) * scale)));
            }
        }

        using (ctx.PushClip(lane))
        {
            DrawSummary(_wave, WaveBrush);
            DrawSummary(_removed, RemovedBrush);
        }
        DrawLabel(ctx, "audio", new Point(4, 4), LabelBrush);
        if (_removed is not null) DrawLabel(ctx, "drums", new Point(4, 20), RemovedBrush);
    }

    void DrawKeyboard(DrawingContext ctx, int low, int high, double rowHeight, Func<int, double> pitchToY)
    {
        ctx.FillRectangle(LaneBackground, new Rect(0, 0, KeyboardWidth, RollTop));
        for (int p = low; p <= high; p++)
        {
            var r = new Rect(0, pitchToY(p), KeyboardWidth - 1, rowHeight);
            bool down = p == _heldKey || p == _auditioning || (SoundingKeys?.Contains(p) ?? false);
            ctx.FillRectangle(down ? PressedKey : IsBlack(p) ? BlackKey : WhiteKey, r);
            ctx.DrawLine(RowPen, r.BottomLeft, r.BottomRight);
            if (p % 12 == 0 && rowHeight >= 7)
                DrawLabel(ctx, DetectedNote.PitchName(p), new Point(3, r.Y + rowHeight / 2 - 6), KeyLabelBrush, 9);
        }
    }

    void DrawHover(DrawingContext ctx, int low, int high, double rowHeight)
    {
        if (_hover is not { } h || h.X < RollLeft) return;
        string text;
        if (DrumAt(h) is { } hit)
            text = $"{FormatTime(hit.Start)}  {DrumRows[DrumRow(hit.Pitch)].Label.ToLowerInvariant()}  vel {hit.Velocity}";
        else if (PatternAt(h) is { } clip)
            text = $"{Patterns![clip.Lane].Label.ToLowerInvariant()}: pattern {clip.Cell.Letter}  ·  click to loop it, right-click to save it";
        else
        {
            if (h.Y < RollTop) return;
            double t = XToTime(h.X);
            int pitch = high - (int)Math.Floor((h.Y - RollTop) / rowHeight);
            if (pitch < low || pitch > high) return;
            text = $"{FormatTime(t)}  {DetectedNote.PitchName(pitch)}";
            if (Notes?.FirstOrDefault(n => n.Pitch == pitch && n.Start <= t && n.End >= t) is { Duration: > 0 } note)
                text += $"  ·  {note.Duration * 1000:F0} ms  vel {note.Velocity}";
        }

        var ft = MakeText(text, LabelBrush, 11);
        var box = new Rect(Bounds.Width - ft.Width - 16, RollTop + 6, ft.Width + 10, ft.Height + 6);
        ctx.FillRectangle(HoverBrush, box);
        ctx.DrawText(ft, new Point(box.X + 5, box.Y + 3));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        bool zoom = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (zoom)
        {
            double anchor = XToTime(pos.X);
            SetCurrentValue(ZoomProperty, Zoom * Math.Pow(1.15, e.Delta.Y));
            _scrollSeconds = anchor - (pos.X - RollLeft) * SecondsPerPixel;
        }
        else
        {
            // Trackpads give horizontal deltas and mouse wheels vertical ones. Both scroll through time.
            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            _scrollSeconds -= delta * 40 * SecondsPerPixel;
        }
        ClampScroll();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);

        // Pattern lane: a clicked repeat becomes the start/stop range for looping, and its pattern the selected clip.
        if (PatternAt(pos) is { } clip)
        {
            SetRange(clip.Cell.Start, clip.Cell.End);
            _selectedClip = (clip.Lane, clip.Cell.Pattern);
            InvalidateVisual();
            ClipClicked?.Invoke(clip.Lane, clip.Cell.Pattern, e.GetCurrentPoint(this).Properties.IsRightButtonPressed);
            e.Handled = true;
            return;
        }

        // Keyboard strip: play the key; dragging up/down glides across keys.
        if (pos.X < KeyboardWidth && PitchAt(pos.Y) is var key and >= 0)
        {
            PressKey(key, VelocityAt(pos.X));
            e.Pointer.Capture(this);
            return;
        }

        // Waveform lane: drag to select start/stop points, or drag an existing edge.
        if (pos.X >= RollLeft && pos.Y < RollTop)
        {
            var mods = e.KeyModifiers;
            if (HasRange && Math.Abs(pos.X - TimeToX(RangeStart)) < 6) _rangeDrag = RangeDrag.Start;
            else if (HasRange && Math.Abs(pos.X - TimeToX(RangeEnd)) < 6) _rangeDrag = RangeDrag.End;
            else
            {
                _rangeDrag = RangeDrag.New;
                _rangeAnchor = RangeTime(pos.X, mods);
                _pressPoint = pos;
            }
            e.Pointer.Capture(this);
            return;
        }

        if (e.ClickCount == 2) { FitAll(); return; }
        _pressPoint = pos;
        _dragStart = pos;
        _dragScrollStart = _scrollSeconds;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_rangeDrag != RangeDrag.None)
        {
            double t = RangeTime(pos.X, e.KeyModifiers);
            switch (_rangeDrag)
            {
                case RangeDrag.Start: SetRange(t, RangeEnd); break;
                case RangeDrag.End: SetRange(RangeStart, t); break;
                case RangeDrag.New when _pressPoint is { } p && Math.Abs(pos.X - p.X) >= 4: SetRange(_rangeAnchor, t); break;
            }
            return;
        }
        if (_heldKey >= 0)
        {
            int key = PitchAt(Math.Max(pos.Y, RollTop));
            if (key != _heldKey) { ReleaseKey(); PressKey(key, VelocityAt(Math.Clamp(pos.X, 0, KeyboardWidth))); }
            return;
        }
        if (_dragStart is { } start)
        {
            _scrollSeconds = _dragScrollStart - (pos.X - start.X) * SecondsPerPixel;
            ClampScroll();
        }
        _hover = pos;
        bool onEdge = HasRange && pos.Y < RollTop &&
                      (Math.Abs(pos.X - TimeToX(RangeStart)) < 6 || Math.Abs(pos.X - TimeToX(RangeEnd)) < 6);
        Cursor = pos.X < KeyboardWidth && pos.Y >= RollTop ? new Cursor(StandardCursorType.Hand)
            : onEdge ? new Cursor(StandardCursorType.SizeWestEast)
            : pos.X >= RollLeft && pos.Y < RollTop ? new Cursor(StandardCursorType.Ibeam)
            : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleaseKey();

        if (_rangeDrag != RangeDrag.None)
        {
            // A plain click on the waveform (no drag) clears the selection.
            if (_rangeDrag == RangeDrag.New && _pressPoint is { } p0 && Math.Abs(e.GetPosition(this).X - p0.X) < 4)
            {
                SetCurrentValue(RangeStartProperty, double.NaN);
                SetCurrentValue(RangeEndProperty, double.NaN);
            }
            _rangeDrag = RangeDrag.None;
            _pressPoint = null;
            e.Pointer.Capture(null);
            return;
        }

        // A click on a note, without dragging, plays it.
        var pos = e.GetPosition(this);
        if (_pressPoint is { } p && Math.Abs(pos.X - p.X) < 3 && Math.Abs(pos.Y - p.Y) < 3 && NoteAt(pos) is { } note)
            Audition(note);

        _pressPoint = null;
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ReleaseKey(); // never leave a note hanging if the window loses the pointer
        _dragStart = null;
    }

    DetectedNote? NoteAt(Point pos)
    {
        if (pos.X < RollLeft || Notes is not { } notes) return null;
        int pitch = PitchAt(pos.Y);
        double t = XToTime(pos.X);
        foreach (var n in notes)
            if (n.Pitch == pitch && n.Start <= t && n.End >= t) return n;
        return null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScroll(); // the zoom is relative to the width, so the whole file still fits at 100%
    }

    static bool IsBlack(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;

    static readonly IBrush[] VelocityBrushes = Enumerable.Range(0, 128).Select(v =>
    {
        // Quiet notes teal, loud notes warm yellow.
        double f = v / 127.0;
        byte Lerp(int a, int b) => (byte)(a + (b - a) * f);
        return (IBrush)new ImmutableSolidColorBrush(Color.FromRgb(Lerp(0x3a, 0xff), Lerp(0x9b, 0xc8), Lerp(0xb0, 0x4a)));
    }).ToArray();

    static IBrush VelocityBrush(int velocity) => VelocityBrushes[Math.Clamp(velocity, 0, 127)];

    // Track colours: keys/other warm yellow, bass blue, vocals pink, then extras.
    static readonly Color[] TrackColors =
    [
        Color.FromRgb(0xf2, 0xc4, 0x4a), Color.FromRgb(0x4a, 0x9d, 0xf2), Color.FromRgb(0xf2, 0x6d, 0xb4),
        Color.FromRgb(0x5c, 0xd6, 0x8a), Color.FromRgb(0xb0, 0x8c, 0xf2), Color.FromRgb(0xf2, 0x8f, 0x3c),
        Color.FromRgb(0x4a, 0xd0, 0xc8), Color.FromRgb(0xc8, 0xc8, 0xc8),
    ];

    public static Color TrackColor(int track) => TrackColors[((track % TrackColors.Length) + TrackColors.Length) % TrackColors.Length];

    static readonly IBrush[][] TrackBrushes = TrackColors.Select(c => Enumerable.Range(0, 8).Select(level =>
    {
        double f = 0.45 + 0.55 * level / 7.0; // quiet notes darker
        return (IBrush)new ImmutableSolidColorBrush(Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f)));
    }).ToArray()).ToArray();

    static IBrush TrackBrush(int track, int velocity) =>
        TrackBrushes[((track % TrackBrushes.Length) + TrackBrushes.Length) % TrackBrushes.Length][Math.Clamp(velocity, 0, 127) * 8 / 128];

    /// <summary>Chord symbols where each chord starts; one that would overlap the previous is left out.</summary>
    void DrawChords(DrawingContext ctx, Rect bounds)
    {
        if (Chords is not { Count: > 0 } chords) return;
        double t0 = _scrollSeconds, t1 = XToTime(bounds.Width), lastRight = double.MinValue;
        using (ctx.PushClip(new Rect(RollLeft, WaveHeight, RollWidth, RulerHeight)))
            foreach (var chord in chords)
            {
                if (chord.End < t0 || chord.Start > t1) continue;
                double x = Math.Max(RollLeft, TimeToX(chord.Start)) + 3;
                if (x < lastRight + 6) continue;
                var text = MakeText(chord.Text, ChordBrush, 11);
                ctx.DrawText(text, new Point(x, WaveHeight + 17));
                lastRight = x + text.Width;
            }
    }

    static FormattedText MakeText(string text, IBrush brush, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush);

    static void DrawLabel(DrawingContext ctx, string text, Point at, IBrush brush, double size = 10) =>
        ctx.DrawText(MakeText(text, brush, size), at);

    static string FormatTime(double t) => t < 60 ? $"{t:F2}s" : $"{(int)(t / 60)}:{t % 60:00.00}";

    static double NiceStep(double raw)
    {
        double[] steps = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120];
        return steps.FirstOrDefault(s => s >= raw, 300);
    }

    /// <summary>Min/max per block of samples so zoomed-out waveforms draw without touching every sample.</summary>
    sealed class WaveSummary
    {
        const int Block = 256;
        readonly float[] _samples;
        readonly float[] _min, _max;

        public WaveSummary(float[] samples)
        {
            _samples = samples;
            int blocks = (samples.Length + Block - 1) / Block;
            _min = new float[blocks];
            _max = new float[blocks];
            for (int b = 0; b < blocks; b++)
            {
                float mn = float.MaxValue, mx = float.MinValue;
                for (int i = b * Block; i < Math.Min(samples.Length, (b + 1) * Block); i++)
                {
                    mn = Math.Min(mn, samples[i]);
                    mx = Math.Max(mx, samples[i]);
                }
                _min[b] = mn; _max[b] = mx;
            }
        }

        public (float Min, float Max) Range(long from, long to)
        {
            from = Math.Max(0, from);
            to = Math.Min(_samples.Length, to);
            float mn = float.MaxValue, mx = float.MinValue;
            if (to - from >= Block * 2)
            {
                for (long b = from / Block; b < to / Block; b++) { mn = Math.Min(mn, _min[b]); mx = Math.Max(mx, _max[b]); }
            }
            else
            {
                for (long i = from; i < to; i++) { mn = Math.Min(mn, _samples[i]); mx = Math.Max(mx, _samples[i]); }
            }
            return (mn, mx);
        }
    }
}
