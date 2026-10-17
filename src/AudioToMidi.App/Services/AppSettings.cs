using System.Text.Json;
using AudioToMidi.Core;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.App.Services;

/// <summary>
/// User preferences, saved as JSON in ~/Library/Application Support/AudioToMidi (macOS)
/// or %LOCALAPPDATA%\AudioToMidi (Windows).
/// </summary>
public sealed class AppSettings
{
    public string? FfmpegPath { get; set; }
    public string? LastFolder { get; set; }
    public AnalysisSettings Analysis { get; set; } = new();
    public NoteSettings Notes { get; set; } = new();
    public StaffLayout Staves { get; set; } = StaffLayout.Auto;
    public bool AlignMidiToBars { get; set; } = true;

    /// <summary>Quantize swung music on its swung grid, and write it straight under a "Swing" mark.</summary>
    public bool KeepSwing { get; set; } = true;

    /// <summary>The piano roll's lane of repeating bar patterns.</summary>
    public bool ShowPatterns { get; set; }

    /// <summary>Chord symbols in the piano roll and in exported sheet music.</summary>
    public bool ShowChords { get; set; } = true;

    /// <summary>"Dark", "Light" or "System".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Saved sets of the tuning settings, by name, and the one last chosen.</summary>
    public Dictionary<string, Preset> Presets { get; set; } = [];
    public string? CurrentPreset { get; set; }
    public bool AutoInstruments { get; set; } = true;
    public string PreviewSound { get; set; } = "Synth keys";
    public double PreviewVolume { get; set; } = 0.6;
    public TrackMode TrackMode { get; set; } = TrackMode.Single;
    public int SplitNote { get; set; } = 48;

    /// <summary>Instrument separation: 4 or 6 stems.</summary>
    public int Stems { get; set; } = 4;

    /// <summary>With 6 stems, use BS-RoFormer when it is installed, and HT-Demucs otherwise.</summary>
    public bool PreferRoformer { get; set; } = true;

    /// <summary>Run BS-RoFormer on the GPU (macOS, through CoreML). It falls back to the CPU by itself if needed.</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>Where model downloads come from: null for each model's publisher, else a URL or file path (see ModelInstaller).</summary>
    public string? ModelSource { get; set; }

    /// <summary>Older setting: the chosen model's file name. Read once into <see cref="Stems"/>.</summary>
    public string? SeparationModelFile { get; set; }

    /// <summary>Base tracks (by name, e.g. "Other", "Piano") to split into melody, harmony and low line.</summary>
    public List<string> SplitTrackNames { get; set; } = [];

    /// <summary>Older setting: split the first track. Read once and moved to <see cref="SplitTrackNames"/>.</summary>
    public bool SplitLines { get; set; }
    /// <summary>Play the tracks and drums through a SoundFont when one is found, and the built-in synth otherwise.</summary>
    public bool UseSoundFont { get; set; } = true;

    /// <summary>The SoundFont (.sf2 or .sf3) to play with; null for the first one found (see SoundFonts.Installed).</summary>
    public string? SoundFontPath { get; set; }

    /// <summary>The General MIDI program (0-based) the piano roll's keys play from the SoundFont.</summary>
    public int KeyboardProgram { get; set; }

    public bool TranscribeDrums { get; set; }
    public double DrumSensitivity { get; set; } = 0.5;

    /// <summary>GM program per track name ("Transcription", "Keys", "Bass", "Other", "Vocals", "Guitar", "Piano").</summary>
    public Dictionary<string, int> TrackPrograms { get; set; } = new()
    {
        ["Transcription"] = 0, ["Keys"] = 0, ["Other"] = 0, ["Bass"] = 33, ["Vocals"] = 53, ["Guitar"] = 27, ["Piano"] = 0,
    };

    // AUDIOTOMIDI_SETTINGS_DIR overrides the folder, so automated test runs never touch real settings.
    static readonly string FilePath = Path.Combine(
        Environment.GetEnvironmentVariable("AUDIOTOMIDI_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioToMidi"),
        "settings.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch { /* corrupt or from an incompatible version: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* not worth interrupting the user over */ }
    }
}

/// <summary>A named set of the settings that tune transcription and notation (the menu's presets).</summary>
public sealed class Preset
{
    public AnalysisSettings Analysis { get; set; } = new();
    public NoteSettings Notes { get; set; } = new();
    public TrackMode TrackMode { get; set; } = TrackMode.Single;
    public int Stems { get; set; } = 4;
    public bool PreferRoformer { get; set; } = true;
    public int SplitNote { get; set; } = 48;
    public bool TranscribeDrums { get; set; }
    public double DrumSensitivity { get; set; } = 0.5;
    public int BeatsPerBar { get; set; } = 4;

    /// <summary>"AUTO", "4/4", "6/8"...; when missing (older presets), <see cref="BeatsPerBar"/>/4, or automatic for 4.</summary>
    public string? TimeSignature { get; set; }
    public StaffLayout Staves { get; set; } = StaffLayout.Auto;
    public bool ShowChords { get; set; } = true;
    public bool AlignMidiToBars { get; set; } = true;
}
