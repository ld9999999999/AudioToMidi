using AudioToMidi.Core.Synth;
using NWaves.Features;

namespace AudioToMidi.Tests;

public class SynthTests
{
    const int Rate = 44100;

    static float[] Render(PolySynth synth, double seconds, int block = 64)
    {
        var output = new float[(int)(seconds * Rate)];
        for (int i = 0; i < output.Length; i += block)
            synth.Render(output.AsSpan(i, Math.Min(block, output.Length - i)));
        return output;
    }

    static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(69)]
    [InlineData(84)]
    public void Oscillators_are_in_tune(int midi)
    {
        // Without the sub oscillator the waveform repeats at the note's own period.
        var synth = new PolySynth(Rate) { Patch = SynthPatch.Organ with { SubLevel = 0 } };
        synth.NoteOn(midi, 0.8f);
        var audio = Render(synth, 0.5);

        double expected = 440 * Math.Pow(2, (midi - 69) / 12.0);
        float f0 = Pitch.FromYin(audio, Rate, Rate / 4, Rate / 4 + 4096, (float)(expected / 1.5), (float)(expected * 1.5), 0.25f);
        Assert.InRange(1200 * Math.Log2(f0 / expected), -30, 30);
    }

    [Fact]
    public void Every_patch_sounds_at_the_written_octave()
    {
        // The sub oscillator adds weight but must stay quieter than the note itself,
        // or the preview would sound an octave low.
        static double Goertzel(float[] x, int from, int n, double hz)
        {
            double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = from; i < from + n; i++) { double s0 = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / n)) + c * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2);
        }
        foreach (var patch in SynthPatch.All)
            foreach (var midi in new[] { 48, 60, 72 })
            {
                var synth = new PolySynth(Rate) { Patch = patch };
                synth.NoteOn(midi, 0.8f);
                var audio = Render(synth, 1.0);
                double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
                // A long window spans several beat cycles of the detuned oscillators (their chorus).
                const int n = 32768;
                double note = Goertzel(audio, Rate / 4, n, f), sub = Goertzel(audio, Rate / 4, n, f / 2);
                Assert.True(note > sub * 1.5, $"{patch.Name} at {midi}: note {note:F1} vs sub-octave {sub:F1}");
            }
    }

    [Fact]
    public void Released_notes_fade_to_silence_and_free_their_voice()
    {
        var synth = new PolySynth(Rate);
        synth.NoteOn(60);
        var held = Render(synth, 0.3);
        Assert.Equal(1, synth.ActiveVoices);
        Assert.True(Rms(held.AsSpan(Rate / 10)) > 0.01);

        synth.NoteOff(60);
        var tail = Render(synth, 1.0);
        Assert.True(Rms(tail.AsSpan(tail.Length - 4410)) < 1e-4, "release tail should end in silence");
        Assert.Equal(0, synth.ActiveVoices);
    }

    [Fact]
    public void Voices_are_limited_and_output_stays_bounded()
    {
        var synth = new PolySynth(Rate, maxVoices: 8) { Volume = 1 };
        foreach (var p in Enumerable.Range(36, 30)) synth.NoteOn(p, 1f); // 30 notes, 8 voices
        var audio = Render(synth, 0.5);

        Assert.Equal(8, synth.ActiveVoices);
        Assert.All(audio, v => Assert.InRange(v, -1f, 1f));
        Assert.DoesNotContain(audio, float.IsNaN);

        synth.AllNotesOff();
        Render(synth, 1.5);
        Assert.Equal(0, synth.ActiveVoices);
    }

    [Fact]
    public void Velocity_changes_loudness_and_every_patch_renders()
    {
        double Level(float velocity)
        {
            var s = new PolySynth(Rate);
            s.NoteOn(60, velocity);
            return Rms(Render(s, 0.2).AsSpan(Rate / 20));
        }
        Assert.True(Level(1f) > Level(0.2f) * 1.5);

        foreach (var patch in SynthPatch.All)
        {
            var s = new PolySynth(Rate) { Patch = patch };
            s.NoteOn(57); s.NoteOn(64);
            var audio = Render(s, 0.8);
            Assert.True(Rms(audio.AsSpan(0, Rate / 4)) > 0.005, $"{patch.Name} is silent"); // plucks decay fast
            Assert.DoesNotContain(audio, float.IsNaN);
        }
    }
}
