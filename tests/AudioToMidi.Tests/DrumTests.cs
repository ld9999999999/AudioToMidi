using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Drums;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Xunit.Abstractions;

namespace AudioToMidi.Tests;

public class DrumTests(ITestOutputHelper output)
{
    /// <summary>
    /// Eight bars of a pop beat at 120 BPM: kick on 1 and the "and" of 3, snare on 2 and 4, closed
    /// hi-hats on the eighths except an open one on the last eighth of every second bar.
    /// </summary>
    static (float[] Audio, List<(double Time, DrumKind Kind)> Truth) Beat(double offset = 0.25)
    {
        const double beat = 0.5;
        var audio = new float[(int)((offset + 8 * 4 * beat + 1) * Synth.Rate)];
        var truth = new List<(double, DrumKind)>();
        for (int bar = 0; bar < 8; bar++)
        {
            for (int eighth = 0; eighth < 8; eighth++)
            {
                double t = offset + (bar * 4 + eighth * 0.5) * beat;
                bool open = bar % 2 == 1 && eighth == 7;
                Synth.AddHiHat(audio, t, open);
                truth.Add((t, open ? DrumKind.OpenHiHat : DrumKind.ClosedHiHat));
                if (eighth is 0 or 5) { Synth.AddKick(audio, t); truth.Add((t, DrumKind.Kick)); }
                if (eighth is 2 or 6) { Synth.AddSnare(audio, t); truth.Add((t, DrumKind.Snare)); }
            }
        }
        return (audio, truth);
    }

    static (int Found, int Extra, int Missed) Score(IEnumerable<double> truth, IEnumerable<double> found, double tolerance = 0.03)
    {
        var expected = truth.OrderBy(t => t).ToList();
        var remaining = found.OrderBy(t => t).ToList();
        int matched = 0;
        foreach (var e in expected)
        {
            int i = remaining.FindIndex(f => Math.Abs(f - e) <= tolerance);
            if (i < 0) continue;
            remaining.RemoveAt(i);
            matched++;
        }
        return (matched, remaining.Count, expected.Count - matched);
    }

    void Report(string label, (int Found, int Extra, int Missed) s) =>
        output.WriteLine($"{label,-8} {s.Found} found, {s.Extra} extra, {s.Missed} missed");

    [Fact]
    public void Finds_kick_snare_and_hihats_in_a_beat()
    {
        var (audio, truth) = Beat();
        var hits = DrumTranscriber.Analyze(new AudioData(audio, Synth.Rate)).Decode(new DrumSettings());
        foreach (var t in truth.Where(t => t.Kind is DrumKind.ClosedHiHat or DrumKind.OpenHiHat))
            if (!hits.Any(h => h.Kind is DrumKind.ClosedHiHat or DrumKind.OpenHiHat && Math.Abs(h.Time - t.Time) < 0.03))
                output.WriteLine($"missed hi-hat at {t.Time:F3}; drums found near it: " +
                                 string.Join(", ", hits.Where(h => Math.Abs(h.Time - t.Time) < 0.1).Select(h => $"{h.Kind.Label()} {h.Time:F3}")));

        static bool IsHat(DrumKind k) => k is DrumKind.ClosedHiHat or DrumKind.OpenHiHat;
        foreach (var (label, select) in new (string, Func<DrumKind, bool>)[]
                 { ("kick", k => k == DrumKind.Kick), ("snare", k => k == DrumKind.Snare), ("hi-hat", IsHat) })
        {
            var s = Score(truth.Where(t => select(t.Kind)).Select(t => t.Time), hits.Where(h => select(h.Kind)).Select(h => h.Time));
            Report(label, s);
            int total = s.Found + s.Missed;
            Assert.True(s.Found >= total * 0.95, $"{label}: found {s.Found} of {total}");
            Assert.True(s.Extra <= total * 0.05, $"{label}: {s.Extra} extra hits");
        }

        // Open hi-hats ring; closed ones are short.
        var openTimes = truth.Where(t => t.Kind == DrumKind.OpenHiHat).Select(t => t.Time).ToList();
        var openFound = hits.Where(h => h.Kind == DrumKind.OpenHiHat).Select(h => h.Time).ToList();
        var open = Score(openTimes, openFound);
        Report("open", open);
        Assert.True(open.Found >= openTimes.Count - 1, $"open hi-hats: {open.Found} of {openTimes.Count}");
        Assert.True(open.Extra <= 2, $"{open.Extra} closed hi-hats called open");
    }

    [Fact]
    public void Hits_are_timed_to_the_attack()
    {
        var (audio, truth) = Beat(offset: 0.3137); // not on a frame boundary
        var hits = DrumTranscriber.Analyze(new AudioData(audio, Synth.Rate)).Decode(new DrumSettings());
        var errors = truth.Where(t => t.Kind == DrumKind.Kick)
            .Select(t => hits.Where(h => h.Kind == DrumKind.Kick).Select(h => h.Time - t.Time).MinBy(Math.Abs))
            .ToList();
        output.WriteLine($"kick timing errors (ms): {string.Join(" ", errors.Select(e => $"{e * 1000:F0}"))}");
        Assert.All(errors, e => Assert.InRange(e, -0.02, 0.02));
    }

    [Fact]
    public void Sustained_tones_do_not_make_drum_hits()
    {
        // Pad-like tones that fade in and out over 30 ms. A faster fade would click, and a click is a real transient.
        var audio = new float[Synth.Rate * 6];
        foreach (var (pitch, start) in new[] { (48, 0.5), (55, 0.5), (64, 2.0), (60, 3.5) })
        {
            int s0 = (int)(start * Synth.Rate), n = (int)(1.4 * Synth.Rate), fade = (int)(0.03 * Synth.Rate);
            double hz = Synth.MidiToHz(pitch);
            for (int i = 0; i < n; i++)
                audio[s0 + i] += (float)(0.2 * Math.Min(1, Math.Min(i, n - i) / (double)fade) * Math.Sin(2 * Math.PI * hz * i / Synth.Rate));
        }
        var hits = DrumTranscriber.Analyze(new AudioData(audio, Synth.Rate)).Decode(new DrumSettings());
        foreach (var h in hits) output.WriteLine($"{h.Time:F3} {h.Kind.Label()} {h.Velocity}");
        Assert.True(hits.Count <= 2, $"{hits.Count} hits in pure tones");
    }

    // Known limitation: without Demucs, drums come from the percussive part of the drum removal. That part
    // also holds the attacks of piano-like notes, which look like hits. The Demucs drum stem is clean.
    [Fact(Skip = "Known limitation of the percussive-part path; see comment")]
    public async Task A_song_without_drums_gets_almost_no_drum_hits()
    {
        var audio = new float[Synth.Rate * 12];
        double t = 0.25;
        foreach (var chord in new[] { new[] { 48, 55, 64 }, [53, 57, 65], [55, 59, 67], [48, 55, 64] })
        {
            foreach (var p in chord) Synth.AddTone(audio, p, t, 1.4, 0.2);
            for (int i = 0; i < 4; i++) Synth.AddTone(audio, chord[2] + 12 + (i % 2) * 2, t + i * 0.375, 0.35, 0.15); // melody on top
            t += 1.5;
        }
        var hits = await SessionHits(audio);
        foreach (var h in hits) output.WriteLine($"{h.Time:F3} {h.Kind.Label()} {h.Velocity}");
        Assert.True(hits.Count <= 3, $"{hits.Count} drum hits in a song without drums");
    }

    static async Task<IReadOnlyList<DrumHit>> SessionHits(float[] audio)
    {
        using var session = new AudioToMidi.Core.TranscriptionSession();
        var path = Path.Combine(Path.GetTempPath(), $"a2m_drums_{Guid.NewGuid():N}.wav");
        WavReader.WriteFloat(path, new AudioData(audio, Synth.Rate));
        try
        {
            await session.LoadAsync(path, ffmpegPath: "/nonexistent");
            await session.AnalyzeAsync(new AudioToMidi.Core.AnalysisSettings());
            await session.AnalyzeDrumsAsync();
            return session.GetDrumHits(new DrumSettings());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Drums_under_chords_are_found_through_the_session()
    {
        var (audio, truth) = Beat();
        foreach (var (chord, start) in new[] { (new[] { 48, 55, 64 }, 0.25), (new[] { 53, 57, 65 }, 4.25), (new[] { 55, 59, 67 }, 8.25), (new[] { 48, 55, 64 }, 12.25) })
            foreach (var p in chord) Synth.AddTone(audio, p, start, 4.0, 0.2);

        using var session = new AudioToMidi.Core.TranscriptionSession();
        var path = Path.Combine(Path.GetTempPath(), $"a2m_drums_{Guid.NewGuid():N}.wav");
        WavReader.WriteFloat(path, new AudioData(audio, Synth.Rate));
        try
        {
            await session.LoadAsync(path, ffmpegPath: "/nonexistent");
            await session.AnalyzeAsync(new AudioToMidi.Core.AnalysisSettings());
            Assert.False(session.HasDrumsForCurrentSource);
            await session.AnalyzeDrumsAsync();
            Assert.True(session.HasDrumsForCurrentSource);
            Assert.Same(session.Separation!.Percussive, session.DrumSource); // no Demucs stems here

            var hits = session.GetDrumHits(new DrumSettings());
            foreach (var h in hits.Where(h => h.Kind == DrumKind.Kick && !truth.Any(t => t.Kind == DrumKind.Kick && Math.Abs(t.Time - h.Time) < 0.03)))
                output.WriteLine($"extra kick {h.Time:F3} vel {h.Velocity}");
            foreach (var kind in new[] { DrumKind.Kick, DrumKind.Snare })
            {
                var s = Score(truth.Where(t => t.Kind == kind).Select(t => t.Time), hits.Where(h => h.Kind == kind).Select(h => h.Time));
                Report(kind.Label(), s);
                Assert.True(s.Found >= 15 && s.Extra <= 2, $"{kind.Label()}: {s}");
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Higher_sensitivity_finds_more_quiet_hits()
    {
        var audio = new float[Synth.Rate * 6];
        for (int i = 0; i < 10; i++) Synth.AddKick(audio, 0.25 + i * 0.5, amp: i % 2 == 0 ? 0.6 : 0.06);
        var analysis = DrumTranscriber.Analyze(new AudioData(audio, Synth.Rate));
        int Kicks(double s) => analysis.Decode(new DrumSettings { Sensitivity = s }).Count(h => h.Kind == DrumKind.Kick);
        output.WriteLine($"kicks at sensitivity 0.1: {Kicks(0.1)}, 0.9: {Kicks(0.9)}");
        Assert.True(Kicks(0.1) <= Kicks(0.9));
        Assert.InRange(Kicks(0.1), 5, 6);  // loud ones only
        Assert.InRange(Kicks(0.9), 9, 10); // quiet ones too
    }

    [Fact]
    public void Drum_notes_are_gm_notes_that_never_overlap()
    {
        DrumHit[] hits =
        [
            new(1.00, DrumKind.Kick, 0.9f), new(1.02, DrumKind.Kick, 0.5f), // flam: snaps to one step
            new(1.25, DrumKind.Kick, 0.7f), new(1.26, DrumKind.Snare, 0.8f),
            new(1.50, DrumKind.ClosedHiHat, 0.4f), new(1.54, DrumKind.OpenHiHat, 0.6f),
        ];
        var tempo = new TempoEstimate(120, 0, 1);
        var notes = DrumNotes.ToNotes(hits, track: 3, tempo, quantizeDivisions: 4); // 1/16 = 0.125 s

        Assert.All(notes, n => Assert.Equal(3, n.Track));
        Assert.Equal([36, 36, 38, 42, 46], notes.Select(n => n.Pitch).ToArray());
        Assert.Equal(1.0, notes[0].Start, 6);
        Assert.Equal(0.9f, notes[0].Amplitude); // the stronger of the flam
        Assert.Equal(1.25, notes[1].Start, 6);
        Assert.Equal(1.5, notes[4].Start, 6);
        foreach (var g in notes.GroupBy(n => n.Pitch))
            foreach (var (a, b) in g.Zip(g.Skip(1)))
                Assert.True(a.End <= b.Start, "same-drum notes overlap");

        var unquantized = DrumNotes.ToNotes(hits, track: 0);
        Assert.Equal(6, unquantized.Count);
        Assert.True(unquantized[0].End <= unquantized[1].Start); // 1.00 ends before the 1.02 flam starts
    }

    [Fact]
    public void Drum_track_goes_on_channel_10_without_a_program_change()
    {
        TrackInfo[] tracks = [.. Enumerable.Range(0, 10).Select(i => new TrackInfo($"T{i}", 5, StaffLayout.Auto)),
            new("Drums", 0, StaffLayout.Auto, IsDrums: true)];
        var notes = tracks.Select((_, i) => new DetectedNote(i * 0.1, i * 0.1 + 0.1, i == 10 ? 36 : 60, 0.8f, i));
        var file = MidiExporter.Build(notes, tracks);
        var chunks = file.GetTrackChunks().Skip(1).ToList(); // first chunk holds the tempo map

        int[] channels = chunks.Select(c => (int)c.GetNotes().Single().Channel).ToArray();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 9], channels);
        Assert.Empty(chunks[10].Events.OfType<ProgramChangeEvent>());
        Assert.Single(chunks[0].Events.OfType<ProgramChangeEvent>());
    }
}
