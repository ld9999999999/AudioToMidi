namespace AudioToMidi.Core.Dsp;

/// <param name="Beats">Time of every beat in seconds, ascending.</param>
/// <param name="Bpm">Typical tempo (from the median beat spacing).</param>
/// <param name="Confidence">How periodic the onsets are, 0..1. Below ~0.1 the beats are a guess.</param>
public sealed record BeatTrack(IReadOnlyList<double> Beats, double Bpm, double Confidence);

/// <summary>
/// Find every beat from an onset-strength envelope, following tempo changes. First estimate the local
/// tempo with a tempogram (autocorrelation in sliding windows) and take the smoothest path through it that
/// still allows a real tempo change. Then cut the path into stretches of steady tempo and fit each with the
/// steady grid (tempo and phase) that lands most often on onsets. Kick drum counts extra, since it marks the beat.
/// </summary>
public static class BeatTracker
{
    const double MinBpm = 40, MaxBpm = 240, BinStep = 1.01; // tempo bins 1% apart
    const double WindowSeconds = 12, HopSeconds = 1;
    const int Harmonics = 4;
    /// <summary>Shortest stretch of steady tempo, in tempogram windows (seconds); shorter ones join a neighbour.</summary>
    const int MinStretch = 10;
    /// <summary>
    /// The onset envelope peaks slightly before the note is heard because its frames reach the attack
    /// early, so beats are moved this much later.
    /// </summary>
    public const double Latency = 0.027;

    /// <param name="lowEnvelope">Onset strength below 200 Hz (kick drum, bass), to tell beats from off-beats.</param>
    public static BeatTrack Track(float[] envelope, double rate, float[]? lowEnvelope = null)
    {
        var x = Normalize(envelope);
        var low = lowEnvelope is { Length: > 0 } ? Normalize(lowEnvelope) : null;
        var bins = TempoBins();
        var tempogram = Tempogram(x, rate, bins, out int hop);
        if (tempogram.Length == 0) return new BeatTrack([], 120, 0);
        var path = TempoPath(tempogram, bins);

        var frames = new List<double>();
        double previousPeriod = 0, periodicity = 0;
        foreach (var (start, end, bpm) in Stretches(path, bins, hop, x.Length))
        {
            var (period, phase) = FitGrid(x, low, start, end, 60 * rate / bpm);
            periodicity += Periodicity(envelope, start, end, period) * (end - start) / x.Length;
            var grid = new List<double>();
            for (double f = phase - 2 * period; f < end; f += period) grid.Add(f);
            if (frames.Count == 0) frames.AddRange(grid.Where(f => f >= start));
            else Join(frames, grid, previousPeriod, period);
            previousPeriod = period;
        }
        var beats = frames.Select(f => f / rate + Latency).ToList();
        double typical = beats.Count > 1 ? 60 / Median(beats.Zip(beats.Skip(1), (a, b) => b - a).ToList()) : bins[path[0]];
        return new BeatTrack(beats, Math.Round(typical, 1), Math.Round(Math.Clamp(periodicity, 0, 1), 3));
    }

    /// <summary>Split the path into stretches of steady tempo, in envelope frames [Start, End), each with its tempo.</summary>
    static List<(int Start, int End, double Bpm)> Stretches(int[] path, double[] bins, int hop, int length)
    {
        // Runs of windows within 3% of the run's first tempo.
        var runs = new List<(int From, int To)>(); // window indexes, To exclusive
        int from = 0;
        for (int w = 1; w <= path.Length; w++)
            if (w == path.Length || Math.Abs(path[w] - path[from]) > 3)
            {
                runs.Add((from, w));
                from = w;
            }
        // Short runs join the neighbour closest in tempo.
        int MedianBin((int From, int To) r) => path[r.From..r.To].Order().ElementAt((r.To - r.From) / 2);
        while (runs.Count > 1)
        {
            int shortest = Enumerable.Range(0, runs.Count).MinBy(i => runs[i].To - runs[i].From);
            if (runs[shortest].To - runs[shortest].From >= MinStretch) break;
            int bin = MedianBin(runs[shortest]);
            int neighbour = shortest == 0 ? 1
                : shortest == runs.Count - 1 ? shortest - 1
                : Math.Abs(MedianBin(runs[shortest - 1]) - bin) <= Math.Abs(MedianBin(runs[shortest + 1]) - bin) ? shortest - 1 : shortest + 1;
            int a = Math.Min(shortest, neighbour);
            runs[a] = (runs[a].From, runs[a + 1].To);
            runs.RemoveAt(a + 1);
        }
        // Window w is centred on envelope frame w * hop; a stretch reaches halfway to the next window.
        return runs.Select((r, i) => (
            Start: i == 0 ? 0 : r.From * hop - hop / 2,
            End: i == runs.Count - 1 ? length : r.To * hop - hop / 2,
            Bpm: bins[MedianBin(r)])).ToList();
    }

    /// <summary>
    /// Continue <paramref name="beats"/> with the next stretch's grid, which starts two beats early.
    /// Two grids fitted separately rarely meet exactly a beat apart, so join them at the pair of beats,
    /// one from each, whose gap is closest to the mean of the two beat lengths. That way no beat is
    /// dropped or squeezed in.
    /// </summary>
    static void Join(List<double> beats, List<double> next, double period, double nextPeriod)
    {
        double target = (period + nextPeriod) / 2;
        int bestKeep = beats.Count, bestFrom = 0;
        double bestCost = double.MaxValue;
        // End the earlier grid up to two beats before its end; start the next anywhere in its first four.
        for (int keep = Math.Max(1, beats.Count - 2); keep <= beats.Count; keep++)
            for (int from = 0; from < Math.Min(4, next.Count); from++)
            {
                double gap = next[from] - beats[keep - 1];
                if (gap < 0.5 * Math.Min(period, nextPeriod)) continue;
                double cost = Math.Abs(gap - target);
                if (cost < bestCost) { bestCost = cost; bestKeep = keep; bestFrom = from; }
            }
        beats.RemoveRange(bestKeep, beats.Count - bestKeep);
        beats.AddRange(next.Skip(bestFrom).Where(f => f > beats[^1]));
    }

    /// <summary>
    /// Find the steady grid over [start, end) whose beats land most on onsets, with a tempo within 2.5%
    /// of the estimate and any phase. Return the period in frames and the first beat's frame.
    /// </summary>
    static (double Period, double Phase) FitGrid(double[] x, double[]? low, int start, int end, double estimate)
    {
        double Score(double period, double phase)
        {
            double sum = 0;
            int count = 0;
            for (double pos = start + phase; pos < end - 1; pos += period, count++)
            {
                int i = (int)pos;
                double f = pos - i;
                sum += x[i] * (1 - f) + x[i + 1] * f;
                if (low is not null) sum += low[i] * (1 - f) + low[i + 1] * f;
            }
            return count > 0 ? sum / count : 0;
        }

        double bestPeriod = estimate, bestPhase = 0, best = double.MinValue;
        for (double period = estimate / 1.025; period <= estimate * 1.025; period *= 1.0005)
            for (double phase = 0; phase < period; phase += 0.25)
            {
                double s = Score(period, phase);
                if (s > best) { best = s; bestPeriod = period; bestPhase = phase; }
            }
        return (bestPeriod, start + bestPhase);
    }

    /// <summary>Onset strength in units of its standard deviation, with slow loudness changes removed.</summary>
    static double[] Normalize(float[] envelope)
    {
        int n = envelope.Length;
        var x = new double[n];
        if (n == 0) return x;
        // Subtract a 1 s moving average and keep what rises above it, which leaves the onsets.
        int half = 21;
        double sum = 0;
        int count = 0;
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + envelope[i];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(n, i + half + 1);
            x[i] = Math.Max(0, envelope[i] - (prefix[b] - prefix[a]) / (b - a));
            sum += x[i] * x[i];
            count++;
        }
        double std = Math.Sqrt(sum / Math.Max(1, count));
        if (std > 0) for (int i = 0; i < n; i++) x[i] /= std;
        return x;
    }

    static double[] TempoBins()
    {
        var bins = new List<double>();
        for (double b = MinBpm; b <= MaxBpm; b *= BinStep) bins.Add(b);
        return [.. bins];
    }

    /// <summary>Tempo salience per window (rows) and tempo bin (columns), each row scaled to a maximum of 1.</summary>
    static double[][] Tempogram(double[] x, double rate, double[] bins, out int hop)
    {
        int window = (int)(WindowSeconds * rate);
        hop = Math.Max(1, (int)(HopSeconds * rate));
        int maxLag = (int)Math.Ceiling(60 * rate / MinBpm * Harmonics) + 2;
        window = Math.Max(window, maxLag + 2);
        int frames = x.Length <= window ? (x.Length > maxLag + 8 ? 1 : 0) : (x.Length - window) / hop + 2;
        var rows = new double[frames][];
        int w = Math.Min(window, x.Length);
        int hopFrames = hop;
        Parallel.For(0, frames, f =>
        {
            // Windows are centred on frame f * hop, clamped to the envelope.
            int start = Math.Clamp(f * hopFrames - w / 2, 0, x.Length - w);
            double mean = 0;
            for (int i = 0; i < w; i++) mean += x[start + i];
            mean /= w;
            var acf = new double[Math.Min(maxLag + 2, w)];
            for (int lag = 0; lag < acf.Length; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < w; i++) s += (x[start + i] - mean) * (x[start + i + lag] - mean);
                acf[lag] = s / (w - lag);
            }
            double At(double lag)
            {
                int i = (int)lag;
                if (i + 1 >= acf.Length) return 0;
                double fr = lag - i;
                return acf[i] * (1 - fr) + acf[i + 1] * fr;
            }
            var row = new double[bins.Length];
            double max = 1e-12;
            for (int b = 0; b < bins.Length; b++)
            {
                double lag = 60 * rate / bins[b];
                double s = 0;
                for (int k = 1; k <= Harmonics; k++) s += At(lag * k);
                row[b] = Math.Max(0, s);
                max = Math.Max(max, row[b]);
            }
            for (int b = 0; b < bins.Length; b++) row[b] /= max;
            rows[f] = row;
        });
        return rows;
    }

    /// <summary>
    /// Pick a tempo bin per window that balances tempogram strength, a preference for moderate tempos,
    /// and steadiness. Small drifts cost little and a jump costs a fixed amount, so only a lasting
    /// tempo change is followed.
    /// </summary>
    static int[] TempoPath(double[][] tempogram, double[] bins)
    {
        int frames = tempogram.Length, n = bins.Length;
        const double PreferredBpm = 120, PriorOctaves = 1.0, DriftCost = 0.5, JumpCost = 6;
        var prior = bins.Select(b => { double o = Math.Log2(b / PreferredBpm) / PriorOctaves; return -0.5 * o * o; }).ToArray();
        double Emission(int f, int b) => Math.Log(0.02 + tempogram[f][b]) + prior[b];

        var score = new double[n];
        var back = new int[frames, n];
        for (int b = 0; b < n; b++) score[b] = Emission(0, b);
        var next = new double[n];
        for (int f = 1; f < frames; f++)
        {
            // Best previous state for a jump (any bin, fixed cost) vs a drift of one or two bins.
            int bestAny = 0;
            for (int b = 1; b < n; b++) if (score[b] > score[bestAny]) bestAny = b;
            for (int b = 0; b < n; b++)
            {
                double best = score[bestAny] - JumpCost;
                int from = bestAny;
                for (int d = -2; d <= 2; d++)
                {
                    int p = b + d;
                    if (p < 0 || p >= n) continue;
                    double s = score[p] - DriftCost * Math.Abs(d);
                    if (s > best) { best = s; from = p; }
                }
                next[b] = best + Emission(f, b);
                back[f, b] = from;
            }
            (score, next) = (next, score);
        }
        var path = new int[frames];
        int bestEnd = 0;
        for (int b = 1; b < n; b++) if (score[b] > score[bestEnd]) bestEnd = b;
        path[frames - 1] = bestEnd;
        for (int f = frames - 1; f > 0; f--) path[f - 1] = back[f, path[f]];
        return path;
    }

    /// <summary>
    /// Measure how strongly the onsets repeat every beat over [start, end): the autocorrelation at the beat
    /// period divided by the one at zero, as in <see cref="TempoEstimator"/>. Regular music scores 0.3 and up.
    /// </summary>
    static double Periodicity(float[] envelope, int start, int end, double period)
    {
        int n = end - start;
        if (n < period * 4) return 0;
        double mean = 0;
        for (int i = start; i < end; i++) mean += envelope[i];
        mean /= n;
        double At(int lag)
        {
            double sum = 0;
            for (int i = start; i + lag < end; i++) sum += (envelope[i] - mean) * (envelope[i + lag] - mean);
            return sum / (n - lag);
        }
        double zero = At(0);
        if (zero <= 0) return 0;
        int whole = (int)period;
        double f = period - whole;
        return (At(whole) * (1 - f) + At(whole + 1) * f) / zero;
    }

    static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
