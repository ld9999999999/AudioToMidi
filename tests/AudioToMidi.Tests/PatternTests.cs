using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Tests;

/// <summary>Bar patterns: stretches of bars a part repeats, as clips.</summary>
public class PatternTests
{
    static readonly BarGrid Grid = BarGrid.Steady(0, 0.5, 4); // 2 s bars

    /// <summary>Notes at beats from the start of <paramref name="bar"/>, an eighth long.</summary>
    static IEnumerable<DetectedNote> Bar(int bar, params (double Beat, int Pitch)[] notes) =>
        notes.Select(n => new DetectedNote(bar * 2 + n.Beat * 0.5, bar * 2 + n.Beat * 0.5 + 0.25, n.Pitch, 0.7f));

    static readonly (double, int)[] RiffA = [(0, 40), (0.5, 40), (1, 43), (2, 45), (3, 43)];
    static readonly (double, int)[] RiffB = [(0, 38), (1, 38), (1.5, 41), (2, 43), (3.5, 45)];

    [Fact]
    public void A_riff_of_two_different_bars_is_one_two_bar_pattern()
    {
        var notes = Enumerable.Range(0, 8).SelectMany(bar => Bar(bar, bar % 2 == 0 ? RiffA : RiffB)).ToList();
        var pattern = Assert.Single(PatternFinder.Find(notes, Grid));
        Assert.Equal(2, pattern.Bars);
        Assert.Equal([0, 2, 4, 6], pattern.Occurrences);
        Assert.Equal(10, pattern.Notes.Count);
        Assert.Equal(new PatternNote(4 + 1.5, 0.5, 41, 89), pattern.Notes.Single(n => n.Pitch == 41)); // in the second bar of the clip
    }

    [Fact]
    public void A_beat_repeated_with_a_fill_is_a_one_bar_pattern_without_the_fill()
    {
        (double, int)[] beat = [(0, 36), (1, 38), (2, 36), (2.5, 36), (3, 38), .. Enumerable.Range(0, 8).Select(i => (i * 0.5, 42))];
        (double, int)[] fill = [(0, 36), (1, 38), (1.5, 38), (2, 45), (2.5, 45), (3, 41), (3.5, 41)];
        var notes = Enumerable.Range(0, 8).SelectMany(bar => Bar(bar, bar == 7 ? fill : beat)).ToList();
        var pattern = Assert.Single(PatternFinder.Find(notes, Grid));
        Assert.Equal(1, pattern.Bars);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], pattern.Occurrences);
    }

    [Fact]
    public void A_repeat_with_a_missed_note_still_counts()
    {
        var notes = Enumerable.Range(0, 4).SelectMany(bar => Bar(bar, bar == 2 ? RiffA[1..] : RiffA)).ToList();
        Assert.Equal([0, 1, 2, 3], Assert.Single(PatternFinder.Find(notes, Grid)).Occurrences);
    }

    [Fact]
    public void Music_that_never_repeats_has_no_patterns()
    {
        var notes = Enumerable.Range(0, 4).SelectMany(bar => Bar(bar, (0, 60 + bar * 2), (1, 64 + bar), (2.5, 55 + bar * 3))).ToList();
        Assert.Empty(PatternFinder.Find(notes, Grid));
        Assert.Empty(PatternFinder.Find(Bar(0, RiffA).ToList(), Grid)); // one bar
    }

    [Fact]
    public void A_clip_saves_as_midi_of_exactly_its_length()
    {
        var notes = Enumerable.Range(0, 8).SelectMany(bar => Bar(bar, bar % 2 == 0 ? RiffA : RiffB)).ToList();
        var clip = Assert.Single(PatternFinder.Find(notes, Grid));
        var file = Core.Midi.MidiExporter.BuildClip(clip, new Core.Tracks.TrackInfo("Bass", 33, StaffLayout.Auto), Meter.Common, 120);

        var events = Melanchall.DryWetMidi.Interaction.TimedEventsManagingUtilities.GetTimedEvents(file).ToList();
        Assert.Equal(2 * 4 * 480, events.Max(e => e.Time)); // two bars of quarters at 480 ticks
        var midiNotes = Melanchall.DryWetMidi.Interaction.NotesManagingUtilities.GetNotes(file).ToList();
        Assert.Equal(10, midiNotes.Count);
        Assert.Equal(4 * 480 + 720, midiNotes.Single(n => n.NoteNumber == 41).Time); // beat 1.5 of the second bar
        Assert.Equal(33, (int)events.Select(e => e.Event).OfType<Melanchall.DryWetMidi.Core.ProgramChangeEvent>().Single().ProgramNumber);
        Assert.Equal(120, Melanchall.DryWetMidi.Interaction.TempoMapManagingUtilities.GetTempoMap(file)
            .GetTempoAtTime(new Melanchall.DryWetMidi.Interaction.MidiTimeSpan(0)).BeatsPerMinute, 6);
    }
}
