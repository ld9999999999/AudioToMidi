using AudioToMidi.Core;
using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Drums;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Project;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using AudioToMidi.Model;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioToMidi.Tests;

public class ProjectTests
{
    [Fact]
    public void Timing_converts_between_seconds_and_beats()
    {
        // 120 BPM, bar 1 at 0.5 s (one pickup beat at 0.0).
        var timing = new TimingInfo(120, 4, [0.0, 0.5, 1.0, 1.5, 2.0, 2.5], FirstDownbeat: 1, Confidence: 1);
        Assert.Equal(0, timing.ToBeats(0.5), 9);
        Assert.Equal(-1, timing.ToBeats(0.0), 9);
        Assert.Equal(1.5, timing.ToBeats(1.25), 9);
        Assert.Equal(6, timing.ToBeats(3.5), 9);    // past the last beat: same spacing continues
        Assert.Equal(-2, timing.ToBeats(-0.5), 9);  // before the first, too
        foreach (var b in new[] { -1.5, 0, 0.25, 3, 7.75 })
            Assert.Equal(b, timing.ToBeats(timing.ToSeconds(b)), 9);
        Assert.Equal((1, 0.0), timing.ToBarBeat(0.5));
        Assert.Equal((2, 0.5), timing.ToBarBeat(2.75));
        Assert.Equal((0, 3.0), timing.ToBarBeat(0.0)); // the pickup is the last beat of bar 0
    }

    [Fact]
    public void Timing_follows_beats_that_speed_up()
    {
        var timing = new TimingInfo(100, 4, [0.0, 0.6, 1.1, 1.5], FirstDownbeat: 0, Confidence: 1);
        Assert.Equal(1.5, timing.ToBeats(0.85), 9);
        Assert.Equal(2.5, timing.ToBeats(1.3), 9);
        Assert.Equal(1.3, timing.ToSeconds(2.5), 9);
    }

    [Fact]
    public void Grid_beats_cover_the_audio_and_mark_bar_1()
    {
        var timing = ProjectExporter.Timing(BarGrid.Steady(originSeconds: 1.25, beatSeconds: 0.5, beatsPerBar: 4), durationSeconds: 10, confidence: 0.5);
        Assert.Equal(0.25, timing.Beats[0], 9);         // earliest beat inside the audio
        Assert.Equal(1.25, timing.Beats[timing.FirstDownbeat], 9);
        Assert.True(timing.Beats[^1] >= 10);
        Assert.Equal(120, timing.Bpm, 6);

        // Bar 1 starting just before the audio (a note slightly ahead of the beat) is kept whole.
        var early = ProjectExporter.Timing(BarGrid.Steady(-0.05, 0.5, 4), 4, 0.5);
        Assert.Equal(-0.05, early.Beats[0], 9);
        Assert.Equal(0, early.FirstDownbeat);
    }

    [Fact]
    public void Analysis_document_round_trips_through_json()
    {
        var document = new AnalysisDocument
        {
            Generator = "test",
            Created = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
            Source = new SourceInfo("song.mp3", 12.5, "ab12"),
            Timing = new TimingInfo(120, 4, [0, 0.5, 1], 0, 0.8) { Swing = 0.62 },
            Key = new KeyInfo(9, true, 0, "A minor"),
            Parts =
            [
                new Part("Bass", PartRole.Bass, 33, "Electric Bass (finger)", "bass", [new PartNote(0, 0.5, 45, 100)]),
                new Part("Drums", PartRole.Drums, 0, "Drum kit", "drums", [new PartNote(0, 0.1, 36, 127)]),
            ],
            Stems = [new StemFile("bass", "stems/bass.wav", 22050, 1)],
        };
        var json = ProjectBundle.Serialize(document);
        Assert.Contains("\"role\": \"drums\"", json); // enums as readable strings
        Assert.Contains("\"firstDownbeat\": 0", json);

        var back = ProjectBundle.Deserialize(json);
        Assert.Equal(document.Source, back.Source);
        Assert.Equal(document.Key, back.Key);
        Assert.Equal(document.Timing.Beats, back.Timing.Beats);
        Assert.Equal(0.62, back.Timing.Swing);
        Assert.Equal(document.Parts.Count, back.Parts.Count);
        Assert.Equal(document.Parts[1].Notes, back.Parts[1].Notes);
        Assert.Equal(PartRole.Drums, back.Parts[1].Role);
        Assert.Equal(document.Stems, back.Stems);
    }

    [Fact]
    public void Newer_documents_and_stems_outside_the_bundle_are_refused()
    {
        var json = ProjectBundle.Serialize(new AnalysisDocument
        {
            SchemaVersion = AnalysisDocument.CurrentSchemaVersion + 1,
            Source = new SourceInfo("a.wav", 1, null),
            Timing = new TimingInfo(120, 4, [0, 0.5], 0, 1),
        });
        Assert.Throws<NotSupportedException>(() => ProjectBundle.Deserialize(json));

        var folder = Path.Combine(Path.GetTempPath(), "song.a2m");
        Assert.Throws<InvalidDataException>(() => ProjectBundle.StemPath(folder, new StemFile("x", "../../etc/passwd", 22050, 1)));
        Assert.Equal(Path.Combine(folder, "stems", "bass.wav"), ProjectBundle.StemPath(folder, new StemFile("bass", "stems/bass.wav", 22050, 1)));
    }

    [Fact]
    public async Task Session_saves_a_complete_bundle()
    {
        // Two bars of a beat with a bass line and a chord.
        var audio = new float[Synth.Rate * 6];
        for (int i = 0; i < 16; i++)
        {
            double t = 0.25 + i * 0.25;
            Synth.AddHiHat(audio, t);
            if (i % 4 == 0) Synth.AddKick(audio, t);
            if (i % 8 == 4) Synth.AddSnare(audio, t);
        }
        foreach (var (p, t) in new[] { (36, 0.25), (43, 1.25), (41, 2.25), (43, 3.25) }) Synth.AddTone(audio, p, t, 0.9, 0.3);
        foreach (var p in new[] { 60, 64, 67 }) Synth.AddTone(audio, p, 0.25, 2.0, 0.15);

        var dir = Path.Combine(Path.GetTempPath(), $"a2m_project_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "groove.wav");
        WavReader.WriteFloat(wav, new AudioData(audio, Synth.Rate));
        var bundle = Path.Combine(dir, "groove.a2m");
        try
        {
            using var session = new TranscriptionSession();
            await session.LoadAsync(wav, ffmpegPath: "/nonexistent");
            await session.AnalyzeAsync(new AnalysisSettings());
            await session.AnalyzeDrumsAsync();
            var tracks = new TrackSettings { Mode = TrackMode.SplitByRegister };
            var notes = session.GetNotes(new NoteSettings(), session.Tempo, tracks);
            var drumNotes = DrumNotes.ToNotes(session.GetDrumHits(new DrumSettings()), track: 2);
            var grid = BarGrid.Create(session.Tempo!, 4, 0, notes);
            TrackInfo[] trackInfos =
            [
                new("Keys: Acoustic Grand Piano", 0, StaffLayout.Auto),
                new("Bass: Electric Bass (finger)", 33, StaffLayout.Bass),
                new("Drums", 0, StaffLayout.Auto, IsDrums: true),
            ];

            var saved = await ProjectExporter.SaveAsync(bundle, session, new ProjectContent([.. notes, .. drumNotes], trackInfos, tracks.Mode, grid));

            var loaded = ProjectBundle.Load(bundle);
            Assert.Equal(saved.Parts.Count, loaded.Parts.Count);
            Assert.Equal("groove.wav", loaded.Source.FileName);
            Assert.Equal(64, loaded.Source.Sha256!.Length);
            Assert.Equal(["Keys", "Bass", "Drums"], loaded.Parts.Select(p => p.Name).ToArray());
            Assert.Equal([PartRole.Melodic, PartRole.Bass, PartRole.Drums], loaded.Parts.Select(p => p.Role).ToArray());
            Assert.Equal(["harmonic", "harmonic", "percussive"], loaded.Parts.Select(p => p.Stem ?? "").ToArray());
            Assert.Contains(loaded.Parts[1].Notes, n => n.Pitch == 36);
            Assert.Contains(loaded.Parts[2].Notes, n => n.Pitch == 36); // kick
            Assert.Contains(loaded.Parts[2].Notes, n => n.Pitch == 38); // snare
            Assert.NotNull(loaded.Key);
            Assert.NotEmpty(loaded.Keys);
            Assert.Contains(loaded.Chords, c => c.Root == 0 && c.Kind == "major"); // the C–E–G chord

            foreach (var stem in loaded.Stems)
                Assert.True(File.Exists(ProjectBundle.StemPath(bundle, stem)), stem.Path);
            Assert.Equal(["harmonic", "percussive"], loaded.Stems.Select(s => s.Name).ToArray());

            // The MIDI is in time with the stems and has the drums on channel 10.
            var midi = MidiFile.Read(Path.Combine(bundle, "groove.mid"));
            var map = midi.GetTempoMap();
            var kicks = midi.GetNotes().Where(n => n.Channel == 9 && n.NoteNumber == 36).ToList();
            Assert.NotEmpty(kicks);
            // Kicks at 0.25, 1.25, 2.25, 3.25 s. Loose tolerance: through the drum removal, bass notes that
            // start with a kick pull its timing by up to ~50 ms (DrumTests checks timing on clean drums).
            var kickTimes = kicks.Select(n => n.TimeAs<MetricTimeSpan>(map).TotalMicroseconds / 1e6).OrderBy(t => t).ToList();
            Assert.Equal(4, kickTimes.Count);
            for (int i = 0; i < 4; i++) Assert.InRange(kickTimes[i], 0.25 + i - 0.06, 0.25 + i + 0.06);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
