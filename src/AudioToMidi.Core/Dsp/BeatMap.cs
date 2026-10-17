namespace AudioToMidi.Core.Dsp;

/// <summary>
/// Beat times, used to convert between seconds and beats when the tempo drifts. Beat i sits at
/// <see cref="Beats"/>[i]. Positions between beats are linear, and the nearest beat spacing carries on
/// before the first beat and after the last. With only two beats, the tempo is steady everywhere.
/// </summary>
public sealed class BeatMap
{
    readonly double[] _beats;

    /// <param name="beats">At least two, strictly ascending.</param>
    public BeatMap(IEnumerable<double> beats)
    {
        _beats = [.. beats];
        if (_beats.Length < 2) throw new ArgumentException("A beat map needs at least two beats.", nameof(beats));
        for (int i = 1; i < _beats.Length; i++)
            if (!(_beats[i] > _beats[i - 1])) throw new ArgumentException("Beats must be strictly ascending.", nameof(beats));
        var spacing = new double[_beats.Length - 1];
        for (int i = 1; i < _beats.Length; i++) spacing[i - 1] = _beats[i] - _beats[i - 1];
        Array.Sort(spacing);
        BeatSeconds = spacing[spacing.Length / 2];
    }

    /// <summary>A steady tempo: beat 0 at <paramref name="firstBeat"/>, one beat every <paramref name="beatSeconds"/>.</summary>
    public static BeatMap Steady(double firstBeat, double beatSeconds) => new([firstBeat, firstBeat + beatSeconds]);

    public IReadOnlyList<double> Beats => _beats;

    /// <summary>Typical (median) beat length.</summary>
    public double BeatSeconds { get; }

    public double Bpm => 60 / BeatSeconds;

    /// <summary>Fractional position in beats, where beat 0 is <see cref="Beats"/>[0].</summary>
    public double ToBeat(double seconds)
    {
        int i = Interval(seconds);
        double a = _beats[i], b = _beats[i + 1];
        return i + (seconds - a) / (b - a);
    }

    /// <summary>The inverse of <see cref="ToBeat"/>.</summary>
    public double ToSeconds(double beat)
    {
        int i = (int)Math.Clamp(Math.Floor(beat), 0, _beats.Length - 2);
        double a = _beats[i], b = _beats[i + 1];
        return a + (beat - i) * (b - a);
    }

    /// <summary>Snap to the nearest 1/<paramref name="divisions"/> of a beat, on a swung grid when <paramref name="swing"/> isn't 0.5 (see <see cref="Swing"/>).</summary>
    public double Snap(double seconds, int divisions = 1, double swing = Swing.Straight)
    {
        if (!Swing.Applies(divisions, swing)) return ToSeconds(Math.Round(ToBeat(seconds) * divisions) / divisions);
        double straight = Swing.ToStraight(ToBeat(seconds), swing);
        return ToSeconds(Swing.ToSwung(Math.Round(straight * divisions) / divisions, swing));
    }

    /// <summary>True when every beat is within 1% of the typical length, so the song keeps one tempo.</summary>
    public bool IsSteady
    {
        get
        {
            for (int i = 1; i < _beats.Length; i++)
                if (Math.Abs(_beats[i] - _beats[i - 1] - BeatSeconds) > 0.01 * BeatSeconds) return false;
            return true;
        }
    }

    /// <summary>Return the slowest and fastest tempo, each taken from the median of 9 beats so a single uneven beat does not count.</summary>
    public (double Min, double Max) BpmRange()
    {
        const int Span = 9;
        int count = _beats.Length - 1;
        if (count < Span) return (Bpm, Bpm);
        double min = double.MaxValue, max = double.MinValue;
        var window = new double[Span];
        for (int i = 0; i + Span <= count; i++)
        {
            for (int k = 0; k < Span; k++) window[k] = _beats[i + k + 1] - _beats[i + k];
            Array.Sort(window);
            double bpm = 60 / window[Span / 2];
            min = Math.Min(min, bpm);
            max = Math.Max(max, bpm);
        }
        return (min, max);
    }

    /// <summary>
    /// Return the map at another tempo. At two or three times the speed, add evenly spaced beats between
    /// each pair. At half or a third, keep every second or third beat starting from beat <paramref name="phase"/>,
    /// so a 6/8 tracked on its eighths can land on the right dotted quarters. Any other ratio gives a steady
    /// tempo of <paramref name="bpm"/> from the first beat.
    /// </summary>
    public BeatMap WithBpm(double bpm, int phase = 0)
    {
        double ratio = bpm / Bpm;
        if (Math.Abs(ratio - 1) < 0.001) return this;
        foreach (int factor in new[] { 2, 3 })
        {
            if (Math.Abs(ratio - factor) < 0.01 * factor)
            {
                var faster = new List<double>(_beats.Length * factor);
                for (int i = 0; i < _beats.Length; i++)
                {
                    faster.Add(_beats[i]);
                    if (i + 1 < _beats.Length)
                        for (int k = 1; k < factor; k++) faster.Add(_beats[i] + (_beats[i + 1] - _beats[i]) * k / factor);
                }
                return new BeatMap(faster);
            }
            if (Math.Abs(ratio - 1.0 / factor) < 0.01 / factor)
            {
                int start = ((phase % factor) + factor) % factor;
                return _beats.Length >= 2 * factor
                    ? new BeatMap(_beats.Where((_, i) => i >= start && (i - start) % factor == 0))
                    : Steady(ToSeconds(start), 60 / bpm); // too few beats: steady tempo from the chosen beat
            }
        }
        return Steady(_beats[0], 60 / bpm);
    }

    /// <summary>Find the beat pair [i, i + 1] that covers <paramref name="seconds"/>; the end pairs extrapolate.</summary>
    int Interval(double seconds)
    {
        int lo = 0, hi = _beats.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_beats[mid] <= seconds) lo = mid; else hi = mid;
        }
        return Math.Min(lo, _beats.Length - 2);
    }
}
