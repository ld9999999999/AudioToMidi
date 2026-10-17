using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Tracks;

/// <summary>
/// Clean up a bass track transcribed from a separated bass stem. basic-pitch hears a low note's strong
/// harmonics as notes of their own, so a bass line that plays one note at a time comes out with an octave,
/// a twelfth and two octaves stacked on many of its notes, and sometimes with only those.
/// </summary>
public static class BassCleanup
{
    /// <summary>
    /// Run the whole clean-up: drop harmonics, fix octaves against the stem, then drop harmonics again,
    /// because a moved note can land on a lower note's harmonic.
    /// </summary>
    public static List<DetectedNote> Clean(IReadOnlyList<DetectedNote> notes, float[] stem, int sampleRate,
        IDictionary<(int Pitch, long Start, long End), (double Octave, double TwoOctaves)>? evidence = null) =>
        DropHarmonics(FixOctaves(DropHarmonics(notes), stem, sampleRate, evidence: evidence));

    /// <summary>Intervals of the 2nd to 6th and 8th harmonics above a fundamental, in semitones.</summary>
    static readonly int[] HarmonicIntervals = [12, 19, 24, 28, 31, 36];

    /// <summary>A harmonic starts within this many seconds of its fundamental.</summary>
    const double SameStartSeconds = 0.08;

    /// <summary>
    /// Drop each note that starts together with a lower, still-sounding note at one of that note's harmonics.
    /// Notes at other intervals, and higher notes that start later, are kept.
    /// </summary>
    public static List<DetectedNote> DropHarmonics(IReadOnlyList<DetectedNote> notes)
    {
        var sorted = notes.OrderBy(n => n.Start).ToList();
        var kept = new List<DetectedNote>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            bool harmonic = false;
            // Only notes starting within SameStartSeconds can be its fundamental, and the list is sorted by start.
            for (int j = i - 1; j >= 0 && n.Start - sorted[j].Start < SameStartSeconds && !harmonic; j--)
                harmonic = IsHarmonicOf(n, sorted[j]);
            for (int j = i + 1; j < sorted.Count && sorted[j].Start - n.Start < SameStartSeconds && !harmonic; j++)
                harmonic = IsHarmonicOf(n, sorted[j]);
            if (!harmonic) kept.Add(n);
        }
        return kept;
    }

    /// <summary>
    /// Move notes down one or two octaves where the stem shows the lower fundamental. A note at f puts no
    /// energy at the odd multiples of f/2 (f/2, 3f/2, 5f/2), but a note really at f/2 does. When that energy
    /// is at least <paramref name="octaveEvidence"/> of the energy at f's own first three harmonics, the note
    /// moves down an octave. The same test with f/4 moves it down two octaves, and is checked first. The
    /// energy is measured on the stem just after the attack, for up to 0.4 s. Duplicate notes that result are merged.
    /// </summary>
    /// <param name="evidence">A cache of measurements by note, since notes are decoded again on every setting change.</param>
    public static List<DetectedNote> FixOctaves(IReadOnlyList<DetectedNote> notes, float[] stem, int sampleRate,
        double octaveEvidence = 0.4, double twoOctaveEvidence = 0.6, int lowestNote = 21,
        IDictionary<(int Pitch, long Start, long End), (double Octave, double TwoOctaves)>? evidence = null)
    {
        var moved = new List<DetectedNote>(notes.Count);
        foreach (var n in notes)
        {
            var key = (n.Pitch, (long)Math.Round(n.Start * 1000), (long)Math.Round(n.End * 1000));
            if (evidence is null || !evidence.TryGetValue(key, out var e))
            {
                e = (LowerFundamental(stem, sampleRate, n, 2), LowerFundamental(stem, sampleRate, n, 4));
                if (evidence is not null) evidence[key] = e;
            }
            int shift = e.TwoOctaves >= twoOctaveEvidence && n.Pitch - 24 >= lowestNote ? 24
                : e.Octave >= octaveEvidence && n.Pitch - 12 >= lowestNote ? 12 : 0;
            moved.Add(n with { Pitch = n.Pitch - shift });
        }
        return MergeCopies(moved);
    }

    /// <summary>Compare the energy at the odd multiples of f/<paramref name="divisor"/> with the energy at f, 2f and 3f.</summary>
    static double LowerFundamental(float[] stem, int sampleRate, DetectedNote note, int divisor)
    {
        int from = (int)((note.Start + 0.02) * sampleRate);
        int length = (int)(Math.Min(note.Duration - 0.02, 0.4) * sampleRate);
        if (length < sampleRate / 50 || from < 0 || from + length > stem.Length) return 0;
        double f = 440 * Math.Pow(2, (note.Pitch - 69) / 12.0), low = f / divisor;
        double odd = 0, own = 0;
        for (int k = 1; k <= 5; k += 2) odd += Magnitude(stem, from, length, low * k, sampleRate);
        for (int k = 1; k <= 3; k++) own += Magnitude(stem, from, length, f * k, sampleRate);
        return own <= 0 ? 0 : odd / own;
    }

    /// <summary>Measure the strength of one frequency over a windowed stretch of audio (Goertzel algorithm, Hann window).</summary>
    static double Magnitude(float[] x, int from, int length, double hz, int sampleRate)
    {
        if (hz >= sampleRate / 2.0) return 0;
        double c = 2 * Math.Cos(2 * Math.PI * hz / sampleRate), s1 = 0, s2 = 0, step = 2 * Math.PI / (length - 1);
        for (int i = 0; i < length; i++)
        {
            double s0 = x[from + i] * (0.5 - 0.5 * Math.Cos(step * i)) + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) / length;
    }

    /// <summary>Merge overlapping notes of one pitch that start together, keeping the longest.</summary>
    static List<DetectedNote> MergeCopies(List<DetectedNote> notes)
    {
        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        var kept = new List<DetectedNote>(notes.Count);
        foreach (var n in notes)
        {
            int copy = kept.FindLastIndex(k => k.Pitch == n.Pitch && n.Start - k.Start < SameStartSeconds && k.End > n.Start);
            if (copy < 0) kept.Add(n);
            else if (n.Duration > kept[copy].Duration) kept[copy] = kept[copy] with { End = Math.Max(kept[copy].End, n.End) };
        }
        return kept;
    }

    static bool IsHarmonicOf(DetectedNote note, DetectedNote fundamental) =>
        fundamental.Pitch < note.Pitch && fundamental.End > note.Start &&
        Array.IndexOf(HarmonicIntervals, note.Pitch - fundamental.Pitch) >= 0;
}
