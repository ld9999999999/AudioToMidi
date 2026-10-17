using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Project;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Interaction;

namespace AudioToMidi.Tests;

public class BeatTests
{
    [Fact]
    public void Beat_map_converts_both_ways_and_continues_past_its_ends()
    {
        var map = new BeatMap([1.0, 1.5, 2.0, 2.4, 2.8]);
        Assert.Equal(0, map.ToBeat(1.0), 9);
        Assert.Equal(2.5, map.ToBeat(2.2), 9);
        Assert.Equal(2.2, map.ToSeconds(2.5), 9);
        Assert.Equal(-1, map.ToBeat(0.5), 9);  // the first spacing continues before
        Assert.Equal(3.2, map.ToSeconds(5), 9); // and the last after
        Assert.Equal(2.4, map.Snap(2.31), 9);
        Assert.Equal(2.2, map.Snap(2.21, 2), 9);
        Assert.False(map.IsSteady);
        Assert.True(BeatMap.Steady(0, 0.5).IsSteady);
    }

    [Fact]
    public void Double_and_half_tempo_keep_the_tracked_beats()
    {
        var tempo = new TempoEstimate(100, 1, 0.5) { Beats = [1.0, 1.6, 2.2, 2.8, 3.4] };

        var doubled = tempo.WithBpm(200);
        Assert.Equal(new[] { 1.0, 1.3, 1.6, 1.9, 2.2, 2.5, 2.8, 3.1, 3.4 }, doubled.Beats!.Select(b => Math.Round(b, 9)));
        Assert.Equal(new[] { 1.0, 2.2, 3.4 }, tempo.WithBpm(50).Beats!);

        // Any other tempo is steady from the first beat.
        var other = tempo.WithBpm(120);
        Assert.Null(other.Beats);
        Assert.Equal(1.0, other.FirstBeatSeconds);
        Assert.Equal(1.5, other.Map.ToSeconds(1), 9);
    }

    [Fact]
    public void Tracker_follows_a_change_of_tempo()
    {
        // 30 s at 100 BPM, then 30 s at 140: a kick on every beat and a quieter hi-hat between.
        var mix = new float[Synth.Rate * 60];
        var truth = new List<double>();
        for (double t = 0.2; t < 59.5; t += t < 30 ? 0.6 : 60.0 / 140) truth.Add(t);
        for (int i = 0; i < truth.Count; i++)
        {
            Synth.AddKick(mix, truth[i]);
            if (i + 1 < truth.Count) Synth.AddHit(mix, (truth[i] + truth[i + 1]) / 2, 0.2);
        }

        var sep = Hpss.Separate(mix, Synth.Rate);
        var track = BeatTracker.Track(sep.OnsetEnvelope, sep.EnvelopeRate, sep.LowOnsetEnvelope);

        // Every beat away from the change is found within 40 ms.
        var steady = truth.Where(b => Math.Abs(b - 30) > 4).ToList();
        int found = steady.Count(b => track.Beats.Any(x => Math.Abs(x - b) < 0.04));
        Assert.True(found >= steady.Count * 0.95, $"{found} of {steady.Count} beats found");
        var (slow, fast) = new BeatMap(track.Beats).BpmRange();
        Assert.InRange(slow, 97, 103);
        Assert.InRange(fast, 136, 144);
    }

    [Fact]
    public void Aligned_midi_follows_a_changing_tempo()
    {
        // Eight beats of 0.5 s, then beats of 0.4 s.
        var beats = Enumerable.Range(0, 9).Select(i => i * 0.5).Concat(Enumerable.Range(1, 8).Select(i => 4 + i * 0.4)).ToList();
        var grid = new BarGrid(new BeatMap(beats), 4, 0);
        var notes = new[] { new DetectedNote(0, 0.5, 60, 0.8f), new DetectedNote(4.8, 5.2, 62, 0.8f) }; // beats 0 and 10

        var file = MidiExporter.Build(notes, alignTo: grid);
        var map = file.GetTempoMap();
        Assert.Equal(500000, map.GetTempoAtTime(new MidiTimeSpan(8 * 480 - 1)).MicrosecondsPerQuarterNote);
        Assert.Equal(400000, map.GetTempoAtTime(new MidiTimeSpan(8 * 480)).MicrosecondsPerQuarterNote);
        var second = file.GetNotes().OrderBy(n => n.Time).ElementAt(1);
        Assert.Equal(10 * 480, second.Time);
        // Played back, it sounds when it did in the audio.
        Assert.Equal(4.8, second.TimeAs<MetricTimeSpan>(map).TotalMicroseconds / 1e6, 3);
    }

    [Fact]
    public void Bundle_timing_lists_uneven_beats()
    {
        var beats = new[] { 0.2, 0.7, 1.2, 1.6, 2.0, 2.4, 2.8 };
        var timing = ProjectExporter.Timing(new BarGrid(new BeatMap(beats), 4, 1), durationSeconds: 2.5, confidence: 0.5);
        Assert.Equal(beats, timing.Beats);
        Assert.Equal(0.7, timing.Beats[timing.FirstDownbeat], 9);
        Assert.Equal(1.4, timing.ToSeconds(1.5), 9);
    }

    [Fact]
    public void Quantizing_snaps_to_uneven_beats()
    {
        var tempo = new TempoEstimate(120, 0, 1) { Beats = [0, 0.5, 1.0, 1.4, 1.8, 2.2] };
        var notes = NoteFilter.Apply([new DetectedNote(1.38, 1.62, 60, 0.8f)], new NoteSettings { QuantizeDivisions = 2 }, tempo);
        Assert.Equal(1.4, notes[0].Start, 9);
        Assert.Equal(1.6, notes[0].End, 9); // half of the 0.4 s beat
    }
}
