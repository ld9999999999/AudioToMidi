using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Harmony;

/// <summary>Chords and keys of a piece, with chord symbols and Roman numerals read in the key at the time.</summary>
public sealed record HarmonyAnalysis(IReadOnlyList<ChordSpan> Chords, IReadOnlyList<KeySpan> Keys)
{
    public static HarmonyAnalysis Empty { get; } = new([], []);

    /// <param name="notes">Pitched notes only (no drums).</param>
    public static HarmonyAnalysis Analyze(IReadOnlyList<DetectedNote> notes, BarGrid grid) =>
        notes.Count == 0 ? Empty : new(ChordRecognizer.Recognize(notes, grid), KeyTracker.Track(notes, grid));

    public MusicalKey KeyAt(double seconds) => KeyTracker.KeyAt(Keys, seconds);

    /// <summary>The chord's symbol in the key at its start, "N" for no chord.</summary>
    public string Symbol(ChordSpan span) => span.Chord is { } c ? c.Symbol(KeyAt(span.Start)) : "N";

    /// <summary>The chord's Roman numeral in the key at its start; null for no chord.</summary>
    public string? Roman(ChordSpan span) => span.Chord?.Roman(KeyAt(span.Start));
}
