using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Tests;

public class BassTests
{
    static DetectedNote N(double start, double end, int pitch) => new(start, end, pitch, 0.8f, TrackSplitter.BassTrack);

    [Fact]
    public void Harmonics_stacked_on_a_bass_note_are_dropped()
    {
        // E1 with its octave, twelfth and two octaves, as basic-pitch hears a bass note; then A1 alone.
        var notes = new[] { N(0, 0.5, 28), N(0.01, 0.45, 40), N(0.02, 0.4, 47), N(0.03, 0.3, 52), N(0.5, 1.0, 33) };
        Assert.Equal([28, 33], BassCleanup.DropHarmonics(notes).Select(n => n.Pitch));
    }

    [Fact]
    public void Other_intervals_and_later_notes_stay()
    {
        var notes = new[]
        {
            N(0, 1.0, 28),     // a held E1
            N(0.02, 0.5, 31),  // a minor third above it: not a harmonic
            N(0.4, 0.8, 40),   // its octave, but starting well after it: a note of its own
            N(1.2, 1.5, 40),   // an octave above a note that has ended
        };
        Assert.Equal([28, 31, 40, 40], BassCleanup.DropHarmonics(notes).Select(n => n.Pitch));
    }

    const int Rate = 22050;

    /// <summary>A bass tone: the fundamental and its first six harmonics, fading in strength.</summary>
    static float[] Tone(int midi, double seconds = 0.6)
    {
        double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
            for (int k = 1; k <= 6; k++)
                x[i] += (float)(0.2 / k * Math.Sin(2 * Math.PI * f * k * i / Rate));
        return x;
    }

    [Theory]
    [InlineData(40, 28)] // heard an octave high
    [InlineData(52, 28)] // two octaves high
    public void A_note_above_the_stems_fundamental_moves_down(int heard, int expected)
    {
        var stem = Tone(28); // E1
        var moved = BassCleanup.FixOctaves([N(0, 0.5, heard)], stem, Rate);
        Assert.Equal(expected, Assert.Single(moved).Pitch);
    }

    [Fact]
    public void A_note_at_the_stems_fundamental_stays()
    {
        var stem = Tone(40); // E2
        Assert.Equal(40, Assert.Single(BassCleanup.FixOctaves([N(0, 0.5, 40)], stem, Rate)).Pitch);
    }

    [Fact]
    public void A_note_moved_onto_its_fundamental_merges_with_it()
    {
        var stem = Tone(28);
        var notes = BassCleanup.FixOctaves([N(0, 0.5, 28), N(0.01, 0.4, 40)], stem, Rate);
        Assert.Equal(28, Assert.Single(notes).Pitch);
    }
}
