namespace AudioToMidi.Core.Synth;

/// <summary>Sound settings for <see cref="PolySynth"/>. Times are in seconds and levels run from 0 to 1.</summary>
public sealed record SynthPatch(
    string Name,
    float SawLevel, float SquareLevel, float SubLevel,
    float DetuneCents,
    float Cutoff, float Resonance, float FilterEnvOctaves, float KeyTracking,
    float AmpAttack, float AmpDecay, float AmpSustain, float AmpRelease,
    float FilterAttack, float FilterDecay, float FilterSustain, float FilterRelease)
{
    public static readonly SynthPatch SynthKeys = new("Synth keys",
        SawLevel: 0.6f, SquareLevel: 0.25f, SubLevel: 0.12f, DetuneCents: 7,
        Cutoff: 900, Resonance: 0.25f, FilterEnvOctaves: 2.5f, KeyTracking: 0.5f,
        AmpAttack: 0.004f, AmpDecay: 0.6f, AmpSustain: 0.55f, AmpRelease: 0.3f,
        FilterAttack: 0.002f, FilterDecay: 0.45f, FilterSustain: 0.2f, FilterRelease: 0.3f);

    public static readonly SynthPatch SoftPad = new("Soft pad",
        SawLevel: 0.5f, SquareLevel: 0.1f, SubLevel: 0.05f, DetuneCents: 12,
        Cutoff: 900, Resonance: 0.15f, FilterEnvOctaves: 1.2f, KeyTracking: 0.4f,
        AmpAttack: 0.35f, AmpDecay: 1.0f, AmpSustain: 0.8f, AmpRelease: 0.9f,
        FilterAttack: 0.6f, FilterDecay: 1.2f, FilterSustain: 0.5f, FilterRelease: 0.9f);

    public static readonly SynthPatch Pluck = new("Pluck",
        SawLevel: 0.7f, SquareLevel: 0.2f, SubLevel: 0.05f, DetuneCents: 4,
        Cutoff: 500, Resonance: 0.35f, FilterEnvOctaves: 4f, KeyTracking: 0.6f,
        AmpAttack: 0.002f, AmpDecay: 0.35f, AmpSustain: 0f, AmpRelease: 0.2f,
        FilterAttack: 0.001f, FilterDecay: 0.18f, FilterSustain: 0f, FilterRelease: 0.15f);

    public static readonly SynthPatch Organ = new("Organ",
        SawLevel: 0.15f, SquareLevel: 0.6f, SubLevel: 0.1f, DetuneCents: 2,
        Cutoff: 2500, Resonance: 0.1f, FilterEnvOctaves: 0.3f, KeyTracking: 0.3f,
        AmpAttack: 0.006f, AmpDecay: 0.1f, AmpSustain: 1f, AmpRelease: 0.08f,
        FilterAttack: 0.005f, FilterDecay: 0.1f, FilterSustain: 1f, FilterRelease: 0.08f);

    public static readonly SynthPatch Bass = new("Bass",
        SawLevel: 0.6f, SquareLevel: 0.3f, SubLevel: 0.25f, DetuneCents: 3,
        Cutoff: 450, Resonance: 0.3f, FilterEnvOctaves: 2.2f, KeyTracking: 1f,
        AmpAttack: 0.003f, AmpDecay: 0.5f, AmpSustain: 0.6f, AmpRelease: 0.12f,
        FilterAttack: 0.001f, FilterDecay: 0.2f, FilterSustain: 0.15f, FilterRelease: 0.12f);

    public static readonly SynthPatch Keys = new("Piano",
        SawLevel: 0.35f, SquareLevel: 0.35f, SubLevel: 0.1f, DetuneCents: 3,
        Cutoff: 1100, Resonance: 0.1f, FilterEnvOctaves: 2.8f, KeyTracking: 0.7f,
        AmpAttack: 0.002f, AmpDecay: 1.4f, AmpSustain: 0.15f, AmpRelease: 0.35f,
        FilterAttack: 0.001f, FilterDecay: 0.6f, FilterSustain: 0.1f, FilterRelease: 0.3f);

    public static readonly SynthPatch Brass = new("Brass",
        SawLevel: 0.85f, SquareLevel: 0.1f, SubLevel: 0f, DetuneCents: 5,
        Cutoff: 700, Resonance: 0.2f, FilterEnvOctaves: 2.5f, KeyTracking: 0.9f,
        AmpAttack: 0.04f, AmpDecay: 0.3f, AmpSustain: 0.8f, AmpRelease: 0.15f,
        FilterAttack: 0.06f, FilterDecay: 0.3f, FilterSustain: 0.6f, FilterRelease: 0.15f);

    public static readonly SynthPatch Reed = new("Reed",
        SawLevel: 0.2f, SquareLevel: 0.7f, SubLevel: 0f, DetuneCents: 2,
        Cutoff: 1400, Resonance: 0.15f, FilterEnvOctaves: 0.8f, KeyTracking: 0.6f,
        AmpAttack: 0.03f, AmpDecay: 0.2f, AmpSustain: 0.85f, AmpRelease: 0.1f,
        FilterAttack: 0.03f, FilterDecay: 0.2f, FilterSustain: 0.8f, FilterRelease: 0.1f);

    /// <summary>The sounds offered on the keyboard.</summary>
    public static readonly IReadOnlyList<SynthPatch> All = [SynthKeys, SoftPad, Pluck, Organ, Keys, Bass, Brass, Reed];

    /// <summary>Pick the closest built-in sound for a General MIDI program (0-based), by instrument family.</summary>
    public static SynthPatch ForProgram(int program) => program switch
    {
        <= 7 => Keys,             // pianos
        <= 15 => Pluck,           // chromatic percussion
        <= 23 => Organ,
        <= 31 => Pluck,           // guitars
        <= 39 => Bass,
        <= 55 => SoftPad,         // strings, ensembles, choir
        <= 63 => Brass,
        <= 79 => Reed,            // reeds and pipes
        <= 87 => SynthKeys,       // synth leads
        <= 103 => SoftPad,        // synth pads and effects
        _ => Pluck,               // ethnic and percussive
    };
}
