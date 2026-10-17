using System.Xml.Linq;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioToMidi.Tests;

/// <summary>Compound meters, key changes and tempo marks in sheet music and MIDI.</summary>
public class MeterTests
{
    static DetectedNote N(double start, double end, int pitch) => new(start, end, pitch, 0.7f);

    static List<XElement> Measures(Score score) => MusicXmlWriter.ToDocument(score).Descendants("measure").ToList();

    [Fact]
    public void Meters_count_their_felt_beats()
    {
        Assert.Equal((4, 1.0, 2), (Meter.Common.BeatsPerBar, Meter.Common.BeatQuarters, Meter.Common.Subdivision));
        var sixEight = new Meter(6, 8);
        Assert.Equal((true, 2, 1.5, 3), (sixEight.IsCompound, sixEight.BeatsPerBar, sixEight.BeatQuarters, sixEight.Subdivision));
        Assert.Equal(4, new Meter(12, 8).BeatsPerBar);
        Assert.Equal([1, 3, 3, 6, 12], new[] { 1, 2, 3, 4, 8 }.Select(sixEight.StepsPerBeat));
        Assert.Equal(new Meter(12, 8), Meter.Parse("12/8"));
        Assert.Null(Meter.Parse("4/5"));
    }

    [Fact]
    public void Six_eight_is_written_in_dotted_quarter_beats_of_three_eighths()
    {
        // 80 dotted quarters a minute: a beat of 0.75 s, an eighth 0.25 s. Three eighths, then a dotted quarter.
        var grid = BarGrid.Steady(0, 0.75, 2);
        var notes = new[] { N(0, 0.25, 60), N(0.25, 0.5, 62), N(0.5, 0.75, 64), N(0.75, 1.5, 65) };
        var score = ScoreBuilder.Build(notes, grid, new ScoreSettings { Meter = new Meter(6, 8), KeyFifths = 0 });

        Assert.Equal(1, score.MeasureCount);
        var measure = Measures(score)[0];
        var time = measure.Descendants("time").Single();
        Assert.Equal(("6", "8"), (time.Element("beats")!.Value, time.Element("beat-type")!.Value));
        var notesOut = measure.Elements("note").ToList();
        Assert.Equal(["eighth", "eighth", "eighth", "quarter"], notesOut.Select(n => n.Element("type")!.Value));
        Assert.True(notesOut[3].Element("dot") is not null, "the second beat is a dotted quarter");
        Assert.Equal(score.MeasureTicks, notesOut.Sum(n => int.Parse(n.Element("duration")!.Value)));
        Assert.Equal(["begin", "continue", "end"], notesOut.Take(3).Select(n => n.Element("beam")!.Value)); // one beat, one beam

        // The tempo mark counts dotted quarters; playback tempo is in quarter notes.
        var metronome = measure.Descendants("metronome").Single();
        Assert.NotNull(metronome.Element("beat-unit-dot"));
        Assert.Equal("80", metronome.Element("per-minute")!.Value);
        Assert.Equal("120", measure.Descendants("sound").Single().Attribute("tempo")!.Value);
    }

    [Fact]
    public void A_change_of_key_gets_a_new_signature_and_its_own_spelling()
    {
        // 4/4 at 120: bars of 2 s. C major for two bars, then E major. B-flat/A-sharp (70) in bar 1 and bar 3.
        var grid = BarGrid.Steady(0, 0.5, 4);
        var keys = new[] { new KeySpan(0, 4, new MusicalKey(0, false)), new KeySpan(4, 8, new MusicalKey(4, false)) };
        var notes = new[] { N(0, 1, 70), N(2, 3, 60), N(4, 5, 70), N(6, 7, 64) };
        var score = ScoreBuilder.Build(notes, grid, new ScoreSettings { Keys = keys });

        Assert.Equal([0, 0, 4, 4], score.MeasureKeys.Select(k => k.Fifths));
        var measures = Measures(score);
        Assert.Equal("4", measures[2].Element("attributes")!.Element("key")!.Element("fifths")!.Value);
        Assert.Null(measures[1].Element("attributes"));
        Assert.Equal("light-light", measures[1].Element("barline")!.Element("bar-style")!.Value); // a double bar before it
        string Spelled(XElement m) => m.Elements("note").Select(n => n.Element("pitch")).First(p => p is not null)!
            is var p ? $"{p.Element("step")!.Value}{p.Element("alter")?.Value}" : "";
        Assert.Equal("B-1", Spelled(measures[0]));
        Assert.Equal("A1", Spelled(measures[2]));
    }

    [Fact]
    public void Tempo_marks_follow_a_lasting_change_only()
    {
        // Eight bars at 120, then eight at 150, with one uneven beat in the first stretch.
        var beats = new List<double>();
        double t = 0;
        for (int i = 0; i < 32; i++) { beats.Add(t); t += i == 10 ? 0.56 : 0.5; }
        for (int i = 0; i < 33; i++) { beats.Add(t); t += 0.4; }
        var grid = new BarGrid(new BeatMap(beats), 4, 0);
        var notes = Enumerable.Range(0, 64).Select(b => N(grid.ToSeconds(b), grid.ToSeconds(b + 1), 60)).ToList();
        var score = ScoreBuilder.Build(notes, grid, new ScoreSettings { KeyFifths = 0 });
        Assert.Equal([new TempoMark(0, 120), new TempoMark(8, 150)], score.Tempos);
    }

    [Fact]
    public void Midi_in_six_eight_counts_dotted_quarters_and_carries_key_changes()
    {
        var grid = BarGrid.Steady(0, 0.75, 2); // 80 dotted quarters a minute = 120 quarters
        var notes = new[] { N(0, 0.25, 60), N(0.75, 1.5, 64), N(1.5, 2.25, 66) };
        var keys = new[] { new KeySpan(0, 1.5, new MusicalKey(0, false)), new KeySpan(1.5, 10, new MusicalKey(2, false)) };
        var file = MidiExporter.Build(notes, [new Core.Tracks.TrackInfo("Piano", 0, StaffLayout.Auto)], alignTo: grid,
            meter: new Meter(6, 8), keys: keys);
        var map = file.GetTempoMap();

        var signature = map.GetTimeSignatureAtTime(new MidiTimeSpan(0));
        Assert.Equal((6, 8), (signature.Numerator, signature.Denominator));
        Assert.Equal(120, map.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 1);
        Assert.Equal([0L, 720L, 1440L], file.GetNotes().Select(n => n.Time)); // a dotted quarter is 720 ticks at 480 a quarter
        var keyEvents = file.GetTimedEvents().Where(e => e.Event is KeySignatureEvent).ToList();
        Assert.Equal([(0L, 0), (1440L, 2)], keyEvents.Select(e => (e.Time, (int)((KeySignatureEvent)e.Event).Key)));
    }

    [Fact]
    public void A_third_of_the_tempo_keeps_the_chosen_beat_of_each_three()
    {
        var eighths = new BeatMap(Enumerable.Range(0, 13).Select(i => i * 0.25));
        var thirds = eighths.WithBpm(eighths.Bpm / 3, phase: 1);
        Assert.Equal([0.25, 1.0, 1.75, 2.5], thirds.Beats);
        var tripled = thirds.WithBpm(thirds.Bpm * 3);
        Assert.Equal(1.25, tripled.Beats[4], 9);
        Assert.Equal(10, tripled.Beats.Count); // two beats between each pair of four
    }

    [Fact]
    public void Downbeat_estimation_picks_the_beat_of_three_the_notes_fall_on()
    {
        // Eighths at 0.25 s; long notes start on every third one from the second.
        var tempo = new TempoEstimate(240, 0, 1);
        var notes = Enumerable.Range(0, 8).Select(i => N(0.25 + i * 0.75, 0.25 + i * 0.75 + 0.6, 48)).ToList();
        Assert.Equal(1, DownbeatEstimator.Estimate(notes, tempo, 3));
    }

    /// <summary>
    /// A piece on a steady beat of <paramref name="beatSeconds"/>: per bar of <paramref name="beatsPerBar"/>
    /// beats, a long bass note on the downbeat, chords on the other beats (or on <paramref name="chordBeats"/>),
    /// a melody note at each of <paramref name="melody"/> (in beats from the bar's start), and a new chord every bar.
    /// </summary>
    static List<DetectedNote> Piece(int beatsPerBar, double beatSeconds, double[] melody, int bars = 32, int firstBeat = 0, int[]? chordBeats = null)
    {
        int[] roots = [48, 53, 55, 45, 50, 52];
        var notes = new List<DetectedNote>();
        for (int bar = 0; bar < bars; bar++)
        {
            int root = roots[bar % roots.Length];
            double t(double beat) => (firstBeat + bar * beatsPerBar + beat) * beatSeconds;
            notes.Add(N(t(0), t(beatsPerBar * 0.8), root - 12));
            foreach (int beat in chordBeats ?? Enumerable.Range(1, beatsPerBar - 1).ToArray())
                foreach (int p in new[] { root + 4, root + 7, root + 12 }) notes.Add(new DetectedNote(t(beat), t(beat + 0.5), p, 0.4f));
            foreach (var m in melody) notes.Add(N(t(m), t(m) + 0.15, root + 16 + (int)(m * 2) % 3));
        }
        return notes;
    }

    [Fact]
    public void A_waltz_is_three_four()
    {
        var estimate = MeterEstimator.Estimate(Piece(3, 0.4, [0, 1, 2, 2.5]), new TempoEstimate(150, 0, 1));
        Assert.Equal((new Meter(3, 4), 1), (estimate.Meter, estimate.BeatDivisor));
    }

    [Fact]
    public void Four_beats_in_halves_are_four_four()
    {
        var estimate = MeterEstimator.Estimate(Piece(4, 0.5, [0, 0.5, 1, 2, 2.5, 3, 3.5]), new TempoEstimate(120, 0, 1));
        Assert.Equal(Meter.Common, estimate.Meter);
    }

    [Fact]
    public void Two_beats_in_thirds_are_compound()
    {
        // Dotted-quarter beats of 0.75 s with the tune on their eighths.
        var estimate = MeterEstimator.Estimate(Piece(2, 0.75, [0, 1 / 3.0, 2 / 3.0, 1, 4 / 3.0, 5 / 3.0]), new TempoEstimate(80, 0, 1));
        Assert.True(estimate.Meter.IsCompound, $"{estimate.Meter}: {estimate.Description}");
        Assert.Equal(1, estimate.BeatDivisor);
    }

    [Fact]
    public void Three_four_tracked_on_its_eighths_halves_the_tempo()
    {
        // Bars of six eighths (0.25 s) from the second tracked beat: the quarters are the odd tracked beats.
        var notes = Piece(6, 0.25, [0, 2, 4, 5], firstBeat: 1, chordBeats: [2, 4]);
        var tracked = new TempoEstimate(240, 0, 1);
        var estimate = MeterEstimator.Estimate(notes, tracked);
        Assert.Equal((new Meter(3, 4), 2, 1), (estimate.Meter, estimate.BeatDivisor, estimate.Phase));
        var beats = estimate.Apply(tracked);
        Assert.Equal(120, beats.Bpm, 1);
        Assert.Equal(0.25, beats.Map.ToSeconds(0), 6);
    }

    [Fact]
    public void Too_few_notes_are_four_four()
    {
        Assert.Equal(MeterEstimate.Default, MeterEstimator.Estimate([N(0, 1, 60), N(1, 2, 62)], new TempoEstimate(120, 0, 1)));
    }
}
