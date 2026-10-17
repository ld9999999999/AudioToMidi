using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Harmony;

/// <summary>
/// Find chords from notes, one decision per beat. Each beat's notes form a pitch-class profile, weighted
/// by how long and how loud they sound. Each chord scores by how much of the profile its tones cover,
/// minus what they leave out and how complex the chord is. A Viterbi pass then picks the sequence that
/// balances those scores against the cost of changing chord, which is lower on the first beat of a bar.
/// </summary>
public static class ChordRecognizer
{
    /// <summary>Weight of pitch classes outside the chord, against those in it.</summary>
    const double OutsidePenalty = 0.6;
    /// <summary>Score a chord needs to beat "no chord" (fraction of the profile, roughly).</summary>
    const double NoChordScore = 0.35;
    /// <summary>Cost of changing chord within a bar, and on its first beat.</summary>
    const double ChangeCost = 0.25, DownbeatChangeCost = 0.1;
    /// <summary>A chord tone counts as present above this share of the profile.</summary>
    const double Present = 0.05;

    static readonly ChordQuality[] Qualities = Enum.GetValues<ChordQuality>();

    /// <summary>Extra score a chord needs over a simpler one: sevenths need a clear seventh, triads a clear third.</summary>
    static double Complexity(ChordQuality q) => q switch
    {
        ChordQuality.Power => 0,
        ChordQuality.Major or ChordQuality.Minor => 0.03,
        ChordQuality.Sus2 or ChordQuality.Sus4 => 0.06,
        ChordQuality.Diminished or ChordQuality.Augmented => 0.08,
        _ => 0.08, // sevenths
    };

    /// <summary>Find chords over the notes' span a beat at a time, merging beats where a chord continues.</summary>
    public static IReadOnlyList<ChordSpan> Recognize(IReadOnlyList<DetectedNote> notes, BarGrid grid)
    {
        if (notes.Count == 0) return [];
        int firstBeat = (int)Math.Floor(grid.ToBeats(notes.Min(n => n.Start)));
        int lastBeat = (int)Math.Ceiling(grid.ToBeats(notes.Max(n => n.End)));
        int beats = Math.Max(1, lastBeat - firstBeat);
        var (profiles, bass) = Profiles(notes, grid, firstBeat, beats);

        // States: every root and quality, then "no chord".
        var chords = new List<Chord>();
        for (int root = 0; root < 12; root++)
            foreach (var q in Qualities) chords.Add(new Chord(root, q));
        int n = chords.Count + 1, none = chords.Count;
        var tones = chords.Select(c => c.PitchClasses.ToArray()).ToArray();

        double Emission(int beat, int state)
        {
            var p = profiles[beat];
            if (state == none) return NoChordScore;
            if (p.Sum() <= 0) return 0;
            double inside = 0, present = 0;
            foreach (int pc in tones[state])
            {
                inside += p[pc];
                if (p[pc] > Present) present++;
            }
            double coverage = present / tones[state].Length;
            double score = inside - OutsidePenalty * (1 - inside) - Complexity(chords[state].Quality) - 0.3 * (1 - coverage);
            // The lowest note is most often the root.
            if (bass[beat] is { } b && b == chords[state].Root) score += 0.1;
            return score;
        }

        int BarOf(int beat) => (int)Math.Floor((double)(firstBeat + beat) / grid.BeatsPerBar);

        // Viterbi over beats.
        var score = new double[n];
        var back = new int[beats, n];
        for (int s = 0; s < n; s++) score[s] = Emission(0, s);
        var next = new double[n];
        for (int beat = 1; beat < beats; beat++)
        {
            int inBar = (((firstBeat + beat) % grid.BeatsPerBar) + grid.BeatsPerBar) % grid.BeatsPerBar;
            double change = inBar == 0 ? DownbeatChangeCost : ChangeCost;
            int best = 0;
            for (int s = 1; s < n; s++) if (score[s] > score[best]) best = s;
            for (int s = 0; s < n; s++)
            {
                bool stay = score[s] >= score[best] - change;
                next[s] = (stay ? score[s] : score[best] - change) + Emission(beat, s);
                back[beat, s] = stay ? s : best;
            }
            (score, next) = (next, score);
        }
        var path = new int[beats];
        for (int s = 1; s < n; s++) if (score[s] > score[path[beats - 1]]) path[beats - 1] = s;
        for (int beat = beats - 1; beat > 0; beat--) path[beat - 1] = back[beat, path[beat]];

        // Within a bar, a chord that changes only in quality (Dm7, Dsus2, Dm) is mostly noise in the
        // notes, so keep one chord on that root with the quality that fits those beats best.
        for (int start = 0; start < beats;)
        {
            int bar = BarOf(start);
            int end = start + 1;
            while (end < beats && BarOf(end) == bar && path[end] != none && path[start] != none
                   && chords[path[end]].Root == chords[path[start]].Root) end++;
            if (end - start > 1)
            {
                int bestState = path[start];
                double bestSum = double.MinValue;
                foreach (int s in path[start..end].Distinct())
                {
                    double sum = 0;
                    for (int i = start; i < end; i++) sum += Emission(i, s);
                    if (sum > bestSum) { bestSum = sum; bestState = s; }
                }
                for (int i = start; i < end; i++) path[i] = bestState;
            }
            start = end;
        }

        // Merge runs; a run whose bass is mostly another chord tone is an inversion.
        var spans = new List<ChordSpan>();
        for (int start = 0; start < beats;)
        {
            int end = start + 1;
            while (end < beats && path[end] == path[start]) end++;
            Chord? chord = path[start] == none ? null : chords[path[start]];
            if (chord is { } c)
            {
                var basses = Enumerable.Range(start, end - start).Select(i => bass[i]).Where(x => x is not null).GroupBy(x => x!.Value)
                    .OrderByDescending(g => g.Count()).FirstOrDefault();
                if (basses is not null && basses.Key != c.Root && c.PitchClasses.Contains(basses.Key) && basses.Count() * 2 > end - start)
                    chord = c with { Bass = basses.Key };
            }
            spans.Add(new ChordSpan(grid.ToSeconds(firstBeat + start), grid.ToSeconds(firstBeat + end), chord));
            start = end;
        }
        return spans;
    }

    /// <summary>
    /// Pitch-class profile of each beat (summing to 1, or all zero when silent), and its bass: the pitch
    /// class of the lowest note sounding for at least a quarter of the beat.
    /// </summary>
    static (double[][] Profiles, int?[] Bass) Profiles(IReadOnlyList<DetectedNote> notes, BarGrid grid, int firstBeat, int beats)
    {
        var profiles = new double[beats][];
        var bass = new int?[beats];
        var lowest = new int[beats];
        for (int i = 0; i < beats; i++) { profiles[i] = new double[12]; lowest[i] = int.MaxValue; }
        foreach (var note in notes)
        {
            int from = Math.Max(0, (int)Math.Floor(grid.ToBeats(note.Start)) - firstBeat);
            int to = Math.Min(beats - 1, (int)Math.Floor(grid.ToBeats(note.End)) - firstBeat);
            for (int i = from; i <= to; i++)
            {
                double a = grid.ToSeconds(firstBeat + i), b = grid.ToSeconds(firstBeat + i + 1);
                double overlap = Math.Min(b, note.End) - Math.Max(a, note.Start);
                if (overlap <= 0) continue;
                profiles[i][((note.Pitch % 12) + 12) % 12] += overlap * (0.5 + note.Amplitude);
                if (overlap >= 0.25 * (b - a) && note.Pitch < lowest[i])
                {
                    lowest[i] = note.Pitch;
                    bass[i] = ((note.Pitch % 12) + 12) % 12;
                }
            }
        }
        foreach (var p in profiles)
        {
            double sum = p.Sum();
            if (sum > 0) for (int k = 0; k < 12; k++) p[k] /= sum;
        }
        return (profiles, bass);
    }
}
