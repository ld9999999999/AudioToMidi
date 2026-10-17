namespace AudioToMidi.Core.Instruments;

/// <summary>Which tracks an instrument class may be suggested for.</summary>
public enum InstrumentRole { Melodic, Bass, Vocal }

/// <param name="Program">General MIDI program (0-based) to suggest.</param>
public sealed record InstrumentClass(string Name, int Program, InstrumentRole Role);

/// <summary>
/// The classes the instrument head predicts, chosen to match the Apple Loops metadata it was trained on,
/// each with the closest General MIDI program.
/// </summary>
public static class InstrumentClasses
{
    public static readonly IReadOnlyList<InstrumentClass> All =
    [
        new("Piano", 0, InstrumentRole.Melodic),
        new("Electric Piano", 4, InstrumentRole.Melodic),
        new("Clavinet", 7, InstrumentRole.Melodic),
        new("Organ", 16, InstrumentRole.Melodic),
        new("Synth Pad", 89, InstrumentRole.Melodic),
        new("Synth Lead", 81, InstrumentRole.Melodic),
        new("Synth", 90, InstrumentRole.Melodic),
        new("Electric Guitar", 27, InstrumentRole.Melodic),
        new("Acoustic Guitar", 25, InstrumentRole.Melodic),
        new("Strings", 48, InstrumentRole.Melodic),
        new("Brass & Wind", 61, InstrumentRole.Melodic),
        new("Mallets", 11, InstrumentRole.Melodic),
        new("Vocals", 53, InstrumentRole.Vocal),
        new("Electric Bass", 33, InstrumentRole.Bass),
        new("Synth Bass", 38, InstrumentRole.Bass),
    ];

    public static int IndexOf(string name)
    {
        for (int i = 0; i < All.Count; i++) if (All[i].Name == name) return i;
        return -1;
    }
}
