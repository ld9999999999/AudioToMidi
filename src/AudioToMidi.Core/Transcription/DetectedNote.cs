namespace AudioToMidi.Core.Transcription;

/// <param name="Start">Seconds.</param>
/// <param name="End">Seconds.</param>
/// <param name="Pitch">MIDI note number.</param>
/// <param name="Amplitude">0 to 1, mapped linearly to MIDI velocity.</param>
/// <param name="Track">Index into the session's track list (instrument part), or 0 when not split.</param>
public readonly record struct DetectedNote(double Start, double End, int Pitch, float Amplitude, int Track = 0)
{
    /// <summary>
    /// How the pitch moves during the note, from basic-pitch's pitch contour. Each point gives the offset in
    /// semitones from <see cref="Pitch"/>, from its time (seconds) until the next point. Null when the pitch holds steady.
    /// </summary>
    public IReadOnlyList<PitchBend>? Bends { get; init; }

    public double Duration => End - Start;
    public int Velocity => Math.Clamp((int)Math.Round(127 * Amplitude), 1, 127);

    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    public static string PitchName(int pitch) => $"{Names[((pitch % 12) + 12) % 12]}{pitch / 12 - 1}";
}

/// <summary>A note's pitch from <see cref="Time"/> (seconds) on, in semitones from its MIDI pitch.</summary>
public readonly record struct PitchBend(double Time, float Semitones)
{
    /// <summary>Notes overlapping by less than this still count as one at a time.</summary>
    const double LegatoSeconds = 0.03;

    /// <summary>
    /// Return the notes of one track whose bends can be played: those with <see cref="DetectedNote.Bends"/> that
    /// sound alone, because a bend moves a whole MIDI channel and would put a chord out of tune.
    /// </summary>
    public static IEnumerable<DetectedNote> Playable(IEnumerable<DetectedNote> trackNotes)
    {
        var sorted = trackNotes.OrderBy(n => n.Start).ToList();
        double endBefore = double.MinValue;
        for (int i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            bool alone = endBefore <= n.Start + LegatoSeconds && (i + 1 >= sorted.Count || sorted[i + 1].Start >= n.End - LegatoSeconds);
            endBefore = Math.Max(endBefore, n.End);
            if (alone && n.Bends is { Count: > 0 }) yield return n;
        }
    }
}
