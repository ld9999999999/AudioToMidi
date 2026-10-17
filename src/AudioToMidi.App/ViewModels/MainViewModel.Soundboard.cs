using System.Collections.ObjectModel;
using System.ComponentModel;
using AudioToMidi.App.Controls;
using AudioToMidi.App.Services;
using AudioToMidi.Core;
using AudioToMidi.Core.Drums;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Separation;
using AudioToMidi.Core.Synth;
using AudioToMidi.Core.Transcription;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioToMidi.App.ViewModels;

/// <summary>What the soundboard window shows beyond the settings themselves: readouts, mixer, presets, theme.</summary>
public sealed partial class MainViewModel
{
    // ---- Readouts (transport LEDs) ----

    public string FileName => SourcePath is null ? "NO FILE — DROP ONE ON THE DISPLAY" : Path.GetFileName(SourcePath);

    /// <summary>Where playback is, or where it would start.</summary>
    double ReadoutTime => Playhead >= 0 ? Playhead : HasRange ? RangeStart : 0;

    /// <summary>"019.2": bar and beat.</summary>
    public string PositionText
    {
        get
        {
            if (BarGrid is not { } grid) return "---.-";
            double beats = grid.ToBeats(ReadoutTime);
            int bar = (int)Math.Floor(beats / grid.BeatsPerBar);
            int beat = (int)Math.Floor(beats - bar * grid.BeatsPerBar) + 1;
            return $"{Math.Max(0, bar + 1):000}.{beat}";
        }
    }

    /// <summary>"00:43.18".</summary>
    public string TimeText
    {
        get
        {
            double t = Math.Max(0, ReadoutTime);
            return $"{(int)(t / 60):00}:{t % 60:00.00}";
        }
    }

    /// <summary>The chord sounding at the playhead, "--" when none.</summary>
    public string ChordText
    {
        get
        {
            double t = ReadoutTime;
            var span = Harmony.Chords.FirstOrDefault(c => c.Chord is not null && t >= c.Start && t < c.End)
                       ?? (Playhead < 0 ? Harmony.Chords.FirstOrDefault(c => c.Chord is not null) : null);
            return span is null ? "--" : Harmony.Symbol(span);
        }
    }

    /// <summary>The key at the playhead, "A MIN"; "--" before analysis.</summary>
    public string KeyText
    {
        get
        {
            if (Notes.Count == 0) return "--";
            var key = Harmony.Keys.Count > 0 ? Harmony.KeyAt(ReadoutTime) : KeyEstimator.Estimate(Notes);
            return key.Name.Replace(" major", " MAJ").Replace(" minor", " MIN");
        }
    }

    public string TempoText => $"{Bpm:0.0}";
    public string TempoLabel => DetectedTempo is { } t
        ? $"TEMPO · {(t.Confidence >= 0.3 ? "SURE" : t.Confidence >= 0.1 ? "UNSURE" : "GUESS")}"
        : "TEMPO";

    /// <summary>Playback speed in percent of the song tempo.</summary>
    public double PlaybackPercent
    {
        get => Bpm > 0 ? (double)(PlaybackBpm / Bpm) * 100 : 100;
        set { if (Bpm > 0) PlaybackBpm = Math.Round(Bpm * (decimal)value / 100, 2); }
    }

    public string TimeSignatureText => CurrentMeter.ToString();
    public string TimeSignatureLabel => IsMeterAuto ? "TIME SIG · AUTO" : "TIME SIGNATURE";
    public string DownbeatText => $"BEAT {Downbeat + 1}";
    public string KeyChoiceText => SelectedKey?.Label ?? "AUTO";

    /// <summary>The separation model in a few words, for its LCD.</summary>
    public string ModelShort =>
        !IsInstrumentMode ? "NOT USED"
        : IsModelMissing ? "NOT INSTALLED"
        : CurrentModel == StemModel.Roformer ? (CanUseGpu && UseGpu ? "BS-ROFORMER · GPU" : "BS-ROFORMER · CPU")
        : CurrentModel == StemModel.SixStem ? "HT-DEMUCS 6" : "HT-DEMUCS 4";

    // ---- Drum counters ----

    int CountOf(params DrumKind[] kinds) => _drumHits.Count(h => kinds.Contains(h.Kind));
    public string KickCount => CountOf(DrumKind.Kick).ToString();
    public string SnareCount => CountOf(DrumKind.Snare).ToString();
    public string HiHatCount => CountOf(DrumKind.ClosedHiHat).ToString();
    public string OpenHatCount => CountOf(DrumKind.OpenHiHat).ToString();
    public string DrumSourceText => _session.Stems is null ? "FROM THE DRUM REMOVAL" : "FROM THE DRUM STEM";

    // ---- Steppers ----

    [RelayCommand] void PreviousTimeSignature() => StepTimeSignature(-1);
    [RelayCommand] void NextTimeSignature() => StepTimeSignature(1);

    /// <summary>From automatic, start stepping at the detected time signature. Stepping back past the first one returns to automatic.</summary>
    void StepTimeSignature(int by)
    {
        var options = TimeSignatures.ToList();
        int i = IsMeterAuto ? options.FindIndex(t => t.Value == CurrentMeter) : options.IndexOf(SelectedTimeSignature);
        int next = Math.Clamp(i + by, 0, options.Count - 1);
        if (options[next] != SelectedTimeSignature) SelectedTimeSignature = options[next];
    }
    [RelayCommand] void PreviousKey() => Step(KeyOptions, SelectedKey, -1, c => SelectedKey = c);
    [RelayCommand] void NextKey() => Step(KeyOptions, SelectedKey, 1, c => SelectedKey = c);

    static void Step<T>(IReadOnlyList<T> options, T current, int by, Action<T> set)
    {
        int i = options.ToList().IndexOf(current);
        int next = Math.Clamp(i + by, 0, options.Count - 1);
        if (next != i) set(options[next]);
    }

    // ---- Display ----

    /// <summary>The display and transport fill the window; the top bar and soundboard are hidden.</summary>
    [ObservableProperty] private bool _displayExpanded;

    // ---- Transport ----

    [RelayCommand]
    void Stop()
    {
        if (IsPlaying) StopPlayback();
    }

    [RelayCommand]
    void Rewind()
    {
        if (!IsPlaying) return;
        StopPlayback();
        TogglePlay();
    }

    // ---- Mixer ----

    /// <summary>The original recording, played under the synth to compare with it.</summary>
    public MixerStrip SourceStrip { get; } = new("Source", Color.Parse("#bfbab0"), "ORIGINAL") { Volume = 0 };
    public MixerStrip DrumStrip { get; } = new("Drums", Color.Parse("#ff6b6b"), "DRUM KIT");

    /// <summary>Peak level of what plays, linear, for the master meters.</summary>
    [ObservableProperty] private double _masterLevel;

    /// <summary>The source, every track, then the drums when they are transcribed.</summary>
    public ObservableCollection<MixerStrip> MixerStrips { get; } = [];

    /// <summary>Rebuild the strip list after the tracks change, keeping each track's mix by name.</summary>
    void RebuildMixer()
    {
        var previous = MixerStrips.ToDictionary(s => s.Name);
        foreach (var s in MixerStrips) s.MixChanged -= OnMixChanged;
        MixerStrips.Clear();
        MixerStrips.Add(SourceStrip);
        foreach (var t in Tracks)
        {
            if (previous.TryGetValue(t.Name, out var old) && !ReferenceEquals(old, t)) t.CopyMix(old);
            MixerStrips.Add(t);
        }
        if (TranscribeDrums) MixerStrips.Add(DrumStrip);
        foreach (var s in MixerStrips) s.MixChanged += OnMixChanged;
        ApplyMix();
    }

    void OnMixChanged(MixerStrip strip) => ApplyMix();

    void ApplyMix()
    {
        if (_audio is not null) ConfigureMix(_audio.Player);
    }

    /// <summary>
    /// Pass the strips to a player (the live one, or one saving the mix): each track's instrument, level,
    /// pan, mute and solo.
    /// </summary>
    void ConfigureMix(NotePlayer player)
    {
        player.SetTracks(Tracks.Select(t => t.Program).ToList());
        foreach (var t in Tracks) player.SetMix(t.Index, Fader.Gain(t.Volume), t.Pan, t.Mute, t.Solo);
        player.SetMix(NotePlayer.DrumChannel, Fader.Gain(DrumStrip.Volume), DrumStrip.Pan, DrumStrip.Mute, DrumStrip.Solo);
        player.SetMix(NotePlayer.SourceChannel, Fader.Gain(SourceStrip.Volume), SourceStrip.Pan, SourceStrip.Mute, SourceStrip.Solo);
    }

    /// <summary>Let the meters fall back smoothly: each shows the higher of the new peak and 80% of the last one.</summary>
    void UpdateMeters()
    {
        if (_audio is null) return;
        var (tracks, drums, source, master) = _audio.Player.ReadLevels();
        static double Fall(double old, float peak) => Math.Max(peak, old * 0.8);
        foreach (var t in Tracks) t.Level = Fall(t.Level, t.Index < tracks.Length ? tracks[t.Index] : 0);
        DrumStrip.Level = Fall(DrumStrip.Level, drums);
        SourceStrip.Level = Fall(SourceStrip.Level, source);
        MasterLevel = Fall(MasterLevel, master * MasterGain);
    }

    void ClearMeters()
    {
        foreach (var s in MixerStrips) s.Level = 0;
        MasterLevel = 0;
    }

    // ---- Theme ----

    public IReadOnlyList<Choice<string>> ThemeOptions { get; } = [new("DARK", "Dark"), new("LIGHT", "Light"), new("SYSTEM", "System")];
    [ObservableProperty] private Choice<string> _selectedTheme = null!;

    partial void OnSelectedThemeChanged(Choice<string> value)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = value.Value switch { "Light" => ThemeVariant.Light, "System" => ThemeVariant.Default, _ => ThemeVariant.Dark };
        SaveSettings();
    }

    [RelayCommand]
    void ToggleTheme()
    {
        bool dark = Application.Current?.ActualThemeVariant != ThemeVariant.Light;
        SelectedTheme = ThemeOptions.First(t => t.Value == (dark ? "Light" : "Dark"));
    }

    // ---- Presets ----

    /// <summary>Saved sets of the tuning settings, by name.</summary>
    public ObservableCollection<string> Presets { get; } = [];
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string _presetName = "";
    bool _applyingPreset;

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is not null && !_applyingPreset && _settings.Presets.TryGetValue(value, out var preset))
        {
            ApplyPreset(preset);
            PresetName = value;
        }
        SaveSettings();
    }

    [RelayCommand]
    void SavePreset()
    {
        var name = string.IsNullOrWhiteSpace(PresetName) ? SelectedPreset : PresetName.Trim();
        if (string.IsNullOrWhiteSpace(name)) { Status = "Type a name for the preset first."; return; }
        _settings.Presets[name] = CapturePreset();
        if (!Presets.Contains(name))
        {
            int at = Presets.TakeWhile(p => string.Compare(p, name, StringComparison.OrdinalIgnoreCase) < 0).Count();
            Presets.Insert(at, name);
        }
        _applyingPreset = true;
        SelectedPreset = name;
        _applyingPreset = false;
        SaveSettings();
        Status = $"Saved preset \"{name}\".";
    }

    [RelayCommand]
    void DeletePreset()
    {
        if (SelectedPreset is not { } name) return;
        _settings.Presets.Remove(name);
        _applyingPreset = true;
        Presets.Remove(name);
        SelectedPreset = null;
        _applyingPreset = false;
        PresetName = "";
        SaveSettings();
        Status = $"Deleted preset \"{name}\".";
    }

    [RelayCommand] void PreviousPreset() => StepPreset(-1);
    [RelayCommand] void NextPreset() => StepPreset(1);

    void StepPreset(int by)
    {
        if (Presets.Count == 0) return;
        int i = SelectedPreset is null ? (by > 0 ? -1 : Presets.Count) : Presets.IndexOf(SelectedPreset);
        SelectedPreset = Presets[(i + by + Presets.Count) % Presets.Count];
    }

    Preset CapturePreset() => new()
    {
        Analysis = new AnalysisSettings { Engine = SelectedEngine.Value, RemovePercussion = RemoveDrums, SeparationStrength = SeparationStrength },
        Notes = CurrentNoteSettings(),
        TrackMode = SelectedTrackMode.Value,
        Stems = SelectedStems.Value,
        PreferRoformer = PreferRoformer,
        SplitNote = (int)SplitNote,
        TranscribeDrums = TranscribeDrums,
        DrumSensitivity = DrumSensitivity,
        BeatsPerBar = CurrentMeter.BeatsPerBar,
        TimeSignature = SelectedTimeSignature.Label,
        Staves = SelectedStaves.Value,
        ShowChords = ShowChords,
        AlignMidiToBars = AlignMidiToBars,
    };

    void ApplyPreset(Preset p)
    {
        SelectedEngine = Engines.FirstOrDefault(e => e.Value == p.Analysis.Engine) ?? Engines[0];
        RemoveDrums = p.Analysis.RemovePercussion;
        SeparationStrength = p.Analysis.SeparationStrength;
        OnsetThreshold = p.Notes.OnsetThreshold;
        FrameThreshold = p.Notes.FrameThreshold;
        MelodiaTrick = p.Notes.MelodiaTrick;
        PitchBends = p.Notes.PitchBends;
        SilenceThresholdDb = p.Notes.SilenceThresholdDb;
        MinNoteMs = p.Notes.MinNoteMs;
        MinVelocity = p.Notes.MinVelocity;
        LowestNote = p.Notes.LowestNote;
        HighestNote = p.Notes.HighestNote;
        MergeGapMs = p.Notes.MergeGapMs;
        SelectedQuantize = QuantizeOptions.FirstOrDefault(q => q.Value == p.Notes.QuantizeDivisions) ?? QuantizeOptions[0];
        SelectedTrackMode = TrackModes.FirstOrDefault(m => m.Value == p.TrackMode) ?? TrackModes[0];
        SelectedStems = StemOptions.FirstOrDefault(o => o.Value == p.Stems) ?? StemOptions[0];
        PreferRoformer = p.PreferRoformer;
        SplitNote = Math.Clamp(p.SplitNote, 24, 72);
        TranscribeDrums = p.TranscribeDrums;
        DrumSensitivity = Math.Clamp(p.DrumSensitivity, 0, 1);
        // "AUTO" or a time signature; presets from before detection have none, or only beats per bar.
        var meter = Meter.Parse(p.TimeSignature) ?? (p.TimeSignature is null && p.BeatsPerBar != 4 ? new Meter(p.BeatsPerBar, 4) : null);
        SelectedTimeSignature = TimeSignatures.FirstOrDefault(t => t.Value == meter) ?? TimeSignatures[0];
        SelectedStaves = StaffOptions.FirstOrDefault(o => o.Value == p.Staves) ?? StaffOptions[0];
        ShowChords = p.ShowChords;
        AlignMidiToBars = p.AlignMidiToBars;
    }

    // ---- Keeping the derived readouts current ----

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Playhead) or nameof(BarGrid) or nameof(Harmony) or nameof(RangeStart):
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(ChordText));
                OnPropertyChanged(nameof(KeyText));
                break;
            case nameof(SourcePath):
                OnPropertyChanged(nameof(FileName));
                break;
            case nameof(Bpm):
                OnPropertyChanged(nameof(TempoText));
                OnPropertyChanged(nameof(PlaybackPercent));
                break;
            case nameof(PlaybackBpm):
                OnPropertyChanged(nameof(PlaybackPercent));
                break;
            case nameof(DetectedTempo):
                OnPropertyChanged(nameof(TempoLabel));
                break;
            case nameof(CurrentMeter):
                OnPropertyChanged(nameof(TimeSignatureText));
                break;
            case nameof(IsMeterAuto):
                OnPropertyChanged(nameof(TimeSignatureLabel));
                break;
            case nameof(Downbeat):
                OnPropertyChanged(nameof(DownbeatText));
                break;
            case nameof(SelectedKey):
                OnPropertyChanged(nameof(KeyChoiceText));
                break;
            case nameof(ModelStatus) or nameof(IsModelMissing) or nameof(UseGpu) or nameof(CanUseGpu) or nameof(SelectedTrackMode):
                OnPropertyChanged(nameof(ModelShort));
                break;
            case nameof(DrumNotes):
                if (IsPlaying) _audio?.Player.SetDrums(TranscribeDrums ? DrumNotes : []);
                UpdatePatterns();
                OnPropertyChanged(nameof(KickCount));
                OnPropertyChanged(nameof(SnareCount));
                OnPropertyChanged(nameof(HiHatCount));
                OnPropertyChanged(nameof(OpenHatCount));
                OnPropertyChanged(nameof(DrumSourceText));
                break;
            case nameof(TranscribeDrums):
                RebuildMixer();
                break;
        }
    }
}
