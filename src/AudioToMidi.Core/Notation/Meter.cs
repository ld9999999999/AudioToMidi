using System.Globalization;

namespace AudioToMidi.Core.Notation;

/// <summary>
/// A time signature. In simple meters (2/4, 3/4, 4/4...) a beat is a quarter note, divided in two; in
/// compound meters (6/8, 9/8, 12/8) a beat is a dotted quarter, divided into three eighths.
/// <see cref="BeatsPerBar"/> counts the beats as they are felt and tracked, as <see cref="BarGrid"/> does:
/// 6/8 has two, 12/8 four.
/// </summary>
public readonly record struct Meter(int Beats, int BeatType)
{
    public static Meter Common { get; } = new(4, 4);

    public bool IsCompound => BeatType == 8 && Beats >= 6 && Beats % 3 == 0;

    public int BeatsPerBar => IsCompound ? Beats / 3 : Beats;

    /// <summary>A beat's length in quarter notes: 1 in x/4, 1.5 (a dotted quarter) in compound meters.</summary>
    public double BeatQuarters => IsCompound ? 1.5 : 4.0 / BeatType;

    /// <summary>How the beat divides: in three in compound meters, else in two.</summary>
    public int Subdivision => IsCompound ? 3 : 2;

    /// <summary>
    /// Convert a grid given per quarter note (1 = quarters, 2 = eighths, 3 = eighth triplets, 4 = 16ths...)
    /// to steps per beat. In compound meters a beat holds three eighths, so eighths become 3 a beat, 16ths 6
    /// and 32nds 12, and triplets map to the meter's own eighths and 16ths.
    /// </summary>
    public int StepsPerBeat(int perQuarter) => !IsCompound ? perQuarter : perQuarter switch
    {
        <= 1 => 1,
        2 or 3 => 3,
        4 or 6 => 6,
        _ => 12,
    };

    public override string ToString() => $"{Beats}/{BeatType}";

    /// <summary>Read a time signature such as "6/8"; return null if the text is not one.</summary>
    public static Meter? Parse(string? text)
    {
        var parts = text?.Split('/');
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int beats) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) ||
            beats < 1 || beats > 16 || type is not (2 or 4 or 8)) return null;
        return new Meter(beats, type);
    }
}
