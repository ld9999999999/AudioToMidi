using System.Collections.ObjectModel;
using AudioToMidi.App.Controls;
using AudioToMidi.App.Services;
using AudioToMidi.Core;
using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Drums;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Instruments;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Project;
using AudioToMidi.Core.Separation;
using AudioToMidi.Core.Synth;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioToMidi.App.ViewModels;

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly TranscriptionSession _session = new();
    readonly SemaphoreSlim _sessionLock = new(1, 1);
    readonly AppSettings _settings;
    CancellationTokenSource? _workCts;
    int _decodeVersion;
    bool _loading = true; // suppresses change handlers while restoring settings

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        var a = _settings.Analysis;
        var n = _settings.Notes;

        SelectedEngine = Engines.FirstOrDefault(e => e.Value == a.Engine) ?? Engines[0];
        RemoveDrums = a.RemovePercussion;
        SeparationStrength = a.SeparationStrength;
        OnsetThreshold = n.OnsetThreshold;
        FrameThreshold = n.FrameThreshold;
        MelodiaTrick = n.MelodiaTrick;
        PitchBends = n.PitchBends;
        SilenceThresholdDb = n.SilenceThresholdDb;
        MinNoteMs = n.MinNoteMs;
        MinVelocity = n.MinVelocity;
        LowestNote = n.LowestNote;
        HighestNote = n.HighestNote;
        MergeGapMs = n.MergeGapMs;
        SelectedQuantize = QuantizeOptions.FirstOrDefault(q => q.Value == n.QuantizeDivisions) ?? QuantizeOptions[0];
        FfmpegPath = _settings.FfmpegPath ?? "";
        SelectedTimeSignature = TimeSignatures[0];
        SelectedStaves = StaffOptions.FirstOrDefault(o => o.Value == _settings.Staves) ?? StaffOptions[0];
        SelectedKey = KeyOptions[0];
        AlignMidiToBars = _settings.AlignMidiToBars;
        KeepSwing = _settings.KeepSwing;
        ShowChords = _settings.ShowChords;
        ShowPatterns = _settings.ShowPatterns;
        SelectedTheme = ThemeOptions.FirstOrDefault(t => t.Value == _settings.Theme) ?? ThemeOptions[0];
        foreach (var name in _settings.Presets.Keys.Order(StringComparer.OrdinalIgnoreCase)) Presets.Add(name);
        if (_settings.CurrentPreset is { } current && Presets.Contains(current))
        {
            _applyingPreset = true;
            SelectedPreset = current;
            PresetName = current;
            _applyingPreset = false;
        }
        SelectedTrackMode = TrackModes.FirstOrDefault(m => m.Value == _settings.TrackMode) ?? TrackModes[0];
        SplitNote = Math.Clamp(_settings.SplitNote, 24, 72);
        if (_settings.SeparationModelFile == StemModel.Roformer.FileName) _settings.Stems = 6; // older setting
        _settings.SeparationModelFile = null;
        SelectedStems = StemOptions.FirstOrDefault(o => o.Value == _settings.Stems) ?? StemOptions[0];
        PreferRoformer = _settings.PreferRoformer;
        UseGpu = _settings.UseGpu;
        ModelSource = _settings.ModelSource ?? "";
        _session.UseGpu = UseGpu;
        foreach (var name in _settings.SplitTrackNames) _splitTracks.Add(name);
        if (_settings.SplitLines) // older setting: the first track
        {
            _splitTracks.Add(TranscriptionSession.TrackNames(SelectedTrackMode.Value)[0]);
            _settings.SplitLines = false;
        }
        _session.SeparationModel = CurrentModel;
        AutoInstruments = _settings.AutoInstruments;
        TranscribeDrums = _settings.TranscribeDrums;
        DrumSensitivity = Math.Clamp(_settings.DrumSensitivity, 0, 1);
        SelectedPreviewSound = PreviewSounds.FirstOrDefault(c => c.Label == _settings.PreviewSound) ?? PreviewSounds[0];
        PreviewVolume = Math.Clamp(_settings.PreviewVolume, 0, 1);
        UseSoundFont = _settings.UseSoundFont;
        SoundFontPath = _settings.SoundFontPath ?? "";
        KeyboardProgram = Math.Clamp(_settings.KeyboardProgram, 0, 127);
        _loading = false;
        _ = UpdateSoundFontAsync();

        RebuildTracks();
        UpdateModelStatus();

        UpdateFfmpegStatus();
        FfmpegDecoder.CleanupStaleTempFiles(TimeSpan.FromDays(1));
    }

    // ---- Options for combo boxes ----

    public IReadOnlyList<Choice<EngineKind>> Engines { get; } =
    [
        new("BASIC PITCH", EngineKind.BasicPitch),
        new("YIN", EngineKind.Yin),
    ];

    public IReadOnlyList<Choice<int>> QuantizeOptions { get; } =
    [
        new("OFF", 0), new("1/4", 1), new("1/8", 2), new("1/8T", 3),
        new("1/16", 4), new("1/16T", 6), new("1/32", 8),
    ];

    /// <summary>Automatic (the detected one), then each time signature.</summary>
    public IReadOnlyList<Choice<Meter?>> TimeSignatures { get; } =
        new[] { new Choice<Meter?>("AUTO", null) }
            .Concat(new[] { new Meter(2, 4), new Meter(3, 4), new Meter(4, 4), new Meter(5, 4), new Meter(6, 4), new Meter(6, 8), new Meter(9, 8), new Meter(12, 8) }
                .Select(m => new Choice<Meter?>(m.ToString(), m)))
            .ToList();

    public IReadOnlyList<Choice<StaffLayout>> StaffOptions { get; } =
    [
        new("AUTO", StaffLayout.Auto), new("TREBLE", StaffLayout.Treble),
        new("BASS", StaffLayout.Bass), new("GRAND", StaffLayout.Grand),
    ];

    public IReadOnlyList<Choice<int?>> KeyOptions { get; } =
        new[] { new Choice<int?>("AUTO", null) }
            .Concat(Enumerable.Range(-7, 15).Select(f => new Choice<int?>(KeyLabel(f), f)))
            .ToList();

    /// <summary>"B♭/Gm": the major key and its relative minor, for a short display.</summary>
    static string KeyLabel(int fifths)
    {
        string Tonic(MusicalKey k) => k.Name.Split(' ')[0];
        return $"{Tonic(new MusicalKey(fifths, false))}/{Tonic(new MusicalKey(fifths, true))}m";
    }

    // ---- File & status ----

    [ObservableProperty] private string? _sourcePath;
    [ObservableProperty] private string _status = "Open or drop an audio or video file to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasNotes;

    public string Title => SourcePath is null ? "Audio to MIDI" : $"{Path.GetFileName(SourcePath)} — Audio to MIDI";
    partial void OnSourcePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    // ---- Analysis settings (changing these re-runs analysis) ----

    [ObservableProperty] private Choice<EngineKind> _selectedEngine = null!;
    [ObservableProperty] private bool _removeDrums;
    [ObservableProperty] private double _separationStrength;

    public bool IsBasicPitch => SelectedEngine?.Value == EngineKind.BasicPitch;
    public bool IsYin => SelectedEngine?.Value == EngineKind.Yin;

    partial void OnSelectedEngineChanged(Choice<EngineKind> value)
    {
        OnPropertyChanged(nameof(IsBasicPitch));
        OnPropertyChanged(nameof(IsYin));
        ScheduleAnalysis();
    }
    partial void OnRemoveDrumsChanged(bool value) => ScheduleAnalysis();
    partial void OnSeparationStrengthChanged(double value) => ScheduleAnalysis();

    // ---- Note settings (live) ----

    [ObservableProperty] private double _onsetThreshold;
    [ObservableProperty] private double _frameThreshold;
    [ObservableProperty] private bool _melodiaTrick;
    [ObservableProperty] private bool _pitchBends;
    [ObservableProperty] private double _silenceThresholdDb;
    [ObservableProperty] private double _minNoteMs;
    [ObservableProperty] private double _minVelocity;
    [ObservableProperty] private decimal _lowestNote;
    [ObservableProperty] private decimal _highestNote;
    [ObservableProperty] private double _mergeGapMs;
    [ObservableProperty] private Choice<int> _selectedQuantize = null!;

    public string LowestNoteName => DetectedNote.PitchName((int)LowestNote);
    public string HighestNoteName => DetectedNote.PitchName((int)HighestNote);

    partial void OnOnsetThresholdChanged(double value) => RefreshNotes();
    partial void OnFrameThresholdChanged(double value) => RefreshNotes();
    partial void OnMelodiaTrickChanged(bool value) => RefreshNotes();
    partial void OnPitchBendsChanged(bool value) => RefreshNotes();
    partial void OnSilenceThresholdDbChanged(double value) => RefreshNotes();
    partial void OnMinNoteMsChanged(double value) => RefreshNotes();
    partial void OnMinVelocityChanged(double value) => RefreshNotes();
    partial void OnLowestNoteChanged(decimal value) { OnPropertyChanged(nameof(LowestNoteName)); RefreshNotes(); }
    partial void OnHighestNoteChanged(decimal value) { OnPropertyChanged(nameof(HighestNoteName)); RefreshNotes(); }
    partial void OnMergeGapMsChanged(double value) => RefreshNotes();
    partial void OnSelectedQuantizeChanged(Choice<int> value) => RefreshNotes();

    // ---- Tempo & export ----

    [ObservableProperty] private TempoEstimate? _detectedTempo;
    [ObservableProperty] private decimal _bpm = 120;

    /// <summary>
    /// The detected beats at the tempo set here. At two or three times the speed, or half or a third, the
    /// detected beats are kept; any other tempo gives a steady beat.
    /// </summary>
    public TempoEstimate? EffectiveTempo => DetectedTempo?.WithBpm((double)Bpm, _tempoPhase);

    /// <summary>At half or a third of the detected tempo, which of every two or three detected beats to keep.</summary>
    int _tempoPhase;
    public string TempoInfo => DetectedTempo is { } t
        ? $"Detected {t.Bpm:F1} BPM{TempoRange(t)} ({(t.Confidence >= 0.3 ? "confident" : t.Confidence >= 0.1 ? "uncertain" : "guess")})"
        : "Tempo is detected during analysis.";

    static string TempoRange(TempoEstimate t)
    {
        var map = t.Map;
        if (map.IsSteady) return "";
        var (min, max) = map.BpmRange();
        return max > min * 1.04 ? $", {min:F0}–{max:F0} in places" : "";
    }

    partial void OnDetectedTempoChanged(TempoEstimate? value)
    {
        _tempoPhase = 0;
        if (value is not null) Bpm = (decimal)value.Bpm;
        if (IsMeterAuto) ApplyDetectedTempo();
        OnPropertyChanged(nameof(TempoInfo));
    }
    partial void OnBpmChanged(decimal value)
    {
        PlaybackBpm = value; // playback starts at the song tempo; change it afterwards to slow down or speed up
        OnPropertyChanged(nameof(EffectiveTempo));
        OnPropertyChanged(nameof(HasSwing));
        OnPropertyChanged(nameof(SwingText));
        OnPropertyChanged(nameof(SwingTip));
        UpdateGrid();
        RefreshNotes();
    }

    /// <summary>The tempo buttons' step: 3 in compound meters (a dotted-quarter beat holds three eighths), else 2.</summary>
    int TempoStep => CurrentMeter.Subdivision;
    public string SlowerLabel => TempoStep == 3 ? "⅓×" : "½×";
    public string FasterLabel => TempoStep == 3 ? "3×" : "2×";
    public string SlowerTip => TempoStep == 3
        ? "A third of the speed: keeps every third detected beat, the ones with the most notes (for a 6/8 tracked on its eighths)"
        : "Half speed: keeps every other detected beat, the ones with the most notes";
    public string FasterTip => TempoStep == 3 ? "Three times the speed: adds two beats between each pair" : "Double speed: adds a beat between each pair";

    [RelayCommand]
    void SlowerTempo()
    {
        decimal bpm = Math.Max(20, Math.Round(Bpm / TempoStep, 1));
        // At half or a third of the detected tempo, keep the beats the notes fall on.
        if (DetectedTempo is { } detected && Notes.Count > 0)
            foreach (int factor in new[] { 2, 3 })
                if (Math.Abs((double)bpm * factor / detected.Bpm - 1) < 0.01)
                    _tempoPhase = DownbeatEstimator.Estimate(Notes, detected, factor);
        Bpm = bpm;
    }

    [RelayCommand] void FasterTempo() => Bpm = Math.Min(400, Math.Round(Bpm * TempoStep, 1));
    [RelayCommand]
    void ResetTempo()
    {
        if (DetectedTempo is not { } t) return;
        _tempoPhase = 0;
        Bpm = (decimal)t.Bpm;
        if (IsMeterAuto) ApplyDetectedTempo();
    }

    // ---- Tracks ----

    public IReadOnlyList<Choice<TrackMode>> TrackModes { get; } =
    [
        new("1 TRACK", TrackMode.Single),
        new("BASS + KEYS", TrackMode.SplitByRegister),
        new("INSTRUMENTS", TrackMode.Instruments),
    ];

    [ObservableProperty] private Choice<TrackMode> _selectedTrackMode = null!;
    [ObservableProperty] private Choice<int> _selectedStems = null!;
    [ObservableProperty] private bool _preferRoformer;
    [ObservableProperty] private bool _canDownloadRoformer;
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private bool _canUseGpu;
    [ObservableProperty] private string _modelSource = "";

    /// <summary>Whether a download button shows, and with it where downloads come from.</summary>
    public bool CanDownload => IsModelMissing || CanDownloadRoformer;
    partial void OnIsModelMissingChanged(bool value) => OnPropertyChanged(nameof(CanDownload));
    partial void OnCanDownloadRoformerChanged(bool value) => OnPropertyChanged(nameof(CanDownload));
    partial void OnModelSourceChanged(string value) => SaveSettings();
    readonly HashSet<string> _splitTracks = [];

    public IReadOnlyList<Choice<int>> StemOptions { get; } =
    [
        new("4 STEMS", 4),
        new("6 STEMS", 6),
    ];

    public bool IsSixStems => SelectedStems?.Value == 6;

    /// <summary>The model separation uses: HT-Demucs, or for 6 stems BS-RoFormer when preferred and installed.</summary>
    StemModel CurrentModel => StemModel.Choose(SelectedStems?.Value ?? 4, PreferRoformer);
    [ObservableProperty] private decimal _splitNote = 48;
    [ObservableProperty] private string _modelStatus = "";
    [ObservableProperty] private bool _isModelMissing;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _autoInstruments;
    CancellationTokenSource? _downloadCts;
    CancellationTokenSource? _instrumentCts;

    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public bool IsRegisterSplit => SelectedTrackMode?.Value == TrackMode.SplitByRegister;
    public bool IsInstrumentMode => SelectedTrackMode?.Value == TrackMode.Instruments;
    public bool CanChooseEngine => !IsInstrumentMode;
    public bool ColorByTrack => Tracks.Count > 1;
    public string SplitNoteName => DetectedNote.PitchName((int)SplitNote);

    partial void OnSelectedTrackModeChanged(Choice<TrackMode> value)
    {
        OnPropertyChanged(nameof(IsRegisterSplit));
        OnPropertyChanged(nameof(IsInstrumentMode));
        OnPropertyChanged(nameof(CanChooseEngine));
        if (_loading) return;
        RebuildTracks();
        SaveSettings();
        if (_session.Original is null) return;
        if (value.Value == TrackMode.Instruments && IsModelMissing)
        {
            Status = "Download the instrument separation model (Tracks section) to use this mode.";
            Notes = [];
            return;
        }
        if (_session.HasNotesFor(value.Value))
        {
            RefreshNotes();
            _ = UpdateInstrumentsAsync();
        }
        else ScheduleAnalysis();
    }

    partial void OnSelectedStemsChanged(Choice<int> value)
    {
        OnPropertyChanged(nameof(IsSixStems));
        ApplyModel();
    }

    partial void OnPreferRoformerChanged(bool value) => ApplyModel();

    partial void OnUseGpuChanged(bool value)
    {
        _session.UseGpu = value; // the stems come out the same either way, so nothing is analysed again
        UpdateModelStatus();
        SaveSettings();
    }

    /// <summary>Switch the session to <see cref="CurrentModel"/> after a setting change or a download.</summary>
    void ApplyModel()
    {
        var model = CurrentModel;
        bool changed = model != _session.SeparationModel;
        _session.SeparationModel = model; // this drops stems made with another model
        UpdateModelStatus();
        if (_loading) return;
        RebuildTracks();
        SaveSettings();
        if (_session.Original is null || !changed) return;
        if (IsModelMissing)
        {
            Status = $"Download the {model.Name} separation model (Tracks section) to use it.";
            if (IsInstrumentMode) Notes = [];
            return;
        }
        if (IsInstrumentMode) ScheduleAnalysis();
        else _ = UpdateInstrumentsAsync();
    }

    /// <summary>Called when a track's "split into lines" box changes.</summary>
    void OnTrackSplitChanged(TrackRow row)
    {
        if (row.BaseName is null) return;
        if (row.Split) _splitTracks.Add(row.BaseName); else _splitTracks.Remove(row.BaseName);
        // Splitting adds or removes two rows, so rebuild the rows after this row's own event has finished.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RebuildTracks();
            SaveSettings();
            RefreshNotes();
            _ = UpdateInstrumentsAsync();
        });
    }

    TrackSettings CurrentTrackSettings()
    {
        var baseNames = _session.TrackNamesFor(SelectedTrackMode.Value);
        return new()
        {
            Mode = SelectedTrackMode.Value,
            SplitNote = (int)SplitNote,
            SplitTracks = Enumerable.Range(0, baseNames.Count).Where(i => _splitTracks.Contains(baseNames[i])).ToList(),
        };
    }

    partial void OnSplitNoteChanged(decimal value)
    {
        OnPropertyChanged(nameof(SplitNoteName));
        RefreshNotes();
    }

    partial void OnAutoInstrumentsChanged(bool value)
    {
        SaveSettings();
        if (value) _ = UpdateInstrumentsAsync();
    }

    /// <summary>
    /// Recognise each track's instrument and show it. With automatic instruments on, also set the track's
    /// program, unless the user picked one for this file.
    /// </summary>
    async Task UpdateInstrumentsAsync(bool debounce = false)
    {
        if (_loading || _session.Original is null) return;
        _instrumentCts?.Cancel();
        var cts = new CancellationTokenSource();
        _instrumentCts = cts;
        var tracks = CurrentTrackSettings();
        if (!_session.HasNotesFor(tracks.Mode)) return;

        try
        {
            if (debounce) await Task.Delay(500, cts.Token);
            IReadOnlyList<InstrumentGuess?> guesses;
            await _sessionLock.WaitAsync(cts.Token);
            try
            {
                // This may run the slow instrument separation first, so show progress while the notes stay usable.
                var progress = new Progress<StageProgress>(p =>
                {
                    if (!IsBusy) Status = $"{p.Stage} for instrument detection… {p.Fraction:P0}";
                });
                guesses = await _session.RecognizeInstrumentsAsync(tracks, progress, cts.Token);
            }
            finally { _sessionLock.Release(); }
            if (!IsBusy) Status = $"{Notes.Count} notes. Instruments detected.";

            if (cts.IsCancellationRequested || SelectedTrackMode.Value != tracks.Mode) return;
            for (int i = 0; i < Tracks.Count && i < guesses.Count; i++)
                Tracks[i].ShowGuess(guesses[i], AutoInstruments);
            _ = UpdateDrumsAsync(); // separation may have produced a drum stem
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = $"Instrument recognition failed: {e.Message}"; }
    }

    void RebuildTracks()
    {
        Tracks.Clear();
        var baseNames = _session.TrackNamesFor(SelectedTrackMode.Value);
        var layout = _session.TrackLayoutFor(CurrentTrackSettings());
        for (int i = 0; i < layout.Count; i++)
        {
            var slot = layout[i];
            // A line without its own saved instrument starts with its base track's.
            int program = _settings.TrackPrograms.TryGetValue(slot.Name, out var p) || _settings.TrackPrograms.TryGetValue(baseNames[slot.Source], out p)
                ? Math.Clamp(p, 0, 127) : 0;
            bool isBase = slot.Line is null or MusicLine.Melody;
            Tracks.Add(new TrackRow(i, slot.Name, program, () => { SaveSettings(); ApplyMix(); },
                isBase ? baseNames[slot.Source] : null, slot.Line == MusicLine.Melody, OnTrackSplitChanged));
        }
        UpdateTrackCounts();
        OnPropertyChanged(nameof(ColorByTrack));
        RebuildMixer();
    }

    void UpdateTrackCounts()
    {
        foreach (var t in Tracks) t.NoteCount = Notes.Count(n => n.Track == t.Index);
    }

    /// <summary>Track list for export. The first track's staves follow the Staves setting.</summary>
    IReadOnlyList<TrackInfo> CurrentTracks()
    {
        var mode = SelectedTrackMode.Value;
        var layout = _session.TrackLayoutFor(CurrentTrackSettings());
        bool single = Tracks.Count == 1;
        // The instrument goes in the track name too: apps that ignore GM programs (GarageBand) still show names.
        return Tracks.Select(t => new TrackInfo(
            single ? GeneralMidi.Programs[t.Program] : $"{t.Name}: {GeneralMidi.Programs[t.Program]}",
            t.Program,
            single || (t.Index == 0 && layout[0].Line is null) ? SelectedStaves.Value
                : t.Index < layout.Count ? TranscriptionSession.StavesFor(mode, layout[t.Index]) : StaffLayout.Auto)).ToList();
    }

    void UpdateModelStatus()
    {
        var model = CurrentModel;
        IsModelMissing = DemucsSeparator.LocateModel(null, model) is null;
        bool roformerInstalled = DemucsSeparator.LocateModel(null, StemModel.Roformer) is not null;
        CanDownloadRoformer = IsSixStems && PreferRoformer && !roformerInstalled;
        CanUseGpu = model == StemModel.Roformer && OperatingSystem.IsMacOS();
        ModelStatus = IsModelMissing
            ? $"Needs the {model.Name} separation model ({model.DownloadBytes / 1_000_000} MB, downloaded once)."
            : model == StemModel.Roformer
                ? _session.SeparationDevice is { } device && device.StartsWith("CPU (") && UseGpu
                    ? $"Using BS-RoFormer on the CPU, about 1.6 minutes per minute of audio: {device[5..^1]}."
                    : CanUseGpu && UseGpu
                        ? "Using BS-RoFormer on the GPU: about 20 s per minute of audio, plus 10–15 s to load it once."
                        : "Using BS-RoFormer: better separation, but takes about 1.6 minutes per minute of audio."
                : $"{model.Name} model installed. Takes roughly 15–20 s per minute of audio."
                  + (CanDownloadRoformer ? $" BS-RoFormer separates better; download it ({StemModel.Roformer.DownloadBytes / 1_000_000} MB) to use it." : "");
    }

    [RelayCommand]
    Task DownloadModel() => DownloadAsync(CurrentModel);

    /// <summary>Download BS-RoFormer, which then becomes the 6-stem model.</summary>
    [RelayCommand]
    Task DownloadRoformer() => DownloadAsync(StemModel.Roformer);

    async Task DownloadAsync(StemModel model)
    {
        if (IsDownloading) return;
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        IsDownloading = true;
        IsBusy = true;
        try
        {
            var source = NullIfEmpty(ModelSource);
            string verb = ModelInstaller.IsRemote(source) ? "Downloading" : "Copying";
            var progress = new Progress<double>(f =>
            {
                Progress = f * 100;
                ModelStatus = $"{verb}… {f * 100:F0}%";
                Status = $"{verb} the separation model… {f * 100:F0}%";
            });
            await ModelInstaller.InstallAsync(model, source, progress, cts.Token);
            Status = $"{model.Name} separation model installed.";
            var before = _session.SeparationModel;
            ApplyModel(); // switch to the new model if it is the one now wanted, and analyse again
            if (_session.SeparationModel == before && IsInstrumentMode && _session.Original is not null && _session.Stems is null)
                ScheduleAnalysis();
        }
        catch (OperationCanceledException) { UpdateModelStatus(); }
        catch (Exception e)
        {
            UpdateModelStatus();
            Status = $"Download failed: {e.Message}";
        }
        finally
        {
            IsDownloading = false;
            if (_downloadCts == cts) IsBusy = false;
        }
    }

    // ---- Drums ----

    [ObservableProperty] private bool _transcribeDrums;
    [ObservableProperty] private double _drumSensitivity;
    [ObservableProperty] private IReadOnlyList<DetectedNote> _drumNotes = [];
    [ObservableProperty] private string _drumSummary = "";
    IReadOnlyList<DrumHit> _drumHits = [];
    CancellationTokenSource? _drumCts;

    static readonly TrackInfo DrumTrack = new("Drums", 0, StaffLayout.Auto, IsDrums: true);

    partial void OnTranscribeDrumsChanged(bool value)
    {
        SaveSettings();
        if (value) _ = UpdateDrumsAsync();
        else RefreshDrums();
    }

    partial void OnDrumSensitivityChanged(double value)
    {
        SaveSettings();
        RefreshDrums();
    }

    /// <summary>
    /// Transcribe drums if they are wanted and not yet done for the current drum audio. This uses the
    /// percussive part right after analysis, then runs again on the much cleaner Demucs drum stem once
    /// separation has run.
    /// </summary>
    async Task UpdateDrumsAsync()
    {
        if (_loading || !TranscribeDrums || _session.DrumSource is null) { RefreshDrums(); return; }
        _drumCts?.Cancel();
        var cts = new CancellationTokenSource();
        _drumCts = cts;
        try
        {
            await _sessionLock.WaitAsync(cts.Token);
            try { await _session.AnalyzeDrumsAsync(null, cts.Token); }
            finally { _sessionLock.Release(); }
            RefreshDrums();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = $"Drum transcription failed: {e.Message}"; }
    }

    /// <summary>Pick the drum hits again for the current sensitivity, quantize setting and range. This takes only milliseconds.</summary>
    void RefreshDrums()
    {
        if (!TranscribeDrums || _session.Drums is null)
        {
            _drumHits = [];
            DrumNotes = [];
            DrumSummary = TranscribeDrums && _session.Original is not null ? "Drums are transcribed after analysis." : "";
            return;
        }
        var hits = _session.GetDrumHits(new DrumSettings { Sensitivity = DrumSensitivity });
        if (HasRange) hits = hits.Where(h => h.Time >= RangeStart && h.Time < RangeEnd).ToList();
        _drumHits = hits;
        DrumNotes = Core.Drums.DrumNotes.ToNotes(hits, Tracks.Count, EffectiveTempo, QuantizeSteps, EffectiveSwing);
        DrumSummary = $"{hits.Count} hits: " + string.Join(", ", DrumKinds.All.Select(k => $"{hits.Count(h => h.Kind == k)} {k.Label()}"))
                      + (_session.Stems is null ? ". From the drum removal; the Demucs drum stem is cleaner." : ". From the Demucs drum stem.");
    }

    // ---- Bars & sheet music ----

    [ObservableProperty] private Choice<Meter?> _selectedTimeSignature = null!;

    /// <summary>The time signature found in this song's notes; null until they arrive.</summary>
    [ObservableProperty] private MeterEstimate? _detectedMeter;
    bool _meterPending;

    /// <summary>The time signature in use: the one chosen, else the detected one.</summary>
    public Meter CurrentMeter => SelectedTimeSignature?.Value ?? DetectedMeter?.Meter ?? Meter.Common;
    public bool IsMeterAuto => SelectedTimeSignature?.Value is null;

    public string MeterInfo => DetectedMeter is { } m
        ? $"Detected {m.Meter}. {m.Description}" + (IsMeterAuto ? "" : " Step back past 2/4 for automatic.")
        : "The time signature is detected from the notes after analysis.";
    [ObservableProperty] private int _downbeat;
    [ObservableProperty] private Choice<StaffLayout> _selectedStaves = null!;
    [ObservableProperty] private Choice<int?> _selectedKey = null!;
    [ObservableProperty] private bool _alignMidiToBars;
    [ObservableProperty] private BarGrid? _barGrid;
    [ObservableProperty] private string _detectedKeyInfo = "";
    bool _autoDownbeatPending;

    public string BarInfo
    {
        get
        {
            if (BarGrid is not { } g || Notes.Count == 0) return "Bar lines appear after analysis.";
            int beat = (int)Math.Floor(g.ToBeats(Notes.Min(n => n.Start)) + 0.25) + 1;
            return beat <= 1 ? "Music starts on beat 1" : $"Music starts on beat {beat} (pickup)";
        }
    }

    /// <summary>The score grid from the quantize setting. Triplet grids fall back to sixteenths.</summary>
    int ScoreSteps => SelectedQuantize.Value is 1 or 2 or 4 or 8 ? SelectedQuantize.Value : 4;

    /// <summary>The quantize grid in steps per beat of the meter (an eighth grid is 3 a beat in 6/8).</summary>
    int QuantizeSteps => CurrentMeter.StepsPerBeat(SelectedQuantize.Value);

    Meter _appliedMeter = Meter.Common;
    partial void OnSelectedTimeSignatureChanged(Choice<Meter?> value)
    {
        if (value?.Value is null) ApplyDetectedTempo();
        OnMeterChanged();
    }
    partial void OnDetectedMeterChanged(MeterEstimate? value)
    {
        if (IsMeterAuto) ApplyDetectedTempo();
        OnMeterChanged();
    }

    void OnMeterChanged()
    {
        OnPropertyChanged(nameof(CurrentMeter));
        OnPropertyChanged(nameof(IsMeterAuto));
        OnPropertyChanged(nameof(MeterInfo));
        OnPropertyChanged(nameof(SlowerLabel));
        OnPropertyChanged(nameof(FasterLabel));
        OnPropertyChanged(nameof(SlowerTip));
        OnPropertyChanged(nameof(FasterTip));
        OnSwingChanged(); // a compound meter has no swing
        if (_loading || CurrentMeter == _appliedMeter) return;
        _appliedMeter = CurrentMeter;
        AutoDownbeat();
        UpdateGrid();
        Requantize(); // the quantize grid follows the meter's beat (three eighths to a beat in 6/8)
    }

    /// <summary>The grid the notes were last decoded with: steps per beat and swing.</summary>
    (int Steps, double Swing) _quantizedWith;

    /// <summary>Decode the notes again when the grid they need differs from the one they were quantized on.</summary>
    void Requantize()
    {
        if (!_loading && _quantizedWith != (QuantizeSteps, EffectiveSwing)) RefreshNotes();
    }

    // ---- Swing ----

    /// <summary>Where this song's off-beat eighths fall in the beat (0.5 straight), found with the time signature.</summary>
    [ObservableProperty] private double _detectedSwing = Swing.Straight;
    /// <summary>The tempo the swing was measured at. At another tempo (½×, 2×) the eighths are different notes.</summary>
    double _swingBpm;
    [ObservableProperty] private bool _keepSwing = true;

    /// <summary>Swing found in a simple meter at the tempo in use.</summary>
    public bool HasSwing => DetectedSwing > Swing.Straight && !CurrentMeter.IsCompound && Math.Abs((double)Bpm - _swingBpm) < 0.5;

    /// <summary>The swing quantizing and sheet music use: the detected one while <see cref="KeepSwing"/> is on.</summary>
    double EffectiveSwing => KeepSwing && HasSwing ? DetectedSwing : Swing.Straight;

    public string SwingText => HasSwing ? $"SWING {DetectedSwing * 100:0}%" : "SWING";
    public string SwingTip => HasSwing
        ? $"Swung eighths: the off-beat ones fall {DetectedSwing * 100:0}% of the way through the beat. On: quantizing keeps them there " +
          "(MIDI, playback and drums keep the feel), and sheet music writes them as straight eighths marked Swing"
        : CurrentMeter.IsCompound ? "No swing in a compound meter: the beat's eighths are its thirds" : "No swing found: the eighths are straight";

    partial void OnDetectedSwingChanged(double value) => OnSwingChanged();
    partial void OnKeepSwingChanged(bool value)
    {
        SaveSettings();
        OnSwingChanged();
    }

    void OnSwingChanged()
    {
        OnPropertyChanged(nameof(HasSwing));
        OnPropertyChanged(nameof(SwingText));
        OnPropertyChanged(nameof(SwingTip));
        Requantize(); // on the swung (or straight) grid
    }

    /// <summary>
    /// If the beats were tracked on the eighths of a 3/4 or 6/8 bar, keep every second or third one, as the
    /// tempo buttons would. Do this only while the tempo is still the detected one.
    /// </summary>
    void ApplyDetectedTempo()
    {
        if (DetectedMeter is not { BeatDivisor: > 1 } m || DetectedTempo is not { } tempo) return;
        if (Math.Abs((double)Bpm - tempo.Bpm) > 0.05) return;
        _tempoPhase = m.Phase;
        Bpm = Math.Round((decimal)tempo.Bpm / m.BeatDivisor, 1);
    }
    partial void OnDownbeatChanged(int value) => UpdateGrid();
    partial void OnSelectedStavesChanged(Choice<StaffLayout> value) => SaveSettings();
    partial void OnAlignMidiToBarsChanged(bool value) => SaveSettings();
    partial void OnBarGridChanged(BarGrid? value) => OnPropertyChanged(nameof(BarInfo));

    [RelayCommand] void ShiftBarsLeft() => Downbeat = Mod(Downbeat - 1, CurrentMeter.BeatsPerBar);
    [RelayCommand] void ShiftBarsRight() => Downbeat = Mod(Downbeat + 1, CurrentMeter.BeatsPerBar);

    void AutoDownbeat()
    {
        if (DetectedTempo is not { } tempo || Notes.Count == 0) return;
        Downbeat = DownbeatEstimator.Estimate(Notes, tempo.WithBpm((double)Bpm, _tempoPhase), CurrentMeter.BeatsPerBar);
    }

    void UpdateGrid()
    {
        BarGrid = EffectiveTempo is { } tempo ? BarGrid.Create(tempo, CurrentMeter.BeatsPerBar, Downbeat, Notes) : null;
        Harmony = BarGrid is { } grid ? HarmonyAnalysis.Analyze(Notes, grid) : HarmonyAnalysis.Empty;
        DetectedKeyInfo = Notes.Count == 0 ? "" : $"Detected: {KeysText()}";
        OnPropertyChanged(nameof(BarInfo)); // depends on the first note too, not only the grid
        UpdatePatterns();
    }

    // ---- Bar patterns ----

    /// <summary>Show each part's repeating bar patterns in a lane of the piano roll.</summary>
    [ObservableProperty] private bool _showPatterns;

    /// <summary>The parts that repeat bar patterns, with their patterns, on the current bar lines (while shown).</summary>
    IReadOnlyList<(TrackInfo Track, IReadOnlyList<BarPattern> Patterns)> _partPatterns = [];
    (int Part, int Pattern)? _selectedClip;

    /// <summary>The pattern lane: a row per part that repeats, a lettered cell per repeat; null when hidden.</summary>
    public IReadOnlyList<PatternLane>? PatternLanes => ShowPatterns && BarGrid is { } grid
        ? _partPatterns.Select(p => new PatternLane(LaneLabel(p.Track),
            p.Patterns.SelectMany((pattern, i) => pattern.Occurrences.Select(bar => new PatternCell(
                grid.ToSeconds(bar * grid.BeatsPerBar), grid.ToSeconds((bar + pattern.Bars) * grid.BeatsPerBar), i)))
                .OrderBy(c => c.Start).ToList())).ToList()
        : null;

    /// <summary>"BASS", "GUITAR", "DRUMS": a track's part, short enough for the lane.</summary>
    static string LaneLabel(TrackInfo track)
    {
        var name = track.Name.Split(':')[0].Trim().ToUpperInvariant();
        return name.Length <= 6 ? name : name[..6];
    }

    public bool HasSelectedClip => _selectedClip is not null;

    /// <summary>"Bass, pattern B (2 bars)"; empty when none is selected.</summary>
    public string SelectedClipText => _selectedClip is { } c && c.Part < _partPatterns.Count
        ? $"{_partPatterns[c.Part].Track.Name.Split(':')[0].Trim()}, pattern {new PatternCell(0, 0, c.Pattern).Letter} " +
          $"({_partPatterns[c.Part].Patterns[c.Pattern].Bars} bar{(_partPatterns[c.Part].Patterns[c.Pattern].Bars == 1 ? "" : "s")})"
        : "";

    /// <summary>A clip clicked in the pattern lane: <paramref name="lane"/> is the part's row there.</summary>
    public void SelectClip(int lane, int pattern)
    {
        _selectedClip = lane < _partPatterns.Count && pattern < _partPatterns[lane].Patterns.Count ? (lane, pattern) : null;
        OnPropertyChanged(nameof(HasSelectedClip));
        OnPropertyChanged(nameof(SelectedClipText));
        if (_selectedClip is not null) Status = $"{SelectedClipText}: the start/stop range is one repeat of it; LOOP plays it again and again.";
    }

    /// <summary>File name ending for the selected clip, e.g. ".bass-B.mid".</summary>
    public string SelectedClipSuffix => _selectedClip is { } c && c.Part < _partPatterns.Count
        ? $".{LaneLabel(_partPatterns[c.Part].Track).ToLowerInvariant()}-{new PatternCell(0, 0, c.Pattern).Letter}.mid"
        : ".clip.mid";

    /// <summary>Save the selected clip as a MIDI file of exactly its length, ready to loop.</summary>
    public void ExportClip(string path)
    {
        if (_selectedClip is not { } c || c.Part >= _partPatterns.Count) return;
        try
        {
            var (track, patterns) = _partPatterns[c.Part];
            var pattern = patterns[c.Pattern];
            MidiExporter.BuildClip(pattern, track, CurrentMeter, (double)Bpm).Write(path, overwriteFile: true, format: Melanchall.DryWetMidi.Core.MidiFileFormat.MultiTrack);
            Status = $"Saved {SelectedClipText}, {pattern.Notes.Count} notes, to {path}";
        }
        catch (Exception e) { Status = e.Message; }
    }

    partial void OnShowPatternsChanged(bool value)
    {
        SaveSettings();
        UpdatePatterns();
    }

    void UpdatePatterns()
    {
        var parts = new List<(TrackInfo, IReadOnlyList<BarPattern>)>();
        if (ShowPatterns && BarGrid is { } grid)
        {
            var tracks = CurrentTracks();
            foreach (var group in Notes.GroupBy(n => n.Track).Where(g => g.Key >= 0 && g.Key < tracks.Count).OrderBy(g => g.Key))
                if (PatternFinder.Find(group.ToList(), grid) is { Count: > 0 } found) parts.Add((tracks[group.Key], found));
            if (DrumNotes.Count > 0 && PatternFinder.Find(DrumNotes, grid) is { Count: > 0 } drums) parts.Add((DrumTrack, drums));
        }
        _partPatterns = parts;
        _selectedClip = null;
        OnPropertyChanged(nameof(PatternLanes));
        OnPropertyChanged(nameof(HasSelectedClip));
        OnPropertyChanged(nameof(SelectedClipText));
    }

    /// <summary>Chords and keys of the notes as shown, on the current bar lines.</summary>
    [ObservableProperty] private HarmonyAnalysis _harmony = HarmonyAnalysis.Empty;
    [ObservableProperty] private bool _showChords = true;

    /// <summary>Chord symbols for the piano roll; null when hidden.</summary>
    public IReadOnlyList<ChordLabel>? ChordLabels => ShowChords
        ? Harmony.Chords.Where(c => c.Chord is not null).Select(c => new ChordLabel(c.Start, c.End, Harmony.Symbol(c))).ToList()
        : null;

    partial void OnHarmonyChanged(HarmonyAnalysis value) => OnPropertyChanged(nameof(ChordLabels));
    partial void OnShowChordsChanged(bool value)
    {
        OnPropertyChanged(nameof(ChordLabels));
        SaveSettings();
    }

    /// <summary>"A minor", or with changes "C major; A minor from bar 31".</summary>
    string KeysText()
    {
        if (Harmony.Keys.Count == 0 || BarGrid is not { } grid) return KeyEstimator.Estimate(Notes).Name;
        int Bar(double seconds) => (int)Math.Floor(grid.ToBeats(seconds) / grid.BeatsPerBar + 1e-6) + 1;
        return string.Join("; ", Harmony.Keys.Select((k, i) => i == 0 ? k.Key.Name : $"{k.Key.Name} from bar {Bar(k.Start)}"));
    }

    static int Mod(int a, int n) => ((a % n) + n) % n;

    // ---- Keyboard preview ----

    public IReadOnlyList<Choice<SynthPatch>> PreviewSounds { get; } = SynthPatch.All.Select(p => new Choice<SynthPatch>(p.Name, p)).ToList();
    [ObservableProperty] private Choice<SynthPatch> _selectedPreviewSound = null!;
    [ObservableProperty] private double _previewVolume;

    AudioEngine? _audio;

    /// <summary>
    /// The audio engine (synth and note player), opened on first use. It falls back to a silent clock when
    /// there is no output device or AUDIOTOMIDI_NO_AUDIO=1, so keys and playback still work, e.g. in tests.
    /// </summary>
    AudioEngine Engine
    {
        get
        {
            if (_audio is null)
            {
                if (Environment.GetEnvironmentVariable("AUDIOTOMIDI_NO_AUDIO") == "1") _audio = AudioEngine.OpenSilent();
                else
                {
                    try { _audio = AudioEngine.Open(); }
                    catch (Exception e)
                    {
                        Status = $"Audio output unavailable: {e.Message}";
                        _audio = AudioEngine.OpenSilent();
                    }
                }
                _audio.Synth.Patch = SelectedPreviewSound.Value;
                _audio.Synth.Volume = 0.6f;
                _audio.MasterVolume = MasterGain;
                ApplySoundFont();
            }
            return _audio;
        }
    }

    /// <summary>The piano roll's keys: the SoundFont's <see cref="KeyboardProgram"/>, or the built-in synth.</summary>
    public void PreviewNoteOn(int pitch, float velocity) => Engine.Keyboard.NoteOn(pitch, velocity);
    public void PreviewNoteOff(int pitch) => Engine.Keyboard.NoteOff(pitch);

    partial void OnSelectedPreviewSoundChanged(Choice<SynthPatch> value)
    {
        if (_audio is not null) _audio.Synth.Patch = value.Value;
        SaveSettings();
    }

    partial void OnPreviewVolumeChanged(double value)
    {
        if (_audio is not null) _audio.MasterVolume = MasterGain;
        SaveSettings();
    }

    /// <summary>The master knob (0..1, 0.6 by default) as a gain: 1 at the default.</summary>
    float MasterGain => (float)(PreviewVolume / 0.6);

    // ---- Start/stop points ----

    [ObservableProperty] private double _rangeStart = double.NaN;
    [ObservableProperty] private double _rangeEnd = double.NaN;

    public bool HasRange => !double.IsNaN(RangeStart) && !double.IsNaN(RangeEnd) && RangeEnd > RangeStart;
    public string RangeInfo => HasRange
        ? $"Range {Clock(RangeStart)} – {Clock(RangeEnd)} ({RangeEnd - RangeStart:F1} s)"
        : "Drag on the waveform to set start/stop points";

    partial void OnRangeStartChanged(double value) => RangeChanged();
    partial void OnRangeEndChanged(double value) => RangeChanged();

    void RangeChanged()
    {
        OnPropertyChanged(nameof(HasRange));
        OnPropertyChanged(nameof(RangeInfo));
        RefreshNotes();
        ConfigurePlayer();
    }

    [RelayCommand]
    void ClearRange()
    {
        RangeStart = double.NaN;
        RangeEnd = double.NaN;
    }

    static string Clock(double seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.00}";

    // ---- Playback through the synth ----

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private bool _metronome;
    [ObservableProperty] private decimal _playbackBpm = 120;
    [ObservableProperty] private double _playhead = -1;
    [ObservableProperty] private IReadOnlyCollection<int>? _soundingKeys;
    Avalonia.Threading.DispatcherTimer? _playTimer;

    public string PlayButtonText => IsPlaying ? "■ Stop" : "▶ Play";
    public string PlaybackRateText => Bpm > 0 ? $"×{PlaybackBpm / Bpm:0.00}" : "";

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayButtonText));
    partial void OnLoopChanged(bool value) => ConfigurePlayer();
    partial void OnMetronomeChanged(bool value) { if (_audio is not null) _audio.Player.Metronome = value; }
    partial void OnPlaybackBpmChanged(decimal value)
    {
        OnPropertyChanged(nameof(PlaybackRateText));
        ConfigurePlayer();
    }

    [RelayCommand]
    void TogglePlay()
    {
        if (IsPlaying) { StopPlayback(); return; }
        if (Notes.Count == 0) return;

        var player = Engine.Player;
        player.Metronome = Metronome;
        player.SetSource(Waveform, SampleRate);
        player.SetDrums(TranscribeDrums ? DrumNotes : []);
        player.SetNotes(Notes, BarGrid);
        ApplyMix();
        ConfigurePlayer();
        player.Play(HasRange ? RangeStart : Math.Max(0, Notes.Min(n => n.Start) - 0.25));
        IsPlaying = true;

        _playTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(33), Avalonia.Threading.DispatcherPriority.Render, (_, _) => UpdatePlayhead());
        _playTimer.Start();
    }

    void StopPlayback()
    {
        _audio?.Player.Stop();
        _playTimer?.Stop();
        IsPlaying = false;
        Playhead = -1;
        SoundingKeys = null;
        ClearMeters();
    }

    void UpdatePlayhead()
    {
        var player = Engine.Player;
        if (!player.IsPlaying) { StopPlayback(); return; }
        // The mixer renders ahead of the speakers: show what is being heard.
        Playhead = Math.Max(HasRange ? RangeStart : 0, player.Position - Engine.Buffered * PlaybackRate);
        SoundingKeys = player.SoundingPitches;
        UpdateMeters();
    }

    /// <summary>Playback speed: 1 = the song's tempo.</summary>
    double PlaybackRate => Bpm > 0 ? (double)(PlaybackBpm / Bpm) : 1;

    void ConfigurePlayer()
    {
        if (_audio is null) return;
        _audio.Player.Configure(HasRange ? RangeStart : 0, HasRange ? RangeEnd : double.MaxValue, Loop, PlaybackRate);
    }

    // ---- ffmpeg ----

    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private string _ffmpegStatus = "";

    partial void OnFfmpegPathChanged(string value)
    {
        UpdateFfmpegStatus();
        SaveSettings();
    }

    void UpdateFfmpegStatus()
    {
        var found = FfmpegDecoder.Locate(string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath);
        FfmpegStatus = found is null
            ? "ffmpeg not found: only .wav files can be opened."
            : $"Using {found}";
    }

    // ---- Results shown in the piano roll ----

    [ObservableProperty] private IReadOnlyList<DetectedNote> _notes = [];
    [ObservableProperty] private float[]? _waveform;
    [ObservableProperty] private float[]? _removedAudio;
    public int SampleRate => TranscriptionSession.SampleRate;

    partial void OnNotesChanged(IReadOnlyList<DetectedNote> value)
    {
        HasNotes = value.Count > 0;
        UpdateTrackCounts();
        if (IsPlaying) { ApplyMix(); _audio?.Player.SetNotes(value, BarGrid); }
        if (_autoDownbeatPending && value.Count > 0)
        {
            _autoDownbeatPending = false;
            AutoDownbeat();
        }
        UpdateGrid();
    }

    // ---- Workflow ----

    public string? LastFolder => _settings.LastFolder;

    public async Task OpenAsync(string path)
    {
        var cts = RestartWork();
        _settings.LastFolder = Path.GetDirectoryName(path);
        SaveSettings();

        try
        {
            await _sessionLock.WaitAsync(cts.Token);
            try
            {
                IsBusy = true;
                Notes = [];
                DrumNotes = [];
                Waveform = null;
                RemovedAudio = null;
                DetectedTempo = null;
                SelectedKey = KeyOptions[0];
                DetectedMeter = null;
                DetectedSwing = Swing.Straight;
                _meterPending = true; // detect the time signature and swing when the first notes arrive
                SelectedTimeSignature = TimeSignatures[0];
                Downbeat = 0;
                foreach (var t in Tracks) { t.UserChose = false; t.Detected = ""; }
                StopPlayback();
                RangeStart = RangeEnd = double.NaN;
                _autoDownbeatPending = true; // guess the bar lines when the first notes arrive
                SourcePath = path;
                Status = $"Decoding {Path.GetFileName(path)}…";
                await _session.LoadAsync(path, NullIfEmpty(FfmpegPath), MakeProgress(), cts.Token);
                Waveform = _session.Original!.Samples;
            }
            finally { _sessionLock.Release(); }

            await AnalyzeAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Status = e.Message;
            SourcePath = null;
        }
        finally
        {
            if (_workCts == cts) IsBusy = false;
        }
    }

    void ScheduleAnalysis()
    {
        if (_loading) return;
        SaveSettings();
        if (_session.Original is null) return;
        var cts = RestartWork();
        _ = DebouncedAnalyzeAsync(cts);
    }

    async Task DebouncedAnalyzeAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(350, cts.Token); // let slider drags settle
            IsBusy = true;
            await AnalyzeAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = e.Message; }
        finally
        {
            if (_workCts == cts) IsBusy = false;
        }
    }

    async Task AnalyzeAsync(CancellationToken ct)
    {
        var settings = new AnalysisSettings
        {
            Engine = SelectedEngine.Value,
            RemovePercussion = RemoveDrums,
            SeparationStrength = SeparationStrength,
            SeparateInstruments = IsInstrumentMode,
        };
        if (IsInstrumentMode && IsModelMissing)
        {
            Status = "Download the instrument separation model (Tracks section) to use this mode.";
            return;
        }

        await _sessionLock.WaitAsync(ct);
        try
        {
            var started = DateTime.UtcNow;
            await _session.AnalyzeAsync(settings, MakeProgress(), ct);
            UpdateModelStatus(); // tell the user if the GPU could not be used
            RemovedAudio = IsInstrumentMode ? _session.Stems?.Drums
                : RemoveDrums ? _session.Separation?.Percussive : null;
            DetectedTempo = _session.Tempo;
            Status = $"Analysed {_session.Original!.Duration:F1}s of audio in {(DateTime.UtcNow - started).TotalSeconds:F1}s.";
        }
        finally { _sessionLock.Release(); }

        RefreshNotes();
        await UpdateDrumsAsync();
        _ = UpdateInstrumentsAsync();
    }

    NoteSettings CurrentNoteSettings() => new()
    {
        OnsetThreshold = OnsetThreshold,
        FrameThreshold = FrameThreshold,
        MelodiaTrick = MelodiaTrick,
        PitchBends = PitchBends,
        SilenceThresholdDb = SilenceThresholdDb,
        MinNoteMs = MinNoteMs,
        MinVelocity = (int)MinVelocity,
        LowestNote = (int)Math.Min(LowestNote, HighestNote),
        HighestNote = (int)Math.Max(LowestNote, HighestNote),
        MergeGapMs = MergeGapMs,
        QuantizeDivisions = QuantizeSteps,
        Swing = EffectiveSwing,
    };

    /// <summary>Decode the notes again off the UI thread. Only the latest request's result is applied.</summary>
    void RefreshNotes()
    {
        if (_loading) return;
        SaveSettings();
        RefreshDrums();
        var tracks = CurrentTrackSettings();
        if (!_session.HasNotesFor(tracks.Mode)) return;

        var settings = CurrentNoteSettings();
        _quantizedWith = (settings.QuantizeDivisions, settings.Swing);
        var tempo = EffectiveTempo;
        int version = Interlocked.Increment(ref _decodeVersion);
        double from = RangeStart, to = RangeEnd;
        bool clip = HasRange;
        var tracked = _meterPending ? DetectedTempo : null;
        _ = Task.Run(() =>
        {
            var notes = _session.GetNotes(settings, tempo, tracks);
            // Detect the time signature and swing once per song, from the notes before quantizing and on the
            // tracked beats. Swing is measured on the meter's beats, and only in a simple meter.
            MeterEstimate? meter = null;
            (double Swing, double Bpm) swing = (Swing.Straight, 0);
            if (tracked is not null && notes.Count > 0)
            {
                var raw = settings.QuantizeDivisions == 0 ? notes : _session.GetNotes(settings with { QuantizeDivisions = 0 }, tempo, tracks);
                meter = MeterEstimator.Estimate(raw, tracked);
                var beats = meter.Apply(tracked);
                if (!meter.Meter.IsCompound) swing = (GrooveEstimator.Swing(raw, beats), beats.Bpm);
            }
            return (Notes: clip ? NoteFilter.ClipToRange(notes, from, to) : notes, Meter: meter, Swing: swing);
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && version == _decodeVersion)
            {
                if (t.Result.Meter is { } meter && _meterPending)
                {
                    _meterPending = false;
                    _swingBpm = Math.Round(t.Result.Swing.Bpm, 1);
                    DetectedSwing = t.Result.Swing.Swing;
                    DetectedMeter = meter;
                }
                Notes = t.Result.Notes;
                if (!IsBusy) Status = $"{t.Result.Notes.Count} notes. Ctrl/⌘ + scroll to zoom, drag to pan, double-click to fit.";
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void ExportMidi(string path)
    {
        try
        {
            var tracks = CurrentTracks();
            var notes = Notes;
            if (DrumNotes.Count > 0)
            {
                tracks = [.. tracks, DrumTrack];
                notes = [.. Notes, .. DrumNotes];
            }
            var chords = ShowChords && Harmony.Chords.Any(c => c.Chord is not null) ? Harmony : null;
            MidiExporter.Write(path, notes, tracks, (double)Bpm, AlignMidiToBars ? BarGrid : null, CurrentMeter, ExportKeys(), chords);
            Status = $"Saved {notes.Count} notes on {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}{(chords is null ? "" : " and the chords")} to {path}";
        }
        catch (Exception e) { Status = $"Export failed: {e.Message}"; }
    }

    /// <summary>Key signatures for the MIDI file: the key chosen, or with AUTO the key over time.</summary>
    IReadOnlyList<KeySpan> ExportKeys() => SelectedKey.Value is { } fifths
        ? [new KeySpan(0, double.MaxValue, new MusicalKey(fifths, KeyEstimator.Estimate(Notes) is var k && k.Fifths == fifths && k.Minor))]
        : Harmony.Keys;

    public void ExportScore(string path)
    {
        if (BarGrid is not { } grid) return;
        try
        {
            var tracks = CurrentTracks().Select(t => (t.Name, t.Program, t.Staves)).ToList();
            var parts = ScoreBuilder.BuildParts(Notes, tracks, grid, new ScoreSettings
            {
                Title = SourcePath is null ? "Transcription" : Path.GetFileNameWithoutExtension(SourcePath),
                Meter = CurrentMeter,
                StepsPerBeat = ScoreSteps,
                Swing = EffectiveSwing,
                KeyFifths = SelectedKey.Value,
                Keys = SelectedKey.Value is null ? Harmony.Keys : null,
                Chords = ShowChords ? Harmony.Chords : null,
            });
            MusicXmlWriter.Write(path, parts);
            Status = $"Saved {parts[0].MeasureCount} bars in {parts[0].Key.Name} ({parts.Count} part{(parts.Count == 1 ? "" : "s")}) to {path}";
        }
        catch (Exception e) { Status = $"Export failed: {e.Message}"; }
    }

    /// <summary>
    /// Save a project bundle: analysis.json (beat grid, key, every part with drums), the stems as WAV,
    /// and the MIDI. Drums are always included, and are transcribed now if the drum option is off.
    /// </summary>
    public async Task SaveProjectAsync(string folder)
    {
        if (BarGrid is not { } grid || _session.Original is null) return;
        IsBusy = true;
        try
        {
            IReadOnlyList<DetectedNote> drums = DrumNotes;
            await _sessionLock.WaitAsync();
            try
            {
                if (!TranscribeDrums || !_session.HasDrumsForCurrentSource)
                {
                    Status = "Transcribing drums…";
                    await _session.AnalyzeDrumsAsync();
                    var hits = _session.GetDrumHits(new DrumSettings { Sensitivity = DrumSensitivity });
                    if (HasRange) hits = hits.Where(h => h.Time >= RangeStart && h.Time < RangeEnd).ToList();
                    drums = Core.Drums.DrumNotes.ToNotes(hits, Tracks.Count, EffectiveTempo, QuantizeSteps, EffectiveSwing);
                }
                Status = "Saving project…";
                var key = SelectedKey.Value is { } fifths && Notes.Count > 0
                    ? new MusicalKey(fifths, KeyEstimator.Estimate(Notes).Minor)
                    : (MusicalKey?)null;
                var content = new ProjectContent([.. Notes, .. drums], [.. CurrentTracks(), DrumTrack], SelectedTrackMode.Value, grid, key,
                    CurrentTrackSettings().SplitTracks, CurrentMeter, EffectiveSwing);
                var document = await ProjectExporter.SaveAsync(folder, _session, content);
                Status = $"Saved project: {document.Parts.Count} parts, {document.Stems.Count} stems, {document.Timing.Beats.Count} beats to {folder}";
            }
            finally { _sessionLock.Release(); }
            if (TranscribeDrums) RefreshDrums();
        }
        catch (Exception e) { Status = $"Saving the project failed: {e.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Render what Play plays (each track's instrument, the drums and the recording at their mixer levels)
    /// at the song's tempo, over the range if one is set, to a stereo WAV. MASTER is only the listening volume,
    /// so it is left out. The mix goes through the same gentle limiter as playback.
    /// </summary>
    public async Task ExportMixAsync(string path)
    {
        if (Notes.Count == 0) return;
        const int rate = 44100;
        double start = HasRange ? RangeStart : 0;
        bool sourceHeard = Waveform is not null && Fader.Gain(SourceStrip.Volume) > 0 && !SourceStrip.Mute;
        double end = HasRange ? RangeEnd : Math.Max(
            Math.Max(Notes.Max(n => n.End), DrumNotes.Count > 0 ? DrumNotes.Max(n => n.End) : 0),
            sourceHeard ? (double)Waveform!.Length / SampleRate : 0);

        var player = new NotePlayer(new PolySynth(rate));
        player.SetSoundFont(UseSoundFont ? _soundFont : null);
        ConfigureMix(player);
        player.SetSource(Waveform, SampleRate);
        player.SetDrums(TranscribeDrums ? DrumNotes : []);
        player.SetNotes(Notes, BarGrid);
        player.Configure(start, end, loop: false, rate: 1);
        player.Play(start);

        Status = "Rendering the mix…";
        try
        {
            var (left, right) = await Task.Run(() =>
            {
                var mix = player.RenderUntilStopped();
                for (int i = 0; i < mix.Left.Length; i++)
                {
                    mix.Left[i] = MathF.Tanh(mix.Left[i]);
                    mix.Right[i] = MathF.Tanh(mix.Right[i]);
                }
                WavReader.WriteFloat(path, mix.Left, mix.Right, rate);
                return mix;
            });
            Status = $"Saved the mix ({(double)left.Length / rate:F1} s, {(player.SoundFont is null ? "built-in synth" : "SoundFont")}) to {path}";
        }
        catch (Exception e) { Status = $"Saving the mix failed: {e.Message}"; }
    }

    public void ExportSeparatedAudio(string path)
    {
        var audio = _session.AnalyzedAudio;
        if (IsInstrumentMode && _session.Stems is { } stems)
        {
            // Use everything except drums, matching how the stems were transcribed.
            var mix = new float[stems.Bass.Length];
            for (int i = 0; i < mix.Length; i++)
                mix[i] = stems.Bass[i] + stems.Other[i] + stems.Vocals[i] + (stems.Guitar?[i] ?? 0) + (stems.Piano?[i] ?? 0);
            audio = new AudioData(mix, stems.SampleRate);
        }
        if (audio is null) return;
        if (HasRange)
        {
            int a = Math.Clamp((int)(RangeStart * audio.SampleRate), 0, audio.Samples.Length);
            int b = Math.Clamp((int)(RangeEnd * audio.SampleRate), a, audio.Samples.Length);
            audio = new AudioData(audio.Samples[a..b], audio.SampleRate);
        }
        try
        {
            WavReader.WriteFloat(path, audio);
            Status = $"Saved the audio that was transcribed to {path}";
        }
        catch (Exception e) { Status = $"Export failed: {e.Message}"; }
    }

    [RelayCommand]
    void Cancel()
    {
        _workCts?.Cancel();
        _downloadCts?.Cancel();
        IsBusy = false;
        Status = "Cancelled.";
    }

    CancellationTokenSource RestartWork()
    {
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        return _workCts;
    }

    IProgress<StageProgress> MakeProgress() => new Progress<StageProgress>(p =>
    {
        Progress = p.Fraction * 100;
        if (p.Stage != "Done") Status = $"{p.Stage}…";
    });

    void SaveSettings()
    {
        if (_loading) return;
        _settings.FfmpegPath = NullIfEmpty(FfmpegPath);
        if (SelectedTrackMode is not null) _settings.TrackMode = SelectedTrackMode.Value;
        _settings.SplitNote = (int)SplitNote;
        _settings.SplitTrackNames = [.. _splitTracks.Order()];
        if (SelectedStems is not null) _settings.Stems = SelectedStems.Value;
        _settings.PreferRoformer = PreferRoformer;
        _settings.UseGpu = UseGpu;
        _settings.ModelSource = NullIfEmpty(ModelSource);
        // Only instruments the user picked become defaults; detected ones are per file.
        foreach (var t in Tracks.Where(t => t.UserChose)) _settings.TrackPrograms[t.Name] = t.Program;
        if (SelectedStaves is not null) _settings.Staves = SelectedStaves.Value;
        _settings.AlignMidiToBars = AlignMidiToBars;
        _settings.KeepSwing = KeepSwing;
        _settings.ShowChords = ShowChords;
        _settings.ShowPatterns = ShowPatterns;
        if (SelectedTheme is not null) _settings.Theme = SelectedTheme.Value;
        _settings.CurrentPreset = SelectedPreset;
        _settings.AutoInstruments = AutoInstruments;
        _settings.TranscribeDrums = TranscribeDrums;
        _settings.DrumSensitivity = DrumSensitivity;
        if (SelectedPreviewSound is not null) _settings.PreviewSound = SelectedPreviewSound.Label;
        _settings.PreviewVolume = PreviewVolume;
        _settings.UseSoundFont = UseSoundFont;
        _settings.SoundFontPath = NullIfEmpty(SoundFontPath);
        _settings.KeyboardProgram = KeyboardProgram;
        _settings.Analysis = new AnalysisSettings
        {
            Engine = SelectedEngine.Value,
            RemovePercussion = RemoveDrums,
            SeparationStrength = SeparationStrength,
        };
        _settings.Notes = CurrentNoteSettings();
        _settings.Save();
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void Dispose()
    {
        _playTimer?.Stop();
        _workCts?.Cancel();
        _audio?.Dispose();
        _session.Dispose();
    }
}
