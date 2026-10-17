using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>A time signature found in the notes.</summary>
/// <param name="BeatDivisor">
/// How many tracked beats make one beat of <see cref="Meter"/>: 2 when the beats were tracked on the
/// eighths of a 3/4 bar, 3 on the eighths of a 6/8 one, else 1.
/// </param>
/// <param name="Phase">With a divisor, which of every two or three tracked beats are the beats (see <see cref="TempoEstimate.WithBpm"/>).</param>
/// <param name="Description">Why, in a sentence.</param>
public sealed record MeterEstimate(Meter Meter, int BeatDivisor, int Phase, string Description)
{
    public static MeterEstimate Default { get; } = new(Meter.Common, 1, 0, "Too few notes to tell; 4/4.");

    /// <summary>Convert the tracked tempo to the meter's beat.</summary>
    public TempoEstimate Apply(TempoEstimate tempo) => BeatDivisor > 1 ? tempo.WithBpm(tempo.Bpm / BeatDivisor, Phase) : tempo;
}

/// <summary>What the notes say about the meter.</summary>
/// <param name="Thirds">The share of onsets at a third or two thirds of a beat.</param>
/// <param name="Halves">The share at a quarter, a half or three quarters of a beat.</param>
/// <param name="Features">Per feature, per bar length in beats (the index), the share of the feature's variation that the position in such a bar explains.</param>
public sealed record MeterEvidence(double Thirds, double Halves, IReadOnlyDictionary<string, double[]> Features)
{
    /// <summary>The share explained by bars of <paramref name="beats"/>, averaged over the features.</summary>
    public double Explained(int beats) => Features.Values.Average(f => f[beats]);

    /// <summary>
    /// True when beats divide in three: clearly more onsets fall at thirds than at halves and quarters, and
    /// both thirds are used. Onsets only at the second third are long-short pairs, which means swung eighths
    /// (see <see cref="GrooveEstimator"/>).
    /// </summary>
    public bool Compound => Thirds > 0.04 && Thirds > 0.6 * (Thirds + Halves) && FirstThird >= 0.25 * (Thirds - FirstThird);

    /// <summary>The share of onsets at the first third of a beat (the rest of <see cref="Thirds"/> is at the second).</summary>
    public double FirstThird { get; init; }
}

/// <summary>
/// Guess the time signature from notes on tracked beats. The number of beats in a bar comes from how much
/// of the change from beat to beat is explained by the position in a bar of 2, 3, 4, 6 or 8 beats: chord
/// changes, new chord tones, accents (long and low notes), bass onsets and the rhythm inside each beat all
/// repeat with the bar. Whether beats divide in two or three comes from where onsets fall between beats.
/// Duple bars in simple time are written as 4/4 (2/4 is not told apart), and 3/4 or a compound meter
/// needs clear evidence.
/// </summary>
public static class MeterEstimator
{
    static readonly int[] Lengths = [2, 3, 4, 6, 8];

    public static MeterEstimate Estimate(IReadOnlyList<DetectedNote> notes, TempoEstimate tempo)
    {
        if (notes.Count < 16) return MeterEstimate.Default;
        var e = Evidence(notes, tempo);
        double S(int beats) => e.Explained(beats);
        double duple = Math.Max(S(2), S(4));
        string division = e.Compound ? "beats divide in three" : "beats divide in two";

        // Bars of six tracked beats: the beats were the eighths, in threes of two (3/4) or twos of three (6/8).
        if (S(6) > 1.5 * Math.Max(S(3), duple) + 0.05 && S(6) > S(8))
        {
            bool threeFour = S(2) >= S(3);
            int divisor = threeFour ? 2 : 3;
            int phase = DownbeatEstimator.Estimate(notes, tempo, 6) % divisor;
            return new MeterEstimate(threeFour ? new Meter(3, 4) : new Meter(6, 8), divisor, phase,
                $"The music repeats every 6 tracked beats, in {(threeFour ? "threes of two: 3/4 at half" : "twos of three: 6/8 at a third of")} the tracked tempo.");
        }
        if (S(3) > duple + 0.03)
            return new MeterEstimate(e.Compound ? new Meter(9, 8) : new Meter(3, 4), 1, 0, $"The music repeats every 3 beats; {division}.");
        if (!e.Compound)
            return new MeterEstimate(Meter.Common, 1, 0,
                S(4) > 0.03 ? "The music repeats every 4 beats; beats divide in two." : "No clear pattern of 3 beats or of beats in three; 4/4.");
        bool two = S(2) >= S(4) - 0.01;
        return new MeterEstimate(two ? new Meter(6, 8) : new Meter(12, 8), 1, 0, $"The music repeats every {(two ? 2 : 4)} beats; {division}.");
    }

    public static MeterEvidence Evidence(IReadOnlyList<DetectedNote> notes, TempoEstimate tempo)
    {
        var map = tempo.Map;
        int first = (int)Math.Floor(map.ToBeat(notes.Min(n => n.Start)));
        int beats = Math.Max(1, (int)Math.Ceiling(map.ToBeat(notes.Max(n => n.End))) - first);

        // How beats divide: compare onsets at thirds of a beat with those at quarters and halves. Measure
        // from where onsets near the beat cluster, since transcribed onsets can sit a little off the beat.
        double sin = 0, cos = 0;
        foreach (var n in notes)
        {
            double b = map.ToBeat(n.Start), off = b - Math.Round(b);
            if (Math.Abs(off) > 0.15) continue;
            sin += Math.Sin(2 * Math.PI * off);
            cos += Math.Cos(2 * Math.PI * off);
        }
        double center = Math.Atan2(sin, cos) / (2 * Math.PI);
        double total = 0, firstThird = 0, secondThird = 0, halves = 0;
        double[] halvesAt = [0.25, 0.5, 0.75];
        foreach (var n in notes)
        {
            double b = map.ToBeat(n.Start) - center, phase = b - Math.Floor(b), w = 0.5 + n.Amplitude;
            total += w;
            if (Math.Abs(phase - 1 / 3.0) <= 0.04) firstThird += w;
            else if (Math.Abs(phase - 2 / 3.0) <= 0.04) secondThird += w;
            else if (halvesAt.Any(p => Math.Abs(phase - p) <= 0.04)) halves += w;
        }
        double thirds = firstThird + secondThird;

        // Per beat: chord changes, new chord tones, accents, bass onsets, and the rhythm inside the beat
        // (onsets at twelfths of the beat, with high and low notes kept apart).
        const int Slots = 12;
        double[][] Vectors(int size) => Enumerable.Range(0, beats).Select(_ => new double[size]).ToArray();
        var changes = Vectors(1);
        var novelty = Vectors(1);
        var accents = Vectors(1);
        var bass = Vectors(1);
        var rhythm = Vectors(2 * Slots);
        var chroma = Vectors(12);

        var chords = ChordRecognizer.Recognize(notes, new BarGrid(map, 1, 0));
        for (int i = 1; i < chords.Count; i++)
        {
            if (chords[i].Chord is null || chords[i - 1].Chord is null) continue;
            double start = map.ToBeat(chords[i].Start);
            int b = (int)Math.Round(start) - first;
            if (b >= 0 && b < beats) changes[b][0] += Math.Min(map.ToBeat(chords[i].End) - start, 4);
        }

        double beatSeconds = map.BeatSeconds;
        foreach (var n in notes)
        {
            double start = map.ToBeat(n.Start) - first, end = map.ToBeat(n.End) - first, w = 0.5 + n.Amplitude;
            int slot = (int)Math.Round(start * Slots), b = slot / Slots;
            if (b < 0 || b >= beats) continue;
            rhythm[b][(n.Pitch < 55 ? Slots : 0) + slot % Slots] += w;
            int on = (int)Math.Round(start);
            if (Math.Abs(start - on) <= 0.2 && on < beats)
            {
                accents[on][0] += Math.Min(n.Duration / beatSeconds, 4) * w * (n.Pitch < 55 ? 2 : 1);
                if (n.Pitch < 55) bass[on][0] += w;
            }
            for (int i = (int)Math.Floor(start); i <= Math.Min(beats - 1, (int)Math.Floor(end)); i++)
            {
                double overlap = Math.Min(i + 1, end) - Math.Max(i, start);
                if (overlap > 0) chroma[i][((n.Pitch % 12) + 12) % 12] += overlap * w;
            }
        }
        for (int i = 1; i < beats; i++) novelty[i][0] = 1 - Cosine(chroma[i - 1], chroma[i]);
        foreach (var r in rhythm)
        {
            double norm = Math.Sqrt(r.Sum(x => x * x));
            if (norm > 0) for (int k = 0; k < r.Length; k++) r[k] /= norm;
        }

        return new MeterEvidence(thirds / Math.Max(1e-9, total), halves / Math.Max(1e-9, total), new Dictionary<string, double[]>
        {
            ["chords"] = Explained(changes),
            ["novelty"] = Explained(novelty),
            ["accents"] = Explained(accents),
            ["bass"] = Explained(bass),
            ["rhythm"] = Explained(rhythm),
        }) { FirstThird = firstThird / Math.Max(1e-9, total) };
    }

    /// <summary>
    /// For each bar length, find the share of the variation in per-beat vectors that the position in the
    /// bar explains, adjusted for the number of positions. Work over windows of 48 beats, so a beat the
    /// tracker dropped or added upsets only one or two windows.
    /// </summary>
    static double[] Explained(double[][] x)
    {
        const int Window = 48, Hop = 24;
        var result = new double[Lengths.Max() + 1];
        var weights = new double[result.Length];
        int dims = x[0].Length;
        for (int start = 0; ; start += Hop)
        {
            int end = Math.Min(x.Length, start + Window), n = end - start;
            if (n < 16) break;
            var mean = new double[dims];
            for (int i = start; i < end; i++) for (int d = 0; d < dims; d++) mean[d] += x[i][d] / n;
            double totalSs = 0;
            for (int i = start; i < end; i++) for (int d = 0; d < dims; d++) totalSs += Sq(x[i][d] - mean[d]);
            if (totalSs > 1e-12)
            {
                foreach (int l in Lengths)
                {
                    double withinSs = 0;
                    for (int p = 0; p < l; p++)
                    {
                        var groupMean = new double[dims];
                        int count = 0;
                        for (int i = start + p; i < end; i += l, count++) for (int d = 0; d < dims; d++) groupMean[d] += x[i][d];
                        if (count == 0) continue;
                        for (int i = start + p; i < end; i += l) for (int d = 0; d < dims; d++) withinSs += Sq(x[i][d] - groupMean[d] / count);
                    }
                    double adjusted = 1 - withinSs / totalSs * (n - 1) / Math.Max(1, n - l);
                    result[l] += adjusted * totalSs;
                    weights[l] += totalSs;
                }
            }
            if (end == x.Length) break;
        }
        for (int l = 0; l < result.Length; l++) result[l] = weights[l] > 0 ? result[l] / weights[l] : 0;
        return result;
    }

    static double Sq(double x) => x * x;

    static double Cosine(double[] a, double[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 1;
    }
}
