namespace AudioToMidi.Model;

/// <summary>
/// What AudioToMidi found in one piece of music, in a form other apps can load: the beat and bar
/// grid, the key, and one part per instrument with its notes. All times are seconds from the start
/// of the source audio, so they line up with the stems saved next to the document.
/// </summary>
/// <remarks>
/// New fields may be added in later versions; readers should ignore what they do not know.
/// <see cref="SchemaVersion"/> only goes up when existing fields change meaning.
/// </remarks>
public sealed record AnalysisDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Name and version of the program that wrote the document.</summary>
    public string Generator { get; init; } = "";

    public DateTimeOffset Created { get; init; }

    public required SourceInfo Source { get; init; }

    public required TimingInfo Timing { get; init; }

    /// <summary>Key of the whole piece. Null when there were no pitched notes to go on.</summary>
    public KeyInfo? Key { get; init; }

    /// <summary>The key over time, in order: one span when it never changes, more where it modulates.</summary>
    public IReadOnlyList<KeySpanInfo> Keys { get; init; } = [];

    /// <summary>Chords in time order, changing at most once a beat. A span without a root has no chord, as in silence or drums alone.</summary>
    public IReadOnlyList<ChordInfo> Chords { get; init; } = [];

    public IReadOnlyList<Part> Parts { get; init; } = [];

    /// <summary>Audio files saved with the document (separated instruments).</summary>
    public IReadOnlyList<StemFile> Stems { get; init; } = [];
}

/// <param name="FileName">File name of the original audio, without its folder.</param>
/// <param name="Sha256">Hash of the original file, hex. Lets a reader check it has the same audio.</param>
public sealed record SourceInfo(string FileName, double DurationSeconds, string? Sha256);

/// <param name="TonicPitchClass">0 = C, 1 = C♯/D♭ ... 11 = B.</param>
/// <param name="Fifths">Key signature: sharps if positive, flats if negative.</param>
/// <param name="Name">For display, e.g. "B♭ major".</param>
public sealed record KeyInfo(int TonicPitchClass, bool Minor, int Fifths, string Name);

/// <summary>A key from <see cref="Start"/> to <see cref="End"/> seconds.</summary>
public sealed record KeySpanInfo(double Start, double End, KeyInfo Key);

/// <summary>A chord from <see cref="Start"/> to <see cref="End"/> seconds.</summary>
/// <param name="Symbol">Spelled in the key at the time, e.g. "B♭m7/D"; "N" when there is no chord.</param>
/// <param name="Root">Pitch class of the root, 0 = C; null when there is no chord.</param>
/// <param name="Kind">MusicXML chord kind: major, minor, diminished, augmented, suspended-second, suspended-fourth,
/// power, dominant, major-seventh, minor-seventh or half-diminished.</param>
/// <param name="Bass">Pitch class of the bass when the chord is inverted; null in root position.</param>
/// <param name="Roman">Roman numeral in the key at the time, e.g. "V7", "ii", "♭VII". It shows the chord's function,
/// which stays the same when the music is transposed.</param>
public sealed record ChordInfo(double Start, double End, string Symbol, int? Root, string? Kind, int? Bass, string? Roman);

public enum PartRole { Melodic, Bass, Vocal, Drums }

/// <summary>One instrument: a MIDI track, a part in the score, a channel in a mixer.</summary>
/// <param name="Program">General MIDI program, 0-based. Not used for drums.</param>
/// <param name="Instrument">Instrument name for display, e.g. "Electric Bass (finger)".</param>
/// <param name="Stem">Name of the stem in <see cref="AnalysisDocument.Stems"/> the part was transcribed from, if any.</param>
/// <param name="Line">When one stem was split into lines: which line this part is. Parts with the same
/// <see cref="Stem"/> and a line together make up that stem.</param>
public sealed record Part(string Name, PartRole Role, int Program, string? Instrument, string? Stem, IReadOnlyList<PartNote> Notes,
    PartLine? Line = null)
{
    /// <summary>Stretches of bars the part plays again and again, as clips to loop.</summary>
    public IReadOnlyList<PatternInfo> Patterns { get; init; } = [];
}

/// <summary>A stretch of bars a part repeats: a clip.</summary>
/// <param name="Bars">How many bars it lasts: 1, 2 or 4.</param>
/// <param name="Occurrences">The bar each repeat starts on (1 = the first bar of <see cref="TimingInfo"/>).</param>
/// <param name="Notes">The most typical repeat's notes, on a grid of sixteenths.</param>
public sealed record PatternInfo(int Bars, IReadOnlyList<int> Occurrences, IReadOnlyList<PatternNoteInfo> Notes);

/// <summary>A note of a clip, in beats from the clip's start (the grid's beats: dotted quarters in 6/8).</summary>
public readonly record struct PatternNoteInfo(double Beat, double Length, int Pitch, int Velocity);

/// <summary>A musical line within a stem: the tune on top, the chords in between, the lowest voice.</summary>
public enum PartLine { Melody, Harmony, Low }

/// <summary>A note in seconds. For drum parts, <see cref="Pitch"/> is a General MIDI drum note
/// (36 kick, 38 snare, 42 closed hi-hat, 46 open hi-hat) and the length has no meaning.</summary>
/// <param name="Velocity">MIDI velocity, 1..127.</param>
public readonly record struct PartNote(double Start, double End, int Pitch, int Velocity);

/// <param name="Name">"drums", "bass", "other", "vocals" (plus "guitar" and "piano" from the 6-stem model), or
/// "harmonic" and "percussive" when instruments were not separated.</param>
/// <param name="Path">Relative to the bundle folder, with forward slashes.</param>
public sealed record StemFile(string Name, string Path, int SampleRate, int Channels);
