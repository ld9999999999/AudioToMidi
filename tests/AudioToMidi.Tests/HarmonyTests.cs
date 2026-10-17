using System.Xml.Linq;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Chord = AudioToMidi.Core.Harmony.Chord;

namespace AudioToMidi.Tests;

public class HarmonyTests
{
    static readonly BarGrid Grid = BarGrid.Steady(0, 0.5, 4); // 120 BPM, 2 s bars

    /// <summary>A bar of a chord: the tones held for the bar, the bass note on beats 1 and 3.</summary>
    static IEnumerable<DetectedNote> Bar(int bar, int bass, params int[] tones)
    {
        double start = bar * 2.0;
        foreach (var p in tones) yield return new DetectedNote(start, start + 1.95, p, 0.6f);
        yield return new DetectedNote(start, start + 0.95, bass, 0.8f);
        yield return new DetectedNote(start + 1, start + 1.95, bass, 0.8f);
    }

    [Fact]
    public void Recognizes_a_progression_with_its_numerals()
    {
        // C – Am – F – G7, twice.
        var notes = Enumerable.Range(0, 8).SelectMany(bar => (bar % 4) switch
        {
            0 => Bar(bar, 36, 60, 64, 67),
            1 => Bar(bar, 45, 60, 64, 69),
            2 => Bar(bar, 41, 60, 65, 69),
            _ => Bar(bar, 43, 59, 62, 65, 67),
        }).ToList();

        var harmony = HarmonyAnalysis.Analyze(notes, Grid);
        var chords = harmony.Chords.Where(c => c.Chord is not null).ToList();
        Assert.Equal(["C", "Am", "F", "G7", "C", "Am", "F", "G7"], chords.Select(harmony.Symbol));
        Assert.Equal(["I", "vi", "IV", "V7"], chords.Take(4).Select(c => harmony.Roman(c)));
        Assert.Equal(2.0, chords[1].Start, 9);
        Assert.Equal("C major", Assert.Single(harmony.Keys).Key.Name);
    }

    [Fact]
    public void Power_chords_and_inversions()
    {
        var notes = Bar(0, 40, 47, 52).Concat(Bar(1, 52, 60, 67)).ToList(); // E5, then C with E in the bass
        var harmony = HarmonyAnalysis.Analyze(notes, Grid);
        Assert.Equal(["E5", "C/E"], harmony.Chords.Where(c => c.Chord is not null).Select(harmony.Symbol));
    }

    [Fact]
    public void Follows_a_change_of_key()
    {
        // Twelve bars of I–IV–V–I in C major, then twelve in E♭ major.
        var notes = Enumerable.Range(0, 24).SelectMany(bar =>
        {
            int t = bar < 12 ? 0 : 3;
            return (bar % 4) switch
            {
                0 or 3 => Bar(bar, 36 + t, 60 + t, 64 + t, 67 + t),
                1 => Bar(bar, 41 + t, 60 + t, 65 + t, 69 + t),
                _ => Bar(bar, 43 + t, 59 + t, 62 + t, 67 + t),
            };
        }).ToList();

        var keys = HarmonyAnalysis.Analyze(notes, Grid).Keys;
        Assert.Equal(["C major", "E♭ major"], keys.Select(k => k.Key.Name));
        Assert.Equal(24, keys[1].Start, 6); // bar 13
    }

    [Fact]
    public void Places_a_change_of_key_at_the_bar_it_happens()
    {
        // Eight bars of I–IV–V–I in C major, then eight in E major: the eight-bar view alone puts it a bar late.
        var notes = Enumerable.Range(0, 16).SelectMany(bar =>
        {
            int t = bar < 8 ? 0 : 4;
            return (bar % 4) switch
            {
                0 or 3 => Bar(bar, 36 + t, 60 + t, 64 + t, 67 + t),
                1 => Bar(bar, 41 + t, 60 + t, 65 + t, 69 + t),
                _ => Bar(bar, 43 + t, 59 + t, 62 + t, 67 + t),
            };
        }).ToList();

        var keys = HarmonyAnalysis.Analyze(notes, Grid).Keys;
        Assert.Equal(["C major", "E major"], keys.Select(k => k.Key.Name));
        Assert.Equal(16, keys[1].Start, 6); // bar 9
    }

    [Theory]
    [InlineData(10, ChordQuality.Major, 0, false, "♭VII")]
    [InlineData(8, ChordQuality.Diminished, 9, true, "vii°")]  // G♯dim in A minor
    [InlineData(7, ChordQuality.Dominant7, 0, false, "V7")]
    [InlineData(2, ChordQuality.Minor7, 0, false, "ii7")]
    [InlineData(5, ChordQuality.Major, 2, true, "III")]         // F in D minor
    [InlineData(1, ChordQuality.Major, 0, false, "♭II")]        // the Neapolitan
    public void Roman_numerals(int root, ChordQuality quality, int tonic, bool minor, string expected) =>
        Assert.Equal(expected, new Chord(root, quality).Roman(MusicalKey.FromTonic(tonic, minor)));

    [Fact]
    public void Sheet_music_has_the_chord_symbols()
    {
        var notes = Bar(0, 36, 60, 64, 67).Concat(Bar(1, 46, 62, 65, 70)).ToList(); // C, B♭
        var harmony = HarmonyAnalysis.Analyze(notes, Grid);
        var score = ScoreBuilder.Build(notes, Grid, new ScoreSettings { KeyFifths = 0, Chords = harmony.Chords });
        var xml = MusicXmlWriter.ToDocument(score);
        var symbols = xml.Descendants("harmony").Select(h =>
            h.Element("root")!.Element("root-step")!.Value + (h.Element("root")!.Element("root-alter")?.Value == "-1" ? "♭" : "") +
            h.Element("kind")!.Attribute("text")!.Value).ToList();
        Assert.Equal(["C", "B♭"], symbols);
        Assert.Equal(2, xml.Descendants("measure").Count(m => m.Element("harmony") is not null));
    }

    [Fact]
    public void Chords_are_voiced_with_the_bass_below_and_the_tones_around_middle_c()
    {
        Assert.Equal([40, 55, 60, 64], new Chord(0, ChordQuality.Major, Bass: 4).Voicing()); // C/E: E2, then G3 C4 E4
        Assert.Equal([46, 56, 58, 61, 65], new Chord(10, ChordQuality.Minor7).Voicing()); // B♭m7: B♭2, then A♭3 B♭3 D♭4 F4
    }

    [Fact]
    public void Midi_gets_a_track_of_held_chords_with_their_symbols()
    {
        // B♭ for a bar, then F/A: ASCII symbols in the text events.
        var notes = Bar(0, 46, 62, 65, 70).Concat(Bar(1, 45, 60, 65, 69)).ToList();
        var harmony = HarmonyAnalysis.Analyze(notes, Grid);
        var file = MidiExporter.Build(notes, [new Core.Tracks.TrackInfo("Piano", 0, StaffLayout.Auto)], alignTo: Grid, chords: harmony);

        var chunks = file.GetTrackChunks().ToList();
        Assert.Equal(3, chunks.Count); // tempo, piano, chords
        var chordTrack = chunks[^1];
        Assert.Equal("Chords", chordTrack.Events.OfType<SequenceTrackNameEvent>().Single().Text);
        Assert.Equal(MidiExporter.ChordProgram, (int)chordTrack.Events.OfType<ProgramChangeEvent>().Single().ProgramNumber);
        var texts = chordTrack.GetTimedEvents().Where(e => e.Event is TextEvent).Select(e => (e.Time, ((TextEvent)e.Event).Text));
        Assert.Equal([(0L, "Bb"), (1920L, "F/A")], texts); // bar 2 at 4 × 480 ticks
        var held = chordTrack.GetNotes()
            .GroupBy(n => n.Time).Select(g => (g.Key, g.Select(n => (int)n.NoteNumber).Order().ToArray(), g.First().Length)).ToList();
        Assert.Equal(0L, held[0].Key);
        Assert.Equal(new Chord(10, ChordQuality.Major).Voicing(), held[0].Item2);
        Assert.Equal(1920L, held[0].Length);
        Assert.Equal([45, 57, 60, 65], held[1].Item2); // F/A: A2, then A3 C4 F4
    }
}
