namespace AudioToMidi.Core.Dsp;

/// <param name="Bpm">Estimated tempo; the typical one when <see cref="Beats"/> are listed.</param>
/// <param name="FirstBeatSeconds">Time of the first beat.</param>
/// <param name="Confidence">How periodic the onsets are, 0..1. Below ~0.1 the estimate is a guess.</param>
public sealed record TempoEstimate(double Bpm, double FirstBeatSeconds, double Confidence)
{
    public double BeatSeconds => 60.0 / Bpm;
    public static TempoEstimate Default { get; } = new(120, 0, 0);

    /// <summary>
    /// Every beat, when they were tracked (the tempo may change); null for a steady tempo of
    /// <see cref="Bpm"/> from <see cref="FirstBeatSeconds"/>.
    /// </summary>
    public IReadOnlyList<double>? Beats { get; init; }

    /// <summary>Where the beats are. Each call builds a new map, so keep one around when converting many notes.</summary>
    public BeatMap Map => Beats is { Count: >= 2 } beats ? new BeatMap(beats) : BeatMap.Steady(FirstBeatSeconds, BeatSeconds);

    /// <summary>Build an estimate from the beats found by <see cref="BeatTracker"/>.</summary>
    public static TempoEstimate From(BeatTrack track) => track.Beats.Count >= 2
        ? new TempoEstimate(track.Bpm, track.Beats[0], track.Confidence) { Beats = track.Beats }
        : new TempoEstimate(track.Bpm, 0, track.Confidence);

    /// <summary>
    /// Return the same beats at another tempo, for example one the user typed in
    /// (see <see cref="BeatMap.WithBpm"/>).
    /// </summary>
    /// <param name="phase">With half or a third of the tempo, which of every two or three beats to keep (see <see cref="BeatMap.WithBpm"/>).</param>
    public TempoEstimate WithBpm(double bpm, int phase = 0)
    {
        if (Math.Abs(bpm - Bpm) < 1e-9) return this;
        var map = Map.WithBpm(bpm, phase);
        return this with { Bpm = bpm, FirstBeatSeconds = map.Beats[0], Beats = map.Beats.Count > 2 ? map.Beats : null };
    }
}

/// <summary>Estimate one steady tempo for the whole envelope. See <see cref="BeatTracker"/> for tempo that changes.</summary>
public static class TempoEstimator
{
    const int CombHarmonics = 4;

    public static TempoEstimate Estimate(float[] envelope, double envelopeRate,
        double minBpm = 60, double maxBpm = 200, double preferredBpm = 120)
    {
        int n = envelope.Length;
        double maxLag = 60 * envelopeRate / minBpm;
        double minLag = 60 * envelopeRate / maxBpm;
        int acfLength = (int)Math.Ceiling(maxLag * CombHarmonics) + 2;
        if (n < acfLength + 8) return TempoEstimate.Default;

        // Light smoothing makes peaks at fractional lags visible to integer-lag autocorrelation.
        var x = Smooth(envelope);
        double mean = x.Average();
        for (int i = 0; i < n; i++) x[i] -= (float)mean;

        var acf = new double[acfLength];
        for (int lag = 0; lag < acfLength; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < n; i++) s += x[i] * x[i + lag];
            acf[lag] = s / (n - lag); // unbiased
        }
        if (acf[0] <= 0) return TempoEstimate.Default;

        double AcfAt(double lag)
        {
            int i = (int)lag;
            double f = lag - i;
            return acf[i] * (1 - f) + acf[i + 1] * f;
        }

        // A true beat period also lines up at 2, 3 and 4 beats; a wrong one (e.g. 1.5 beats) does not.
        // The log-normal prior around the preferred tempo breaks ties between tempo octaves.
        double bestLag = minLag, bestScore = double.MinValue;
        for (double lag = minLag; lag <= maxLag; lag += 0.05)
        {
            double comb = 0;
            for (int k = 1; k <= CombHarmonics; k++) comb += AcfAt(lag * k);
            double octaves = Math.Log2(60 * envelopeRate / lag / preferredBpm);
            double score = comb * Math.Exp(-0.5 * octaves * octaves);
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }

        // Fine-tune tempo and phase together so the beat grid stays on the onsets across the whole
        // file; a small tempo error otherwise drifts the grid by a beat or more over a few minutes.
        double coarseBpm = 60 * envelopeRate / bestLag;
        double bpm = coarseBpm, bestPhase = 0, bestFit = double.MinValue;
        for (double candidate = coarseBpm * 0.985; candidate <= coarseBpm * 1.015; candidate += 0.05)
        {
            double period = 60 * envelopeRate / candidate;
            for (double phase = 0; phase < period; phase += 0.25)
            {
                double fit = GridScore(envelope, phase, period);
                if (fit > bestFit) { bestFit = fit; bpm = candidate; bestPhase = phase; }
            }
        }

        double confidence = Math.Clamp(AcfAt(bestLag) / acf[0], 0, 1);
        return new TempoEstimate(Math.Round(bpm, 1), bestPhase / envelopeRate, confidence);
    }

    /// <summary>Mean onset strength sampled at every beat position of the grid.</summary>
    static double GridScore(float[] envelope, double phase, double period)
    {
        double sum = 0;
        int count = 0;
        for (double pos = phase; pos < envelope.Length - 1; pos += period, count++)
        {
            int i = (int)pos;
            double f = pos - i;
            sum += envelope[i] * (1 - f) + envelope[i + 1] * f;
        }
        return count > 0 ? sum / count : 0;
    }

    static float[] Smooth(float[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            float a = i > 0 ? x[i - 1] : x[i], b = x[i], c = i < x.Length - 1 ? x[i + 1] : x[i];
            y[i] = 0.25f * a + 0.5f * b + 0.25f * c;
        }
        return y;
    }
}
