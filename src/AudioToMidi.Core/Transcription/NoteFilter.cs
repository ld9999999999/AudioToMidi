using AudioToMidi.Core.Dsp;

namespace AudioToMidi.Core.Transcription;

/// <summary>Clean up decoded notes, the same way for every engine.</summary>
public static class NoteFilter
{
    public static List<DetectedNote> Apply(IEnumerable<DetectedNote> input, NoteSettings s, TempoEstimate? tempo)
    {
        var notes = input
            .Where(n => n.Pitch >= s.LowestNote && n.Pitch <= s.HighestNote && n.Velocity >= s.MinVelocity)
            .OrderBy(n => n.Pitch).ThenBy(n => n.Start)
            .ToList();

        if (s.MergeGapMs > 0) notes = Merge(notes, s.MergeGapMs / 1000);

        if (s.QuantizeDivisions > 0 && tempo is not null) notes = Quantize(notes, tempo, s.QuantizeDivisions, s.Swing);

        notes = notes.Where(n => n.Duration * 1000 >= s.MinNoteMs || s.QuantizeDivisions > 0).ToList();
        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return notes;
    }

    /// <summary>Merge same-pitch notes whose gap is shorter than <paramref name="maxGap"/>. The input must be sorted by pitch, then start.</summary>
    static List<DetectedNote> Merge(List<DetectedNote> notes, double maxGap)
    {
        var result = new List<DetectedNote>(notes.Count);
        foreach (var n in notes)
        {
            if (result.Count > 0 && result[^1] is var last && last.Pitch == n.Pitch && n.Start - last.End < maxGap)
            {
                double lenA = last.Duration, lenB = n.Duration;
                float amp = (float)((last.Amplitude * lenA + n.Amplitude * lenB) / Math.Max(1e-9, lenA + lenB));
                result[^1] = last with { End = Math.Max(last.End, n.End), Amplitude = amp };
            }
            else result.Add(n);
        }
        return result;
    }

    static List<DetectedNote> Quantize(List<DetectedNote> notes, TempoEstimate tempo, int divisions, double swing)
    {
        var map = tempo.Map;
        if (!Swing.Applies(divisions, swing)) swing = Swing.Straight;
        var lateness = swing == Swing.Straight ? null : Lateness(notes, map);

        var result = new List<DetectedNote>(notes.Count);
        foreach (var n in notes)
        {
            // On a swung grid the steps around the off-beat are narrow, so an instrument whose onsets are
            // found late would land on the following sixteenth. Snap from where its notes would be on time.
            double early = lateness is not null && lateness.TryGetValue(n.Pitch / 12, out double late) ? late : 0;
            double start = Math.Max(0, map.Snap(map.ToSeconds(map.ToBeat(n.Start) - early), divisions, swing));
            // Make each note at least one step long, up to the next grid point, since beats (and swung steps) vary.
            double next = map.ToSeconds(Swing.ToSwung(Swing.ToStraight(map.ToBeat(start), swing) + 1.0 / divisions, swing));
            double end = Math.Max(next, map.Snap(n.End, divisions, swing));
            // Snapping can make consecutive same-pitch notes overlap, so trim the earlier one.
            if (result.Count > 0 && result[^1].Pitch == n.Pitch && result[^1].End > start)
            {
                if (result[^1].Start >= start) continue;
                result[^1] = result[^1] with { End = start };
            }
            result.Add(n with { Start = start, End = end });
        }
        return result;
    }

    /// <summary>
    /// Estimate how late (in beats) each octave's onsets are found, as the median offset of onsets near a
    /// beat, for octaves with enough of them. Instruments with soft attacks are found later than the drums
    /// and bass that the beats follow.
    /// </summary>
    static Dictionary<int, double> Lateness(List<DetectedNote> notes, BeatMap map)
    {
        var result = new Dictionary<int, double>();
        foreach (var octave in notes.GroupBy(n => n.Pitch / 12))
        {
            var offsets = octave.Select(n => map.ToBeat(n.Start)).Select(b => b - Math.Round(b)).Where(o => Math.Abs(o) <= 0.15).Order().ToList();
            if (offsets.Count >= 8) result[octave.Key] = offsets[offsets.Count / 2];
        }
        return result;
    }

    /// <summary>
    /// Keep the notes that sound inside [start, end), cut at the edges. A note held from before the
    /// start is trimmed to begin at the start, so held chords are kept.
    /// </summary>
    public static List<DetectedNote> ClipToRange(IEnumerable<DetectedNote> notes, double start, double end)
    {
        const double minimum = 0.03;
        return notes
            .Where(n => n.End > start + minimum && n.Start < end - minimum)
            .Select(n => n with { Start = Math.Max(n.Start, start), End = Math.Min(n.End, end) })
            .ToList();
    }
}
