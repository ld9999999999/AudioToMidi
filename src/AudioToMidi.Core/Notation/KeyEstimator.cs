using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>Estimate the key from how much each pitch class is heard (Krumhansl–Schmuckler with Krumhansl–Kessler profiles).</summary>
public static class KeyEstimator
{
    static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    public static MusicalKey Estimate(IEnumerable<DetectedNote> notes)
    {
        var histogram = new double[12];
        foreach (var n in notes)
            histogram[((n.Pitch % 12) + 12) % 12] += Math.Min(n.Duration, 2.0) * (0.5 + n.Amplitude);

        if (histogram.All(h => h == 0)) return new MusicalKey(0, false);

        var best = (Score: double.MinValue, Tonic: 0, Minor: false);
        for (int tonic = 0; tonic < 12; tonic++)
        {
            double major = Correlation(histogram, MajorProfile, tonic);
            double minor = Correlation(histogram, MinorProfile, tonic);
            if (major > best.Score) best = (major, tonic, false);
            if (minor > best.Score) best = (minor, tonic, true);
        }
        return MusicalKey.FromTonic(best.Tonic, best.Minor);
    }

    /// <summary>Score how well a pitch-class histogram fits each key, from -1 to 1: the 12 major keys by tonic, then the 12 minor ones.</summary>
    internal static double[] Fit(double[] histogram)
    {
        var fit = new double[24];
        for (int tonic = 0; tonic < 12; tonic++)
        {
            fit[tonic] = Correlation(histogram, MajorProfile, tonic);
            fit[12 + tonic] = Correlation(histogram, MinorProfile, tonic);
        }
        return fit;
    }

    static double Correlation(double[] histogram, double[] profile, int tonic)
    {
        double mh = histogram.Average(), mp = profile.Average();
        double num = 0, dh = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double h = histogram[(i + tonic) % 12] - mh;
            double p = profile[i] - mp;
            num += h * p; dh += h * h; dp += p * p;
        }
        return dh > 0 && dp > 0 ? num / Math.Sqrt(dh * dp) : 0;
    }
}
