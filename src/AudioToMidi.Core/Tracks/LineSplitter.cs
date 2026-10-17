using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Tracks;

public enum MusicLine { Melody, Harmony, Low }

/// <summary>
/// Split one instrument's notes into three lines: top ("melody"), middle ("harmony") and low. This helps
/// when one track holds a whole section (Demucs puts strings, brass and organ all in "other").
/// </summary>
/// <remarks>
/// <see cref="Assign"/> keeps three lines that each follow a register. It follows the real parts more
/// closely than <see cref="AssignSkyline"/>, which takes the top of what is sounding as the melody and
/// lets it jump between registers.
/// </remarks>
public static class LineSplitter
{
    /// <summary>Notes starting within this many seconds of a group's first note count as starting together.</summary>
    const double Together = 0.04;

    /// <summary>A note still sounding this many seconds after a new group starts counts as held over it.</summary>
    const double StillHeld = 0.08;

    /// <summary>After this many seconds a held note counts as a pad, which other lines may move above or below.</summary>
    public const double PadSeconds = 1.2;

    /// <summary>Within this gap after a melody note, a drop of more than <see cref="MaxDrop"/> semitones is not the melody.</summary>
    const double GapSeconds = 0.3;
    const int MaxDrop = 9;

    /// <summary>
    /// Assign lines with the simpler "skyline" rules, kept for comparison. The top note of a group that
    /// starts together is the melody when nothing higher is sounding (except pads held longer than
    /// <see cref="PadSeconds"/>) and it is not a big drop straight after the last melody note. The bottom
    /// note is the low line when nothing lower is sounding, and the rest is harmony.
    /// </summary>
    public static MusicLine[] AssignSkyline(IReadOnlyList<DetectedNote> notes)
    {
        var lines = new MusicLine[notes.Count];
        Array.Fill(lines, MusicLine.Harmony);
        var order = Enumerable.Range(0, notes.Count)
            .OrderBy(i => notes[i].Start).ThenBy(i => notes[i].Pitch).ToList();

        int melody = -1, low = -1;
        var sounding = new List<int>();
        for (int g = 0; g < order.Count;)
        {
            double t = notes[order[g]].Start;
            int end = g;
            while (end < order.Count && notes[order[end]].Start - t <= Together) end++;
            var group = order.GetRange(g, end - g);
            group.Sort((a, b) => notes[a].Pitch.CompareTo(notes[b].Pitch));
            sounding.RemoveAll(i => notes[i].End <= t + StillHeld);

            bool IsPad(int i) => t - notes[i].Start >= PadSeconds;
            bool Blocks(int i) => i >= 0 && notes[i].End > t + StillHeld && !IsPad(i);
            int top = group[^1], bottom = group[0];

            // The melody is the top of what is sounding (ignoring pads). A big drop straight after the
            // melody's last note is usually an inner voice filling a gap, so it does not count.
            bool underSomething = sounding.Any(i => notes[i].Pitch > notes[top].Pitch && !IsPad(i));
            bool justEnded = melody >= 0 && notes[melody].End <= t + StillHeld && t - notes[melody].End < GapSeconds;
            bool bigDrop = justEnded && notes[melody].Pitch - notes[top].Pitch > MaxDrop;
            bool topIsMelody = !underSomething && !bigDrop;
            if (topIsMelody)
            {
                lines[top] = MusicLine.Melody;
                melody = top;
            }

            if (bottom != top || !topIsMelody)
            {
                bool lowest = sounding.All(i => notes[i].Pitch > notes[bottom].Pitch);
                bool overHeldLow = Blocks(low) && notes[low].Pitch < notes[bottom].Pitch;
                if (lowest && !overHeldLow)
                {
                    lines[bottom] = MusicLine.Low;
                    low = bottom;
                }
            }

            sounding.AddRange(group);
            g = end;
        }
        return lines;
    }

    /// <summary>How far each group of notes pulls a line's register towards them (0 to 1). Slow following
    /// works best, and still lets lines move when a section changes register.</summary>
    const double RegisterFollow = 0.05;

    /// <summary>Extra cost, in semitones, of putting a second note from one chord on the top or low line.
    /// It is small, so it only settles near ties (a four-part chord doubles up in the middle).</summary>
    const double SharePenalty = 2;

    /// <summary>
    /// Assign notes to three lines that each follow a register over time. Every note joins the line whose
    /// current register is nearest, notes that start together never cross (a higher note never goes to a
    /// lower line), and each line's register drifts towards the notes it takes. The lines start at three
    /// clusters of the track's pitches.
    /// </summary>
    public static MusicLine[] Assign(IReadOnlyList<DetectedNote> notes)
    {
        var lines = new MusicLine[notes.Count];
        if (notes.Count == 0) return lines;
        var pitches = notes.Select(n => (double)n.Pitch).Order().ToList();
        double Quantile(double q) => pitches[(int)Math.Clamp(q * (pitches.Count - 1), 0, pitches.Count - 1)];
        // Start from three pitch clusters, so a busy part does not drag every line towards it.
        double[] register = PitchClusters(pitches, [Quantile(5 / 6.0), Quantile(0.5), Quantile(1 / 6.0)]); // top, middle, low

        var order = Enumerable.Range(0, notes.Count).OrderBy(i => notes[i].Start).ThenByDescending(i => notes[i].Pitch).ToList();
        for (int g = 0; g < order.Count;)
        {
            double t = notes[order[g]].Start;
            int end = g;
            while (end < order.Count && notes[order[end]].Start - t <= Together) end++;
            var group = order.GetRange(g, end - g);
            group.Sort((a, b) => notes[b].Pitch.CompareTo(notes[a].Pitch)); // highest first

            // Assign in pitch order: note k (the k-th highest) never goes to a higher line than note k-1.
            int m = group.Count;
            var cost = new double[m, 3];
            var from = new int[m, 3];
            for (int k = 0; k < m; k++)
                for (int line = 0; line < 3; line++)
                {
                    double here = Math.Abs(notes[group[k]].Pitch - register[line]);
                    if (k == 0) { cost[k, line] = here; continue; }
                    // Doubling up within one chord costs extra on the top and low lines, which should stay single lines.
                    double Via(int prev) => cost[k - 1, prev] + (prev == line && line != 1 ? SharePenalty : 0);
                    int best = 0;
                    for (int prev = 1; prev <= line; prev++) if (Via(prev) < Via(best)) best = prev;
                    cost[k, line] = Via(best) + here;
                    from[k, line] = best;
                }
            int last = 0;
            for (int line = 1; line < 3; line++) if (cost[m - 1, line] < cost[m - 1, last]) last = line;
            var assigned = new int[m];
            for (int k = m - 1; k >= 0; k--) { assigned[k] = last; last = from[k, last]; }

            for (int line = 0; line < 3; line++)
            {
                var mine = Enumerable.Range(0, m).Where(k => assigned[k] == line).Select(k => (double)notes[group[k]].Pitch).ToList();
                if (mine.Count > 0) register[line] += RegisterFollow * (mine.Average() - register[line]);
            }
            register[1] = Math.Min(register[1], register[0] - 1);
            register[2] = Math.Min(register[2], register[1] - 1);
            for (int k = 0; k < m; k++)
                lines[group[k]] = assigned[k] switch { 0 => MusicLine.Melody, 1 => MusicLine.Harmony, _ => MusicLine.Low };
            g = end;
        }
        return lines;
    }

    /// <summary>Find three pitch clusters (1-D k-means), highest first, starting from <paramref name="start"/>.</summary>
    static double[] PitchClusters(List<double> pitches, double[] start)
    {
        var centres = (double[])start.Clone();
        for (int iteration = 0; iteration < 30; iteration++)
        {
            var sums = new double[3];
            var counts = new int[3];
            foreach (var p in pitches)
            {
                int best = 0;
                for (int c = 1; c < 3; c++) if (Math.Abs(p - centres[c]) < Math.Abs(p - centres[best])) best = c;
                sums[best] += p;
                counts[best]++;
            }
            for (int c = 0; c < 3; c++) if (counts[c] > 0) centres[c] = sums[c] / counts[c];
        }
        Array.Sort(centres);
        Array.Reverse(centres);
        return centres;
    }

    /// <summary>
    /// Split the notes on <paramref name="track"/>. The melody stays there, the harmony moves to
    /// <paramref name="harmonyTrack"/> and the low line to <paramref name="lowTrack"/>. Other tracks are untouched.
    /// </summary>
    public static List<DetectedNote> Split(IReadOnlyList<DetectedNote> notes, int track, int harmonyTrack, int lowTrack)
    {
        var mine = notes.Where(n => n.Track == track).ToList();
        var lines = Assign(mine);
        var result = notes.Where(n => n.Track != track).ToList();
        for (int i = 0; i < mine.Count; i++)
            result.Add(mine[i] with
            {
                Track = lines[i] switch { MusicLine.Harmony => harmonyTrack, MusicLine.Low => lowTrack, _ => track },
            });
        result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return result;
    }
}
