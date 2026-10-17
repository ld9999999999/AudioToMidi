using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Instruments;
using Xunit.Abstractions;

namespace AudioToMidi.Tests;

public class InstrumentTests(ITestOutputHelper output)
{
    static float[] Tone(double seconds, int rate, params int[] midi)
    {
        var x = new float[(int)(seconds * rate)];
        foreach (var m in midi)
        {
            double f = 440 * Math.Pow(2, (m - 69) / 12.0);
            for (int i = 0; i < x.Length; i++)
            {
                double t = (double)i / rate;
                x[i] += (float)(0.2 * Math.Exp(-(t % 1.0) * 2) * (Math.Sin(2 * Math.PI * f * t) + 0.4 * Math.Sin(4 * Math.PI * f * t)));
            }
        }
        return x;
    }

    [Fact]
    public void YamNet_class_map_and_frames()
    {
        Assert.Equal(521, YamNet.ClassNames.Count);
        Assert.Equal("Speech", YamNet.ClassNames[0]);
        Assert.Equal("Music", YamNet.ClassNames[132]);
        Assert.Contains("Electric piano", YamNet.ClassNames);

        using var yam = new YamNet();
        var result = yam.Run(Tone(5, YamNet.SampleRate, 60, 64, 67));
        Assert.InRange(result.Frames, 9, 11); // one frame per 0.48 s
        Assert.All(result.Scores, f => Assert.Equal(521, f.Length));
        Assert.All(result.Embeddings, f => Assert.Equal(1024, f.Length));
        Assert.NotNull(result.Pool(o => o.Embeddings));

        // Silence has nothing to pool.
        Assert.Null(yam.Run(new float[YamNet.SampleRate * 2]).Pool(o => o.Embeddings));
    }

    [Fact]
    public void Head_outputs_a_distribution_over_the_classes()
    {
        var head = InstrumentHead.LoadEmbedded();
        Assert.NotNull(head);
        using var yam = new YamNet();
        var r = yam.Run(Tone(4, YamNet.SampleRate, 48, 55, 64));
        var p = head!.Predict(r.Pool(o => o.Embeddings)!, r.Pool(o => o.Scores)!);
        Assert.Equal(InstrumentClasses.All.Count, p.Length);
        Assert.Equal(1.0, p.Sum(), 3);
        Assert.All(p, v => Assert.InRange(v, 0, 1));
    }

    [Fact]
    public void Suggestions_respect_the_track_role()
    {
        using var recognizer = new InstrumentRecognizer();
        var bass = recognizer.Recognize(Tone(6, 22050, 36), 22050, TrackRole.Bass);
        Assert.NotNull(bass);
        Assert.Contains(bass!.Program, new[] { 32, 33, 38 }); // acoustic, electric or synth bass

        var keys = recognizer.Recognize(Tone(6, 22050, 60, 64, 67), 22050, TrackRole.Melodic);
        Assert.NotNull(keys);
        Assert.DoesNotContain(keys!.Program, new[] { 32, 33, 38 });
        Assert.InRange(keys.Confidence, 0, 1);

        Assert.Null(recognizer.Recognize(new float[22050 * 3], 22050, TrackRole.Any)); // silence
    }

    /// <summary>Smoke test on real recordings, for machines with GarageBand's loops installed.</summary>
    [Theory]
    [InlineData("09 Disco Funk/Shining Star Electric Piano 01.caf", TrackRole.Melodic, "Electric Piano")]
    [InlineData("09 Disco Funk/Hot Pursuit Bass 01.caf", TrackRole.Bass, "Electric Bass")]
    [InlineData("09 Disco Funk/Neon Light Guitar 01.caf", TrackRole.Melodic, "Electric Guitar")]
    public async Task Recognises_apple_loops(string loop, TrackRole role, string expected)
    {
        var path = Path.Combine("/Library/Audio/Apple Loops/Apple", loop);
        if (!File.Exists(path) || FfmpegDecoder.Locate() is null)
        {
            output.WriteLine("Apple Loops or ffmpeg not available; skipping.");
            return;
        }
        var audio = await FfmpegDecoder.DecodeAsync(path, 22050);
        using var recognizer = new InstrumentRecognizer();
        var guess = recognizer.Recognize(audio.Samples, audio.SampleRate, role);
        output.WriteLine($"{loop}: {guess}");
        Assert.Equal(expected, guess?.Label);
    }
}
