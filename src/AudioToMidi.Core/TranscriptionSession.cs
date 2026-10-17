using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Drums;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Instruments;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Separation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core;

public enum EngineKind { BasicPitch, Yin }

/// <summary>Settings that need a new analysis when changed.</summary>
public sealed record AnalysisSettings
{
    public EngineKind Engine { get; init; } = EngineKind.BasicPitch;
    public bool RemovePercussion { get; init; } = true;

    /// <summary>HPSS margin for drum removal: 1 is gentle, 3 or more is strong.</summary>
    public double SeparationStrength { get; init; } = 2.0;

    /// <summary>
    /// Split the mix into bass, vocals and other with Demucs, and transcribe each with basic-pitch. This
    /// replaces HPSS drum removal for the notes, though HPSS still drives tempo detection.
    /// </summary>
    public bool SeparateInstruments { get; init; }
}

/// <summary>Settings for turning analysed notes into tracks. They are fast to change.</summary>
public sealed record TrackSettings
{
    public TrackMode Mode { get; init; } = TrackMode.Single;

    /// <summary>For a register split, notes below this MIDI note go to the bass track.</summary>
    public int SplitNote { get; init; } = 48;

    /// <summary>
    /// Base tracks (indexes into the mode's track names) to split into melody, harmony and low line
    /// (see <see cref="LineSplitter"/> and <see cref="TrackLayout"/>).
    /// </summary>
    public IReadOnlyList<int> SplitTracks { get; init; } = [];
}

public readonly record struct StageProgress(string Stage, double Fraction);

/// <summary>
/// One loaded file and its cached processing stages: decode (ffmpeg), drum separation and tempo, engine
/// analysis, then note decoding (fast and repeatable). With instrument separation the stages are a stereo
/// decode, Demucs stems, and one analysis per stem.
/// </summary>
public sealed class TranscriptionSession : IDisposable
{
    public const int SampleRate = 22050;

    /// <summary>Stem tracks in instrument mode, in track order. Guitar and Piano come only with the 6-stem model.</summary>
    public static readonly IReadOnlyList<string> StemNames = ["Other", "Bass", "Vocals", "Guitar", "Piano"];

    BasicPitchEngine? _basicPitch;
    InstrumentRecognizer? _recognizer;
    readonly Dictionary<(TrackMode, int), IReadOnlyList<InstrumentGuess?>> _instrumentCache = [];
    IStemSeparator? _demucs;
    string? _demucsPath;
    bool _demucsGpu;
    readonly YinEngine _yin = new();
    double _separatedWith = double.NaN;
    AnalysisSettings? _mixAnalyzedWith;
    string? _ffmpegPath;

    public string? SourcePath { get; private set; }
    public AudioData? Original { get; private set; }
    public HpssResult? Separation { get; private set; }
    public TempoEstimate? Tempo { get; private set; }

    /// <summary>Analysis of the whole mix (single track and register-split modes).</summary>
    public IAnalysis? Analysis { get; private set; }

    /// <summary>The audio the mix analysis transcribed, with drums removed if percussion removal is on.</summary>
    public AudioData? AnalyzedAudio { get; private set; }

    /// <summary>Demucs stems at <see cref="SampleRate"/>, once instrument separation has run.</summary>
    public StemSet? Stems { get; private set; }

    /// <summary>One analysis per entry of <see cref="StemNames"/>.</summary>
    public IReadOnlyList<IAnalysis>? StemAnalyses { get; private set; }

    /// <summary>Drum analysis of <see cref="DrumSource"/>, once <see cref="AnalyzeDrumsAsync"/> has run.</summary>
    public DrumAnalysis? Drums { get; private set; }
    float[]? _drumsAnalyzedFrom;

    /// <summary>Path of the Demucs ONNX model, or null to find <see cref="SeparationModel"/> automatically.</summary>
    public string? DemucsModelPath { get; set; }

    /// <summary>Run separation on the GPU where the model supports it (BS-RoFormer on macOS). The stems are the same, only faster.</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>Where the separation model last ran ("CPU", "GPU", or why the GPU was not used), or null before it loads.</summary>
    public string? SeparationDevice => (_demucs as RoformerSeparator)?.Device ?? (_demucs is null ? null : "CPU");

    StemModel _separationModel = StemModel.FourStem;

    /// <summary>The Demucs model that separates instruments. Changing it clears stems made with the previous one.</summary>
    public StemModel SeparationModel
    {
        get => _separationModel;
        set
        {
            if (value == _separationModel) return;
            _separationModel = value;
            Stems = null;
            StemAnalyses = null;
            _mixLevel = null;
            _mixLevelBright = null;
            _stemLevels = null;
            _instrumentCache.Clear();
            _octaveEvidence.Clear();
        }
    }

    public async Task LoadAsync(string path, string? ffmpegPath, IProgress<StageProgress>? progress = null,
        CancellationToken ct = default)
    {
        var decodeProgress = new Progress<double>(f => progress?.Report(new("Decoding", f)));
        var audio = await FfmpegDecoder.DecodeAsync(path, SampleRate, ffmpegPath, decodeProgress, ct);

        SourcePath = path;
        _ffmpegPath = ffmpegPath;
        Original = audio;
        Separation = null;
        Tempo = null;
        Analysis = null;
        AnalyzedAudio = null;
        Stems = null;
        StemAnalyses = null;
        Drums = null;
        _drumsAnalyzedFrom = null;
        _separatedWith = double.NaN;
        _mixAnalyzedWith = null;
        _instrumentCache.Clear();
        _octaveEvidence.Clear();
    }

    public async Task AnalyzeAsync(AnalysisSettings settings, IProgress<StageProgress>? progress = null,
        CancellationToken ct = default)
    {
        var audio = Original ?? throw new InvalidOperationException("Load a file first.");

        // HPSS drives tempo detection, so it runs whatever else is selected.
        if (Separation is null || _separatedWith != settings.SeparationStrength)
        {
            progress?.Report(new("Separating drums", 0));
            var strength = settings.SeparationStrength;
            Separation = await Task.Run(() => Hpss.Separate(audio.Samples, audio.SampleRate, strength, ct: ct), ct);
            Tempo = TempoEstimate.From(BeatTracker.Track(Separation.OnsetEnvelope, Separation.EnvelopeRate, Separation.LowOnsetEnvelope));
            _separatedWith = strength;
            _mixAnalyzedWith = null;
            _instrumentCache.Clear();
        }

        if (settings.SeparateInstruments) await AnalyzeStemsAsync(progress, ct);
        else if (_mixAnalyzedWith != settings) await AnalyzeMixAsync(settings, audio, progress, ct);

        progress?.Report(new("Done", 1));
    }

    async Task AnalyzeMixAsync(AnalysisSettings settings, AudioData audio, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var input = settings.RemovePercussion ? new AudioData(Separation!.Harmonic, audio.SampleRate) : audio;
        ITranscriptionEngine engine = settings.Engine switch
        {
            EngineKind.BasicPitch => _basicPitch ??= new BasicPitchEngine(),
            _ => _yin,
        };

        var engineProgress = new Progress<double>(f => progress?.Report(new("Transcribing", f)));
        Analysis = await engine.AnalyzeAsync(input, engineProgress, ct);
        if (!ReferenceEquals(AnalyzedAudio?.Samples, input.Samples))
            foreach (var key in _instrumentCache.Keys.Where(k => k.Item1 != TrackMode.Instruments).ToList())
                _instrumentCache.Remove(key);
        AnalyzedAudio = input;
        _mixAnalyzedWith = settings;
    }

    async Task AnalyzeStemsAsync(IProgress<StageProgress>? progress, CancellationToken ct)
    {
        if (StemAnalyses is not null) return;
        await EnsureStemsAsync(progress, ct);

        var engine = _basicPitch ??= new BasicPitchEngine();
        var sources = StemTrackAudio(Stems!);
        var analyses = new List<IAnalysis>();
        for (int i = 0; i < sources.Length; i++)
        {
            var name = StemNames[i];
            var stemProgress = new Progress<double>(f => progress?.Report(new($"Transcribing {name.ToLowerInvariant()}", f)));
            analyses.Add(await engine.AnalyzeAsync(new AudioData(sources[i], SampleRate), stemProgress, ct));
        }
        StemAnalyses = analyses;
    }

    /// <summary>True if Demucs stems exist or can be made (the model is installed).</summary>
    public bool CanSeparate => Stems is not null || DemucsSeparator.LocateModel(DemucsModelPath, SeparationModel) is not null;

    /// <summary>Return the audio of each stem track (3 or 5 of them), in <see cref="StemNames"/> order.</summary>
    static float[][] StemTrackAudio(StemSet stems) => stems.HasGuitarAndPiano
        ? [stems.Other, stems.Bass, stems.Vocals, stems.Guitar!, stems.Piano!]
        : [stems.Other, stems.Bass, stems.Vocals];

    /// <summary>Run Demucs on the loaded file once. Later calls return immediately.</summary>
    public async Task EnsureStemsAsync(IProgress<StageProgress>? progress, CancellationToken ct)
    {
        if (Stems is null)
        {
            var modelPath = DemucsSeparator.LocateModel(DemucsModelPath, SeparationModel)
                            ?? throw new FileNotFoundException($"The {SeparationModel.Name} separation model is not installed. Download it first.");
            if (_demucs is null || _demucsPath != modelPath || _demucsGpu != UseGpu)
            {
                _demucs?.Dispose();
                _demucs = null;
                var model = SeparationModel;
                bool gpu = UseGpu;
                // The first GPU run prepares the model for CoreML and caches it for later launches.
                bool firstGpuRun = gpu && OperatingSystem.IsMacOS() && model.Architecture == StemArchitecture.BsRoformer
                                   && !RoformerSeparator.IsReadyOnGpu(modelPath);
                progress?.Report(new(firstGpuRun ? "Preparing the separation model for the GPU (first time only)" : "Loading separation model", 0));
                _demucs = await Task.Run(() => model.Open(modelPath, gpu: gpu), ct);
                _demucsPath = modelPath;
                _demucsGpu = gpu;
            }

            var decodeProgress = new Progress<double>(f => progress?.Report(new("Decoding stereo", f)));
            var (channels, _) = await FfmpegDecoder.DecodeChannelsAsync(SourcePath!, _demucs.SampleRate, 2,
                _ffmpegPath, decodeProgress, ct);

            var separateProgress = new Progress<double>(f => progress?.Report(new("Separating instruments", f)));
            var demucs = _demucs;
            Stems = await Task.Run(() =>
            {
                var s = demucs.Separate(channels[0], channels[1], separateProgress, ct);
                // Resample to the transcription rate, which also makes the stems four times smaller in memory.
                float[] Down(float[] x) => FfmpegDecoder.Resample(new AudioData(x, s.SampleRate), SampleRate).Samples;
                return new StemSet(Down(s.Drums), Down(s.Bass), Down(s.Other), Down(s.Vocals), SampleRate,
                    s.Guitar is null ? null : Down(s.Guitar), s.Piano is null ? null : Down(s.Piano));
            }, ct);
            _mixLevel = FrameLevels(Original!.Samples, bright: false);
            _mixLevelBright = FrameLevels(Original.Samples, bright: true);
            _stemLevels = StemTrackAudio(Stems).Select((x, t) => FrameLevels(x, bright: t != BassStem)).ToList();
        }
    }

    /// <summary>Whether notes for this mode are available without running analysis again.</summary>
    public bool HasNotesFor(TrackMode mode) => mode == TrackMode.Instruments ? StemAnalyses is not null : Analysis is not null;

    /// <summary>Decode and clean up notes, and assign tracks. This is fast enough to call on every slider change.</summary>
    public IReadOnlyList<DetectedNote> GetNotes(NoteSettings settings, TempoEstimate? tempoOverride = null,
        TrackSettings? tracks = null)
    {
        tracks ??= new TrackSettings();
        var tempo = tempoOverride ?? Tempo;

        if (tracks.Mode == TrackMode.Instruments)
        {
            if (StemAnalyses is null) return [];
            var all = new List<DetectedNote>();
            for (int t = 0; t < StemAnalyses.Count; t++)
            {
                // Filter each stem separately, since merging and overlap fixes only make sense within one instrument.
                var stemSettings = t == BassStem
                    ? settings with { HighestNote = Math.Min(settings.HighestNote, 67), MinNoteMs = settings.MinNoteMs * BassMinLength }
                    : settings;
                var stemNotes = NoteFilter.Apply(StemAnalyses[t].Decode(stemSettings), stemSettings, tempo);
                if (t == BassStem && Stems is { } stems) stemNotes = BassCleanup.Clean(stemNotes, stems.Bass, stems.SampleRate, _octaveEvidence);
                all.AddRange(stemNotes.Where(n => StemIsAudible(t, n)).Select(n => n with { Track = t }));
            }
            all.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
            return WithLines(DropLeakedDuplicates(all), tracks, StemAnalyses.Count);
        }

        if (Analysis is null) return [];
        var notes = NoteFilter.Apply(Analysis.Decode(settings), settings, tempo);
        return tracks.Mode == TrackMode.SplitByRegister
            ? WithLines(TrackSplitter.SplitByRegister(notes, tracks.SplitNote), tracks, 2)
            : WithLines(notes, tracks, 1);
    }

    static IReadOnlyList<DetectedNote> WithLines(List<DetectedNote> notes, TrackSettings tracks, int baseTracks)
    {
        IReadOnlyList<DetectedNote> result = notes;
        foreach (int track in tracks.SplitTracks.Where(t => t >= 0 && t < baseTracks).Distinct())
        {
            var (harmony, low) = TrackLayout.LineTracks(baseTracks, tracks.SplitTracks.Where(t => t >= 0 && t < baseTracks), track);
            result = LineSplitter.Split(result, track, harmony, low);
        }
        return result;
    }

    /// <summary>Notes of the same pitch in two stems that start within this many seconds may be one note.</summary>
    const double SameNoteSeconds = 0.05;

    /// <summary>A duplicate this many dB quieter (in its stem's share of the mix) than the loudest copy counts as leakage.</summary>
    const double LeakageDb = 9;

    /// <summary>
    /// Demucs leaks a little of each instrument into the other stems, so one note can be transcribed
    /// from two of them. When a note appears in several stems, drop the copies that are far quieter than
    /// the loudest. Copies at similar levels stay, because two instruments really can play the same note.
    /// </summary>
    List<DetectedNote> DropLeakedDuplicates(List<DetectedNote> notes)
    {
        if (_stemLevels is null) return notes;
        var share = notes.Select(n => StemShareDb(n.Track, n)).ToArray();
        var drop = new bool[notes.Count];
        for (int i = 0; i < notes.Count; i++) // sorted by start
        {
            double loudest = share[i];
            var copies = new List<int> { i };
            for (int j = i + 1; j < notes.Count && notes[j].Start - notes[i].Start <= SameNoteSeconds; j++)
                if (notes[j].Pitch == notes[i].Pitch && notes[j].Track != notes[i].Track)
                {
                    copies.Add(j);
                    loudest = Math.Max(loudest, share[j]);
                }
            if (copies.Count == 1) continue;
            foreach (int c in copies)
                if (share[c] < loudest - LeakageDb) drop[c] = true;
        }
        return notes.Where((_, i) => !drop[i]).ToList();
    }

    // ---- Drums ----

    /// <summary>
    /// The audio that drums are transcribed from. This is the Demucs drum stem once instrument separation
    /// has run, since it is much cleaner, or else the percussive part from drum removal. Null before analysis.
    /// </summary>
    public float[]? DrumSource => Stems?.Drums ?? Separation?.Percussive;

    /// <summary>True if drum hits are available from the best audio source there is.</summary>
    public bool HasDrumsForCurrentSource => Drums is not null && ReferenceEquals(_drumsAnalyzedFrom, DrumSource);

    /// <summary>
    /// Find drum activity in <see cref="DrumSource"/>. This runs again only when the source changes, that is
    /// when the drum removal strength changes or instrument separation produces a drum stem.
    /// </summary>
    public async Task AnalyzeDrumsAsync(IProgress<StageProgress>? progress = null, CancellationToken ct = default)
    {
        var source = DrumSource ?? throw new InvalidOperationException("Analyse the file first.");
        if (HasDrumsForCurrentSource) return;
        progress?.Report(new("Transcribing drums", 0));
        Drums = await DrumTranscriber.AnalyzeAsync(new AudioData(source, SampleRate), Original, ct);
        _drumsAnalyzedFrom = source;
    }

    /// <summary>Return drum hits for the settings, or none before drum analysis. This is fast enough to call on every slider change.</summary>
    public IReadOnlyList<DrumHit> GetDrumHits(DrumSettings settings) => Drums?.Decode(settings) ?? [];

    // ---- Stem gate ----
    // Demucs leaves faint leftovers of every instrument in every stem (an instrumental still has a
    // quiet "vocals" stem), and basic-pitch happily transcribes them. Keep a note only if its stem
    // carries a real share of the mix's energy while the note sounds.
    //
    // Raw energy is mostly low frequencies, so a loud sub bass can hide a clearly audible piano. Stems
    // other than bass are therefore compared with high frequencies boosted (about +6 dB per octave,
    // closer to how loud things sound). The bass stem is compared as it is, because its energy is all low.

    const int LevelHop = 512;
    const int BassStem = 1;

    /// <summary>Bass notes may be this fraction of the minimum note length, because fast bass lines (sixteenths in metal) run under 120 ms.</summary>
    const double BassMinLength = 2.0 / 3;

    /// <summary>Cached measurements from the bass octave check, by note (see <see cref="BassCleanup.FixOctaves"/>).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, long, long), (double, double)> _octaveEvidence = new();
    const double MinStemShareDb = -24;
    float[]? _mixLevel, _mixLevelBright;
    List<float[]>? _stemLevels;

    static float[] FrameLevels(float[] x, bool bright)
    {
        var levels = new float[x.Length / LevelHop + 1];
        for (int f = 0; f < levels.Length; f++)
        {
            double sum = 0;
            int end = Math.Min(x.Length, (f + 1) * LevelHop);
            for (int i = f * LevelHop; i < end; i++)
            {
                double v = bright ? x[i] - 0.95 * (i > 0 ? x[i - 1] : 0) : x[i];
                sum += v * v;
            }
            levels[f] = (float)sum;
        }
        return levels;
    }

    float[]? MixLevelFor(int stem) => stem == BassStem ? _mixLevel : _mixLevelBright;

    bool StemIsAudible(int stem, DetectedNote note) => StemShareDb(stem, note) >= MinStemShareDb;

    /// <summary>Return the stem's share of the mix while the note sounds, in dB (0 = all of it), or 0 before separation.</summary>
    double StemShareDb(int stem, DetectedNote note)
    {
        if (MixLevelFor(stem) is not { } mixLevel || _stemLevels is null) return 0;
        var levels = _stemLevels[stem];
        int from = Math.Clamp((int)(note.Start * SampleRate / LevelHop), 0, levels.Length - 1);
        int to = Math.Clamp((int)Math.Ceiling(note.End * SampleRate / LevelHop), from + 1, levels.Length);
        double stemEnergy = 0, mixEnergy = 0;
        for (int f = from; f < to; f++) { stemEnergy += levels[f]; mixEnergy += mixLevel[f]; }
        if (mixEnergy <= 0) return double.NegativeInfinity;
        return 10 * Math.Log10(stemEnergy / mixEnergy + 1e-12);
    }

    // ---- Instrument recognition ----

    /// <summary>
    /// Suggest an instrument for each track in the mode of <paramref name="tracks"/>, or null where there is
    /// nothing to go on. Recognition listens to the Demucs stems, and runs separation first if needed and the
    /// model is installed. Register mode maps the bass stem to Bass and the other stem to Keys, and a single
    /// track uses the main instrument stem. Without the model it falls back to the drum-free mix and marks
    /// the guesses unreliable, because a full mix sounds very different from the single instruments the
    /// recognizer was trained on.
    /// </summary>
    public async Task<IReadOnlyList<InstrumentGuess?>> RecognizeInstrumentsAsync(TrackSettings tracks,
        IProgress<StageProgress>? progress = null, CancellationToken ct = default)
    {
        var guesses = await RecognizeBaseTracksAsync(tracks, progress, ct);
        // Split lines come from their base track's audio, so they get its instrument.
        return TrackLayoutFor(tracks).Select(s => s.Source < guesses.Count ? guesses[s.Source] : null).ToList();
    }

    async Task<IReadOnlyList<InstrumentGuess?>> RecognizeBaseTracksAsync(TrackSettings tracks,
        IProgress<StageProgress>? progress, CancellationToken ct)
    {
        if (_instrumentCache.TryGetValue((tracks.Mode, 0), out var cached)) return cached;

        if (Stems is null && CanSeparate) await EnsureStemsAsync(progress, ct);
        var inputs = InstrumentInputs(tracks);
        if (inputs is null) return [];

        progress?.Report(new("Recognising instruments", 0));
        var result = await Task.Run(() =>
        {
            _recognizer ??= new InstrumentRecognizer();
            var guesses = new List<InstrumentGuess?>();
            for (int i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (audio, role) = inputs[i];
                var guess = audio is null ? null : _recognizer.Recognize(audio, SampleRate, role);
                guesses.Add(Stems is null && guess is not null ? guess with { Reliable = false } : guess);
                progress?.Report(new("Recognising instruments", (i + 1.0) / inputs.Count));
            }
            return (IReadOnlyList<InstrumentGuess?>)guesses;
        }, ct);

        _instrumentCache[(tracks.Mode, 0)] = result;
        return result;
    }

    List<(float[]? Audio, TrackRole Role)>? InstrumentInputs(TrackSettings tracks)
    {
        if (Stems is { } stems)
        {
            // A stem the gate treats as silent throughout has nothing to recognise.
            float[]? Audible(float[] stem, int t) => _stemLevels is { } levels && MixLevelFor(t) is { } mix
                && 10 * Math.Log10(levels[t].Sum() / Math.Max(1e-12, mix.Sum())) < MinStemShareDb ? null : stem;
            var other = Audible(stems.Other, 0);
            var bass = Audible(stems.Bass, 1);
            var vocals = Audible(stems.Vocals, 2);
            var guitar = stems.Guitar is null ? null : Audible(stems.Guitar, 3);
            var piano = stems.Piano is null ? null : Audible(stems.Piano, 4);
            // With six stems the main instrument may be in the guitar or piano stem.
            var (main, mainRole) = new[] { (other, TrackRole.Melodic), (guitar, TrackRole.Guitar), (piano, TrackRole.Keys) }
                .Where(x => x.Item1 is not null)
                .Select(x => (x.Item1, x.Item2))
                .DefaultIfEmpty((null, TrackRole.Melodic))
                .MaxBy(x => x.Item1 is null ? 0 : Energy(x.Item1));
            return tracks.Mode switch
            {
                TrackMode.Instruments when stems.HasGuitarAndPiano =>
                    [(other, TrackRole.Melodic), (bass, TrackRole.Bass), (vocals, TrackRole.Vocal), (guitar, TrackRole.Guitar), (piano, TrackRole.Keys)],
                TrackMode.Instruments => [(other, TrackRole.Melodic), (bass, TrackRole.Bass), (vocals, TrackRole.Vocal)],
                TrackMode.SplitByRegister => [(main ?? vocals, main is not null ? mainRole : TrackRole.Melodic), (bass, TrackRole.Bass)],
                _ => [(main ?? vocals ?? bass, main is not null ? mainRole : TrackRole.Any)],
            };
        }

        // Without stems, use the drum-free mix, and only for a single track.
        return AnalyzedAudio is { } audio && tracks.Mode == TrackMode.Single ? [(audio.Samples, TrackRole.Any)] : null;
    }

    static double Energy(float[] x)
    {
        double sum = 0;
        foreach (var v in x) sum += v * v;
        return sum;
    }

    /// <summary>
    /// Return the base track names for a mode, in track-index order. Instrument mode has 3 tracks with the
    /// 4-stem model and 5 (adding Guitar and Piano) with the 6-stem one.
    /// </summary>
    public static IReadOnlyList<string> TrackNames(TrackMode mode, StemModel? model = null) => mode switch
    {
        TrackMode.SplitByRegister => ["Keys", "Bass"],
        TrackMode.Instruments => StemNames.Take((model ?? StemModel.FourStem).Stems == 6 ? 5 : 3).ToList(),
        _ => ["Transcription"],
    };

    /// <summary>Return the base track names for this session's stems, or for its chosen model before separation.</summary>
    public IReadOnlyList<string> TrackNamesFor(TrackMode mode) =>
        TrackNames(mode, Stems is { } s ? (s.HasGuitarAndPiano ? StemModel.SixStem : StemModel.FourStem) : SeparationModel);

    /// <summary>Return all output tracks, including split lines, in the order <see cref="GetNotes"/> numbers them.</summary>
    public IReadOnlyList<TrackSlot> TrackLayoutFor(TrackSettings tracks) =>
        TrackLayout.Build(TrackNamesFor(tracks.Mode), tracks.SplitTracks);

    /// <summary>Suggested staff layout per track for sheet music.</summary>
    public static StaffLayout StavesFor(TrackMode mode, int track) => (mode, track) switch
    {
        (TrackMode.Single, _) => StaffLayout.Auto,
        (_, 1) => StaffLayout.Bass,
        (TrackMode.Instruments, 2) => StaffLayout.Treble,
        _ => StaffLayout.Auto,
    };

    /// <summary>Staff layout for an output track. A split track's melody goes on treble and its low line on bass.</summary>
    public static StaffLayout StavesFor(TrackMode mode, TrackSlot slot) => slot.Line switch
    {
        MusicLine.Melody => StaffLayout.Treble,
        MusicLine.Low => StaffLayout.Bass,
        MusicLine.Harmony => StaffLayout.Auto,
        _ => StavesFor(mode, slot.Source),
    };

    public void Dispose()
    {
        _basicPitch?.Dispose();
        _recognizer?.Dispose();
        _demucs?.Dispose();
    }
}
