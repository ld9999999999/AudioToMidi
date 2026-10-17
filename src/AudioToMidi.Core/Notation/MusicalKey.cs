namespace AudioToMidi.Core.Notation;

/// <summary>A key signature (number of sharps if positive, flats if negative) and mode.</summary>
public readonly record struct MusicalKey(int Fifths, bool Minor)
{
    const string Letters = "FCGDAEB";

    /// <summary>Position of the tonic on the line of fifths (C = 0, G = 1, F = -1, ...).</summary>
    int TonicPosition => Fifths + (Minor ? 3 : 0);

    public int TonicPitchClass => PitchClassOf(TonicPosition);

    public string Name
    {
        get
        {
            var (step, alter) = FromPosition(TonicPosition);
            return $"{step}{(alter > 0 ? "♯" : alter < 0 ? "♭" : "")} {(Minor ? "minor" : "major")}";
        }
    }

    public string SignatureName => Fifths switch
    {
        0 => "no sharps or flats",
        > 0 => $"{Fifths}♯",
        _ => $"{-Fifths}♭",
    };

    /// <summary>
    /// Spell a MIDI pitch in this key: diatonic notes as in the key signature, and chromatic notes with
    /// whichever enharmonic is closest to the key (sharps in sharp keys, flats in flat keys).
    /// </summary>
    public (char Step, int Alter, int Octave) Spell(int midi)
    {
        int pc = ((midi % 12) + 12) % 12;
        // The diatonic notes span positions Fifths-1 .. Fifths+5. Minor keys lean one step sharper
        // so the raised leading tone comes out as G♯ in A minor.
        double centre = Fifths + 2 + (Minor ? 1 : 0);
        int basePos = pc * 7 % 12;
        int best = int.MaxValue;
        foreach (var p in new[] { basePos - 24, basePos - 12, basePos, basePos + 12 })
        {
            // Ties go to the flatter spelling, such as A♭ in C major.
            if (best == int.MaxValue || Math.Abs(p - centre) < Math.Abs(best - centre)) best = p;
        }
        var (step, alter) = FromPosition(best);
        int octave = (midi - alter) / 12 - 1; // octave of the written letter, e.g. B♯3 sounds as C4
        return (step, alter, octave);
    }

    /// <summary>Get the key signature for a tonic pitch class, using the spelling with fewer accidentals.</summary>
    public static MusicalKey FromTonic(int pitchClass, bool minor)
    {
        // Sharps or flats of each major key by tonic pitch class: C, D♭, D, E♭, E, F, F♯, G, A♭, A, B♭, B
        int[] majorFifths = [0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5];
        pitchClass = ((pitchClass % 12) + 12) % 12;
        if (!minor) return new MusicalKey(majorFifths[pitchClass], false);
        // The relative major is three semitones up. Pitch class 3 in minor is written as E♭ minor (6♭).
        int fifths = pitchClass == 3 ? -6 : majorFifths[(pitchClass + 3) % 12];
        return new MusicalKey(fifths, true);
    }

    static int PitchClassOf(int position) => ((position * 7 % 12) + 12) % 12;

    static (char Step, int Alter) FromPosition(int position)
    {
        int shifted = position + 1; // F = 0
        int letter = ((shifted % 7) + 7) % 7;
        int alter = (int)Math.Floor(shifted / 7.0);
        return (Letters[letter], alter);
    }
}
