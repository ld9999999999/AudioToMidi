using AudioToMidi.Core.Audio;

namespace AudioToMidi.Core.Instruments;

/// <summary>What kind of track the audio belongs to, which limits the instruments that may be suggested.
/// Guitar and Keys are the guitar and piano stems of the 6-stem model: melodic, and limited to that family.</summary>
public enum TrackRole { Any, Melodic, Bass, Vocal, Guitar, Keys }

/// <param name="Label">Instrument name shown to the user, e.g. "Electric Piano" or "Saxophone".</param>
/// <param name="Program">Suggested General MIDI program (0-based).</param>
/// <param name="Confidence">Classifier probability of the chosen class, 0..1.</param>
/// <param name="Alternatives">Next most likely classes with their probabilities.</param>
/// <param name="Reliable">False when the guess comes from a full mix; the app then shows it without applying it.</param>
public sealed record InstrumentGuess(string Label, int Program, double Confidence,
    IReadOnlyList<(string Label, double Probability)> Alternatives, bool Reliable = true)
{
    /// <summary>
    /// Below this the guess is shown but not applied. Treat the value as a starting point.
    /// </summary>
    public const double ApplyThreshold = 0.35;

    /// <summary>Confident enough to set the track's instrument automatically.</summary>
    public bool ShouldApply => Reliable && Confidence >= ApplyThreshold;
}

/// <summary>
/// Suggest a General MIDI instrument for a track's audio. YAMNet embeddings go through the instrument
/// head (trained on Apple Loops), limited to the track's role. YAMNet's own AudioSet scores then pick the
/// specific instrument within groups the head lumps together (saxophone or flute, violin or cello...).
/// </summary>
public sealed class InstrumentRecognizer : IDisposable
{
    readonly YamNet _yamNet = new();
    readonly InstrumentHead? _head = InstrumentHead.LoadEmbedded();

    /// <summary>True when the trained head is embedded; without it, only AudioSet labels are used.</summary>
    public bool HasTrainedHead => _head is not null;

    public InstrumentGuess? Recognize(float[] samples, int sampleRate, TrackRole role)
    {
        var audio = FfmpegDecoder.Resample(new AudioData(samples, sampleRate), YamNet.SampleRate).Samples;
        var output = _yamNet.Run(audio);
        var embedding = output.Pool(o => o.Embeddings);
        var scores = output.Pool(o => o.Scores);
        if (embedding is null || scores is null) return null;

        var probs = _head?.Predict(embedding, scores, role) ?? AudioSetPrior(scores);
        var classes = InstrumentClasses.All;
        for (int i = 0; i < probs.Length; i++)
            if (!Allowed(classes[i], role)) probs[i] = 0;
        double total = probs.Sum();
        if (total <= 0) return null;
        for (int i = 0; i < probs.Length; i++) probs[i] /= total;

        var ranked = Enumerable.Range(0, probs.Length).OrderByDescending(i => probs[i]).ToList();
        var best = classes[ranked[0]];
        var (label, program) = Refine(best, scores);

        // A rare instrument YAMNet is very sure about beats a low-confidence head guess.
        if (probs[ranked[0]] < 0.6 && StrongSpecific(scores, role) is { } specific) (label, program) = specific;

        var alternatives = ranked.Skip(1).Take(3).Where(i => probs[i] >= 0.05)
            .Select(i => (classes[i].Name, probs[i])).ToList();
        return new InstrumentGuess(label, program, probs[ranked[0]], alternatives);
    }

    public static bool Allowed(InstrumentRole cls, TrackRole track) => track switch
    {
        TrackRole.Bass => cls == InstrumentRole.Bass,
        TrackRole.Melodic or TrackRole.Guitar or TrackRole.Keys => cls == InstrumentRole.Melodic,
        TrackRole.Vocal => cls != InstrumentRole.Bass,
        _ => true,
    };

    static readonly HashSet<string> GuitarFamily = ["Electric Guitar", "Acoustic Guitar", "Banjo", "Sitar"];
    static readonly HashSet<string> KeysFamily = ["Piano", "Electric Piano", "Clavinet", "Organ", "Harpsichord", "Accordion"];

    /// <summary>Apply the role check, and on a guitar or piano stem allow only that family's instruments.</summary>
    public static bool Allowed(InstrumentClass cls, TrackRole track) => Allowed(cls.Role, track) && InFamily(cls.Name, track);

    static bool InFamily(string name, TrackRole track) => track switch
    {
        TrackRole.Guitar => GuitarFamily.Contains(name),
        TrackRole.Keys => KeysFamily.Contains(name),
        _ => true,
    };

    // ---- AudioSet refinements ----

    static readonly Dictionary<string, (string AudioSet, string Label, int Program)[]> FamilyDetail = new()
    {
        ["Brass & Wind"] =
        [
            ("Saxophone", "Saxophone", 65), ("Flute", "Flute", 73), ("Clarinet", "Clarinet", 71),
            ("Trumpet", "Trumpet", 56), ("Trombone", "Trombone", 57), ("French horn", "French Horn", 60),
            ("Harmonica", "Harmonica", 22), ("Accordion", "Accordion", 21),
        ],
        ["Strings"] =
        [
            ("Violin, fiddle", "Violin", 40), ("Cello", "Cello", 42), ("Harp", "Harp", 46), ("Pizzicato", "Pizzicato Strings", 45),
        ],
        ["Mallets"] =
        [
            ("Marimba, xylophone", "Marimba", 12), ("Glockenspiel", "Glockenspiel", 9), ("Vibraphone", "Vibraphone", 11),
            ("Steelpan", "Steel Drums", 114), ("Tubular bells", "Tubular Bells", 14),
        ],
        ["Acoustic Guitar"] =
        [
            ("Banjo", "Banjo", 105), ("Sitar", "Sitar", 104), ("Ukulele", "Ukulele", 24), ("Mandolin", "Mandolin", 24),
        ],
        ["Vocals"] = [("Choir", "Choir", 52)],
        ["Electric Bass"] = [("Double bass", "Acoustic Bass", 32)],
        ["Clavinet"] = [("Harpsichord", "Harpsichord", 6)],
    };

    /// <summary>Within a family, pick the specific instrument AudioSet favours, if it says anything.</summary>
    static (string Label, int Program) Refine(InstrumentClass cls, float[] scores)
    {
        if (!FamilyDetail.TryGetValue(cls.Name, out var options)) return (cls.Name, cls.Program);
        var best = options.Select(o => (o, Score: Score(scores, o.AudioSet))).MaxBy(x => x.Score);
        double threshold = cls.Name is "Vocals" or "Electric Bass" or "Acoustic Guitar" or "Clavinet" ? 0.15 : 0.05;
        return best.Score >= threshold ? (best.o.Label, best.o.Program) : (cls.Name, cls.Program);
    }

    static readonly (string AudioSet, string Label, int Program, InstrumentRole Role)[] Specific =
    [
        ("Harp", "Harp", 46, InstrumentRole.Melodic), ("Harpsichord", "Harpsichord", 6, InstrumentRole.Melodic),
        ("Accordion", "Accordion", 21, InstrumentRole.Melodic), ("Harmonica", "Harmonica", 22, InstrumentRole.Melodic),
        ("Banjo", "Banjo", 105, InstrumentRole.Melodic), ("Sitar", "Sitar", 104, InstrumentRole.Melodic),
        ("Saxophone", "Saxophone", 65, InstrumentRole.Melodic), ("Flute", "Flute", 73, InstrumentRole.Melodic),
        ("Violin, fiddle", "Violin", 40, InstrumentRole.Melodic), ("Cello", "Cello", 42, InstrumentRole.Melodic),
        ("Trumpet", "Trumpet", 56, InstrumentRole.Melodic), ("Marimba, xylophone", "Marimba", 12, InstrumentRole.Melodic),
        ("Bagpipes", "Bagpipe", 109, InstrumentRole.Melodic), ("Steelpan", "Steel Drums", 114, InstrumentRole.Melodic),
        ("Double bass", "Acoustic Bass", 32, InstrumentRole.Bass), ("Choir", "Choir", 52, InstrumentRole.Vocal),
    ];

    static (string, int)? StrongSpecific(float[] scores, TrackRole role)
    {
        var best = Specific.Where(s => Allowed(s.Role, role) && InFamily(s.Label, role)).Select(s => (s, Score: Score(scores, s.AudioSet))).MaxBy(x => x.Score);
        return best.Score >= 0.4 ? (best.s.Label, best.s.Program) : null;
    }

    static readonly (string AudioSet, string Class)[] PriorMap =
    [
        ("Piano", "Piano"), ("Electric piano", "Electric Piano"), ("Harpsichord", "Clavinet"),
        ("Organ", "Organ"), ("Electronic organ", "Organ"), ("Hammond organ", "Organ"),
        // "Synthesizer" supports every synth class equally; the trained head tells them apart.
        ("Synthesizer", "Synth"), ("Synthesizer", "Synth Pad"), ("Synthesizer", "Synth Lead"), ("Synthesizer", "Synth Bass"),
        ("New-age music", "Synth Pad"), ("Ambient music", "Synth Pad"),
        ("Guitar", "Electric Guitar"), ("Electric guitar", "Electric Guitar"), ("Acoustic guitar", "Acoustic Guitar"), ("Strum", "Acoustic Guitar"),
        ("Bass guitar", "Electric Bass"), ("Bowed string instrument", "Strings"), ("String section", "Strings"),
        ("Violin, fiddle", "Strings"), ("Cello", "Strings"), ("Orchestra", "Strings"),
        ("Brass instrument", "Brass & Wind"), ("Trumpet", "Brass & Wind"), ("Saxophone", "Brass & Wind"), ("Flute", "Brass & Wind"),
        ("Wind instrument, woodwind instrument", "Brass & Wind"), ("Mallet percussion", "Mallets"), ("Marimba, xylophone", "Mallets"),
        ("Vibraphone", "Mallets"), ("Singing", "Vocals"), ("Choir", "Vocals"),
    ];

    /// <summary>
    /// Map YAMNet's own AudioSet scores onto the instrument classes. Each class gets its best matching
    /// AudioSet score, normalised to sum to 1. This works well for acoustic instruments but cannot tell synth types apart.
    /// </summary>
    public static double[] AudioSetPrior(float[] scores)
    {
        var probs = new double[InstrumentClasses.All.Count];
        foreach (var (a, c) in PriorMap)
        {
            int k = InstrumentClasses.IndexOf(c);
            probs[k] = Math.Max(probs[k], Score(scores, a));
        }
        double sum = probs.Sum() + 1e-3 * probs.Length;
        return probs.Select(p => (p + 1e-3) / sum).ToArray();
    }

    static readonly Lazy<Dictionary<string, int>> ClassIndex =
        new(() => YamNet.ClassNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i));

    static double Score(float[] scores, string audioSetName) =>
        ClassIndex.Value.TryGetValue(audioSetName, out var i) ? scores[i] : 0;

    public void Dispose() => _yamNet.Dispose();
}
