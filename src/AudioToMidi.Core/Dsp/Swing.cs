namespace AudioToMidi.Core.Dsp;

/// <summary>
/// Swung time within a beat. A swing of 0.5 is straight; at 0.6 the off-beat eighth falls 60% of the
/// way through the beat, at 2/3 on its last third (a triplet shuffle). Sixteenths stay evenly spaced
/// within each of the two unequal halves. Straight positions are where the notes are written (straight
/// eighths, marked "swing"); swung positions are where they sound.
/// </summary>
public static class Swing
{
    public const double Straight = 0.5;

    /// <summary>True when a grid of <paramref name="divisions"/> steps per beat swings. Only even grids (halves, quarters, eighths) swing, so triplets stay straight.</summary>
    public static bool Applies(int divisions, double swing) => divisions >= 2 && divisions % 2 == 0 && Math.Abs(swing - Straight) > 1e-6;

    /// <summary>Convert a straight position in beats to where it sounds with <paramref name="swing"/>.</summary>
    public static double ToSwung(double beats, double swing)
    {
        double whole = Math.Floor(beats), f = beats - whole;
        return whole + (f < 0.5 ? f * 2 * swing : swing + (f - 0.5) * 2 * (1 - swing));
    }

    /// <summary>Convert a sounding position in beats back to its straight one (the inverse of <see cref="ToSwung"/>).</summary>
    public static double ToStraight(double beats, double swing)
    {
        double whole = Math.Floor(beats), f = beats - whole;
        return whole + (f < swing ? f * 0.5 / swing : 0.5 + (f - swing) * 0.5 / (1 - swing));
    }
}
