using AudioToMidi.Core;
using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Xunit.Abstractions;

namespace AudioToMidi.Tests;

public class TranscriptionTests(ITestOutputHelper output)
{
    static readonly NoteSettings Defaults = new();

    void Dump(string label, IEnumerable<DetectedNote> notes)
    {
        output.WriteLine(label);
        foreach (var n in notes)
            output.WriteLine($"  {n.Start,6:F2}-{n.End,6:F2}  {DetectedNote.PitchName(n.Pitch),-4} ({n.Pitch}) vel {n.Velocity}");
    }

    static async Task<IReadOnlyList<DetectedNote>> BasicPitch(float[] audio, bool removeDrums, NoteSettings? s = null)
    {
        using var session = new TranscriptionSession();
        var path = Path.Combine(Path.GetTempPath(), $"a2m_test_{Guid.NewGuid():N}.wav");
        WavReader.WriteFloat(path, new AudioData(audio, Synth.Rate));
        try
        {
            await session.LoadAsync(path, ffmpegPath: "/nonexistent"); // force the WAV fallback
            await session.AnalyzeAsync(new AnalysisSettings { Engine = EngineKind.BasicPitch, RemovePercussion = removeDrums });
            return session.GetNotes(s ?? Defaults);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BasicPitch_transcribes_a_melody_and_a_chord()
    {
        var audio = new float[Synth.Rate * 4];
        int[] melody = [60, 64, 67, 72];
        for (int i = 0; i < melody.Length; i++) Synth.AddTone(audio, melody[i], i * 0.5, 0.45);
        foreach (var p in new[] { 60, 64, 67 }) Synth.AddTone(audio, p, 2.5, 1.0);

        var notes = await BasicPitch(audio, removeDrums: false);
        Dump("Melody + chord", notes);

        for (int i = 0; i < melody.Length; i++)
        {
            double t = i * 0.5;
            Assert.Contains(notes, n => n.Pitch == melody[i] && Math.Abs(n.Start - t) < 0.06);
        }
        foreach (var p in new[] { 60, 64, 67 })
            Assert.Contains(notes, n => n.Pitch == p && Math.Abs(n.Start - 2.5) < 0.06);
    }

    [Fact]
    public async Task Drum_removal_reduces_spurious_notes()
    {
        var audio = new float[Synth.Rate * 8];
        int[] melody = [57, 60, 62, 64, 65, 64, 62, 60];
        for (int i = 0; i < melody.Length; i++) Synth.AddTone(audio, melody[i], i * 1.0, 0.9, 0.2);
        Synth.AddBeat(audio, 120);

        var withDrums = await BasicPitch(audio, removeDrums: false);
        var withoutDrums = await BasicPitch(audio, removeDrums: true);
        Dump("Drums kept", withDrums);
        Dump("Drums removed", withoutDrums);

        int Spurious(IEnumerable<DetectedNote> notes) => notes.Count(n => !melody.Contains(n.Pitch));
        int Found(IEnumerable<DetectedNote> notes) =>
            melody.Select((p, i) => notes.Any(n => n.Pitch == p && Math.Abs(n.Start - i) < 0.08)).Count(x => x);

        output.WriteLine($"Spurious: kept={Spurious(withDrums)} removed={Spurious(withoutDrums)}");
        Assert.True(Spurious(withDrums) >= 5, "Test signal should provoke drum artefacts without separation");
        Assert.True(Spurious(withoutDrums) <= 2, $"{Spurious(withoutDrums)} spurious notes left after drum removal");
        // Drums also chop sustained notes into fragments at every hit.
        Assert.True(withoutDrums.Count(n => melody.Contains(n.Pitch)) <= melody.Length + 1, "Melody notes are fragmented");
        Assert.True(Found(withoutDrums) >= melody.Length - 1, $"Only found {Found(withoutDrums)} of {melody.Length} melody notes");
    }

    [Fact]
    public async Task Yin_transcribes_a_monophonic_line()
    {
        var audio = new float[Synth.Rate * 3];
        int[] melody = [45, 52, 57, 69, 76];
        for (int i = 0; i < melody.Length; i++) Synth.AddSine(audio, Synth.MidiToHz(melody[i]), 0.1 + i * 0.55, 0.5);

        var analysis = await new YinEngine().AnalyzeAsync(new AudioData(audio, Synth.Rate));
        var notes = analysis.Decode(Defaults);
        Dump("YIN", notes);

        Assert.Equal(melody, notes.Select(n => n.Pitch));
        for (int i = 0; i < melody.Length; i++)
        {
            Assert.InRange(notes[i].Start, 0.1 + i * 0.55 - 0.06, 0.1 + i * 0.55 + 0.06);
            Assert.InRange(notes[i].Duration, 0.38, 0.6);
        }
    }

    [Fact]
    public void Decoder_finds_a_note_from_onset_and_frame_activations()
    {
        int frames = 100, p = 39; // MIDI 60
        var note = new float[frames * 88];
        var onset = new float[frames * 88];
        for (int t = 20; t < 60; t++) note[t * 88 + p] = 0.8f;
        onset[20 * 88 + p] = 0.9f;

        var notes = BasicPitchDecoder.Decode(note, onset, frames, 0.5, 0.3, minNoteFrames: 11);

        var n = Assert.Single(notes);
        Assert.Equal(60, n.Pitch);
        Assert.Equal(20 * 256.0 / 22050, n.Start, 3);
        Assert.Equal(60 * 256.0 / 22050, n.End, 3);
        Assert.Equal(102, n.Velocity); // round(127 * 0.8)
    }

    [Fact]
    public void Filter_merges_and_quantizes()
    {
        var tempo = new TempoEstimate(120, 0, 1); // 0.5 s beats, 0.125 s sixteenths
        DetectedNote[] input =
        [
            new(0.02, 0.40, 60, 0.5f),
            new(0.43, 0.70, 60, 0.5f), // 30 ms gap -> merged
            new(1.01, 1.20, 64, 0.5f),
            new(2.00, 2.03, 67, 0.5f), // too short, removed when not quantizing
        ];

        var merged = NoteFilter.Apply(input, Defaults with { MergeGapMs = 50 }, tempo);
        Assert.Equal([60, 64], merged.Select(n => n.Pitch));
        Assert.Equal(0.70, merged[0].End, 3);

        var quantized = NoteFilter.Apply(input, Defaults with { QuantizeDivisions = 4 }, tempo);
        Assert.All(quantized, n => Assert.Equal(0, n.Start / 0.125 - Math.Round(n.Start / 0.125), 6));
        Assert.Contains(quantized, n => n.Pitch == 67 && n.Duration >= 0.125 - 1e-9);
    }

    [Fact]
    public void MidiExport_round_trips_timing_and_velocity()
    {
        DetectedNote[] notes = [new(0.5, 1.0, 60, 0.5f), new(1.0, 2.0, 64, 1f)];
        var path = Path.GetTempFileName();
        try
        {
            MidiExporter.Write(path, notes, bpm: 90, program: 33);
            var file = MidiFile.Read(path);
            var tempoMap = file.GetTempoMap();
            var read = file.GetNotes().ToList();

            Assert.Equal(2, read.Count);
            Assert.Equal(90, tempoMap.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 0);
            var first = read[0].TimeAs<MetricTimeSpan>(tempoMap);
            Assert.Equal(500, first.TotalMilliseconds, 0);
            Assert.Equal(64, read[0].Velocity);
            Assert.Equal(127, read[1].Velocity);
            Assert.Contains(file.GetTimedEvents(), e => e.Event is ProgramChangeEvent pc && pc.ProgramNumber == 33);
        }
        finally { File.Delete(path); }
    }
}
