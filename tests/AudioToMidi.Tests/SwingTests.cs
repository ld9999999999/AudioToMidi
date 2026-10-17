using System.Xml.Linq;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Tests;

/// <summary>Swung eighths: found, quantized where they sound, and written straight.</summary>
public class SwingTests
{
    static readonly TempoEstimate Tempo120 = new(120, 0, 1); // beats of 0.5 s

    /// <summary>
    /// Bars of 4/4: a bass note on each beat (track 1), and a tune of long-short eighth pairs, the short one
    /// at <paramref name="swing"/> of the beat (76 or 77), its onsets found <paramref name="late"/> seconds late.
    /// </summary>
    static List<DetectedNote> Swung(double swing, int bars = 16, double late = 0)
    {
        var notes = new List<DetectedNote>();
        for (int beat = 0; beat < bars * 4; beat++)
        {
            double t = beat * 0.5;
            notes.Add(new DetectedNote(t, t + 0.45, 40 + beat % 5, 0.7f, Track: 1));
            notes.Add(new DetectedNote(t + late, t + swing * 0.5 - 0.02, 72 + beat % 3, 0.6f));
            notes.Add(new DetectedNote(t + swing * 0.5 + late, t + 0.48, 76 + beat % 2, 0.5f));
        }
        return notes;
    }

    [Fact]
    public void Swung_and_straight_positions_map_both_ways()
    {
        Assert.Equal(2.62, Swing.ToSwung(2.5, 0.62), 9);   // the off-beat eighth
        Assert.Equal(2.31, Swing.ToSwung(2.25, 0.62), 9);  // the sixteenth before it, halfway to it
        Assert.Equal(2.81, Swing.ToSwung(2.75, 0.62), 9);  // and after it
        foreach (double b in new[] { 0.0, 0.1, 0.5, 0.77, 3.99 })
            Assert.Equal(b, Swing.ToStraight(Swing.ToSwung(b, 0.6), 0.6), 9);
        Assert.False(Swing.Applies(3, 0.62)); // triplet grids don't swing
        Assert.False(Swing.Applies(4, 0.5));
    }

    [Fact]
    public void Swing_is_found_and_straight_eighths_are_not()
    {
        Assert.Equal(0.62, GrooveEstimator.Swing(Swung(0.62), Tempo120), 2);
        Assert.Equal(0.67, GrooveEstimator.Swing(Swung(2 / 3.0), Tempo120), 2);
        Assert.Equal(0.5, GrooveEstimator.Swing(Swung(0.5), Tempo120));
    }

    [Fact]
    public void A_late_instrument_does_not_make_the_swing_wider()
    {
        // The tune's onsets found 40 ms (0.08 beats) late, as soft attacks are, against a bass on time.
        Assert.Equal(0.62, GrooveEstimator.Swing(Swung(0.62, late: 0.04), Tempo120), 2);
    }

    [Fact]
    public void Quantizing_keeps_the_swing_even_when_onsets_are_found_late()
    {
        var notes = Swung(0.62, late: 0.04).Select(n => n with { Start = n.Start + (n.Pitch == 76 ? 0.01 : 0) }).ToList();
        var swungGrid = NoteFilter.Apply(notes, new NoteSettings { QuantizeDivisions = 4, Swing = 0.62, MinNoteMs = 0 }, Tempo120);
        var offbeats = swungGrid.Where(n => n.Pitch is 76 or 77).Select(n => n.Start / 0.5 % 1).ToList();
        Assert.All(offbeats, f => Assert.Equal(0.62, f, 6)); // the swung eighth itself
        var straightGrid = NoteFilter.Apply(notes, new NoteSettings { QuantizeDivisions = 2, MinNoteMs = 0 }, Tempo120);
        Assert.All(straightGrid.Where(n => n.Pitch is 76 or 77), n => Assert.Equal(0.5, n.Start / 0.5 % 1, 6));
    }

    [Fact]
    public void Swung_eighths_are_written_straight_under_a_swing_mark()
    {
        // A triplet shuffle. On a straight sixteenth grid its off-beats would land on the last sixteenth.
        double shuffle = 2 / 3.0;
        var notes = NoteFilter.Apply(Swung(shuffle, bars: 2).Where(n => n.Track == 0),
            new NoteSettings { QuantizeDivisions = 4, Swing = shuffle, MinNoteMs = 0 }, Tempo120);
        var grid = BarGrid.Steady(0, 0.5, 4);
        var xml = MusicXmlWriter.ToDocument(ScoreBuilder.Build(notes, grid, new ScoreSettings { KeyFifths = 0, StepsPerBeat = 4, Swing = shuffle }));
        var types = xml.Descendants("note").Where(n => n.Element("rest") is null).Select(n => n.Element("type")!.Value).Distinct();
        Assert.Equal(["eighth"], types); // plain eighths
        Assert.Empty(xml.Descendants("dot"));
        Assert.Equal("Swing", xml.Descendants("words").Single().Value);
        var swing = xml.Descendants("swing").Single();
        Assert.Equal(("67", "33", "eighth"), (swing.Element("first")!.Value, swing.Element("second")!.Value, swing.Element("swing-type")!.Value));

        // Written as played, the same notes need sixteenths, and get no swing mark.
        var plain = MusicXmlWriter.ToDocument(ScoreBuilder.Build(notes, grid, new ScoreSettings { KeyFifths = 0, StepsPerBeat = 4 }));
        Assert.Contains(plain.Descendants("note"), n => n.Element("type")?.Value == "16th");
        Assert.Empty(plain.Descendants("words"));
    }

    [Fact]
    public void The_swing_ratio_is_written_in_lowest_terms()
    {
        var xml = MusicXmlWriter.ToDocument(ScoreBuilder.Build(Swung(0.62, bars: 1).Where(n => n.Track == 0), BarGrid.Steady(0, 0.5, 4),
            new ScoreSettings { KeyFifths = 0, Swing = 0.62 }));
        var swing = xml.Descendants("swing").Single();
        Assert.Equal(("31", "19"), (swing.Element("first")!.Value, swing.Element("second")!.Value));
    }

    [Fact]
    public void Long_short_pairs_are_swing_rather_than_a_compound_meter()
    {
        // Onsets fall only on the second third of each beat, which reads as swung 4/4.
        var estimate = MeterEstimator.Estimate(Swung(2 / 3.0), Tempo120);
        Assert.False(estimate.Meter.IsCompound, estimate.Description);
    }
}
