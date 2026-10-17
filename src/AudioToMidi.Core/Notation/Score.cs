using AudioToMidi.Core.Harmony;

namespace AudioToMidi.Core.Notation;

public enum Clef { Treble, Bass }

public enum StaffLayout { Auto, Treble, Bass, Grand }

public sealed record ScoreSettings
{
    public string Title { get; init; } = "Transcription";
    public string PartName { get; init; } = "Piano";
    public int MidiProgram { get; init; }

    /// <summary>The time signature. The grid's beats are its beats: quarter notes, or dotted quarters in compound meters.</summary>
    public Meter Meter { get; init; } = Meter.Common;

    /// <summary>Quantization grid per quarter note: 1, 2, 4 or 8 (quarter to 32nd notes). Compound meters use at least eighths.</summary>
    public int StepsPerBeat { get; init; } = 4;

    public StaffLayout Staves { get; init; } = StaffLayout.Auto;

    /// <summary>Key signature override (-7..7). When null, follow <see cref="Keys"/> or detect one key from the notes.</summary>
    public int? KeyFifths { get; init; }

    /// <summary>The key over time (from <see cref="KeyTracker"/>): each measure gets the key at its middle, and a change of key signature shows as a new one.</summary>
    public IReadOnlyList<KeySpan>? Keys { get; init; }

    /// <summary>The most voices at once on a staff; extra overlapping notes are cut short to fit.</summary>
    public int MaxVoicesPerStaff { get; init; } = 3;

    /// <summary>Chord symbols to write above the staff. <see cref="ScoreBuilder.BuildParts"/> gives them to the top part only.</summary>
    public IReadOnlyList<ChordSpan>? Chords { get; init; }

    /// <summary>
    /// Where the off-beat eighths fall (see <see cref="Dsp.Swing"/>): above 0.5 in a simple meter, notes are
    /// written in straight eighths from where they would fall unswung, under a "Swing" mark.
    /// </summary>
    public double Swing { get; init; } = Dsp.Swing.Straight;
}

/// <summary>A written note value such as "quarter" or "eighth", with its dots and its number of beams or flags.</summary>
public readonly record struct NoteValue(string Type, int Dots, int Beams);

public sealed record ScorePitch(int Midi, char Step, int Alter, int Octave);

public enum EventKind { Note, Rest, MeasureRest, Skip }

/// <param name="Start">Ticks from the start of the measure.</param>
/// <param name="Pitches">Chord notes, lowest first. Empty for rests and skips.</param>
/// <param name="TieFromPrevious">Continues a note from the previous event (tie stop).</param>
/// <param name="TieToNext">Continues into the next event (tie start).</param>
public sealed record ScoreEvent(EventKind Kind, int Start, int Duration, NoteValue Value,
    IReadOnlyList<ScorePitch> Pitches, bool TieFromPrevious, bool TieToNext);

/// <param name="Number">MusicXML voice number: 1–4 on the upper staff, 5–8 on the lower.</param>
/// <param name="Measures">Events per measure. An empty list means the voice is silent there and is omitted.</param>
public sealed record ScoreVoice(int Staff, int Number, IReadOnlyList<IReadOnlyList<ScoreEvent>> Measures);

/// <param name="Divisions">Ticks per quarter note.</param>
/// <param name="Key">The key at the start; see <see cref="MeasureKeys"/> for changes.</param>
public sealed record Score(string Title, string PartName, int MidiProgram, double Bpm, Meter Meter,
    int Divisions, MusicalKey Key, IReadOnlyList<Clef> Staves, int MeasureCount, IReadOnlyList<ScoreVoice> Voices)
{
    /// <summary>Ticks per beat: a quarter note, or a dotted quarter in compound meters.</summary>
    public int BeatTicks => (int)Math.Round(Divisions * Meter.BeatQuarters);
    public int MeasureTicks => BeatTicks * Meter.BeatsPerBar;

    /// <summary>Chord symbols above the staff, in order.</summary>
    public IReadOnlyList<ScoreHarmony> Harmonies { get; init; } = [];

    /// <summary>The key signature of each measure; a different one from the measure before is written as a change.</summary>
    public IReadOnlyList<MusicalKey> MeasureKeys { get; init; } = [];

    /// <summary>Tempo marks in beats per minute (the meter's beats), where the tempo settles somewhere new; the first at measure 0.</summary>
    public IReadOnlyList<TempoMark> Tempos { get; init; } = [];

    /// <summary>Written straight but played swung, with the off-beat eighth this far through the beat; 0.5 when straight.</summary>
    public double Swing { get; init; } = Dsp.Swing.Straight;

    public MusicalKey KeyOf(int measure) => measure < MeasureKeys.Count ? MeasureKeys[measure] : Key;
}

/// <param name="Measure">0-based measure.</param>
public sealed record TempoMark(int Measure, double Bpm);

/// <summary>A chord symbol, spelled in the score's key.</summary>
/// <param name="Measure">0-based measure.</param>
/// <param name="Offset">Ticks from the start of the measure.</param>
/// <param name="Kind">MusicXML kind ("major", "minor-seventh"...); <paramref name="Text"/> is how it is written ("", "m7").</param>
public sealed record ScoreHarmony(int Measure, int Offset, char RootStep, int RootAlter, string Kind, string Text,
    char? BassStep = null, int BassAlter = 0);
