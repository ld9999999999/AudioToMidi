using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Dsp;

namespace AudioToMidi.Tests;

public class DspTests
{
    [Theory]
    [InlineData(1, 3)]
    [InlineData(10, 5)]
    [InlineData(100, 31)]
    [InlineData(7, 31)] // kernel larger than input
    public void SlidingMedian_matches_naive_reflected_median(int length, int kernel)
    {
        var rng = new Random(length * 31 + kernel);
        var x = Enumerable.Range(0, length).Select(_ => (float)Math.Round(rng.NextDouble() * 10)).ToArray(); // ties included
        var actual = new float[length];
        SlidingMedian.Apply(x, actual, kernel);

        int r = kernel / 2;
        for (int i = 0; i < length; i++)
        {
            var w = Enumerable.Range(i - r, kernel).Select(j => x[Reflect(j, length)]).OrderBy(v => v).ToArray();
            Assert.Equal(w[r], actual[i]);
        }

        static int Reflect(int i, int n)
        {
            int p = 2 * n;
            i = ((i % p) + p) % p;
            return i < n ? i : p - 1 - i;
        }
    }

    [Fact]
    public void Hpss_removes_drum_hits_and_keeps_tone()
    {
        int n = Synth.Rate * 4;
        var tone = new float[n];
        Synth.AddSine(tone, 330, 0, 4, 0.3);
        var mix = (float[])tone.Clone();
        for (double t = 0.25; t < 4; t += 0.5) Synth.AddHit(mix, t, 0.6);

        var result = Hpss.Separate(mix, Synth.Rate, margin: 2);

        double ErrorDb(float[] estimate) =>
            10 * Math.Log10(estimate.Zip(tone, (a, b) => (double)(a - b) * (a - b)).Sum() / tone.Sum(v => (double)v * v));

        double before = ErrorDb(mix), after = ErrorDb(result.Harmonic);
        Assert.True(after < before - 6, $"Expected >6 dB less drum energy: before {before:F1} dB, after {after:F1} dB");
        Assert.Equal(n, result.Harmonic.Length);
    }

    [Theory]
    [InlineData(128, 0.2)]
    [InlineData(95, 0.0)]
    [InlineData(174, 0.1)]
    [InlineData(140, 0.05)]
    [InlineData(100, 0.3)]
    public void Tempo_is_detected_from_percussive_part(double bpm, double offset)
    {
        var mix = new float[Synth.Rate * 12];
        Synth.AddBeat(mix, bpm, offset);
        for (int i = 0; i < 6; i++) Synth.AddTone(mix, 60 + i * 2, i * 2, 2); // some harmonic content too

        var sep = Hpss.Separate(mix, Synth.Rate);
        var tempo = TempoEstimator.Estimate(sep.OnsetEnvelope, sep.EnvelopeRate);

        // Half/double tempo is a legitimate reading of the same beat (e.g. 87 vs 174 for drum and bass).
        double ratio = new[] { 0.5, 1, 2 }.MinBy(r => Math.Abs(tempo.Bpm - bpm * r));
        Assert.InRange(tempo.Bpm, bpm * ratio - 1.5, bpm * ratio + 1.5);
        // The grid should land on the beats (any eighth note position is acceptable as phase).
        double half = 30 / bpm;
        double phaseError = Math.Abs(((tempo.FirstBeatSeconds - offset) % half + half) % half);
        Assert.True(Math.Min(phaseError, half - phaseError) < 0.03, $"Beat phase off by {phaseError:F3} s");
    }

    [Fact]
    public void WavReader_round_trips_float_and_reads_stereo_pcm16()
    {
        var path = Path.GetTempFileName();
        try
        {
            var audio = new AudioData([0f, 0.5f, -0.25f, 1f], 22050);
            WavReader.WriteFloat(path, audio);
            var read = WavReader.Read(path);
            Assert.Equal(audio.SampleRate, read.SampleRate);
            Assert.Equal(audio.Samples, read.Samples);

            // 16-bit stereo, two frames: (16384, -16384) and (32767, 32767)
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write("RIFF"u8); w.Write(36 + 8); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((ushort)1); w.Write((ushort)2);
                w.Write(44100); w.Write(44100 * 4); w.Write((ushort)4); w.Write((ushort)16);
                w.Write("data"u8); w.Write(8);
                w.Write((short)16384); w.Write((short)-16384); w.Write((short)32767); w.Write((short)32767);
            }
            var stereo = WavReader.Read(path);
            Assert.Equal(44100, stereo.SampleRate);
            Assert.Equal(0f, stereo.Samples[0], 4);
            Assert.Equal(32767 / 32768f, stereo.Samples[1], 4);
        }
        finally { File.Delete(path); }
    }
}
