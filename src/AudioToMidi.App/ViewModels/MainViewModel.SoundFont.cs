using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Synth;
using CommunityToolkit.Mvvm.ComponentModel;
using MeltySynth;

namespace AudioToMidi.App.ViewModels;

/// <summary>A link to a free SoundFont's home page. The links go to pages because GeneralUser GS's licence asks for that.</summary>
public sealed record SoundFontLink(string Name, Uri Page, string Description);

public partial class MainViewModel
{
    // ---- SoundFont playback ----

    /// <summary>Free General MIDI SoundFonts, as suggested by the LMMS documentation.</summary>
    public IReadOnlyList<SoundFontLink> SoundFontLinks { get; } =
    [
        new("GeneralUser GS", new Uri("https://www.schristiancollins.com/generaluser.php"),
            "General MIDI and GS, about 30 MB. Free for any music, private or commercial"),
        new("FreePats", new Uri("http://freepats.zenvoid.org/"), "Free and open instrument sets, some as SoundFonts"),
        new("ArachnoSoft percussion", new Uri("http://www.arachnosoft.com/main/download.php?id=soundfont-sf2"), "SoundFonts of special percussion"),
        new("More (LMMS)", new Uri("https://docs.lmms.io/user-manual/resources/soundfonts"), "LMMS's list of SoundFonts"),
    ];

    /// <summary>Play through a SoundFont when one is found, and the built-in synth otherwise.</summary>
    [ObservableProperty] private bool _useSoundFont;

    /// <summary>The chosen SoundFont; empty for the first one found.</summary>
    [ObservableProperty] private string _soundFontPath = "";

    [ObservableProperty] private string _soundFontStatus = "";

    /// <summary>A SoundFont is loaded and the tracks play from it.</summary>
    [ObservableProperty] private bool _hasSoundFont;

    /// <summary>The instrument the piano roll's keys play from the SoundFont (0-based General MIDI program).</summary>
    [ObservableProperty] private int _keyboardProgram;

    public IReadOnlyList<string> KeyboardPrograms => GeneralMidi.NumberedPrograms;

    partial void OnKeyboardProgramChanged(int value)
    {
        if (_loading) return;
        ApplySoundFont();
        SaveSettings();
    }

    public string SoundFontTip =>
        "Play the tracks and drums with a SoundFont's General MIDI instruments instead of the built-in synth.\n" + SoundFontStatus;

    SoundFont? _soundFont;
    string? _soundFontFile;
    int _soundFontRequest; // the newest request wins when loads overlap

    partial void OnSoundFontStatusChanged(string value) => OnPropertyChanged(nameof(SoundFontTip));

    partial void OnUseSoundFontChanged(bool value)
    {
        if (_loading) return;
        SaveSettings();
        _ = UpdateSoundFontAsync(announce: true);
    }

    partial void OnSoundFontPathChanged(string value)
    {
        if (_loading) return;
        SaveSettings();
        _ = UpdateSoundFontAsync();
    }

    /// <summary>Use a SoundFont the user picked. A .zip is unpacked into the SoundFonts folder first.</summary>
    public async Task ChooseSoundFontAsync(string path)
    {
        try
        {
            if (!SoundFonts.IsSoundFont(path)) SoundFontStatus = $"Unpacking {Path.GetFileName(path)}…";
            var font = await Task.Run(() => SoundFonts.Import(path));
            UseSoundFont = true;
            SoundFontPath = font;
        }
        catch (Exception e)
        {
            SoundFontStatus = $"Couldn't use {Path.GetFileName(path)}: {e.Message}";
            Status = SoundFontStatus;
        }
    }

    /// <summary>
    /// Load the chosen SoundFont (or the first one found) in the background if it is wanted and not loaded yet,
    /// then give it to the player. Turning it off frees the memory, since an .sf3 can take hundreds of MB once decoded.
    /// </summary>
    async Task UpdateSoundFontAsync(bool announce = false)
    {
        int request = ++_soundFontRequest;
        string? chosen = NullIfEmpty(SoundFontPath);
        string? path = UseSoundFont ? chosen ?? SoundFonts.Installed().FirstOrDefault() : null;

        void Drop(string status)
        {
            _soundFont = null;
            _soundFontFile = null;
            HasSoundFont = false;
            SoundFontStatus = status;
            ApplySoundFont();
        }

        if (!UseSoundFont) { Drop("Off: the tracks play on the built-in synth."); return; }
        if (path is null)
        {
            Drop("No SoundFont found. Download one (links below), then pick its .sf2 or .zip with …, or put it in the SoundFonts folder.");
            if (announce) Status = "No SoundFont found: choose one in the menu (≡), under SOUND FONT.";
            return;
        }
        if (path == _soundFontFile && _soundFont is not null) { ApplySoundFont(); return; }
        if (!File.Exists(path)) { Drop($"Not found: {path}"); return; }

        SoundFontStatus = $"Loading {Path.GetFileName(path)}…";
        try
        {
            var font = await Task.Run(() => SoundFonts.Load(path));
            if (request != _soundFontRequest) return; // a newer choice took over
            _soundFont = font;
            _soundFontFile = path;
            HasSoundFont = true;
            SoundFontStatus = $"{Path.GetFileName(path)}: {font.Presets.Count} instruments" +
                (chosen is null ? $", found in {Path.GetDirectoryName(path)}." : ".");
            ApplySoundFont();
        }
        catch (Exception e)
        {
            if (request != _soundFontRequest) return;
            Drop($"Couldn't load {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>Give the loaded SoundFont, when on, to the mixer and the keyboard, or fall back to the built-in synth.</summary>
    void ApplySoundFont()
    {
        if (_audio is null) return;
        var font = UseSoundFont ? _soundFont : null;
        _audio.Player.SetSoundFont(font);
        _audio.SetKeyboardSound(font, KeyboardProgram);
    }
}
