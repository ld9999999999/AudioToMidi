namespace AudioToMidi.Model;

/// <summary>
/// The beat grid. Every beat is listed, so a grid that speeds up or slows down is described the same
/// way as a steady one. Use <see cref="ToBeats"/> and <see cref="ToSeconds"/> to place things on it.
/// </summary>
/// <param name="Bpm">Average tempo, beats per minute.</param>
/// <param name="BeatsPerBar">Beats per bar as counted: the time signature's numerator in simple meters
/// (4 in 4/4, beats of a quarter note), a third of it in compound ones (2 in 6/8, beats of a dotted quarter).</param>
/// <param name="Beats">Time of every beat in seconds, ascending. The first can be negative when bar 1
/// starts just before the audio does (a note slightly ahead of the beat).</param>
/// <param name="FirstDownbeat">Index into <see cref="Beats"/> of beat 1 of bar 1.</param>
/// <param name="Confidence">How periodic the music was, 0..1. Below about 0.1 the grid is a guess.</param>
public sealed record TimingInfo(double Bpm, int BeatsPerBar, IReadOnlyList<double> Beats, int FirstDownbeat, double Confidence)
{
    /// <summary>The time signature as written, such as "4/4" or "6/8".</summary>
    public string TimeSignature { get; init; } = "4/4";

    /// <summary>
    /// Where the off-beat eighths fall in the beat: 0.5 when straight, 0.6 when the eighth after the beat
    /// starts 60% of the way through it, 2/3 for a triplet shuffle. Sixteenths fall evenly within each half.
    /// </summary>
    public double Swing { get; init; } = 0.5;

    double BeatSeconds => 60 / Bpm;

    /// <summary>
    /// Convert seconds to beats, counted from beat 1 of bar 1 (which is 0). Before the first and after
    /// the last listed beat, the nearest beat spacing continues.
    /// </summary>
    public double ToBeats(double seconds)
    {
        if (Beats.Count == 0) return seconds / BeatSeconds;
        if (Beats.Count == 1) return (seconds - Beats[0]) / BeatSeconds - FirstDownbeat;

        int i = Interval(seconds);
        double a = Beats[i], b = Beats[i + 1];
        return i + (seconds - a) / (b - a) - FirstDownbeat;
    }

    /// <summary>Convert beats back to seconds; the inverse of <see cref="ToBeats"/>.</summary>
    public double ToSeconds(double beats)
    {
        if (Beats.Count == 0) return beats * BeatSeconds;
        if (Beats.Count == 1) return Beats[0] + (beats + FirstDownbeat) * BeatSeconds;

        double index = beats + FirstDownbeat;
        int i = (int)Math.Clamp(Math.Floor(index), 0, Beats.Count - 2);
        double a = Beats[i], b = Beats[i + 1];
        return a + (index - i) * (b - a);
    }

    /// <summary>Get the bar number (1 = the first bar) and the beat within the bar (0 = the downbeat).</summary>
    public (int Bar, double Beat) ToBarBeat(double seconds)
    {
        double beats = ToBeats(seconds);
        int bar = (int)Math.Floor(beats / BeatsPerBar);
        return (bar + 1, beats - bar * BeatsPerBar);
    }

    int Interval(double seconds)
    {
        int lo = 0, hi = Beats.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (Beats[mid] <= seconds) lo = mid; else hi = mid;
        }
        return Math.Min(lo, Beats.Count - 2);
    }
}
