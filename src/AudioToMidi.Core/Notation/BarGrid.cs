using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>
/// Where the bars fall. Bar 1 starts at beat <see cref="OriginBeat"/> of <see cref="Map"/>, the downbeat
/// of the bar that holds the first note, so the score starts with music. The beats need not be evenly
/// spaced; convert with <see cref="ToBeats"/> and <see cref="ToSeconds"/>.
/// </summary>
public sealed record BarGrid(BeatMap Map, int BeatsPerBar, int OriginBeat)
{
    /// <summary>Start of bar 1.</summary>
    public double OriginSeconds => Map.ToSeconds(OriginBeat);

    /// <summary>Typical beat length (the beats may vary).</summary>
    public double BeatSeconds => Map.BeatSeconds;
    public double BarSeconds => BeatSeconds * BeatsPerBar;
    public double Bpm => Map.Bpm;

    /// <summary>Convert seconds to beats from the start of bar 1.</summary>
    public double ToBeats(double seconds) => Map.ToBeat(seconds) - OriginBeat;

    /// <summary>Convert beats back to seconds; the inverse of <see cref="ToBeats"/>.</summary>
    public double ToSeconds(double beats) => Map.ToSeconds(beats + OriginBeat);

    /// <summary>Make a grid with a steady tempo and bar 1 at <paramref name="originSeconds"/>.</summary>
    public static BarGrid Steady(double originSeconds, double beatSeconds, int beatsPerBar) =>
        new(BeatMap.Steady(originSeconds, beatSeconds), beatsPerBar, 0);

    /// <param name="downbeat">Which detected beat (0-based, taken modulo <paramref name="beatsPerBar"/>) is beat 1 of a bar.</param>
    public static BarGrid Create(TempoEstimate tempo, int beatsPerBar, int downbeat, IEnumerable<DetectedNote> notes)
    {
        var map = tempo.Map;
        int origin = ((downbeat % beatsPerBar) + beatsPerBar) % beatsPerBar;

        // A note that starts slightly early (within a quarter beat) still belongs to the bar.
        var starts = notes.Select(n => n.Start).ToList();
        double earliest = starts.Count > 0 ? map.ToBeat(starts.Min()) : origin;
        while (origin - earliest > 0.25) origin -= beatsPerBar;
        while (origin + beatsPerBar <= earliest + 0.25) origin += beatsPerBar; // skip leading empty bars
        return new BarGrid(map, beatsPerBar, origin);
    }
}
