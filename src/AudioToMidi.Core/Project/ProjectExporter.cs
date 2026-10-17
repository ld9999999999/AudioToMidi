using System.Reflection;
using System.Security.Cryptography;
using AudioToMidi.Core.Audio;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Midi;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using AudioToMidi.Model;

namespace AudioToMidi.Core.Project;

/// <summary>What goes into a project: the notes and tracks as they would be exported to MIDI.</summary>
/// <param name="Notes">All notes, drums included; <see cref="DetectedNote.Track"/> indexes <paramref name="Tracks"/>.</param>
/// <param name="Tracks">The session's tracks for <paramref name="Mode"/> in order, then the drum track if any.</param>
/// <param name="Grid">Bar lines as the user has them (tempo, time signature, downbeat).</param>
/// <param name="Key">Key as chosen by the user; null detects it from the notes.</param>
/// <param name="SplitTracks">Base tracks split into melody, harmony and low line (see <see cref="TrackSettings.SplitTracks"/>).</param>
/// <param name="Swing">Where the off-beat eighths fall (see <see cref="Dsp.Swing"/>).</param>
public sealed record ProjectContent(IReadOnlyList<DetectedNote> Notes, IReadOnlyList<TrackInfo> Tracks, TrackMode Mode,
    BarGrid Grid, MusicalKey? Key = null, IReadOnlyList<int>? SplitTracks = null, Meter? Meter = null, double Swing = Dsp.Swing.Straight);

/// <summary>Save a session as a project bundle (see <see cref="ProjectBundle"/>) for other apps to build on.</summary>
public static class ProjectExporter
{
    /// <summary>
    /// Write the bundle folder: the stems the session has (Demucs instruments, else the drum-removal
    /// split), a MIDI file in time with them, and <c>analysis.json</c> last, so a folder with an
    /// analysis file is complete. Files the bundle writes are overwritten, and any others are left alone.
    /// </summary>
    public static async Task<AnalysisDocument> SaveAsync(string folder, TranscriptionSession session, ProjectContent content,
        CancellationToken ct = default)
    {
        if (File.Exists(folder)) throw new IOException($"{folder} is a file, not a folder.");
        var stemsDir = Path.Combine(folder, ProjectBundle.StemsFolder);
        Directory.CreateDirectory(stemsDir);

        var stems = new List<StemFile>();
        foreach (var (name, samples) in StemAudio(session))
        {
            ct.ThrowIfCancellationRequested();
            var relative = $"{ProjectBundle.StemsFolder}/{name}.wav";
            await Task.Run(() => WavReader.WriteFloat(Path.Combine(folder, relative), new AudioData(samples, TranscriptionSession.SampleRate)), ct);
            stems.Add(new StemFile(name, relative, TranscriptionSession.SampleRate, 1));
        }

        var source = session.SourcePath ?? "audio";
        var midiPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".mid");
        MidiExporter.Write(midiPath, content.Notes, content.Tracks, content.Grid.Bpm, meter: content.Meter);

        string? hash = File.Exists(source) ? await Sha256Async(source, ct) : null;
        var document = BuildDocument(session, content, stems, hash);
        ProjectBundle.Save(folder, document);
        return document;
    }

    /// <summary>Build the analysis document for a session without writing anything.</summary>
    public static AnalysisDocument BuildDocument(TranscriptionSession session, ProjectContent content,
        IReadOnlyList<StemFile> stems, string? sourceHash = null)
    {
        var duration = session.Original?.Duration ?? content.Notes.Select(n => n.End).DefaultIfEmpty(0).Max();
        var layout = session.TrackLayoutFor(new TrackSettings { Mode = content.Mode, SplitTracks = content.SplitTracks ?? [] });
        var stemNames = stems.Select(s => s.Name).ToHashSet();
        bool demucs = stemNames.Contains("drums");
        bool sixStems = stemNames.Contains("piano");

        var parts = content.Tracks.Select((track, i) =>
        {
            var trackNotes = content.Notes.Where(n => n.Track == i).ToList();
            var notes = trackNotes.Select(n => new PartNote(Round(n.Start), Round(n.End), n.Pitch, n.Velocity)).ToList();
            var patterns = PatternFinder.Find(trackNotes, content.Grid).Select(p => new PatternInfo(p.Bars, p.Occurrences.Select(o => o + 1).ToList(),
                p.Notes.Select(n => new PatternNoteInfo(n.Beat, n.Length, n.Pitch, n.Velocity)).ToList())).ToList();
            if (track.IsDrums)
                return new Part("Drums", PartRole.Drums, 0, "Drum kit", demucs ? "drums" : Existing("percussive"), notes) { Patterns = patterns };
            var slot = i < layout.Count ? layout[i] : new TrackSlot(track.Name, i, null);
            var (name, source, line) = (slot.Name, slot.Source, slot.Line); // split lines come from their base track
            var role = (content.Mode, source) switch
            {
                (TrackMode.Instruments or TrackMode.SplitByRegister, 1) => PartRole.Bass,
                (TrackMode.Instruments, 2) => PartRole.Vocal,
                _ => PartRole.Melodic,
            };
            string? stem = (content.Mode, source) switch
            {
                (TrackMode.Instruments, 0) => "other",
                (TrackMode.Instruments or TrackMode.SplitByRegister, 1) when demucs => "bass",
                (TrackMode.Instruments, 2) => "vocals",
                (TrackMode.Instruments, 3) => "guitar",
                (TrackMode.Instruments, 4) => "piano",
                // Keys come from the mix; with six stems they may span other, guitar and piano.
                (TrackMode.SplitByRegister, 0) when demucs && !sixStems => "other",
                _ when !demucs => Existing("harmonic"),
                _ => null,
            };
            var instrument = GeneralMidi.Programs[Math.Clamp(track.Program, 0, 127)];
            return new Part(name, role, track.Program, instrument, stem, notes, line switch
            {
                MusicLine.Melody => PartLine.Melody,
                MusicLine.Harmony => PartLine.Harmony,
                MusicLine.Low => PartLine.Low,
                _ => null,
            }) { Patterns = patterns };
        }).ToList();

        var melodic = content.Notes.Where(n => n.Track >= 0 && n.Track < content.Tracks.Count && !content.Tracks[n.Track].IsDrums).ToList();
        MusicalKey? key = content.Key ?? (melodic.Count > 0 ? KeyEstimator.Estimate(melodic) : null);
        var harmony = HarmonyAnalysis.Analyze(melodic, content.Grid);
        static KeyInfo Info(MusicalKey k) => new(k.TonicPitchClass, k.Minor, k.Fifths, k.Name);

        return new AnalysisDocument
        {
            Generator = $"AudioToMidi {typeof(ProjectExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev"}",
            Created = DateTimeOffset.UtcNow,
            Source = new SourceInfo(Path.GetFileName(session.SourcePath ?? ""), Round(duration), sourceHash),
            Timing = Timing(content.Grid, duration, session.Tempo?.Confidence ?? 0, content.Meter) with { Swing = Math.Round(content.Swing, 3) },
            Key = key is { } k ? Info(k) : null,
            Keys = harmony.Keys.Select(s => new KeySpanInfo(Round(s.Start), Round(s.End), Info(s.Key))).ToList(),
            Chords = harmony.Chords.Select(s => new ChordInfo(Round(s.Start), Round(s.End), harmony.Symbol(s), s.Chord?.Root,
                s.Chord is { } c ? Chord.MusicXmlKind(c.Quality) : null, s.Chord?.Bass, harmony.Roman(s))).ToList(),
            Parts = parts,
            Stems = stems,
        };

        string? Existing(string stem) => stemNames.Contains(stem) ? stem : null;
    }

    /// <summary>
    /// List every beat of the grid from bar 1 (or from the first beat inside the audio, when bar 1 starts
    /// later) to one beat past the end of the audio.
    /// </summary>
    public static TimingInfo Timing(BarGrid grid, double durationSeconds, double confidence, Meter? meter = null)
    {
        // Beats counted from bar 1; the first listed is bar 1's, or the earliest at or after 0 s when that is sooner.
        int first = grid.OriginSeconds >= 0 ? (int)Math.Ceiling(grid.ToBeats(0) - 1e-9) : 0;
        var beats = new List<double>();
        for (int beat = first; ; beat++)
        {
            double t = grid.ToSeconds(beat);
            beats.Add(Round(t));
            if (t > durationSeconds + 1e-9 && beats.Count >= 2) break;
        }
        return new TimingInfo(Math.Round(grid.Bpm, 3), grid.BeatsPerBar, beats, -first, Math.Round(confidence, 3))
        {
            TimeSignature = (meter ?? Meter.Common).ToString(),
        };
    }

    static IEnumerable<(string Name, float[] Samples)> StemAudio(TranscriptionSession session)
    {
        if (session.Stems is { } s)
        {
            yield return ("drums", s.Drums);
            yield return ("bass", s.Bass);
            yield return ("other", s.Other);
            yield return ("vocals", s.Vocals);
            if (s.Guitar is not null) yield return ("guitar", s.Guitar);
            if (s.Piano is not null) yield return ("piano", s.Piano);
        }
        else if (session.Separation is { } hpss)
        {
            yield return ("harmonic", hpss.Harmonic);
            yield return ("percussive", hpss.Percussive);
        }
    }

    static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>Round times to 0.1 ms, which is plenty for music and keeps the JSON readable.</summary>
    static double Round(double seconds) => Math.Round(seconds, 4);
}
