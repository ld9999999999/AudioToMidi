using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>How swung the eighths are (see <see cref="Dsp.Swing"/>).</summary>
public static class GrooveEstimator
{
    const double MaxSwing = 0.72;

    /// <summary>
    /// Find where the off-beat eighths fall in the beat, from 0.5 (straight) to 0.72. The music counts as
    /// swung when a swung grid of sixteenths (0, s/2, s and (1 + s)/2 of the beat) fits the onsets clearly
    /// better than a straight one. The amount then comes from the typical gap between a note on the beat and
    /// the next one on the same track, which cancels out how late each instrument's onsets are detected.
    /// Meant for simple meters; in 6/8 the eighths are the beat's thirds.
    /// </summary>
    public static double Swing(IReadOnlyList<DetectedNote> notes, TempoEstimate tempo)
    {
        if (notes.Count < 16) return Dsp.Swing.Straight;
        var map = tempo.Map;
        double sin = 0, cos = 0;
        foreach (var n in notes)
        {
            double b = map.ToBeat(n.Start), off = b - Math.Round(b);
            if (Math.Abs(off) > 0.15) continue;
            sin += Math.Sin(2 * Math.PI * off);
            cos += Math.Cos(2 * Math.PI * off);
        }
        double center = Math.Atan2(sin, cos) / (2 * Math.PI);
        var phases = notes.Select(n => (Phase: Frac(map.ToBeat(n.Start) - center), Weight: 0.5 + n.Amplitude)).ToList();

        const double Sigma = 0.03;
        double Fit(double s)
        {
            double[] grid = [0, s / 2, s, (1 + s) / 2, 1];
            double sum = 0;
            foreach (var (phase, weight) in phases)
            {
                double d = grid.Min(g => Math.Abs(phase - g));
                sum += weight * Math.Exp(-0.5 * d * d / (Sigma * Sigma));
            }
            return sum;
        }

        double straight = Fit(Dsp.Swing.Straight), best = Dsp.Swing.Straight, bestFit = straight;
        for (double s = 0.51; s <= MaxSwing + 1e-9; s += 0.01)
        {
            double fit = Fit(s);
            if (fit > bestFit) { bestFit = fit; best = s; }
        }
        double offbeat = phases.Where(p => p.Phase > 0.12 && p.Phase < 0.88).Sum(p => p.Weight);
        if (best < 0.54 || bestFit - straight < 0.08 * offbeat) return Dsp.Swing.Straight;

        // Measure the gap from a note on the beat to the next one in the same voice (same track, within a fifth).
        var gaps = new List<double>();
        foreach (var track in notes.GroupBy(n => n.Track))
        {
            var voice = track.Select(n => (Beat: map.ToBeat(n.Start), n.Pitch)).OrderBy(n => n.Beat).ToList();
            for (int i = 0; i < voice.Count; i++)
            {
                if (Math.Abs(voice[i].Beat - Math.Round(voice[i].Beat)) > 0.1) continue;
                for (int j = i + 1; j < voice.Count && voice[j].Beat - voice[i].Beat < MaxSwing + 0.06; j++)
                {
                    double gap = voice[j].Beat - voice[i].Beat;
                    if (gap <= 0.3 || Math.Abs(voice[j].Pitch - voice[i].Pitch) > 7) continue;
                    if (gap > 0.5) gaps.Add(gap);
                    break; // the next note of this voice
                }
            }
        }
        double swing = gaps.Count >= 12 ? gaps.Order().ElementAt(gaps.Count / 2) : best;
        return Math.Round(Math.Clamp(swing, 0.54, MaxSwing), 2);
    }

    static double Frac(double x) => x - Math.Floor(x);
}
