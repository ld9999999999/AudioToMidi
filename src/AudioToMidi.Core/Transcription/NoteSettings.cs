namespace AudioToMidi.Core.Transcription;

/// <summary>Settings applied after analysis. Changing them is fast and needs no new analysis.</summary>
public sealed record NoteSettings
{
    /// <summary>basic-pitch: how confident an onset must be to start a note (0 to 1). Higher means fewer notes.</summary>
    public double OnsetThreshold { get; init; } = 0.5;

    /// <summary>basic-pitch: how strong a note must stay to continue (0 to 1). Higher means shorter notes.</summary>
    public double FrameThreshold { get; init; } = 0.3;

    /// <summary>basic-pitch: also start notes where sustained energy appears without a detected onset.</summary>
    public bool MelodiaTrick { get; init; } = true;

    /// <summary>YIN: frames quieter than this (dBFS) are treated as silence.</summary>
    public double SilenceThresholdDb { get; init; } = -45;

    public double MinNoteMs { get; init; } = 120;
    public int MinVelocity { get; init; } = 1;
    public int LowestNote { get; init; } = 21;   // A0
    public int HighestNote { get; init; } = 108; // C8

    /// <summary>Join notes of the same pitch separated by less than this gap.</summary>
    public double MergeGapMs { get; init; } = 0;

    /// <summary>Grid divisions per beat for quantizing (4 = sixteenth notes, 0 = off).</summary>
    public int QuantizeDivisions { get; init; } = 0;

    /// <summary>
    /// basic-pitch: follow each note's pitch through the pitch contour, for bends and slides (see
    /// <see cref="DetectedNote.Bends"/>). Notes the model cut into steps along a bend are joined into one
    /// bent note, so this also changes the notes and gives fewer of them in the sheet music.
    /// </summary>
    public bool PitchBends { get; init; }

    /// <summary>Where the quantize grid's off-beat eighth falls in the beat: 0.5 for straight, higher to keep a swing feel (see <see cref="Dsp.Swing"/>).</summary>
    public double Swing { get; init; } = Dsp.Swing.Straight;
}
