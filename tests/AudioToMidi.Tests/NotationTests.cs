using System.Xml.Linq;
using AudioToMidi.Core;
using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioToMidi.Tests;

public class NotationTests
{
    // 120 BPM, first beat at 0: one beat = 0.5 s.
    static readonly TempoEstimate Tempo120 = new(120, 0, 1);

    static DetectedNote N(double startBeat, double endBeat, int pitch) => new(startBeat * 0.5, endBeat * 0.5, pitch, 0.7f);

    static IEnumerable<DetectedNote> Scale(int[] pitches, int[] weights) =>
        pitches.SelectMany((p, i) => Enumerable.Range(0, weights[i]).Select(k => N(i * 4 + k, i * 4 + k + 1, p)));

    [Fact]
    public void Key_is_detected_from_the_notes()
    {
        // Tonic and dominant weighted more, as in real music.
        Assert.Equal(new MusicalKey(0, false), KeyEstimator.Estimate(Scale([60, 62, 64, 65, 67, 69, 71], [4, 1, 2, 1, 3, 1, 1])));
        Assert.Equal(new MusicalKey(4, false), KeyEstimator.Estimate(Scale([64, 66, 68, 69, 71, 73, 75], [4, 1, 2, 1, 3, 1, 1])));
        Assert.Equal(new MusicalKey(-2, false), KeyEstimator.Estimate(Scale([58, 60, 62, 63, 65, 67, 69], [4, 1, 2, 1, 3, 1, 1])));
        // A harmonic minor: A, B, C, D, E, F, G#
        Assert.Equal(new MusicalKey(0, true), KeyEstimator.Estimate(Scale([57, 59, 60, 62, 64, 65, 68], [4, 1, 3, 1, 3, 1, 1])));
    }

    [Theory]
    [InlineData(2, false, 66, 'F', 1, 4)]   // D major: F#
    [InlineData(-1, false, 70, 'B', -1, 4)] // F major: Bb
    [InlineData(0, true, 68, 'G', 1, 4)]    // A minor: leading tone G#
    [InlineData(0, false, 63, 'E', -1, 4)]  // C major: Eb
    [InlineData(-3, false, 61, 'D', -1, 4)] // Eb major: Db
    [InlineData(7, false, 60, 'B', 1, 3)]   // C# major: B#3 sounds as middle C
    [InlineData(-6, false, 71, 'C', -1, 5)] // Gb major: Cb5 sounds as B4
    public void Pitches_are_spelled_for_the_key(int fifths, bool minor, int midi, char step, int alter, int octave)
    {
        Assert.Equal((step, alter, octave), new MusicalKey(fifths, minor).Spell(midi));
    }

    [Fact]
    public void Key_names_and_signatures()
    {
        Assert.Equal("E♭ minor", MusicalKey.FromTonic(3, minor: true).Name);
        Assert.Equal("F♯ major", MusicalKey.FromTonic(6, minor: false).Name);
        Assert.Equal("D minor", MusicalKey.FromTonic(2, minor: true).Name);
        Assert.Equal("3♭", MusicalKey.FromTonic(3, minor: false).SignatureName);
    }

    [Fact]
    public void Durations_split_into_readable_values()
    {
        string Show(int start, int length, int beats = 4) =>
            string.Join(" ", ScoreBuilder.Split(start, length, 4, beats).Select(p => $"{p.Start}:{p.Value.Type}{new string('.', p.Value.Dots)}"));

        Assert.Equal("0:whole", Show(0, 16));
        Assert.Equal("1:eighth. 4:16th", Show(1, 4));          // off-beat note tied at the beat
        Assert.Equal("4:quarter 8:quarter", Show(4, 8));       // beat 2 note does not cross mid-bar
        Assert.Equal("8:half", Show(8, 8));
        Assert.Equal("0:half.", Show(0, 12, beats: 3));
        Assert.Equal("0:quarter..", Show(0, 7));
    }

    [Fact]
    public void Notes_across_bar_lines_are_tied()
    {
        var grid = BarGrid.Create(Tempo120, 4, 0, [N(0, 1, 67)]);
        var score = ScoreBuilder.Build([N(0, 1, 67), N(3.5, 5.5, 72)], grid, new ScoreSettings { Staves = StaffLayout.Treble });
        // The note ends half a beat short of beat 6; that small gap is closed, giving a half note.

        Assert.Equal(2, score.MeasureCount);
        var voice = Assert.Single(score.Voices);
        var m1 = voice.Measures[0].Where(e => e.Kind == EventKind.Note).ToList();
        var m2 = voice.Measures[1].Where(e => e.Kind == EventKind.Note).ToList();

        Assert.Equal(("eighth", 0), (m1[^1].Value.Type, m1[^1].Value.Dots));
        Assert.True(m1[^1].TieToNext);
        Assert.Equal(("half", 0), (m2[0].Value.Type, m2[0].Value.Dots));
        Assert.True(m2[0].TieFromPrevious);
        Assert.Equal(('C', 0, 5), (m2[0].Pitches[0].Step, m2[0].Pitches[0].Alter, m2[0].Pitches[0].Octave));
    }

    [Fact]
    public void Small_gaps_close_but_real_rests_stay()
    {
        DetectedNote[] notes = [N(0, 0.9, 60), N(1, 2, 62), N(3, 4, 64)]; // 0.1 beat gap, then a full beat rest
        var grid = BarGrid.Create(Tempo120, 4, 0, notes);
        var events = ScoreBuilder.Build(notes, grid, new ScoreSettings { Staves = StaffLayout.Treble }).Voices[0].Measures[0];

        Assert.Equal(["quarter:Note", "quarter:Note", "quarter:Rest", "quarter:Note"], events.Select(e => $"{e.Value.Type}:{e.Kind}"));
    }

    [Fact]
    public void Chords_voices_and_staves()
    {
        DetectedNote[] notes =
        [
            N(0, 2, 60), N(0, 2, 64), N(0, 2, 67), // C major chord
            N(0, 4, 72),                           // long note over it -> second voice
            N(2, 4, 62),
            N(0, 4, 36), N(0, 4, 43),              // bass fifth
        ];
        var grid = BarGrid.Create(Tempo120, 4, 0, notes);
        var score = ScoreBuilder.Build(notes, grid, new ScoreSettings());

        Assert.Equal([Clef.Treble, Clef.Bass], score.Staves);
        var upper = score.Voices.Where(v => v.Staff == 1).ToList();
        Assert.Equal(2, upper.Count);
        Assert.Contains(upper.SelectMany(v => v.Measures[0]), e => e.Pitches.Select(p => p.Midi).SequenceEqual([60, 64, 67]));
        var lower = Assert.Single(score.Voices, v => v.Staff == 2);
        Assert.Equal(5, lower.Number);
        Assert.Equal([36, 43], lower.Measures[0][0].Pitches.Select(p => p.Midi));
    }

    [Fact]
    public void Downbeat_and_bar_grid()
    {
        // Long bass notes every 4 beats starting on detected beat 2, a new root each time, short notes elsewhere.
        var notes = new List<DetectedNote>();
        int[] roots = [40, 45, 38, 43];
        for (int bar = 0; bar < 8; bar++)
        {
            notes.Add(N(2 + bar * 4, 5.5 + bar * 4, roots[bar % 4]));
            notes.Add(N(3 + bar * 4, 3.5 + bar * 4, roots[bar % 4] + 28));
            notes.Add(N(4 + bar * 4, 4.5 + bar * 4, roots[bar % 4] + 31));
        }
        Assert.Equal(2, DownbeatEstimator.Estimate(notes, Tempo120, 4));

        // A pickup note half a bar before the downbeat starts bar 1 one bar earlier.
        var grid = BarGrid.Create(Tempo120 with { FirstBeatSeconds = 0.3 }, 4, 2, [N(0.5, 1, 60), N(2, 3, 62)]);
        Assert.Equal(0.3 + 2 * 0.5 - 2.0, grid.OriginSeconds, 6);
        // Leading silence is skipped: first note in bar 3 -> bar 1 starts there.
        grid = BarGrid.Create(Tempo120, 4, 0, [N(9, 10, 60)]);
        Assert.Equal(4.0, grid.OriginSeconds, 6);
    }

    [Fact]
    public void Chord_changes_place_the_downbeat_when_notes_are_alike()
    {
        // Even quarter-note chords, the same length and loudness on every beat, changing chord on detected
        // beat 1 of each four: C, F, G, Am.
        int[][] chords = [[48, 52, 55], [53, 57, 60], [55, 59, 62], [57, 60, 64]];
        var notes = new List<DetectedNote>();
        for (int beat = 0; beat < 33; beat++)
        {
            var chord = chords[((beat + 3) / 4) % 4];
            foreach (int pitch in chord) notes.Add(N(beat, beat + 0.9, pitch));
        }
        Assert.Equal(1, DownbeatEstimator.Estimate(notes, Tempo120, 4));
    }

    [Fact]
    public void Aligned_midi_starts_bar_one_at_zero()
    {
        var notes = new[] { N(2.25, 3, 60), N(4, 5, 64) };
        var grid = BarGrid.Create(Tempo120 with { FirstBeatSeconds = 0.125 }, 3, 0, notes);
        var file = MidiExporter.Build(notes, alignTo: grid);
        var map = file.GetTempoMap();
        var first = file.GetNotes().First().TimeAs<BarBeatTicksTimeSpan>(map);

        Assert.Equal(3, map.GetTimeSignatureAtTime(new MidiTimeSpan(0)).Numerator);
        Assert.Equal(0, first.Bars);       // bar 1 (0-based)
        Assert.Equal(2, first.Beats);      // third beat: 2.25 beats - 0.25 beat phase
        Assert.Equal(0, first.Ticks);
    }

    [Fact]
    public async Task MusicXml_measures_add_up_for_a_real_transcription()
    {
        // Melody, chords and drums through the full pipeline.
        var audio = new float[Synth.Rate * 12];
        Synth.AddBeat(audio, 100, 0.2);
        int[] melody = [72, 74, 76, 77, 79, 77, 76, 74, 72, 71, 72, 74, 76, 74, 72, 71, 69, 71, 72];
        for (int i = 0; i < melody.Length; i++) Synth.AddTone(audio, melody[i], 0.2 + i * 0.6, 0.55, 0.15);
        foreach (var (t, chord) in new[] { (0.2, new[] { 48, 52, 55 }), (2.6, [53, 57, 60]), (5.0, [55, 59, 62]), (7.4, [48, 52, 55]) })
            foreach (var p in chord) Synth.AddTone(audio, p, t, 2.3, 0.1);

        var path = Path.Combine(Path.GetTempPath(), $"a2m_score_{Guid.NewGuid():N}.wav");
        var xmlPath = Path.ChangeExtension(path, ".musicxml");
        WavReader.WriteFloat(path, new AudioData(audio, Synth.Rate));
        try
        {
            using var session = new TranscriptionSession();
            await session.LoadAsync(path, "/nonexistent");
            await session.AnalyzeAsync(new AnalysisSettings());
            var notes = session.GetNotes(new NoteSettings { QuantizeDivisions = 4 });
            var tempo = session.Tempo!;
            int downbeat = DownbeatEstimator.Estimate(notes, tempo, 4);
            var grid = BarGrid.Create(tempo, 4, downbeat, notes);
            var score = ScoreBuilder.Build(notes, grid, new ScoreSettings { Title = "Test" });

            Assert.Equal(new MusicalKey(0, false), score.Key); // C major material
            Assert.Equal([Clef.Treble, Clef.Bass], score.Staves);

            // Model level: every non-empty voice fills its measure exactly, with no gaps.
            foreach (var voice in score.Voices)
                foreach (var events in voice.Measures.Where(e => e.Count > 0))
                {
                    int pos = 0;
                    foreach (var e in events) { Assert.Equal(pos, e.Start); pos += e.Duration; }
                    Assert.Equal(score.MeasureTicks, pos);
                }

            // XML level: add up note, forward and backup durations in each measure.
            MusicXmlWriter.Write(xmlPath, score);
            var doc = XDocument.Load(xmlPath);
            Assert.Equal("score-partwise", doc.Root!.Name.LocalName);
            int notesWritten = 0;
            foreach (var measure in doc.Descendants("measure"))
            {
                int pos = 0, max = 0;
                foreach (var el in measure.Elements())
                {
                    int d = (int?)el.Element("duration") ?? 0;
                    switch (el.Name.LocalName)
                    {
                        case "note" when el.Element("chord") is null: pos += d; break;
                        case "forward": pos += d; break;
                        case "backup": pos -= d; break;
                    }
                    Assert.InRange(pos, 0, score.MeasureTicks);
                    max = Math.Max(max, pos);
                }
                Assert.Equal(score.MeasureTicks, max);
                Assert.Equal(score.MeasureTicks, pos);
                notesWritten += measure.Elements("note").Count(n => n.Element("pitch") is not null && n.Element("tie")?.Attribute("type")?.Value != "stop");
            }
            Assert.True(notesWritten >= notes.Count * 0.9, $"{notesWritten} notes written for {notes.Count} transcribed");
        }
        finally
        {
            File.Delete(path);
            File.Delete(xmlPath);
        }
    }
}
