namespace AudioToMidi.Tests;

/// <summary>Simple test signals.</summary>
static class Synth
{
    public const int Rate = 22050;

    public static double MidiToHz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);

    /// <summary>Piano-ish tone: a few harmonics with a quick attack and exponential decay.</summary>
    public static void AddTone(float[] buf, int midi, double start, double duration, double amp = 0.25)
    {
        double f = MidiToHz(midi);
        int s0 = (int)(start * Rate), n = (int)(duration * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, t / 0.01) * Math.Exp(-t * 1.5) * Math.Min(1, (duration - t) / 0.02);
            double v = Math.Sin(2 * Math.PI * f * t) + 0.5 * Math.Sin(4 * Math.PI * f * t) + 0.25 * Math.Sin(6 * Math.PI * f * t);
            buf[s0 + i] += (float)(amp * env * v / 1.75);
        }
    }

    /// <summary>Pure sine, constant level (for the monophonic YIN tests).</summary>
    public static void AddSine(float[] buf, double hz, double start, double duration, double amp = 0.3)
    {
        int s0 = (int)(start * Rate), n = (int)(duration * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, Math.Min(t, duration - t) / 0.005);
            buf[s0 + i] += (float)(amp * env * Math.Sin(2 * Math.PI * hz * t));
        }
    }

    /// <summary>Hi-hat/snare-like noise burst.</summary>
    public static void AddHit(float[] buf, double start, double amp = 0.5, int seed = 0)
    {
        var rng = new Random(seed + (int)(start * 1000));
        int s0 = (int)(start * Rate), n = (int)(0.08 * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
            buf[s0 + i] += (float)(amp * Math.Exp(-i / (0.015 * Rate)) * (rng.NextDouble() * 2 - 1));
    }

    /// <summary>Kick drum: a fast downward pitch sweep with a sharp decay.</summary>
    public static void AddKick(float[] buf, double start, double amp = 0.6)
    {
        int s0 = (int)(start * Rate), n = (int)(0.25 * Rate);
        double phase = 0;
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double f = 50 + 100 * Math.Exp(-t * 30);
            phase += 2 * Math.PI * f / Rate;
            buf[s0 + i] += (float)(amp * Math.Exp(-t * 18) * Math.Sin(phase));
        }
    }

    /// <summary>Snare drum: a short tone around 190 Hz (the drum) plus bright noise (the wires).</summary>
    public static void AddSnare(float[] buf, double start, double amp = 0.5)
    {
        var rng = new Random((int)(start * 1000) + 7);
        int s0 = (int)(start * Rate), n = (int)(0.2 * Rate);
        double prev = 0;
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double white = rng.NextDouble() * 2 - 1;
            double bright = white - prev; // first difference: tilts the noise towards high frequencies
            prev = white;
            double body = Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 30);
            buf[s0 + i] += (float)(amp * (0.6 * body + 0.5 * bright * Math.Exp(-t * 22)));
        }
    }

    /// <summary>Hi-hat: noise with almost nothing below a few kHz. Open hi-hats ring for a third of a second.</summary>
    public static void AddHiHat(float[] buf, double start, bool open = false, double amp = 0.3)
    {
        var rng = new Random((int)(start * 1000) + 13);
        int s0 = (int)(start * Rate), n = (int)((open ? 0.45 : 0.08) * Rate);
        double decay = open ? 0.12 : 0.012;
        double p1 = 0, p2 = 0, p3 = 0;
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double white = rng.NextDouble() * 2 - 1;
            double d1 = white - p1, d2 = d1 - p2, d3 = d2 - p3; // three differences: a steep high-pass
            p1 = white; p2 = d1; p3 = d2;
            buf[s0 + i] += (float)(amp * 0.25 * d3 * Math.Exp(-(double)i / Rate / decay));
        }
    }

    /// <summary>A rock-style beat: kick on 1 and 3, snare-ish on 2 and 4, hats on eighths.</summary>
    public static void AddBeat(float[] buf, double bpm, double offset = 0)
    {
        double beat = 60 / bpm;
        int i = 0;
        for (double t = offset; t < (double)buf.Length / Rate; t += beat / 2, i++)
        {
            AddHit(buf, t, 0.15, i);
            if (i % 4 == 0) AddKick(buf, t);
            if (i % 4 == 2) AddHit(buf, t, 0.5, i + 1000);
        }
    }
}
