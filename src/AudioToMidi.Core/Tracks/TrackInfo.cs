using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Tracks;

public enum TrackMode
{
    /// <summary>Everything on one track.</summary>
    Single,
    /// <summary>Notes below a split point go to a bass track, the rest to an upper track. Instant.</summary>
    SplitByRegister,
    /// <summary>Demucs separates bass, vocals and other instruments before transcription. Slow, but gives the best result.</summary>
    Instruments,
}

/// <summary>One instrument part: a MIDI track with its own channel, and a part in the score.</summary>
/// <param name="Program">General MIDI program, 0-based. Not used for drums.</param>
/// <param name="IsDrums">True for General MIDI drum notes, written on channel 10 and left out of the sheet music.</param>
public sealed record TrackInfo(string Name, int Program, StaffLayout Staves, bool IsDrums = false);

public static class TrackSplitter
{
    public const int UpperTrack = 0;
    public const int BassTrack = 1;

    /// <summary>
    /// Give the bass track the notes below <paramref name="splitNote"/>, one at a time. When two low notes
    /// overlap, the lower stays in the bass and the higher moves up, since a bass line rarely plays chords.
    /// </summary>
    public static List<DetectedNote> SplitByRegister(IEnumerable<DetectedNote> notes, int splitNote)
    {
        var result = new List<DetectedNote>();
        var bass = new List<DetectedNote>();
        foreach (var n in notes)
            (n.Pitch < splitNote ? bass : result).Add(n with { Track = UpperTrack });

        // Sweep bass candidates by start time, keeping the lowest of any overlapping group.
        bass.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        DetectedNote? current = null;
        foreach (var n in bass)
        {
            const double tolerance = 0.03; // notes ending as the next starts are not overlapping
            if (current is { } c && n.Start < c.End - tolerance)
            {
                if (n.Pitch < c.Pitch)
                {
                    // A lower note under a held one becomes the bass, and the held note moves to the upper track.
                    result.Add(c with { Track = UpperTrack });
                    current = n with { Track = BassTrack };
                }
                else result.Add(n with { Track = UpperTrack });
                continue;
            }
            if (current is { } done) result.Add(done);
            current = n with { Track = BassTrack };
        }
        if (current is { } last) result.Add(last);

        result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return result;
    }
}
