using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioToMidi.Tests;

/// <summary>Pitch bends: found in basic-pitch's pitch contour, written to MIDI where a note sounds alone.</summary>
public class BendTests
{
    const int Frames = 100, Bins = 264;

    /// <summary>A note on MIDI 60 from frame 20 to 60, with the contour's peak following <paramref name="bin"/> (contour bins from the note's own).</summary>
    static List<DetectedNote> Decode(Func<int, int> bin)
    {
        int p = 39; // MIDI 60
        var note = new float[Frames * 88];
        var onset = new float[Frames * 88];
        var contour = new byte[Frames * Bins];
        for (int t = 20; t < 60; t++)
        {
            note[t * 88 + p] = 0.8f;
            contour[t * Bins + p * 3 + bin(t)] = 220;
        }
        onset[20 * 88 + p] = 0.9f;
        return BasicPitchDecoder.Decode(note, onset, Frames, 0.5, 0.3, minNoteFrames: 11, contour: contour);
    }

    [Fact]
    public void A_bend_up_a_whole_tone_is_followed()
    {
        // In tune for 15 frames, then up a whole tone (six bins of a third of a semitone).
        var n = Assert.Single(Decode(t => t < 35 ? 0 : 6));
        var bends = Assert.IsAssignableFrom<IReadOnlyList<PitchBend>>(n.Bends);
        Assert.Equal(0, bends[0].Semitones);
        Assert.Equal(2, bends[^1].Semitones);
        Assert.Equal(35 * 256.0 / 22050, bends[^1].Time, 2);
    }

    [Fact]
    public void A_steady_note_and_a_one_frame_jump_have_no_bends()
    {
        Assert.Null(Assert.Single(Decode(_ => 0)).Bends);
        Assert.Null(Assert.Single(Decode(t => t == 40 ? 9 : 0)).Bends); // a glimpse of another note
        Assert.Null(Assert.Single(Decode(t => t % 8 < 4 ? 1 : -1)).Bends); // vibrato of a third of a semitone
    }

    /// <summary>MIDI 60 from frame 20, then 61 from 35 and 62 from 45 to 60, the contour gliding with them; an onset at 61 too when <paramref name="reattack"/>.</summary>
    static List<DetectedNote> Glide(bool reattack)
    {
        var note = new float[Frames * 88];
        var onset = new float[Frames * 88];
        var contour = new byte[Frames * Bins];
        for (int t = 20; t < 60; t++)
        {
            int p = t < 35 ? 39 : t < 45 ? 40 : 41; // MIDI 60, 61, 62
            note[t * 88 + p] = 0.8f;
            contour[t * Bins + p * 3] = 220;
        }
        onset[20 * 88 + 39] = 0.9f;
        if (reattack) onset[35 * 88 + 40] = 0.9f;
        return BasicPitchDecoder.Decode(note, onset, Frames, 0.5, 0.3, minNoteFrames: 5, contour: contour);
    }

    [Fact]
    public void Steps_without_an_attack_are_joined_into_one_bent_note()
    {
        var n = Assert.Single(Glide(reattack: false));
        Assert.Equal(60, n.Pitch);
        Assert.Equal(60 * 256.0 / 22050, n.End, 2);
        Assert.Equal([0f, 1f, 2f], n.Bends!.Select(b => b.Semitones));
    }

    [Fact]
    public void A_step_struck_again_stays_a_note_of_its_own()
    {
        Assert.Equal([60, 61], Glide(reattack: true).Select(n => n.Pitch));
    }

    static DetectedNote Bent(double start, double end, int pitch) =>
        new(start, end, pitch, 0.7f) { Bends = [new PitchBend(start, 0), new PitchBend(start + 0.2, 1.5f)] };

    [Fact]
    public void Midi_bends_notes_that_sound_alone_and_returns_to_centre()
    {
        var notes = new[] { Bent(0, 1, 60), Bent(2, 3, 62), Bent(2.5, 3.5, 66) }; // the last two overlap: a chord
        var file = MidiExporter.Build(notes, [new TrackInfo("Guitar", 27, StaffLayout.Auto)], bpm: 120);
        var bends = file.GetTimedEvents().Where(e => e.Event is PitchBendEvent).Select(e => (e.Time, (int)((PitchBendEvent)e.Event).PitchValue)).ToList();
        // At 120 BPM and 480 ticks a quarter, a second is 960 ticks; 1.5 semitones is 6144 above centre.
        Assert.Equal([(0L, 8192), (192L, 8192 + 6144), (960L, 8192)], bends);
    }

    [Fact]
    public void Notes_overlapping_by_a_hair_still_count_as_alone()
    {
        var notes = new[] { Bent(0, 1.01, 60), Bent(1, 2, 62) };
        Assert.Equal(2, PitchBend.Playable(notes).Count());
    }
}
