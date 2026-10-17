using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Synth;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Tests;

public class PlayerTests
{
    const int Rate = 44100;
    const int Block = 64;

    /// <summary>Render <paramref name="seconds"/> of wall-clock time and return the (wall time, pitch) of each note start.</summary>
    static List<(double Time, int Pitch)> Run(NotePlayer player, double seconds, float[]? capture = null)
    {
        var starts = new List<(double, int)>();
        var buf = new float[Block];
        var previous = new HashSet<int>();
        int total = (int)(seconds * Rate);
        for (int i = 0; i < total; i += Block)
        {
            player.Render(buf);
            if (capture is not null) buf.AsSpan(0, Math.Min(Block, capture.Length - i)).CopyTo(capture.AsSpan(i));
            var now = player.SoundingPitches.ToHashSet();
            foreach (var p in now.Except(previous)) starts.Add(((double)i / Rate, p));
            previous = now;
        }
        return starts;
    }

    static DetectedNote N(double start, double end, int pitch) => new(start, end, pitch, 0.8f);

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void Notes_start_on_time_at_the_playback_tempo(double rate)
    {
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetNotes([N(0.5, 0.8, 60), N(1.0, 1.2, 64)], null);
        player.Configure(0, double.MaxValue, loop: false, rate);
        player.Play(0);

        var starts = Run(player, 1.2 / rate + 0.2);
        Assert.Equal([60, 64], starts.Select(s => s.Pitch));
        Assert.Equal(0.5 / rate, starts[0].Time, 0.005);
        Assert.Equal(1.0 / rate, starts[1].Time, 0.005);
    }

    [Fact]
    public void Loops_the_range_and_stops_at_its_end_otherwise()
    {
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetNotes([N(0.25, 0.5, 60), N(1.5, 1.8, 72)], null);

        player.Configure(0, 1.0, loop: true, rate: 1);
        player.Play(0);
        var starts = Run(player, 3.1);
        Assert.Equal([0.25, 1.25, 2.25], starts.Where(s => s.Pitch == 60).Select(s => Math.Round(s.Time, 2))); // once per pass
        Assert.DoesNotContain(starts, s => s.Pitch == 72);   // outside the loop range
        Assert.True(player.IsPlaying);

        player.Configure(0, 1.0, loop: false, rate: 1);
        player.Play(0);
        Run(player, 1.2);
        Assert.False(player.IsPlaying);
        Assert.Equal(1.0, player.Position, 3);
        Assert.Empty(player.SoundingPitches);
    }

    [Fact]
    public void Metronome_clicks_on_the_beats()
    {
        var player = new NotePlayer(new PolySynth(Rate)) { Metronome = true };
        var grid = BarGrid.Steady(0, 0.5, 4); // 120 BPM from t = 0
        player.SetNotes([N(3.9, 4.0, 60)], grid);
        player.Configure(0, double.MaxValue, loop: false, rate: 1);
        player.Play(0.25);

        var audio = new float[(int)(1.3 * Rate)];
        Run(player, 1.3, audio);
        double Energy(double from, double to) => audio.AsSpan((int)(from * Rate), (int)((to - from) * Rate)).ToArray().Sum(v => (double)v * v);

        // Clicks at 0.5 s and 1.0 s of song time = 0.25 s and 0.75 s after starting at 0.25.
        Assert.True(Energy(0.25, 0.28) > 100 * Energy(0.1, 0.2) + 1e-6);
        Assert.True(Energy(0.75, 0.78) > 100 * Energy(0.6, 0.7) + 1e-6);
    }

    [Fact]
    public void Stop_releases_everything()
    {
        var synth = new PolySynth(Rate);
        var player = new NotePlayer(synth);
        player.SetNotes([N(0, 5, 60), N(0, 5, 64), N(0, 5, 67)], null);
        player.Configure(0, double.MaxValue, false, 1);
        player.Play(0);
        Run(player, 0.2);
        Assert.Equal(3, player.SoundingPitches.Length);

        player.Stop();
        Run(player, 1.0);
        Assert.Empty(player.SoundingPitches);
        Assert.Equal(0, synth.ActiveVoices);
    }

    /// <summary>Render stereo for a while and return the energy of each side.</summary>
    static (double Left, double Right) RunStereo(NotePlayer player, double seconds)
    {
        var left = new float[Block];
        var right = new float[Block];
        double l = 0, r = 0;
        for (int i = 0; i < (int)(seconds * Rate); i += Block)
        {
            player.Render(left, right);
            for (int k = 0; k < Block; k++) { l += left[k] * left[k]; r += right[k] * right[k]; }
        }
        return (l, r);
    }

    static NotePlayer TwoTracks()
    {
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetTracks([0, 33]); // piano, bass
        player.SetNotes([N(0, 1, 72) with { Track = 0 }, N(0, 1, 40) with { Track = 1 }], null);
        player.Configure(0, double.MaxValue, false, 1);
        return player;
    }

    [Fact]
    public void Each_track_plays_on_its_own_channel_with_mute_and_solo()
    {
        var player = TwoTracks();
        player.Play(0);
        RunStereo(player, 0.3);
        var both = player.ReadLevels();
        Assert.True(both.Tracks[0] > 0.01 && both.Tracks[1] > 0.01, "both tracks sound");

        player.SetMix(1, 1, 0.5, mute: false, solo: true);
        RunStereo(player, 0.2);
        var solo = player.ReadLevels();
        Assert.Equal(0, solo.Tracks[0]);
        Assert.True(solo.Tracks[1] > 0.01);
        Assert.Equal([40, 72], player.SoundingPitches.Order()); // a silenced track still holds its notes
    }

    [Fact]
    public void Pan_places_a_track_in_the_stereo_field()
    {
        var player = TwoTracks();
        player.SetMix(0, 1, 0, false, false); // hard left
        player.SetMix(1, 0, 0.5, false, false); // silent
        player.Play(0);
        var (left, right) = RunStereo(player, 0.3);
        Assert.True(left > 1 && right < 1e-9, $"left {left}, right {right}");
    }

    [Fact]
    public void Drum_hits_play_on_the_kit()
    {
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetNotes([], null);
        player.SetDrums([new DetectedNote(0.1, 0.2, 36, 0.9f), new DetectedNote(0.3, 0.4, 38, 0.9f)]);
        player.Configure(0, 1, false, 1);
        player.Play(0);
        var audio = new float[(int)(0.5 * Rate)];
        Run(player, 0.5, audio);
        double Energy(double from, double to) => audio.AsSpan((int)(from * Rate), (int)((to - from) * Rate)).ToArray().Sum(v => (double)v * v);
        Assert.True(Energy(0.1, 0.15) > 100 * Energy(0.0, 0.09) + 1e-6, "kick at 0.1 s");
        Assert.True(Energy(0.3, 0.35) > 10 * Energy(0.27, 0.29) + 1e-6, "snare at 0.3 s");
    }

    [Fact]
    public void The_recording_plays_in_time_at_its_level()
    {
        // One second of silence, then a tone: it must sound from 1 s of song time, at half speed from 2 s of playing.
        int sourceRate = 22050;
        var source = new float[3 * sourceRate];
        for (int i = sourceRate; i < source.Length; i++) source[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / sourceRate);
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetNotes([], null);
        player.SetSource(source, sourceRate);
        player.Configure(0, 2.5, false, rate: 0.5);
        player.Play(0);
        var audio = new float[(int)(3 * Rate)];
        Run(player, 3, audio);
        double Energy(double from, double to) => audio.AsSpan((int)(from * Rate), (int)((to - from) * Rate)).ToArray().Sum(v => (double)v * v);
        Assert.True(Energy(0.2, 1.8) < 1e-6, "silent before 1 s of song time (2 s of playing at half speed)");
        Assert.True(Energy(2.2, 2.8) > 100, "the tone after it");

        player.SetMix(NotePlayer.SourceChannel, 0, 0.5, false, false);
        player.Play(1.5);
        var muted = new float[(int)(0.5 * Rate)];
        Run(player, 0.5, muted);
        Assert.True(muted.Sum(v => (double)v * v) < 1e-6, "gain 0 silences it");
    }

    [Fact]
    public void Renders_the_range_offline_then_lets_it_ring()
    {
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetNotes([N(0.5, 0.8, 60), N(2.0, 2.5, 64), N(3.5, 4.0, 67)], null);
        player.Configure(1.5, 3.0, loop: false, rate: 1);
        player.Play(1.5);
        var (left, right) = player.RenderUntilStopped(tail: 1.0);

        Assert.Equal(left.Length, right.Length);
        Assert.InRange(left.Length / (double)Rate, 2.5, 2.6); // 1.5 s of range, then 1 s, in 4096-frame blocks
        double Energy(double from, double to) => left.AsSpan((int)(from * Rate), (int)((to - from) * Rate)).ToArray().Sum(v => (double)v * v);
        Assert.True(Energy(0, 0.45) < 1e-9, "nothing before the note at 2 s");
        Assert.True(Energy(0.5, 1.0) > 1, "the note at 2 s");
        Assert.True(Energy(left.Length / (double)Rate - 0.3, left.Length / (double)Rate) < 1e-6, "rung out by the end; the note at 3.5 s isn't in the range");
    }

    [Fact]
    public void Stereo_wav_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"a2m-stereo-{Guid.NewGuid():N}.wav");
        try
        {
            float[] left = [0.5f, -0.25f, 0.125f], right = [-1f, 0f, 1f];
            AudioToMidi.Core.Audio.WavReader.WriteFloat(path, left, right, 44100);
            var (channels, rate) = AudioToMidi.Core.Audio.WavReader.ReadChannels(path);
            Assert.Equal(44100, rate);
            Assert.Equal(left, channels[0]);
            Assert.Equal(right, channels[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Clip_to_range_trims_held_notes()
    {
        var clipped = NoteFilter.ClipToRange([N(0, 4, 48), N(1, 1.5, 60), N(3, 3.5, 62), N(5, 6, 64)], 0.5, 3.2);
        Assert.Equal([48, 60, 62], clipped.Select(n => n.Pitch));
        Assert.Equal((0.5, 3.2), (clipped[0].Start, clipped[0].End));
        Assert.Equal(3.2, clipped[2].End, 6);
    }
}

public class RenderAheadTests
{
    const int Rate = 48000;

    /// <summary>A ramp, so any dropped, repeated or reordered frame shows.</summary>
    sealed class Ramp
    {
        long _next;
        public void Render(Span<float> left, Span<float> right)
        {
            for (int i = 0; i < left.Length; i++) { left[i] = _next; right[i] = -_next; _next++; }
        }
    }

    static void WaitFor(Func<bool> done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && clock.Elapsed.TotalSeconds < 5) Thread.Sleep(1);
    }

    [Fact]
    public void Delivers_every_frame_in_order_whatever_the_read_size()
    {
        using var ahead = new RenderAhead(new Ramp().Render, Rate, lead: 0.05);
        long expected = 0;
        var left = new float[700];
        var right = new float[700];
        foreach (int n in new[] { 512, 64, 700, 1, 333, 512, 512, 129 })
        {
            WaitFor(() => ahead.Buffered * Rate >= n);
            ahead.Read(left.AsSpan(0, n), right.AsSpan(0, n));
            for (int i = 0; i < n; i++, expected++)
            {
                Assert.Equal(expected, left[i]);
                Assert.Equal(-expected, right[i]);
            }
        }
        Assert.Equal(0, ahead.Starved);
    }

    [Fact]
    public void Keeps_its_lead_and_fills_silence_when_behind()
    {
        int renders = 0;
        using var ahead = new RenderAhead((l, r) => { Interlocked.Increment(ref renders); l.Fill(1); r.Fill(1); }, Rate, lead: 0.05);
        WaitFor(() => ahead.Buffered >= 0.04);
        Thread.Sleep(50);
        Assert.InRange(ahead.Buffered, 0.04, 0.05 + 1e-9); // renders up to its lead, then waits

        // Asking for more than is buffered: the rest is silence, counted.
        int frames = (int)(ahead.Buffered * Rate) + 100;
        var left = new float[frames];
        var right = new float[frames];
        ahead.Read(left, right);
        Assert.Equal(1, ahead.Starved);
        Assert.Equal(1, left[0]);
        Assert.Equal(0, left[^1]);
    }
}
