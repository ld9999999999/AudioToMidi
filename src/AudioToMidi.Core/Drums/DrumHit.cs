using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Drums;

public enum DrumKind { Kick, Snare, ClosedHiHat, OpenHiHat }

public static class DrumKinds
{
    public static readonly IReadOnlyList<DrumKind> All = Enum.GetValues<DrumKind>();

    /// <summary>General MIDI drum note (channel 10).</summary>
    public static int GmNote(this DrumKind kind) => kind switch
    {
        DrumKind.Kick => 36,
        DrumKind.Snare => 38,
        DrumKind.ClosedHiHat => 42,
        DrumKind.OpenHiHat => 46,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Label(this DrumKind kind) => kind switch
    {
        DrumKind.Kick => "kick",
        DrumKind.Snare => "snare",
        DrumKind.ClosedHiHat => "closed hi-hat",
        DrumKind.OpenHiHat => "open hi-hat",
        _ => kind.ToString(),
    };
}

/// <param name="Time">Seconds.</param>
/// <param name="Strength">0..1, mapped linearly to MIDI velocity.</param>
public readonly record struct DrumHit(double Time, DrumKind Kind, float Strength)
{
    public int Velocity => Math.Clamp((int)Math.Round(127 * Strength), 1, 127);
}

/// <summary>Settings for picking hits out of a <see cref="DrumAnalysis"/>. Cheap to change.</summary>
public sealed record DrumSettings
{
    /// <summary>0..1. Higher finds quieter hits, and more false ones.</summary>
    public double Sensitivity { get; init; } = 0.5;

    /// <summary>A hi-hat that rings longer than this counts as open. Decay is only a weak cue for this.</summary>
    public double OpenHiHatMs { get; init; } = 60;
}

public static class DrumNotes
{
    /// <summary>Length given to drum notes in MIDI. Drum sounds ignore it, but some apps show it.</summary>
    const double NoteSeconds = 0.1;

    /// <summary>
    /// Turn drum hits into General MIDI drum notes on track <paramref name="track"/>, optionally snapped to
    /// 1/<paramref name="quantizeDivisions"/> of a beat (on a swung grid, with <paramref name="swing"/>).
    /// When two hits of one drum snap to the same step, keep the stronger one.
    /// </summary>
    public static List<DetectedNote> ToNotes(IEnumerable<DrumHit> hits, int track,
        TempoEstimate? tempo = null, int quantizeDivisions = 0, double swing = Swing.Straight)
    {
        var map = tempo is not null && quantizeDivisions > 0 ? tempo.Map : null;
        double Snap(double t) => map is not null ? Math.Max(0, map.Snap(t, quantizeDivisions, swing)) : t;

        var notes = new List<DetectedNote>();
        foreach (var group in hits.GroupBy(h => h.Kind))
        {
            var times = group
                .Select(h => (Time: Snap(h.Time), h.Strength))
                .GroupBy(h => Math.Round(h.Time, 6))
                .Select(g => g.MaxBy(h => h.Strength))
                .OrderBy(h => h.Time)
                .ToList();
            int pitch = group.Key.GmNote();
            for (int i = 0; i < times.Count; i++)
            {
                double end = times[i].Time + NoteSeconds;
                if (i + 1 < times.Count) end = Math.Min(end, times[i + 1].Time - 0.005); // never overlap the next hit
                notes.Add(new DetectedNote(times[i].Time, Math.Max(times[i].Time + 0.005, end), pitch, times[i].Strength, track));
            }
        }
        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return notes;
    }
}
