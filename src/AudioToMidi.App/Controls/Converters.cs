using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace AudioToMidi.App.Controls;

public static partial class Converters
{
    /// <summary>"33. Electric Bass (finger)" → "EL. BASS FINGER", short enough for a strip's instrument display.</summary>
    public static readonly IValueConverter ShortProgram = new FuncValueConverter<string?, string>(s => s is null ? "" : Shorten(s));

    /// <summary>Tooltip for the display's expand/restore toggle.</summary>
    public static readonly IValueConverter ExpandTip = new FuncValueConverter<bool, string>(expanded =>
        expanded ? "Restore the soundboard" : "Expand the display to the whole window");

    static readonly (string Word, string Short)[] Abbreviations =
    [
        ("Electric", "El."), ("Acoustic", "Ac."), ("Guitar", "Gtr"), ("Piano", "Pno"), ("Ensemble", "Ens."),
        ("Synth", "Syn."), ("Orchestra", "Orch."), ("Instrument", "Instr."), ("Contrabass", "C.bass"),
    ];

    public static string Shorten(string program)
    {
        var s = Numbering().Replace(program, "").Replace("(", "").Replace(")", "");
        foreach (var (word, abbreviation) in Abbreviations) s = s.Replace(word, abbreviation, StringComparison.OrdinalIgnoreCase);
        return s.ToUpperInvariant();
    }

    [GeneratedRegex(@"^\d+\.\s*")]
    private static partial Regex Numbering();
}
