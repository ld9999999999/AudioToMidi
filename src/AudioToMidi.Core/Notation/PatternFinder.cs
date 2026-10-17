using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>A note of a <see cref="BarPattern"/>, in beats from the pattern's start.</summary>
public readonly record struct PatternNote(double Beat, double Length, int Pitch, int Velocity);

/// <summary>A stretch of bars a part plays again and again: a clip to loop.</summary>
/// <param name="Bars">How many bars it lasts (1, 2 or 4).</param>
/// <param name="Occurrences">The bar each repeat starts on (0-based from bar 1), in order.</param>
/// <param name="Notes">The most typical repeat's notes, on the grid, in beats from its start.</param>
public sealed record BarPattern(int Bars, IReadOnlyList<int> Occurrences, IReadOnlyList<PatternNote> Notes);

/// <summary>
/// Find the bar patterns of one part. Bars are compared by their notes on a grid of sixteenths, where a
/// note matches one of the same pitch within a step. A stretch of 1, 2 or 4 bars that starts on a multiple
/// of its length from bar 1 joins the pattern with the repeat most like it, if they are alike enough.
/// The length kept is the one that needs the fewest distinct bars to rebuild the part (a riff of two
/// different bars is one 2-bar pattern), the longer one on a tie, unless its halves are alike (a bar
/// played again and again, with a fill now and then, is a 1-bar pattern).
/// </summary>
public static class PatternFinder
{
    /// <summary>
    /// How alike two stretches must be to count as repeats: matched notes over all notes (an F-measure).
    /// It is set lower than clean MIDI would need, because transcription misses some notes and adds others.
    /// </summary>
    public const double MinSimilarity = 0.65;

    static readonly int[] Lengths = [1, 2, 4];

    public static IReadOnlyList<BarPattern> Find(IReadOnlyList<DetectedNote> notes, BarGrid grid, int stepsPerBeat = 4)
    {
        if (notes.Count == 0) return [];
        int barSteps = grid.BeatsPerBar * stepsPerBeat;
        int bars = (int)Math.Ceiling(notes.Max(n => grid.ToBeats(n.Start)) / grid.BeatsPerBar + 1e-9);
        if (bars < 2) return [];

        // Each bar's notes as (step in the bar, pitch), with the notes for the clip.
        var steps = new List<(int Step, int Pitch)>[bars];
        var barNotes = new List<PatternNote>[bars];
        for (int b = 0; b < bars; b++) { steps[b] = []; barNotes[b] = []; }
        foreach (var n in notes)
        {
            int step = (int)Math.Round(grid.ToBeats(n.Start) * stepsPerBeat);
            int bar = (int)Math.Floor((double)step / barSteps);
            if (bar < 0 || bar >= bars) continue;
            int length = Math.Max(1, (int)Math.Round(grid.ToBeats(n.End) * stepsPerBeat) - step);
            steps[bar].Add((step - bar * barSteps, n.Pitch));
            barNotes[bar].Add(new PatternNote((double)(step - bar * barSteps) / stepsPerBeat, (double)length / stepsPerBeat, n.Pitch, n.Velocity));
        }

        // Per length, group the stretches into repeats; keep the length that needs the fewest distinct bars.
        var byLength = new Dictionary<int, List<List<int>>>();
        (int Length, List<List<int>> Groups)? best = null;
        int bestCost = int.MaxValue;
        foreach (int length in Lengths)
        {
            if (length * 2 > bars) break;
            var groups = new List<List<int>>();
            for (int start = 0; start + length <= bars; start += length)
            {
                var events = Stretch(steps, start, length, barSteps);
                if (events.Count == 0) continue; // silence is not a pattern
                // The group with the repeat most like this one, if any is alike enough.
                var group = groups.Select(g => (Group: g, Score: g.Max(m => Similarity(Stretch(steps, m, length, barSteps), events))))
                    .Where(x => x.Score >= MinSimilarity).OrderByDescending(x => x.Score).Select(x => x.Group).FirstOrDefault();
                if (group is null) groups.Add([start]);
                else group.Add(start);
            }
            byLength[length] = groups;
            int cost = groups.Count * length;
            if (cost <= bestCost) { bestCost = cost; best = (length, groups); }
        }
        if (best is not { } chosen) return [];
        // A pattern whose halves are alike is the shorter one played twice (with a fill, say): keep that.
        while (chosen.Length > 1 && chosen.Groups.Where(g => g.Count >= 2).All(g =>
                   Similarity(Stretch(steps, g[0], chosen.Length / 2, barSteps), Stretch(steps, g[0] + chosen.Length / 2, chosen.Length / 2, barSteps)) >= MinSimilarity))
            chosen = (chosen.Length / 2, byLength[chosen.Length / 2]);

        var patterns = new List<BarPattern>();
        foreach (var group in chosen.Groups.Where(g => g.Count >= 2))
        {
            // The most typical repeat: the one most like all the others.
            int typical = group.MaxBy(a => group.Sum(b => Similarity(Stretch(steps, a, chosen.Length, barSteps), Stretch(steps, b, chosen.Length, barSteps))));
            var clip = Enumerable.Range(0, chosen.Length)
                .SelectMany(i => barNotes[typical + i].Select(n => n with { Beat = n.Beat + i * grid.BeatsPerBar }))
                .OrderBy(n => n.Beat).ThenBy(n => n.Pitch).ToList();
            patterns.Add(new BarPattern(chosen.Length, group, clip));
        }
        return patterns;
    }

    /// <summary>The notes of <paramref name="length"/> bars from <paramref name="start"/>, as (step from the start, pitch).</summary>
    static List<(int Step, int Pitch)> Stretch(List<(int Step, int Pitch)>[] bars, int start, int length, int barSteps)
    {
        var result = new List<(int, int)>();
        for (int i = 0; i < length && start + i < bars.Length; i++)
            foreach (var (step, pitch) in bars[start + i]) result.Add((step + i * barSteps, pitch));
        return result;
    }

    /// <summary>Match notes one to one (same pitch, within a step) and return the F-measure of the two stretches.</summary>
    static double Similarity(List<(int Step, int Pitch)> a, List<(int Step, int Pitch)> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var used = new bool[b.Count];
        int matched = 0;
        foreach (var (step, pitch) in a)
            for (int j = 0; j < b.Count; j++)
                if (!used[j] && b[j].Pitch == pitch && Math.Abs(b[j].Step - step) <= 1) { used[j] = true; matched++; break; }
        return 2.0 * matched / (a.Count + b.Count);
    }
}
