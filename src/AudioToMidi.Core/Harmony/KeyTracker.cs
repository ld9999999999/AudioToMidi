using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Harmony;

/// <summary>
/// Track the key over time. Score each bar against every key using the notes in the eight bars around it
/// (the same profiles as <see cref="KeyEstimator"/>). A Viterbi pass changes key only where the new key fits
/// clearly better for a while, so a borrowed chord or a short passage does not count as a modulation.
/// The eight-bar view places a change a bar or two late, so each change then moves to the bar that best
/// divides the two keys, judged bar by bar.
/// </summary>
public static class KeyTracker
{
    const int WindowBars = 8;
    /// <summary>Cost of a change of key, in correlation summed over bars: a new key must fit better for several bars.</summary>
    const double ChangeCost = 1.5;

    public static IReadOnlyList<KeySpan> Track(IReadOnlyList<DetectedNote> notes, BarGrid grid)
    {
        if (notes.Count == 0) return [];
        int perBar = grid.BeatsPerBar;
        int firstBar = (int)Math.Floor(grid.ToBeats(notes.Min(n => n.Start)) / perBar);
        int lastBar = (int)Math.Floor(grid.ToBeats(notes.Max(n => n.End)) / perBar);
        int bars = lastBar - firstBar + 1;

        // Pitch-class weight per bar, as KeyEstimator weighs notes.
        var perBarHistogram = new double[bars][];
        for (int i = 0; i < bars; i++) perBarHistogram[i] = new double[12];
        foreach (var n in notes)
        {
            int bar = Math.Clamp((int)Math.Floor(grid.ToBeats(n.Start) / perBar) - firstBar, 0, bars - 1);
            perBarHistogram[bar][((n.Pitch % 12) + 12) % 12] += Math.Min(n.Duration, 2.0) * (0.5 + n.Amplitude);
        }

        // Fit of each key around each bar.
        var fit = new double[bars][];
        for (int i = 0; i < bars; i++)
        {
            var window = new double[12];
            for (int j = Math.Max(0, i - WindowBars / 2); j < Math.Min(bars, i + WindowBars / 2); j++)
                for (int k = 0; k < 12; k++) window[k] += perBarHistogram[j][k];
            fit[i] = window.Any(w => w > 0) ? KeyEstimator.Fit(window) : new double[24];
        }

        var score = (double[])fit[0].Clone();
        var back = new int[bars, 24];
        var next = new double[24];
        for (int i = 1; i < bars; i++)
        {
            int best = 0;
            for (int k = 1; k < 24; k++) if (score[k] > score[best]) best = k;
            for (int k = 0; k < 24; k++)
            {
                bool stay = score[k] >= score[best] - ChangeCost;
                next[k] = (stay ? score[k] : score[best] - ChangeCost) + fit[i][k];
                back[i, k] = stay ? k : best;
            }
            (score, next) = (next, score);
        }
        var path = new int[bars];
        for (int k = 1; k < 24; k++) if (score[k] > score[path[bars - 1]]) path[bars - 1] = k;
        for (int i = bars - 1; i > 0; i--) path[i - 1] = back[i, path[i]];
        PlaceChanges(path, perBarHistogram);

        var spans = new List<KeySpan>();
        for (int start = 0; start < bars;)
        {
            int end = start + 1;
            while (end < bars && path[end] == path[start]) end++;
            var key = MusicalKey.FromTonic(path[start] % 12, minor: path[start] >= 12);
            spans.Add(new KeySpan(grid.ToSeconds((firstBar + start) * perBar), grid.ToSeconds((firstBar + end) * perBar), key));
            start = end;
        }
        return spans;
    }

    /// <summary>How far a change of key may move, in bars either way.</summary>
    const int Reach = 3;

    /// <summary>
    /// Move each change of key to the bar, within <see cref="Reach"/>, where the bars before fit the old
    /// key and the bars after the new one best, each bar on its own notes. Bars without notes don't count.
    /// </summary>
    static void PlaceChanges(int[] path, double[][] perBar)
    {
        var fits = perBar.Select(h => h.Any(w => w > 0) ? KeyEstimator.Fit(h) : null).ToArray();
        for (int i = 1; i < path.Length; i++)
        {
            if (path[i] == path[i - 1]) continue;
            int from = path[i - 1], to = path[i];
            // The runs of each key either side; each keeps at least one bar, so changes keep their order.
            int fromStart = i - 1, toEnd = i;
            while (fromStart > 0 && path[fromStart - 1] == from) fromStart--;
            while (toEnd < path.Length && path[toEnd] == to) toEnd++;
            int lo = Math.Max(i - Reach, fromStart + 1), hi = Math.Min(i + Reach, toEnd - 1);

            // Bars before the change in the old key, from it on in the new one.
            int best = i;
            double bestScore = double.MinValue;
            for (int split = lo; split <= hi; split++)
            {
                double total = 0;
                for (int b = lo - 1; b <= hi; b++)
                    if (fits[b] is { } f) total += b < split ? f[from] : f[to];
                if (total > bestScore + 1e-9) { bestScore = total; best = split; }
            }
            for (int b = lo; b <= hi; b++) path[b] = b < best ? from : to;
            i = Math.Max(i, best);
        }
    }

    /// <summary>The key at <paramref name="seconds"/>, or the nearest span's key outside them.</summary>
    public static MusicalKey KeyAt(IReadOnlyList<KeySpan> keys, double seconds) =>
        keys.Count == 0 ? new MusicalKey(0, false)
        : keys.FirstOrDefault(k => seconds >= k.Start && seconds < k.End)?.Key
          ?? (seconds < keys[0].Start ? keys[0].Key : keys[^1].Key);
}
