namespace AudioToMidi.Core.Drums;

/// <summary>
/// Kick, snare and hi-hat activation and attack curves from <see cref="DrumTranscriber"/>. Picking
/// hits from them is cheap, so it runs again whenever <see cref="DrumSettings"/> change.
/// </summary>
public sealed class DrumAnalysis
{
    /// <summary>Curve order.</summary>
    public const int Kick = 0, Snare = 1, HiHat = 2;

    /// <summary>A hit's peak must be this many times the lowest point in the 70 ms before it. Hi-hats get
    /// a looser ratio because a closed hi-hat often lands while an open one is still ringing.</summary>
    static double RiseRatio(int drum) => drum == HiHat ? 1.5 : 2.5;

    /// <summary>A hit needs an attack of at least this share of the drum's strong attacks (99th percentile).</summary>
    const double AttackRelative = 0.1;

    /// <summary>How far before the activation peak to look for the attack, in frames (about 70 ms).</summary>
    const int AttackSearch = 6;

    /// <summary>A hit that overlaps the drum it could be leaking from needs at least this share of a typical loud hit.</summary>
    const double CrossTalk = 0.2;

    /// <summary>
    /// Treat a drum's typical loud hit as at least as loud as the mix gets in that drum's bands.
    /// In a song with drums this changes nothing; without drums it keeps faint leftovers from looking loud.
    /// </summary>
    const double MixFloor = 1.0;

    readonly float[][] _activations;
    readonly float[][] _attacks;
    readonly double[] _mixLevels;

    internal DrumAnalysis(float[][] activations, float[][] attacks, double[] mixLevels, double frameRate)
    {
        _activations = activations;
        _attacks = attacks;
        _mixLevels = mixLevels;
        FrameRate = frameRate;
    }

    /// <summary>Curve frames per second. Frame t is centred at t / FrameRate seconds.</summary>
    public double FrameRate { get; }

    public int FrameCount => _activations[0].Length;

    /// <summary>How strongly a drum sounds over time (<see cref="Kick"/>, <see cref="Snare"/> or <see cref="HiHat"/>).</summary>
    public IReadOnlyList<float> Activation(int drum) => _activations[drum];

    /// <summary>How sharply a drum's part of the spectrum jumps up at each frame.</summary>
    public IReadOnlyList<float> Attack(int drum) => _attacks[drum];

    public List<DrumHit> Decode(DrumSettings settings)
    {
        // Ignore peaks below this share of the drum's typical loud hit: 40% at sensitivity 0,
        // 13% at 0.5, 4% at 1.
        double relative = 0.4 * Math.Pow(0.1, Math.Clamp(settings.Sensitivity, 0, 1));
        var hits = new List<DrumHit>();

        var found = new List<(int Onset, int Peak)>[_activations.Length];
        var scales = new double[_activations.Length];
        for (int c = 0; c < _activations.Length; c++)
            found[c] = FindHits(c, relative, out scales[c], minGapSeconds: c == HiHat ? 0.045 : 0.07);

        // Cross-talk: a kick lights up the snare a little, and a snare's drum body lights up the kick.
        // When a kick and a snare land together, drop a weak one (below CrossTalk of its drum's typical
        // hit) if the other is relatively stronger.
        int near = (int)Math.Round(0.03 * FrameRate);
        double Relative(int c, (int Onset, int Peak) hit) => _activations[c][hit.Peak] / scales[c];
        bool Masked(int c, int other, (int Onset, int Peak) hit)
        {
            double r = Relative(c, hit);
            return r < CrossTalk && found[other].Any(o => Math.Abs(o.Onset - hit.Onset) <= near && Relative(other, o) > r);
        }
        var keptKicks = found[Kick].Where(h => !Masked(Kick, Snare, h)).ToList();
        var keptSnares = found[Snare].Where(h => !Masked(Snare, Kick, h)).ToList();
        found[Kick] = keptKicks;
        found[Snare] = keptSnares;

        for (int c = 0; c < _activations.Length; c++)
        {
            var a = _activations[c];
            var hitFrames = found[c];
            double scale = scales[c];
            for (int i = 0; i < hitFrames.Count; i++)
            {
                var (onset, peak) = hitFrames[i];
                var kind = c switch
                {
                    Kick => DrumKind.Kick,
                    Snare => DrumKind.Snare,
                    _ => DecaySeconds(a, peak, i + 1 < hitFrames.Count ? hitFrames[i + 1].Onset : a.Length) * 1000 >= settings.OpenHiHatMs
                        ? DrumKind.OpenHiHat
                        : DrumKind.ClosedHiHat,
                };
                float strength = (float)Math.Sqrt(Math.Min(1, a[peak] / scale));
                hits.Add(new DrumHit(onset / FrameRate, kind, strength));
            }
        }

        hits.Sort((x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.Kind.CompareTo(y.Kind));
        return hits;
    }

    /// <summary>
    /// Find the hits of one drum as (attack frame, activation peak frame). A hit is an activation peak that
    /// <list type="bullet">
    /// <item>reaches <paramref name="relative"/> of <paramref name="scale"/>, the drum's typical loud hit;</item>
    /// <item>rises well above the curve just before it, so a wobble on a decaying tail does not count;</item>
    /// <item>comes with a real attack in the drum's bands just before it, so energy that the
    /// factorisation shuffles between drums does not count;</item>
    /// <item>keeps at least the minimum gap from any stronger hit.</item>
    /// </list>
    /// </summary>
    List<(int Onset, int Peak)> FindHits(int drum, double relative, out double scale, double minGapSeconds)
    {
        var a = _activations[drum];
        var attack = _attacks[drum];

        const int radius = 2;
        var candidates = new List<int>();
        for (int t = 1; t < a.Length; t++)
        {
            if (a[t] <= a[t - 1]) continue;
            bool isMax = true;
            for (int j = Math.Max(0, t - radius); j <= Math.Min(a.Length - 1, t + radius) && isMax; j++)
                if (a[j] > a[t]) isMax = false;
            if (isMax) candidates.Add(t);
        }
        if (candidates.Count == 0) { scale = 1; return []; }

        scale = Math.Max(1e-12, Math.Max(Percentile(candidates.Select(t => a[t]), 0.95), MixFloor * _mixLevels[drum]));
        double threshold = relative * scale;
        double attackThreshold = AttackRelative * Math.Max(1e-12, Percentile(attack, 0.99));

        int lookBack = (int)Math.Ceiling(0.07 * FrameRate);
        int minGap = (int)Math.Round(minGapSeconds * FrameRate);
        var hits = new List<(int Onset, int Peak)>();
        foreach (int t in candidates)
        {
            if (a[t] < threshold) continue;
            float before = a[t];
            for (int j = Math.Max(0, t - lookBack); j < t; j++) before = Math.Min(before, a[j]);
            if (a[t] - before < 0.5 * threshold || a[t] < RiseRatio(drum) * before) continue;

            int onset = t;
            for (int j = Math.Max(1, t - AttackSearch); j <= t; j++)
                if (attack[j] > attack[onset]) onset = j;
            if (attack[onset] < attackThreshold) continue;

            if (hits.Count > 0 && onset - hits[^1].Onset < minGap)
            {
                if (a[t] > a[hits[^1].Peak]) hits[^1] = (onset, t);
                continue;
            }
            hits.Add((onset, t));
        }
        return hits;
    }

    static double Percentile(IEnumerable<float> values, double p)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted.Count == 0 ? 0 : sorted[(int)Math.Floor(p * (sorted.Count - 1))];
    }

    /// <summary>
    /// Measure how long the curve takes to fall most of the way (to 37%) from peak <paramref name="p"/>
    /// towards the lowest point before the next hit. Measuring above that floor keeps a cymbal ringing
    /// underneath from making every hi-hat look open.
    /// </summary>
    double DecaySeconds(float[] a, int p, int next)
    {
        int limit = Math.Min(next, p + (int)(0.6 * FrameRate));
        if (limit <= p + 1) return 0;
        float floor = a[p];
        for (int t = p + 1; t < limit; t++) floor = Math.Min(floor, a[t]);
        float target = floor + 0.37f * (a[p] - floor);
        for (int t = p + 1; t < limit; t++)
            if (a[t] <= target) return (t - p) / FrameRate;
        return (limit - p) / FrameRate;
    }
}
