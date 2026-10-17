namespace AudioToMidi.Core.Tracks;

/// <summary>One output track.</summary>
/// <param name="Source">Index of the base track (for example a Demucs stem) its notes come from.</param>
/// <param name="Line">The line it carries when its base track is split into lines, or null.</param>
public sealed record TrackSlot(string Name, int Source, MusicLine? Line);

/// <summary>
/// Lay out the output tracks for a set of base tracks, some of which may be split into lines. A split track
/// keeps its place for its melody, and the harmony and low line of each split track come after all the base
/// tracks, in base-track order. This way base tracks keep their indexes whatever is split.
/// </summary>
public static class TrackLayout
{
    /// <param name="baseNames">The mode's tracks, for example Other, Bass, Vocals, Guitar, Piano.</param>
    /// <param name="split">Indexes of the base tracks to split.</param>
    public static IReadOnlyList<TrackSlot> Build(IReadOnlyList<string> baseNames, IEnumerable<int> split)
    {
        var splitSet = split.Where(i => i >= 0 && i < baseNames.Count).Distinct().Order().ToList();
        var slots = baseNames
            .Select((name, i) => splitSet.Contains(i)
                ? new TrackSlot(LineName(baseNames, i, MusicLine.Melody), i, MusicLine.Melody)
                : new TrackSlot(name, i, null))
            .ToList();
        foreach (int i in splitSet)
        {
            slots.Add(new TrackSlot(LineName(baseNames, i, MusicLine.Harmony), i, MusicLine.Harmony));
            slots.Add(new TrackSlot(LineName(baseNames, i, MusicLine.Low), i, MusicLine.Low));
        }
        return slots;
    }

    /// <summary>Return the output track indexes of a split base track's harmony and low line.</summary>
    public static (int Harmony, int Low) LineTracks(int baseCount, IEnumerable<int> split, int track)
    {
        int rank = split.Distinct().Order().ToList().IndexOf(track);
        return (baseCount + 2 * rank, baseCount + 2 * rank + 1);
    }

    /// <summary>Name a line, such as "Other melody" or "Piano low line". With only one base track the name is just "Melody", "Harmony" or "Low line".</summary>
    static string LineName(IReadOnlyList<string> baseNames, int track, MusicLine line)
    {
        string what = line switch { MusicLine.Melody => "melody", MusicLine.Harmony => "harmony", _ => "low line" };
        return baseNames.Count == 1 ? char.ToUpperInvariant(what[0]) + what[1..] : $"{baseNames[track]} {what}";
    }
}
