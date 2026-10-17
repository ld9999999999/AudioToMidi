using AudioToMidi.App.Controls;
using AudioToMidi.Core.Instruments;
using AudioToMidi.Core.Midi;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioToMidi.App.ViewModels;

/// <summary>One output track: its mixer strip (colour matching the piano roll), name, note count, instrument.</summary>
/// <param name="baseName">For a base track (or the melody of a split one): its name, e.g. "Piano". Null for
/// the harmony and low line of a split track, which cannot be split again.</param>
public sealed partial class TrackRow(int index, string name, int program, Action onChanged,
    string? baseName = null, bool split = false, Action<TrackRow>? onSplitChanged = null)
    : MixerStrip(name, PianoRollView.TrackColor(index))
{
    bool _settingSuggestion;

    public int Index { get; } = index;
    public string? BaseName { get; } = baseName;
    public override bool CanSplit => BaseName is not null;

    [ObservableProperty] private bool _split = split;

    partial void OnSplitChanged(bool value) => onSplitChanged?.Invoke(this);
    public IReadOnlyList<string> Programs => GeneralMidi.NumberedPrograms;

    [ObservableProperty] private int _program = program;
    [ObservableProperty] private int _noteCount;
    [ObservableProperty] private string _detected = "";

    /// <summary>The user picked an instrument for this file; automatic suggestions leave it alone.</summary>
    public bool UserChose { get; set; }

    public string Summary => $"{Name} · {NoteCount} notes";

    /// <summary>The instrument in a few words, for the mixer strip's display.</summary>
    public string ProgramName => Controls.Converters.Shorten(GeneralMidi.Programs[Math.Clamp(Program, 0, 127)]);
    public bool HasDetected => Detected.Length > 0;

    public void ShowGuess(InstrumentGuess? guess, bool apply)
    {
        if (guess is null) { Detected = ""; return; }
        var also = guess.Alternatives.Count > 0
            ? " · also " + string.Join(", ", guess.Alternatives.Select(a => $"{a.Label} {a.Probability:P0}"))
            : "";
        Detected = !guess.Reliable
            ? $"Rough guess from the full mix: {guess.Label}. Download the separation model (Separate instruments mode) for reliable detection."
            : guess.ShouldApply
                ? $"Detected: {guess.Label} ({guess.Confidence:P0}){also}"
                : $"Unsure, not applied: {guess.Label} ({guess.Confidence:P0}){also}";
        if (apply && guess.ShouldApply && !UserChose)
        {
            _settingSuggestion = true;
            Program = guess.Program;
            _settingSuggestion = false;
        }
    }

    partial void OnProgramChanged(int value)
    {
        OnPropertyChanged(nameof(ProgramName));
        if (!_settingSuggestion) UserChose = true;
        onChanged();
    }

    partial void OnNoteCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnDetectedChanged(string value) => OnPropertyChanged(nameof(HasDetected));
}
