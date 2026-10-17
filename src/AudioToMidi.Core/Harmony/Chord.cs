using AudioToMidi.Core.Notation;

namespace AudioToMidi.Core.Harmony;

/// <summary>Chord qualities, by the intervals above the root.</summary>
public enum ChordQuality { Major, Minor, Diminished, Augmented, Sus2, Sus4, Power, Dominant7, Major7, Minor7, HalfDiminished7 }

/// <summary>A chord: root and quality, and the bass note when it is not the root (an inversion).</summary>
/// <param name="Root">Pitch class, 0 = C.</param>
/// <param name="Bass">Pitch class of the lowest note when that is another chord tone; null in root position.</param>
public readonly record struct Chord(int Root, ChordQuality Quality, int? Bass = null)
{
    /// <summary>Semitones above the root of each chord tone.</summary>
    public static int[] Intervals(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => [0, 4, 7],
        ChordQuality.Minor => [0, 3, 7],
        ChordQuality.Diminished => [0, 3, 6],
        ChordQuality.Augmented => [0, 4, 8],
        ChordQuality.Sus2 => [0, 2, 7],
        ChordQuality.Sus4 => [0, 5, 7],
        ChordQuality.Power => [0, 7],
        ChordQuality.Dominant7 => [0, 4, 7, 10],
        ChordQuality.Major7 => [0, 4, 7, 11],
        ChordQuality.Minor7 => [0, 3, 7, 10],
        ChordQuality.HalfDiminished7 => [0, 3, 6, 10],
        _ => [0],
    };

    /// <summary>Pitch classes of the chord tones.</summary>
    public IEnumerable<int> PitchClasses
    {
        get
        {
            int root = Root;
            return Intervals(Quality).Select(i => (root + i) % 12);
        }
    }

    /// <summary>Suffix after the root: "", "m", "7", "maj7", "sus4"...</summary>
    public static string Suffix(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => "",
        ChordQuality.Minor => "m",
        ChordQuality.Diminished => "dim",
        ChordQuality.Augmented => "+",
        ChordQuality.Sus2 => "sus2",
        ChordQuality.Sus4 => "sus4",
        ChordQuality.Power => "5",
        ChordQuality.Dominant7 => "7",
        ChordQuality.Major7 => "maj7",
        ChordQuality.Minor7 => "m7",
        ChordQuality.HalfDiminished7 => "m7♭5",
        _ => "",
    };

    /// <summary>The MusicXML &lt;kind&gt; value for a quality.</summary>
    public static string MusicXmlKind(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => "major",
        ChordQuality.Minor => "minor",
        ChordQuality.Diminished => "diminished",
        ChordQuality.Augmented => "augmented",
        ChordQuality.Sus2 => "suspended-second",
        ChordQuality.Sus4 => "suspended-fourth",
        ChordQuality.Power => "power",
        ChordQuality.Dominant7 => "dominant",
        ChordQuality.Major7 => "major-seventh",
        ChordQuality.Minor7 => "minor-seventh",
        ChordQuality.HalfDiminished7 => "half-diminished",
        _ => "other",
    };

    /// <summary>
    /// Return MIDI notes to play the chord: its bass (the root, or the inversion's bass) between C2 and B2,
    /// and its tones close together between G3 and F♯4, so one chord leads smoothly into the next.
    /// </summary>
    public int[] Voicing()
    {
        int bass = 36 + (Bass ?? Root);
        return [bass, .. PitchClasses.Select(pc => 55 + ((pc - 7) % 12 + 12) % 12).Order()];
    }

    /// <summary>Chord symbol spelled in <paramref name="key"/>, e.g. "B♭", "F♯m7", "C/E".</summary>
    public string Symbol(MusicalKey key) =>
        Name(Root, key) + Suffix(Quality) + (Bass is { } bass ? "/" + Name(bass, key) : "");

    /// <summary>
    /// Roman numeral in <paramref name="key"/>: upper case for major-type chords, lower case for minor and
    /// diminished, with ♭ or ♯ for roots outside the key's scale (natural minor for minor keys, whose
    /// raised seventh counts as in the scale). E.g. "V7", "ii", "♭VII", "vii°".
    /// </summary>
    public string Roman(MusicalKey key)
    {
        int degree = ((Root - key.TonicPitchClass) % 12 + 12) % 12;
        int[] scale = key.Minor ? [0, 2, 3, 5, 7, 8, 10] : [0, 2, 4, 5, 7, 9, 11];
        string[] numerals = ["I", "II", "III", "IV", "V", "VI", "VII"];
        string accidental = "";
        int step = Array.IndexOf(scale, degree);
        if (step < 0 && key.Minor && degree == 11) step = 6; // the raised leading tone
        else if (step < 0)
        {
            // Name it from the scale step a semitone above (♭) or below (♯), whichever is in the scale;
            // flats are the usual reading (♭VII, ♭VI, ♭III), so they win a tie.
            int above = Array.IndexOf(scale, (degree + 1) % 12), below = Array.IndexOf(scale, (degree + 11) % 12);
            (step, accidental) = above >= 0 ? (above, "♭") : (below, "♯");
        }
        bool lower = Quality is ChordQuality.Minor or ChordQuality.Diminished or ChordQuality.Minor7 or ChordQuality.HalfDiminished7;
        string numeral = lower ? numerals[step].ToLowerInvariant() : numerals[step];
        string suffix = Quality switch
        {
            ChordQuality.Diminished => "°",
            ChordQuality.Augmented => "+",
            ChordQuality.Dominant7 or ChordQuality.Minor7 => "7",
            ChordQuality.Major7 => "maj7",
            ChordQuality.HalfDiminished7 => "ø7",
            ChordQuality.Sus2 => "sus2",
            ChordQuality.Sus4 => "sus4",
            ChordQuality.Power => "5",
            _ => "",
        };
        return accidental + numeral + suffix;
    }

    static string Name(int pitchClass, MusicalKey key)
    {
        var (step, alter, _) = key.Spell(60 + pitchClass);
        return step + (alter > 0 ? new string('♯', alter) : alter < 0 ? new string('♭', -alter) : "");
    }
}

/// <summary>A chord over a stretch of time; <see cref="Chord"/> null where nothing pitched sounds.</summary>
public sealed record ChordSpan(double Start, double End, Chord? Chord);

/// <summary>A key over a stretch of time.</summary>
public sealed record KeySpan(double Start, double End, MusicalKey Key);
