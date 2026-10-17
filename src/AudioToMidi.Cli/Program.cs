using System.Diagnostics;
using System.Globalization;
using AudioToMidi.Core;
using AudioToMidi.Core.Drums;
using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Project;
using AudioToMidi.Core.Separation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;

const string Usage = """
    audio2midi <input> [options]

      -o, --output <file.mid>    Output path (default: input name with .mid)
      --engine basicpitch|yin    Polyphonic (default) or monophonic transcription
      --keep-drums               Do not remove percussion before transcribing
      --strength <1..4>          Drum removal strength (default 2)
      --onset <0..1>             basic-pitch onset threshold (default 0.5)
      --frame <0..1>             basic-pitch frame threshold (default 0.3)
      --min-ms <ms>              Minimum note length (default 120)
      --low <midi> --high <midi> Pitch range (default 21..108)
      --merge-ms <ms>            Merge same-pitch notes closer than this
      --pitch-bends              Follow each note's pitch for bends and slides (basic-pitch): notes cut into
                                 steps along a bend are joined, and MIDI gets pitch bends where a note
                                 sounds alone on its track
      --quantize <n>             Snap to 1/n of a beat (4 = sixteenths)
      --swing auto|off|<0.5..0.75>
                                 Swung eighths: quantize on the swung grid, and write sheet music straight
                                 under a "Swing" mark. A number is where the off-beat eighth falls in the
                                 beat (0.67 = triplet shuffle). Default: detected, in simple meters
      --bpm <bpm>                Override detected tempo. Half or double the detected one keeps the detected
                                 beats (adding or dropping every other one); any other tempo is steady
      --program <0..127>         General MIDI instrument for the main track (default 0, piano)
      --ffmpeg <path>            Path to ffmpeg

    Sheet music and bar lines:
      --musicxml <file>          Also write sheet music (MusicXML; open in MuseScore etc.), with chord symbols
      --chords                   Print the chords bar by bar, and the key (with any key changes)
      --chord-track              Add a "Chords" track to the MIDI: each chord held (bass below, tones around
                                 middle C) with its symbol as a text event
      --beats <n>                Beats per bar, in quarter notes (default: detected)
      --time <n/d>               Time signature, e.g. 3/4, 6/8, 12/8 (in 6/8 a beat is a dotted quarter).
                                 Default: detected from the notes (4/4, 3/4, 6/8, 9/8 or 12/8)
      --downbeat <n>             Which detected beat starts the bar (default: detected)
      --staves auto|treble|bass|grand
      --key <fifths>             Key signature, e.g. 2 = D major/B minor, -3 = Eb major (default: detected)
      --keep-timing              Keep MIDI notes at their original times instead of aligning bar 1 to the start

    Tracks:
      --tracks single|register|instruments
                                 single: one track (default)
                                 register: Keys + Bass, split at --split
                                 instruments: Demucs separates Other, Bass and Vocals (slow; needs the model)
      --split <midi>             Register split note (default 48 = C3)
      --split-lines[=a,b]        Split tracks into melody, harmony and low line: the first track (Other,
                                 Keys or the only track), or the named ones, e.g. --split-lines=other,piano
      --stems 4|6|roformer       Separation model: 4 stems (bass, vocals, other) or 6 (adds guitar and
                                 piano tracks in instruments mode). With 6, BS-RoFormer is used when
                                 installed (better, several times slower), else HT-Demucs; "roformer"
                                 insists on it. Default 4
      --no-roformer              With --stems 6, use HT-Demucs even if BS-RoFormer is installed
      --cpu                      Run BS-RoFormer on the CPU (default on macOS: the GPU, about four times faster)
      --demucs-model <file>      Path to a Demucs model (default: auto-locate the --stems model)
      --no-auto-instruments      Do not pick instruments from the audio (use --program and GM defaults)
      --download-model [4|6|roformer] [--from <url|path>]
                                 Download a separation model (166 MB, 136 MB, 353 MB) and exit. --from
                                 installs it from elsewhere: a URL or file path to the model file (.onnx),
                                 a model archive (.zip) or a folder holding it

    Drums:
      --drums                    Add a drum track (kick, snare, hi-hats) on MIDI channel 10
      --drum-sensitivity <0..1>  Higher finds quieter hits, and more false ones (default 0.5)

    Project:
      --bundle <folder>          Also save a project bundle (e.g. song.a2m): analysis.json with the beat
                                 grid, key and every part including drums, the stems as WAV, and the MIDI
    """;

if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }

if (args[0] == "--download-model")
{
    var model = args.ElementAtOrDefault(1) switch { "6" => StemModel.SixStem, "roformer" => StemModel.Roformer, _ => StemModel.FourStem };
    int fromAt = Array.IndexOf(args, "--from");
    string? from = fromAt > 0 ? args.ElementAtOrDefault(fromAt + 1) : null;
    var dest = model.DefaultPath;
    try
    {
        if (fromAt > 0 && from is null) throw new ArgumentException("--from needs a URL or path.");
        var source = ModelInstaller.Resolve(model, from);
        Console.Error.WriteLine($"{(source.Remote ? "Downloading" : "Copying")} {model.Name}{(source.Archive ? " from the archive" : "")} {source.Location}\n  to {dest}");
        int lastPct = -1;
        await ModelInstaller.InstallAsync(model, from, dest, new SyncProgress<double>(f =>
        {
            int pct = (int)(f * 100);
            if (pct / 10 != lastPct / 10) Console.Error.WriteLine($"  {pct}%");
            lastPct = pct;
        }));
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Error: {e.Message}");
        return 1;
    }
    Console.WriteLine(dest);
    return 0;
}

string input = args[0];
string? output = null, ffmpeg = null, musicXml = null, bundle = null;
bool drums = false;
bool printChords = false, chordTrack = false;
var drumSettings = new DrumSettings();
Meter? forcedMeter = null;
double? forcedSwing = null; // null = detect
int? downbeat = null, keyFifths = null;
var staves = StaffLayout.Auto;
bool keepTiming = false;
var trackSettings = new TrackSettings();
List<string> splitNames = []; // "" = the first track
string? demucsModel = null;
var separationModel = StemModel.FourStem;
bool noRoformer = false;
bool cpuOnly = false;
bool autoInstruments = true;
double? bpm = null;
int program = 0;
var analysis = new AnalysisSettings();
var notes = new NoteSettings();

try
{
    for (int i = 1; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        double Num() => double.Parse(Next(), CultureInfo.InvariantCulture);
        switch (args[i])
        {
            case "-o" or "--output": output = Next(); break;
            case "--engine":
                analysis = analysis with { Engine = Next().ToLowerInvariant() is "yin" ? EngineKind.Yin : EngineKind.BasicPitch };
                break;
            case "--keep-drums": analysis = analysis with { RemovePercussion = false }; break;
            case "--strength": analysis = analysis with { SeparationStrength = Num() }; break;
            case "--onset": notes = notes with { OnsetThreshold = Num() }; break;
            case "--frame": notes = notes with { FrameThreshold = Num() }; break;
            case "--min-ms": notes = notes with { MinNoteMs = Num() }; break;
            case "--low": notes = notes with { LowestNote = (int)Num() }; break;
            case "--high": notes = notes with { HighestNote = (int)Num() }; break;
            case "--merge-ms": notes = notes with { MergeGapMs = Num() }; break;
            case "--pitch-bends": notes = notes with { PitchBends = true }; break;
            case "--quantize": notes = notes with { QuantizeDivisions = (int)Num() }; break;
            case "--swing":
                string swingArg = Next();
                forcedSwing = swingArg == "auto" ? null : swingArg == "off" ? Swing.Straight
                    : double.TryParse(swingArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double swingValue) && swingValue >= 0.5 && swingValue <= 0.75 ? swingValue
                    : throw new ArgumentException("--swing takes auto, off or a number from 0.5 to 0.75");
                break;
            case "--bpm": bpm = Num(); break;
            case "--program": program = (int)Num(); break;
            case "--ffmpeg": ffmpeg = Next(); break;
            case "--musicxml": musicXml = Next(); break;
            case "--beats": forcedMeter = new Meter(Math.Clamp((int)Num(), 1, 12), 4); break;
            case "--time": forcedMeter = Meter.Parse(Next()) ?? throw new ArgumentException("--time takes a time signature such as 3/4 or 6/8"); break;
            case "--downbeat": downbeat = (int)Num(); break;
            case "--key": keyFifths = Math.Clamp((int)Num(), -7, 7); break;
            case "--staves": staves = Enum.Parse<StaffLayout>(Next(), ignoreCase: true); break;
            case "--keep-timing": keepTiming = true; break;
            case "--tracks":
                trackSettings = trackSettings with
                {
                    Mode = Next().ToLowerInvariant() switch
                    {
                        "single" => TrackMode.Single,
                        "register" => TrackMode.SplitByRegister,
                        "instruments" => TrackMode.Instruments,
                        var other => throw new ArgumentException($"Unknown track mode {other}"),
                    },
                };
                break;
            case "--split": trackSettings = trackSettings with { SplitNote = (int)Num() }; break;
            case "--split-lines": splitNames.Add(""); break;
            case var a when a.StartsWith("--split-lines="):
                splitNames.AddRange(a["--split-lines=".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                break;
            case "--demucs-model": demucsModel = Next(); break;
            case "--no-roformer": noRoformer = true; break;
            case "--cpu": cpuOnly = true; break;
            case "--stems":
                separationModel = Next() switch
                {
                    "4" => StemModel.FourStem,
                    "6" => StemModel.SixStem, // resolved below: BS-RoFormer when installed
                    "roformer" => StemModel.Roformer,
                    var other => throw new ArgumentException($"--stems must be 4, 6 or roformer, not {other}"),
                };
                break;
            case "--no-auto-instruments": autoInstruments = false; break;
            case "--drums": drums = true; break;
            case "--chords": printChords = true; break;
            case "--chord-track": chordTrack = true; break;
            case "--drum-sensitivity": drumSettings = drumSettings with { Sensitivity = Math.Clamp(Num(), 0, 1) }; break;
            case "--bundle": bundle = Next(); break;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
}
catch (Exception e) when (e is ArgumentException or FormatException)  // includes Enum.Parse failures
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

output ??= Path.ChangeExtension(input, ".mid");
int quantizePerQuarter = notes.QuantizeDivisions; // --quantize counts per quarter note; notes quantize per beat of the meter
if (separationModel == StemModel.SixStem && demucsModel is null) separationModel = StemModel.Choose(6, preferRoformer: !noRoformer);
if (trackSettings.Mode == TrackMode.Instruments) Console.Error.WriteLine($"Separating with {separationModel.Name}");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var sw = Stopwatch.StartNew();
string lastStage = "";
var progress = new SyncProgress<StageProgress>(p =>
{
    lock (sw)
    {
        if (p.Stage == lastStage || p.Stage == "Done") return;
        Console.Error.WriteLine($"[{sw.Elapsed.TotalSeconds,5:F1}s] {p.Stage}...");
        lastStage = p.Stage;
    }
});

try
{
    using var session = new TranscriptionSession { DemucsModelPath = demucsModel, SeparationModel = separationModel, UseGpu = !cpuOnly };
    await session.LoadAsync(input, ffmpeg, progress, cts.Token);
    analysis = analysis with { SeparateInstruments = trackSettings.Mode == TrackMode.Instruments };
    await session.AnalyzeAsync(analysis, progress, cts.Token);
    if (session.SeparationDevice is { } device) Console.Error.WriteLine($"Separated on the {device}");

    // Find the tracks to split into lines by name. The names are known only after separation has run.
    var baseNames = session.TrackNamesFor(trackSettings.Mode);
    var splitTracks = splitNames.Select(n => n == "" ? 0 : baseNames.ToList().FindIndex(b => b.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
    if (splitTracks.Contains(-1))
        throw new ArgumentException($"--split-lines: tracks in this mode are {string.Join(", ", baseNames)}");
    trackSettings = trackSettings with { SplitTracks = splitTracks };

    var tempo = session.Tempo!;
    if (bpm is { } forced) tempo = tempo.WithBpm(forced);

    // Detect the time signature from the notes before quantizing. If the beat tracker followed the eighths
    // of a 3/4 or 6/8 bar, keep every second or third beat.
    var meter = forcedMeter ?? Meter.Common;
    var raw = session.GetNotes(notes with { QuantizeDivisions = 0 }, tempo, trackSettings);
    if (forcedMeter is null)
    {
        var detected = MeterEstimator.Estimate(raw, tempo);
        meter = detected.Meter;
        if (detected.BeatDivisor > 1 && bpm is null) tempo = detected.Apply(tempo);
        Console.Error.WriteLine($"Time signature {meter} (detected: {detected.Description})");
    }
    // Swing applies only in a simple meter, since in 6/8 the eighths already split the beat into thirds.
    double swing = meter.IsCompound ? Swing.Straight : forcedSwing ?? GrooveEstimator.Swing(raw, tempo);
    if (swing > Swing.Straight) Console.Error.WriteLine($"Swing {swing * 100:0}%{(forcedSwing is null ? " (detected)" : "")}");
    notes = notes with { QuantizeDivisions = meter.StepsPerBeat(quantizePerQuarter), Swing = swing };
    var result = session.GetNotes(notes, tempo, trackSettings);
    int beat1 = downbeat ?? DownbeatEstimator.Estimate(result, tempo, meter.BeatsPerBar);
    var grid = BarGrid.Create(tempo, meter.BeatsPerBar, beat1, result);
    var harmony = HarmonyAnalysis.Analyze(result, grid);

    // Recognise instruments from the audio. Otherwise use --program for the main track and General MIDI
    // defaults for the rest.
    var layout = session.TrackLayoutFor(trackSettings);
    var guesses = autoInstruments
        ? await session.RecognizeInstrumentsAsync(trackSettings, progress, cts.Token)
        : [];
    var tracks = layout.Select((slot, i) =>
    {
        var name = slot.Name;
        var guess = i < guesses.Count ? guesses[i] : null;
        int fallback = slot.Source == 0 ? program : baseNames[slot.Source] switch { "Bass" => 33, "Guitar" => 27, "Piano" => 0, _ => 53 };
        int gm = guess is { ShouldApply: true } ? guess.Program : fallback;
        if (guess is not null)
            Console.Error.WriteLine($"  {name}: {guess.Label} ({guess.Confidence:P0}) -> " +
                                    (guess.ShouldApply ? GeneralMidi.Programs[gm] : $"unsure, keeping {GeneralMidi.Programs[gm]}"));
        string trackName = layout.Count == 1
            ? GeneralMidi.Programs[gm]
            : $"{name}: {GeneralMidi.Programs[gm]}";
        return new TrackInfo(trackName, gm,
            layout.Count == 1 ? staves : TranscriptionSession.StavesFor(trackSettings.Mode, slot));
    }).ToList();

    // Find drums after instrument recognition, which may have run Demucs. Its drum stem is much cleaner.
    List<DetectedNote> drumNotes = [];
    var drumTrack = new TrackInfo("Drums", 0, StaffLayout.Auto, IsDrums: true);
    if (drums || bundle is not null)
    {
        await session.AnalyzeDrumsAsync(progress, cts.Token);
        var hits = session.GetDrumHits(drumSettings);
        drumNotes = DrumNotes.ToNotes(hits, tracks.Count, tempo, notes.QuantizeDivisions, swing);
        Console.Error.WriteLine($"Drums from the {(session.Stems is null ? "percussive part" : "Demucs drum stem")}: " +
                                string.Join(", ", DrumKinds.All.Select(k => $"{hits.Count(h => h.Kind == k)} {k.Label()}")));
    }

    var midiTracks = drums ? [.. tracks, drumTrack] : tracks;
    var midiNotes = drums ? result.Concat(drumNotes).ToList() : result;
    IReadOnlyList<KeySpan> keys = keyFifths is { } fifths
        ? [new KeySpan(0, double.MaxValue, new MusicalKey(fifths, KeyEstimator.Estimate(result).Minor))]
        : harmony.Keys;
    MidiExporter.Write(output, midiNotes, midiTracks, tempo.Bpm, keepTiming ? null : grid, meter, keys, chordTrack ? harmony : null);
    if (midiTracks.Count > 1)
        Console.Error.WriteLine("Tracks: " + string.Join(", ", midiTracks.Select((t, i) => $"{t.Name} ({midiNotes.Count(n => n.Track == i)} notes)")));

    if (bundle is not null)
    {
        Console.Error.WriteLine("Saving project bundle...");
        var key = keyFifths is { } f ? new MusicalKey(f, KeyEstimator.Estimate(result).Minor) : (MusicalKey?)null;
        var content = new ProjectContent([.. result, .. drumNotes], [.. tracks, drumTrack], trackSettings.Mode, grid, key,
            trackSettings.SplitTracks, meter, swing);
        var document = await ProjectExporter.SaveAsync(bundle, session, content, cts.Token);
        Console.Error.WriteLine($"Project: {document.Parts.Count} parts, {document.Stems.Count} stems, {document.Timing.Beats.Count} beats, key {document.Key?.Name ?? "unknown"}");
        Console.WriteLine(bundle);
    }

    if (musicXml is not null)
    {
        var parts = ScoreBuilder.BuildParts(result, tracks.Select(t => (t.Name, t.Program, t.Staves)).ToList(), grid, new ScoreSettings
        {
            Title = Path.GetFileNameWithoutExtension(input),
            Meter = meter,
            StepsPerBeat = quantizePerQuarter is 1 or 2 or 4 or 8 ? quantizePerQuarter : 4,
            Swing = swing,
            KeyFifths = keyFifths,
            Keys = keyFifths is null ? harmony.Keys : null,
            Chords = harmony.Chords,
        });
        MusicXmlWriter.Write(musicXml, parts);
        Console.Error.WriteLine($"Sheet music: {parts[0].MeasureCount} bars, {parts[0].Key.Name}, downbeat on beat {beat1 + 1}");
        Console.WriteLine(musicXml);
    }

    if (printChords) PrintChords(harmony, grid);

    var beatMap = tempo.Map;
    string range = beatMap.IsSteady ? "" : beatMap.BpmRange() is var (slow, fast) && fast > slow * 1.04 ? $", {slow:F0}–{fast:F0} in places" : "";
    Console.Error.WriteLine($"Tempo {tempo.Bpm:F1} BPM{range} (confidence {session.Tempo!.Confidence:F2}), " +
                            $"{result.Count} notes, {session.Original!.Duration:F1}s of audio in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine(output);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

/// <summary>Print the key and any key changes, then the chords four bars to a line ("%" repeats the bar before).</summary>
static void PrintChords(HarmonyAnalysis harmony, BarGrid grid)
{
    if (harmony.Chords.Count == 0) { Console.Error.WriteLine("No chords: no pitched notes."); return; }
    int Bar(double seconds) => (int)Math.Floor(grid.ToBeats(seconds) / grid.BeatsPerBar + 1e-6) + 1;
    Console.Error.WriteLine("Key: " + string.Join("; ", harmony.Keys.Select((k, i) => i == 0 ? k.Key.Name : $"{k.Key.Name} from bar {Bar(k.Start)}")));
    int first = Math.Max(1, Bar(harmony.Chords[0].Start)), last = Bar(harmony.Chords[^1].End - 1e-3);
    var line = new System.Text.StringBuilder();
    string? previous = null;
    for (int bar = first; bar <= last; bar++)
    {
        double a = grid.ToSeconds((bar - 1) * grid.BeatsPerBar), b = grid.ToSeconds(bar * grid.BeatsPerBar);
        var symbols = harmony.Chords.Where(c => c.End > a + 1e-3 && c.Start < b - 1e-3).Select(harmony.Symbol).ToList();
        string text = string.Join(" ", symbols);
        if ((bar - first) % 4 == 0) line.Append($"{bar,5} |");
        line.Append($" {(text == previous ? "%" : text),-12}|");
        previous = text;
        if ((bar - first) % 4 == 3 || bar == last) { Console.Error.WriteLine(line); line.Clear(); }
    }
}

/// <summary>Report right away on the calling thread. In a console app Progress&lt;T&gt; posts to the
/// thread pool, so stages could print out of order.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
