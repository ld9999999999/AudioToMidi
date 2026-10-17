using System.Xml.Linq;
using AudioToMidi.Core;
using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Instruments;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Separation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Xunit.Abstractions;

namespace AudioToMidi.Tests;

public class TrackTests(ITestOutputHelper output)
{
    static DetectedNote N(double start, double end, int pitch, int track = 0) => new(start, end, pitch, 0.7f, track);

    [Fact]
    public void Register_split_keeps_the_bass_line_monophonic()
    {
        DetectedNote[] notes =
        [
            N(0, 1, 36), N(0, 1, 43),  // low fifth: 36 is bass, 43 moves up
            N(0, 1, 60), N(0, 1, 64),  // chord above the split
            N(1, 2, 38),               // next bass note
            N(1.5, 2.5, 33),           // lower note under a held one: 33 bass, 38 moves up
            N(2.5, 3, 47),             // single low note: bass
        ];
        var split = TrackSplitter.SplitByRegister(notes, splitNote: 48);
        int[] Track(int t) => split.Where(n => n.Track == t).Select(n => n.Pitch).Order().ToArray();

        Assert.Equal([33, 36, 47], Track(TrackSplitter.BassTrack));
        Assert.Equal([38, 43, 60, 64], Track(TrackSplitter.UpperTrack));
    }

    [Fact]
    public void Midi_export_writes_named_tracks_on_separate_channels()
    {
        DetectedNote[] notes = [N(0, 1, 72, 0), N(0, 1, 40, 1), N(1, 2, 67, 2), N(1, 2, 43, 1)];
        TrackInfo[] tracks =
        [
            new("Other", 0, StaffLayout.Auto), new("Bass", 33, StaffLayout.Bass), new("Vocals", 53, StaffLayout.Treble),
        ];
        var path = Path.GetTempFileName();
        try
        {
            MidiExporter.Write(path, notes, tracks, bpm: 100);
            var file = MidiFile.Read(path);
            var chunks = file.GetTrackChunks().ToList();

            Assert.Equal(MidiFileFormat.MultiTrack, file.OriginalFormat);
            Assert.Equal(4, chunks.Count); // tempo track + 3 instruments
            for (int t = 0; t < 3; t++)
            {
                var chunk = chunks[t + 1];
                var name = chunk.Events.OfType<SequenceTrackNameEvent>().Single().Text;
                var program = chunk.Events.OfType<ProgramChangeEvent>().Single();
                var pitches = chunk.GetNotes().Select(n => (int)n.NoteNumber).Order().ToArray();
                Assert.Equal(tracks[t].Name, name);
                Assert.Equal(tracks[t].Program, (int)program.ProgramNumber);
                Assert.Equal(t, (int)program.Channel);
                Assert.All(chunk.GetNotes(), n => Assert.Equal(t, (int)n.Channel));
                Assert.Equal(notes.Where(n => n.Track == t).Select(n => n.Pitch).Order().ToArray(), pitches);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Score_parts_share_bars_and_key()
    {
        DetectedNote[] notes = [N(0, 0.5, 74, 0), N(0.5, 1, 76, 0), N(0, 4, 38, 1)]; // short melody, long bass
        var grid = BarGrid.Create(new TempoEstimate(120, 0, 1), 4, 0, notes);
        var parts = ScoreBuilder.BuildParts(notes,
            [("Melody", 73, StaffLayout.Treble), ("Bass", 33, StaffLayout.Bass)], grid, new ScoreSettings { KeyFifths = 2 });

        Assert.Equal(2, parts.Count);
        Assert.Equal(parts[0].MeasureCount, parts[1].MeasureCount);
        Assert.Equal(2, parts[0].MeasureCount); // the 4 s bass note (8 beats at 120 BPM) sets the length
        Assert.All(parts, p => Assert.Equal(2, p.Key.Fifths));
        Assert.Equal([Clef.Bass], parts[1].Staves);

        var doc = MusicXmlWriter.ToDocument(parts);
        Assert.Equal(["Melody", "Bass"], doc.Descendants("score-part").Select(p => p.Element("part-name")!.Value));
        Assert.Equal(2, doc.Root!.Elements("part").Count());
        Assert.Single(doc.Descendants("metronome")); // tempo only in the first part
    }

    [Fact]
    public async Task Demucs_separates_bass_from_the_rest()
    {
        var model = DemucsSeparator.LocateModel();
        if (model is null)
        {
            output.WriteLine("Demucs model not installed; skipping.");
            return;
        }

        // Bass line, a higher melody and drums, at 100 BPM.
        var audio = new float[Synth.Rate * 16];
        Synth.AddBeat(audio, 100);
        int[] bass = [36, 36, 41, 43, 36, 36, 41, 43, 45, 45, 41, 43, 36, 36, 41, 43];
        int[] melody = [72, 74, 76, 79, 77, 76, 74, 72, 72, 74, 76, 79, 77, 76, 74, 72];
        for (int i = 0; i < 16; i++)
        {
            Synth.AddTone(audio, bass[i], i * 0.9, 0.85, 0.3);
            Synth.AddTone(audio, melody[i], i * 0.9 + 0.3, 0.5, 0.12);
        }

        var path = Path.Combine(Path.GetTempPath(), $"a2m_stems_{Guid.NewGuid():N}.wav");
        WavReader.WriteFloat(path, new AudioData(audio, Synth.Rate));
        try
        {
            using var session = new TranscriptionSession();
            await session.LoadAsync(path, null);
            await session.AnalyzeAsync(new AnalysisSettings { SeparateInstruments = true });
            var notes = session.GetNotes(new NoteSettings(), tracks: new TrackSettings { Mode = TrackMode.Instruments });

            foreach (var g in notes.GroupBy(n => n.Track))
                output.WriteLine($"{TranscriptionSession.StemNames[g.Key]}: {string.Join(" ", g.Select(n => n.Pitch))}");

            var bassTrack = notes.Where(n => n.Track == 1).ToList();
            int bassFound = bass.Select((p, i) => bassTrack.Any(n => n.Pitch == p && Math.Abs(n.Start - i * 0.9) < 0.1)).Count(x => x);
            Assert.True(bassFound >= 12, $"Only {bassFound} of 16 bass notes on the bass track");
            Assert.True(bassTrack.Count(n => n.Pitch >= 60) <= 2, "Melody leaked into the bass track");
            Assert.True(notes.Count(n => n.Track != 1 && melody.Contains(n.Pitch)) >= 10, "Melody missing from the other tracks");
            // There are no vocals, so the gate must drop the near-silent vocal stem.
            Assert.True(notes.Count(n => n.Track == 2) <= 2, $"{notes.Count(n => n.Track == 2)} notes from an empty vocal stem");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Six_stem_model_adds_guitar_and_piano_tracks()
    {
        Assert.Equal(["Other", "Bass", "Vocals"], TranscriptionSession.TrackNames(TrackMode.Instruments));
        Assert.Equal(["Other", "Bass", "Vocals", "Guitar", "Piano"], TranscriptionSession.TrackNames(TrackMode.Instruments, StemModel.SixStem));
        Assert.Equal(["Keys", "Bass"], TranscriptionSession.TrackNames(TrackMode.SplitByRegister, StemModel.SixStem));

        using var session = new TranscriptionSession { SeparationModel = StemModel.SixStem };
        Assert.Equal(5, session.TrackNamesFor(TrackMode.Instruments).Count);
    }

    [Fact]
    public void Guitar_and_piano_stems_only_get_their_own_family()
    {
        Assert.True(InstrumentRecognizer.Allowed(InstrumentClasses.All[InstrumentClasses.IndexOf("Electric Guitar")], TrackRole.Guitar));
        Assert.False(InstrumentRecognizer.Allowed(InstrumentClasses.All[InstrumentClasses.IndexOf("Piano")], TrackRole.Guitar));
        Assert.True(InstrumentRecognizer.Allowed(InstrumentClasses.All[InstrumentClasses.IndexOf("Organ")], TrackRole.Keys));
        Assert.False(InstrumentRecognizer.Allowed(InstrumentClasses.All[InstrumentClasses.IndexOf("Strings")], TrackRole.Keys));
        Assert.False(InstrumentRecognizer.Allowed(InstrumentClasses.All[InstrumentClasses.IndexOf("Synth Bass")], TrackRole.Keys));
    }

    /// <summary>
    /// GarageBand's "Longing" loops (piano, guitar, strings, a sub bass and a beat) mixed together. The
    /// sub bass dominates the mix's energy, and the piano and guitar must still pass the stem gate.
    /// </summary>
    [Fact]
    public async Task Six_stem_separation_puts_piano_and_guitar_on_their_own_tracks()
    {
        const string pack = "/Library/Audio/Apple Loops/Apple/01 Hip Hop";
        string[] loops = ["Longing Piano", "Longing Guitar", "Longing Strings 01", "Longing Bass", "Longing Beat"];
        var model = DemucsSeparator.LocateModel(null, StemModel.SixStem);
        if (model is null || FfmpegDecoder.Locate() is null || !loops.All(l => File.Exists(Path.Combine(pack, l + ".caf"))))
        {
            output.WriteLine("6-stem model, ffmpeg or Apple Loops not available; skipping.");
            return;
        }

        var mix = new float[0];
        foreach (var loop in loops)
        {
            var audio = (await FfmpegDecoder.DecodeAsync(Path.Combine(pack, loop + ".caf"), 44100)).Samples;
            if (mix.Length == 0) mix = new float[audio.Length];
            for (int i = 0; i < Math.Min(mix.Length, audio.Length); i++) mix[i] += 0.6f * audio[i];
        }
        var path = Path.Combine(Path.GetTempPath(), $"a2m_longing_{Guid.NewGuid():N}.wav");
        WavReader.WriteFloat(path, new AudioData(mix, 44100));
        try
        {
            using var session = new TranscriptionSession { SeparationModel = StemModel.SixStem };
            await session.LoadAsync(path, null);
            await session.AnalyzeAsync(new AnalysisSettings { SeparateInstruments = true });
            Assert.True(session.Stems!.HasGuitarAndPiano);

            var tracks = new TrackSettings { Mode = TrackMode.Instruments };
            var notes = session.GetNotes(new NoteSettings(), tracks: tracks);
            var counts = Enumerable.Range(0, 5).Select(t => notes.Count(n => n.Track == t)).ToArray();
            output.WriteLine("notes per track (other, bass, vocals, guitar, piano): " + string.Join(", ", counts));
            // The loops alone transcribe to about 15 guitar and 54 piano notes. Separation is partial: the
            // guitar stem also picks up some piano and strings, and part of the piano stays in other.
            Assert.True(counts[3] >= 10, $"only {counts[3]} guitar notes");
            Assert.True(counts[4] >= 10, $"only {counts[4]} piano notes");
            Assert.True(counts[2] <= 2, $"{counts[2]} notes from an empty vocal stem");

            var guesses = await session.RecognizeInstrumentsAsync(tracks);
            output.WriteLine("instruments: " + string.Join(", ", guesses.Select(g => g?.Label ?? "-")));
            Assert.Equal("Electric Guitar", guesses[3]?.Label);
            Assert.Equal("Piano", guesses[4]?.Label);
        }
        finally { File.Delete(path); }
    }

    // ---- Lines ----

    static MusicLine[] Lines(params DetectedNote[] notes) => LineSplitter.Assign(notes);

    [Fact]
    public void Chorale_splits_into_soprano_inner_voices_and_bass()
    {
        // Four-part block chords: soprano / alto / tenor / bass.
        int[][] chords = [[72, 67, 64, 48], [74, 67, 62, 43], [76, 72, 67, 48], [74, 71, 67, 55], [72, 67, 64, 48]];
        var notes = chords.SelectMany((c, i) => c.Select(p => N(i * 0.5, i * 0.5 + 0.48, p))).ToArray();
        var lines = Lines(notes);
        for (int i = 0; i < notes.Length; i++)
        {
            string what = $"{DetectedNote.PitchName(notes[i].Pitch)} at {notes[i].Start}: {lines[i]}";
            switch (i % 4)
            {
                case 0: Assert.True(lines[i] == MusicLine.Melody, what); break;  // soprano always on top
                case 3: Assert.True(lines[i] == MusicLine.Low, what); break;     // bass always at the bottom
                default: Assert.True(lines[i] != MusicLine.Low, what); break;    // inner voices never the low line
            }
        }
        // Most inner-voice notes are the middle line (one alto note close to the soprano may join the top).
        Assert.True(Enumerable.Range(0, notes.Length).Count(i => i % 4 is 1 or 2 && lines[i] == MusicLine.Harmony) >= 8);
    }

    [Fact]
    public void Tune_over_a_held_chord_stays_above_it()
    {
        DetectedNote[] notes =
        [
            N(0, 4, 48), N(0, 4, 55), N(0, 4, 64),                    // held chord
            N(0, 0.5, 72), N(0.5, 1, 74), N(1, 1.5, 76), N(1.5, 2, 79), // tune above it
            N(2, 2.5, 77), N(2.5, 3, 76), N(3, 3.5, 74), N(3.5, 4, 72),
        ];
        var lines = Lines(notes);
        Assert.Equal(MusicLine.Low, lines[0]);
        Assert.NotEqual(MusicLine.Melody, lines[1]);
        Assert.NotEqual(MusicLine.Melody, lines[2]);
        // Lines follow registers: the tune's upper notes are the top line; none of it sinks to the low line.
        Assert.All(lines.Skip(3), l => Assert.NotEqual(MusicLine.Low, l));
        Assert.All(notes.Select((n, i) => (n, i)).Where(x => x.i >= 3 && x.n.Pitch >= 76), x => Assert.Equal(MusicLine.Melody, lines[x.i]));
    }

    [Fact]
    public void Moving_inner_voice_under_a_held_melody_note_is_harmony()
    {
        DetectedNote[] notes =
        [
            N(0, 1, 76), N(1, 2, 74),                                    // melody, one note per beat
            N(0, 0.25, 64), N(0.25, 0.5, 65), N(0.5, 0.75, 67), N(0.75, 1, 65),
            N(1, 1.25, 64), N(1.25, 1.5, 62), N(1.5, 1.75, 60), N(1.75, 2, 62),
        ];
        var lines = Lines(notes);
        Assert.Equal(MusicLine.Melody, lines[0]);
        Assert.Equal(MusicLine.Melody, lines[1]);
        Assert.DoesNotContain(MusicLine.Melody, lines.Skip(2));
    }

    [Fact]
    public void A_high_pad_keeps_the_top_line_and_a_tune_below_stays_on_one_line()
    {
        // Lines follow registers: the pad is the top line, and the tune stays on one line.
        var notes = new List<DetectedNote> { N(0, 6, 84), N(0, 6, 88), N(0, 6, 40) }; // pad above, pedal below
        for (int i = 0; i < 10; i++) notes.Add(N(0.5 + i * 0.5, 0.95 + i * 0.5, 67 + (i % 3)));
        var lines = Lines([.. notes]);
        Assert.Equal(MusicLine.Melody, lines[0]);
        Assert.Equal(MusicLine.Melody, lines[1]);
        Assert.Equal(MusicLine.Low, lines[2]);
        Assert.Single(lines.Skip(3).Distinct());
        Assert.Equal(MusicLine.Harmony, lines[3]);
    }

    [Fact]
    public void Skyline_rules_follow_the_tune_under_a_long_pad()
    {
        // The skyline rules read the tune as the melody once the pad has settled.
        var notes = new List<DetectedNote> { N(0, 6, 84), N(0, 6, 88) };
        for (int i = 0; i < 10; i++) notes.Add(N(0.5 + i * 0.5, 0.95 + i * 0.5, 67 + (i % 3)));
        var lines = LineSplitter.AssignSkyline([.. notes]);
        for (int i = 2; i < notes.Count; i++)
            if (notes[i].Start >= LineSplitter.PadSeconds)
                Assert.True(lines[i] == MusicLine.Melody, $"note at {notes[i].Start}: {lines[i]}");
    }

    [Fact]
    public void Splitting_moves_lines_to_new_tracks_and_leaves_the_others()
    {
        DetectedNote[] notes = [N(0, 1, 72), N(0, 1, 64), N(0, 1, 52), N(0, 1, 36, track: 1)];
        var split = LineSplitter.Split(notes, track: 0, harmonyTrack: 2, lowTrack: 3);
        Assert.Equal(4, split.Count);
        Assert.Equal(0, split.Single(n => n.Pitch == 72).Track);
        Assert.Equal(2, split.Single(n => n.Pitch == 64).Track);
        Assert.Equal(3, split.Single(n => n.Pitch == 52).Track);
        Assert.Equal(1, split.Single(n => n.Pitch == 36).Track);

        Assert.Equal(["Melody", "Harmony", "Low line"], TrackLayout.Build(["Transcription"], [0]).Select(t => t.Name));
        Assert.Equal(["Other melody", "Bass", "Vocals", "Other harmony", "Other low line"],
            TrackLayout.Build(TranscriptionSession.TrackNames(TrackMode.Instruments), [0]).Select(t => t.Name));
    }

    [Fact]
    public void Several_tracks_can_be_split_and_base_tracks_keep_their_places()
    {
        var layout = TrackLayout.Build(["Other", "Bass", "Vocals", "Guitar", "Piano"], [4, 0]);
        Assert.Equal(["Other melody", "Bass", "Vocals", "Guitar", "Piano melody", "Other harmony", "Other low line",
            "Piano harmony", "Piano low line"], layout.Select(t => t.Name));
        Assert.Equal([0, 1, 2, 3, 4, 0, 0, 4, 4], layout.Select(t => t.Source));
        Assert.Equal([MusicLine.Melody, null, null, null, MusicLine.Melody, MusicLine.Harmony, MusicLine.Low, MusicLine.Harmony, MusicLine.Low],
            layout.Select(t => t.Line));
        Assert.Equal((7, 8), TrackLayout.LineTracks(5, [4, 0], 4));
        Assert.Equal(StaffLayout.Bass, TranscriptionSession.StavesFor(TrackMode.Instruments, layout[8]));
        Assert.Equal(StaffLayout.Treble, TranscriptionSession.StavesFor(TrackMode.Instruments, layout[4]));
        Assert.Equal(StaffLayout.Bass, TranscriptionSession.StavesFor(TrackMode.Instruments, layout[1])); // unsplit bass as before
    }
}
