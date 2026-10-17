using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Tracks;
using AudioToMidi.Core.Transcription;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioToMidi.Core.Midi;

public static class MidiExporter
{
    /// <summary>Channel 10, zero-based.</summary>
    public const int DrumChannel = 9;

    /// <summary>General MIDI String Ensemble 1: held chords sound like a pad.</summary>
    public const int ChordProgram = 48;

    /// <summary>Pitch-bend steps per semitone at the General MIDI default range of ±2 semitones.</summary>
    const int BendPerSemitone = 4096;

    /// <summary>
    /// Build a type-1 MIDI file: a tempo track, then one named track per <see cref="TrackInfo"/>, each
    /// on its own channel with its own program change. Drum tracks go on channel 10 (index 9), which
    /// GM reserves for drums, and other tracks skip it. Each note goes to the track given by <see cref="DetectedNote.Track"/>.
    /// </summary>
    /// <param name="bpm">Tempo written to the file when <paramref name="alignTo"/> is null.</param>
    /// <param name="alignTo">
    /// If set, bar 1 of this grid starts the file and each beat is a quarter note (a dotted quarter in a
    /// compound <paramref name="meter"/>). The grid's tempo, changing wherever the beats do, and the time
    /// signature are written, so the DAW's bar lines match the music and playback stays with the audio.
    /// If null, notes keep their original times at a steady <paramref name="bpm"/>, which also keeps the
    /// file in sync with the source audio.
    /// </param>
    /// <param name="meter">The time signature; when null, the grid's beats per bar over 4 (or 4/4).</param>
    /// <param name="keys">Key signatures, each written where its span starts (the first at the start).</param>
    /// <param name="chords">
    /// When set, add a last track, "Chords", that holds each chord as a block (see <see cref="Chord.Voicing"/>)
    /// with its symbol as a text event where it starts.
    /// </param>
    public static MidiFile Build(IEnumerable<DetectedNote> notes, IReadOnlyList<TrackInfo> tracks,
        double bpm = 120, BarGrid? alignTo = null, short ticksPerQuarter = 480, Meter? meter = null,
        IReadOnlyList<KeySpan>? keys = null, HarmonyAnalysis? chords = null)
    {
        var noteList = notes.ToList();
        var division = new TicksPerQuarterNoteTimeDivision(ticksPerQuarter);
        var time = meter ?? new Meter(alignTo?.BeatsPerBar ?? 4, 4); // without one, the grid's bars of quarter notes
        TempoMap tempoMap;
        Func<double, long> Ticks;
        if (alignTo is { } grid)
        {
            double end = noteList.Select(n => n.End).DefaultIfEmpty(grid.OriginSeconds).Max();
            tempoMap = TempoMapOf(grid, time, end, division);
            double ticksPerBeat = ticksPerQuarter * time.BeatQuarters;
            Ticks = seconds => (long)Math.Round(Math.Max(0, grid.ToBeats(seconds)) * ticksPerBeat);
        }
        else
        {
            tempoMap = TempoMap.Create(division, Tempo.FromMillisecondsPerQuarterNote((long)Math.Round(60000.0 / bpm)),
                new TimeSignature(time.Beats, time.BeatType));
            var steady = tempoMap;
            Ticks = seconds => TimeConverter.ConvertFrom(new MetricTimeSpan(TimeSpan.FromSeconds(Math.Max(0, seconds))), steady);
        }

        var conductor = new List<ITimedObject>(); // tempo map goes here too, added below
        MusicalKey? previousKey = null;
        foreach (var span in keys ?? [])
        {
            if (previousKey is { } p && p == span.Key) continue;
            long at = conductor.Count == 0 ? 0 : Ticks(span.Start);
            conductor.Add(new TimedEvent(new KeySignatureEvent((sbyte)span.Key.Fifths, (byte)(span.Key.Minor ? 1 : 0)), at));
            previousKey = span.Key;
        }
        var chunks = new List<TrackChunk> { conductor.ToTrackChunk() };
        int melodic = 0;
        for (int t = 0; t < tracks.Count; t++)
        {
            int c = tracks[t].IsDrums ? DrumChannel : melodic < DrumChannel ? melodic++ : Math.Min(15, ++melodic);
            var channel = (FourBitNumber)c;
            var objects = new List<ITimedObject> { new TimedEvent(new SequenceTrackNameEvent(tracks[t].Name), 0) };
            if (!tracks[t].IsDrums)
                objects.Add(new TimedEvent(new ProgramChangeEvent((SevenBitNumber)Math.Clamp(tracks[t].Program, 0, 127)) { Channel = channel }, 0));
            var trackNotes = noteList.Where(n => n.Track == t || (t == 0 && (n.Track < 0 || n.Track >= tracks.Count))).ToList();
            foreach (var n in trackNotes)
            {
                long start = Ticks(n.Start), end = Ticks(n.End);
                objects.Add(new Note((SevenBitNumber)Math.Clamp(n.Pitch, 0, 127), Math.Max(1, end - start), start)
                {
                    Channel = channel,
                    Velocity = (SevenBitNumber)n.Velocity,
                    OffVelocity = (SevenBitNumber)0,
                });
            }
            if (!tracks[t].IsDrums) objects.AddRange(PitchBends(trackNotes, channel, Ticks));
            chunks.Add(objects.ToTrackChunk());
        }
        if (chords is { Chords.Count: > 0 })
        {
            var channel = (FourBitNumber)(melodic < DrumChannel ? melodic : Math.Min(15, melodic + 1));
            var objects = new List<ITimedObject>
            {
                new TimedEvent(new SequenceTrackNameEvent("Chords"), 0),
                new TimedEvent(new ProgramChangeEvent((SevenBitNumber)ChordProgram) { Channel = channel }, 0),
            };
            foreach (var span in chords.Chords)
            {
                if (span.Chord is not { } chord) continue;
                long start = Ticks(span.Start), end = Ticks(span.End);
                if (end <= start) continue;
                // MIDI text is plain ASCII: B♭m7 is written Bbm7.
                objects.Add(new TimedEvent(new TextEvent(chords.Symbol(span).Replace("♭", "b").Replace("♯", "#")), start));
                foreach (int pitch in chord.Voicing())
                    objects.Add(new Note((SevenBitNumber)pitch, end - start, start) { Channel = channel, Velocity = (SevenBitNumber)64, OffVelocity = (SevenBitNumber)0 });
            }
            chunks.Add(objects.ToTrackChunk());
        }

        var file = new MidiFile(chunks) { TimeDivision = tempoMap.TimeDivision };
        file.ReplaceTempoMap(tempoMap);
        return file;
    }

    /// <summary>
    /// Make pitch-bend events for the notes whose bends can be played (see <see cref="PitchBend.Playable"/>),
    /// returning to centre as each note ends. Bends past the General MIDI default range of ±2 semitones are
    /// held at its edge.
    /// </summary>
    static IEnumerable<TimedEvent> PitchBends(List<DetectedNote> notes, FourBitNumber channel, Func<double, long> ticks)
    {
        foreach (var n in PitchBend.Playable(notes))
        {
            long start = ticks(n.Start), end = Math.Max(start + 1, ticks(n.End));
            foreach (var bend in n.Bends!)
            {
                long at = Math.Clamp(ticks(bend.Time), start, end - 1);
                int value = Math.Clamp(8192 + (int)Math.Round(bend.Semitones * BendPerSemitone), 0, 16383);
                yield return new TimedEvent(new PitchBendEvent((ushort)value) { Channel = channel }, at);
            }
            yield return new TimedEvent(new PitchBendEvent(8192) { Channel = channel }, end);
        }
    }

    /// <summary>
    /// Turn the grid's beats into a MIDI tempo map from bar 1, with a tempo change at each beat whose length
    /// differs from the one before, up to a beat past <paramref name="endSeconds"/>. MIDI tempo counts quarter
    /// notes, so a dotted-quarter beat (compound meters) is one and a half of them.
    /// </summary>
    static TempoMap TempoMapOf(BarGrid grid, Meter meter, double endSeconds, TicksPerQuarterNoteTimeDivision division)
    {
        using var manager = new TempoMapManager(division);
        manager.SetTimeSignature(0, new TimeSignature(meter.Beats, meter.BeatType));
        long previous = -1;
        double ticksPerBeat = division.TicksPerQuarterNote * meter.BeatQuarters;
        int last = Math.Max(1, (int)Math.Ceiling(grid.ToBeats(endSeconds)) + 1);
        for (int beat = 0; beat <= last; beat++)
        {
            long micros = (long)Math.Round((grid.ToSeconds(beat + 1) - grid.ToSeconds(beat)) / meter.BeatQuarters * 1e6);
            if (previous >= 0 && Math.Abs(micros - previous) <= 1) continue; // ignore rounding noise
            manager.SetTempo((long)Math.Round(beat * ticksPerBeat), new Tempo(micros));
            previous = micros;
        }
        return manager.TempoMap;
    }

    /// <summary>
    /// Build a MIDI file for a clip (see <see cref="PatternFinder"/>) that is exactly its length, ready to loop.
    /// It holds the clip's notes from the start on the part's program (or channel 10 for drums), with the
    /// time signature and tempo.
    /// </summary>
    /// <param name="beatBpm">Beats a minute, counting the meter's beats (dotted quarters in 6/8).</param>
    public static MidiFile BuildClip(BarPattern clip, TrackInfo track, Meter meter, double beatBpm, short ticksPerQuarter = 480)
    {
        var division = new TicksPerQuarterNoteTimeDivision(ticksPerQuarter);
        double ticksPerBeat = ticksPerQuarter * meter.BeatQuarters;
        long length = (long)Math.Round(clip.Bars * meter.BeatsPerBar * ticksPerBeat);
        var channel = (FourBitNumber)(track.IsDrums ? DrumChannel : 0);
        var objects = new List<ITimedObject> { new TimedEvent(new SequenceTrackNameEvent(track.Name), 0) };
        if (!track.IsDrums)
            objects.Add(new TimedEvent(new ProgramChangeEvent((SevenBitNumber)Math.Clamp(track.Program, 0, 127)) { Channel = channel }, 0));
        foreach (var n in clip.Notes)
        {
            long start = (long)Math.Round(n.Beat * ticksPerBeat);
            if (start >= length) continue;
            long end = Math.Min(length, (long)Math.Round((n.Beat + n.Length) * ticksPerBeat));
            objects.Add(new Note((SevenBitNumber)Math.Clamp(n.Pitch, 0, 127), Math.Max(1, end - start), start)
            {
                Channel = channel,
                Velocity = (SevenBitNumber)Math.Clamp(n.Velocity, 1, 127),
                OffVelocity = (SevenBitNumber)0,
            });
        }
        objects.Add(new TimedEvent(new TextEvent("loop end"), length)); // the track lasts the whole clip, rests and all

        using var tempo = new TempoMapManager(division);
        tempo.SetTempo(0, Tempo.FromBeatsPerMinute(beatBpm * meter.BeatQuarters));
        tempo.SetTimeSignature(0, new TimeSignature(meter.Beats, meter.BeatType));
        var file = new MidiFile(new TrackChunk(), objects.ToTrackChunk()) { TimeDivision = division };
        file.ReplaceTempoMap(tempo.TempoMap);
        return file;
    }

    /// <summary>Single-track convenience overload.</summary>
    public static MidiFile Build(IEnumerable<DetectedNote> notes, double bpm = 120, int program = 0, BarGrid? alignTo = null) =>
        Build(notes.Select(n => n with { Track = 0 }), [new TrackInfo("Transcription", program, StaffLayout.Auto)], bpm, alignTo);

    public static void Write(string path, IEnumerable<DetectedNote> notes, IReadOnlyList<TrackInfo> tracks,
        double bpm = 120, BarGrid? alignTo = null, Meter? meter = null, IReadOnlyList<KeySpan>? keys = null, HarmonyAnalysis? chords = null) =>
        Build(notes, tracks, bpm, alignTo, meter: meter, keys: keys, chords: chords).Write(path, overwriteFile: true, format: MidiFileFormat.MultiTrack);

    public static void Write(string path, IEnumerable<DetectedNote> notes, double bpm = 120, int program = 0,
        BarGrid? alignTo = null) =>
        Build(notes, bpm, program, alignTo).Write(path, overwriteFile: true, format: MidiFileFormat.MultiTrack);
}
