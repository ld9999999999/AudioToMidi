using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>Turn timed notes into notation: measures, staves, voices, note values and ties.</summary>
public static class ScoreBuilder
{
    sealed class Chord(int start, int end, int pitch)
    {
        public int Start = start, End = end;
        public readonly List<int> Pitches = [pitch];
    }

    /// <summary>
    /// Build one score part per track. All parts share the keys (the key over time, or one detected from
    /// every note, unless overridden) and the bar count, as a multi-part score requires.
    /// </summary>
    public static IReadOnlyList<Score> BuildParts(IEnumerable<DetectedNote> notes,
        IReadOnlyList<(string Name, int Program, StaffLayout Staves)> tracks, BarGrid grid, ScoreSettings settings)
    {
        var all = notes.ToList();
        var shared = settings.KeyFifths is null && settings.Keys is { Count: > 0 }
            ? settings
            : settings with { KeyFifths = settings.KeyFifths ?? KeyEstimator.Estimate(all).Fifths };

        var parts = tracks.Select((t, i) => (Track: t, Notes: all.Where(n => n.Track == i).ToList())).ToList();
        int measures = parts.Max(p => Build(p.Notes, grid, shared with { Chords = null }).MeasureCount);
        return parts.Select((p, i) => Build(p.Notes, grid, shared with
        {
            PartName = p.Track.Name,
            MidiProgram = p.Track.Program,
            Staves = p.Track.Staves,
            Chords = i == 0 ? settings.Chords : null,
        }, measures)).ToList();
    }

    public static Score Build(IEnumerable<DetectedNote> notes, BarGrid grid, ScoreSettings settings, int? measureCount = null)
    {
        var meter = settings.Meter;
        int steps = settings.StepsPerBeat is 1 or 2 or 4 or 8 ? settings.StepsPerBeat : 4;
        int g = meter.IsCompound ? Math.Max(2, steps) : steps;          // ticks per quarter note
        int beatTicks = (int)Math.Round(g * meter.BeatQuarters);       // a dotted quarter in compound meters
        int beatsPerBar = Math.Max(1, meter.BeatsPerBar);
        int measureTicks = beatTicks * beatsPerBar;
        var noteList = notes.ToList();
        double swing = meter.IsCompound ? Dsp.Swing.Straight : Math.Clamp(settings.Swing, 0.5, 0.75);
        double Beats(double seconds) => Dsp.Swing.ToStraight(grid.ToBeats(seconds), swing); // where it is written

        var ticked = ToTicks(noteList, Beats, beatTicks);
        var clefs = ChooseStaves(ticked, settings.Staves);

        var voices = new List<ScoreVoice>();
        int lastTick = ticked.Count > 0 ? ticked.Max(n => n.End) : 0;
        int measures = Math.Max(measureCount ?? 1, (lastTick + measureTicks - 1) / measureTicks);

        var keys = MeasureKeys(noteList, grid, settings, measures, beatsPerBar);
        var upper = clefs.Count == 1 ? [] : UpperStaffNotes(ticked);
        for (int staff = 0; staff < clefs.Count; staff++)
        {
            var staffNotes = clefs.Count == 1 ? ticked
                : ticked.Where(n => upper.Contains(n) == (staff == 0)).ToList();
            var lines = AssignVoices(staffNotes, Math.Clamp(settings.MaxVoicesPerStaff, 1, 4));
            foreach (var line in lines) CloseSmallGaps(line, beatTicks);
            if (lines.Count == 0) lines.Add([]); // an empty staff still needs rests

            for (int v = 0; v < lines.Count; v++)
            {
                var laidOut = LayOut(lines[v], measures, measureTicks, g, beatTicks, beatsPerBar, keys, primary: v == 0);
                voices.Add(new ScoreVoice(staff + 1, staff * 4 + v + 1, laidOut));
            }
        }

        return new Score(settings.Title, settings.PartName, settings.MidiProgram, grid.Bpm, meter, g, keys[0],
            clefs, measures, voices)
        {
            Harmonies = Harmonies(settings.Chords, Beats, beatTicks, measureTicks, measures, keys),
            MeasureKeys = keys,
            Tempos = TempoMarks(grid, measures, beatsPerBar),
            Swing = swing,
        };
    }

    /// <summary>
    /// The key of each measure: the override, or the key over time at the measure's middle, or one key
    /// detected from all the notes.
    /// </summary>
    static List<MusicalKey> MeasureKeys(List<DetectedNote> notes, BarGrid grid, ScoreSettings settings, int measures, int beatsPerBar)
    {
        if (settings.KeyFifths is null && settings.Keys is { Count: > 0 } spans)
            return [.. Enumerable.Range(0, measures).Select(m => KeyTracker.KeyAt(spans, grid.ToSeconds((m + 0.5) * beatsPerBar)))];
        var key = settings.KeyFifths is { } f
            ? new MusicalKey(Math.Clamp(f, -7, 7), KeyEstimator.Estimate(notes) is var k && k.Fifths == f && k.Minor)
            : KeyEstimator.Estimate(notes);
        return [.. Enumerable.Repeat(key, measures)];
    }

    /// <summary>Tempo moves this much (and stays) before the score gets a new tempo mark.</summary>
    const double TempoChange = 0.04;

    /// <summary>
    /// Tempo marks: the first bars' tempo, then a new mark wherever a bar and the bars after it (the
    /// median of four) are more than 4% off the last mark. A few uneven beats don't make a mark.
    /// </summary>
    static List<TempoMark> TempoMarks(BarGrid grid, int measures, int beatsPerBar)
    {
        var bpm = Enumerable.Range(0, measures)
            .Select(m => beatsPerBar * 60 / Math.Max(1e-6, grid.ToSeconds((m + 1) * beatsPerBar) - grid.ToSeconds(m * beatsPerBar)))
            .ToArray();
        double Median(int from) { var w = bpm.Skip(from).Take(4).Order().ToList(); return w[w.Count / 2]; }
        var marks = new List<TempoMark> { new(0, Math.Round(Median(0))) };
        for (int m = 1; m < measures; m++)
        {
            double last = marks[^1].Bpm, ahead = Median(m);
            if (Math.Abs(bpm[m] - last) > TempoChange * last && Math.Abs(ahead - last) > TempoChange * last && Math.Round(ahead) != last)
                marks.Add(new TempoMark(m, Math.Round(ahead)));
        }
        return marks;
    }

    /// <summary>Chord symbols where each chord starts, on the score's grid, spelled in the key of their measure.</summary>
    static List<ScoreHarmony> Harmonies(IReadOnlyList<ChordSpan>? chords, Func<double, double> beats, int beatTicks, int measureTicks, int measures,
        IReadOnlyList<MusicalKey> keys)
    {
        var result = new List<ScoreHarmony>();
        if (chords is null) return result;
        int lastTick = -1;
        foreach (var span in chords)
        {
            if (span.Chord is not { } chord) continue;
            int tick = (int)Math.Round(beats(span.Start) * beatTicks);
            if (tick < 0 || tick / measureTicks >= measures || tick <= lastTick) continue;
            lastTick = tick;
            var key = keys[tick / measureTicks];
            var (rootStep, rootAlter, _) = key.Spell(60 + chord.Root);
            (char Step, int Alter, int)? bass = chord.Bass is { } b ? key.Spell(60 + b) : null;
            result.Add(new ScoreHarmony(tick / measureTicks, tick % measureTicks, rootStep, rootAlter,
                Harmony.Chord.MusicXmlKind(chord.Quality), Harmony.Chord.Suffix(chord.Quality), bass?.Step, bass?.Alter ?? 0));
        }
        return result;
    }

    readonly record struct TickNote(int Start, int End, int Pitch);

    /// <param name="beats">Seconds to written beats from bar 1.</param>
    /// <param name="g">Ticks per grid beat.</param>
    static List<TickNote> ToTicks(List<DetectedNote> notes, Func<double, double> beats, int g)
    {
        var result = new List<TickNote>();
        foreach (var n in notes.OrderBy(n => n.Pitch).ThenBy(n => n.Start))
        {
            int start = Math.Max(0, (int)Math.Round(beats(n.Start) * g));
            int end = Math.Max(start + 1, (int)Math.Round(beats(n.End) * g));

            // Same pitch overlapping after rounding: the earlier note gives way.
            if (result.Count > 0 && result[^1].Pitch == n.Pitch && result[^1].End > start)
            {
                if (result[^1].Start >= start) continue; // duplicate onset
                result[^1] = result[^1] with { End = start };
            }
            result.Add(new TickNote(start, end, n.Pitch));
        }
        return result;
    }

    static List<Clef> ChooseStaves(List<TickNote> notes, StaffLayout layout)
    {
        if (layout == StaffLayout.Auto)
        {
            if (notes.Count == 0) layout = StaffLayout.Treble;
            else
            {
                double low = notes.Count(n => n.Pitch < 60) / (double)notes.Count;
                if (low >= 0.1 && low <= 0.9) layout = StaffLayout.Grand;
                else
                {
                    var sorted = notes.Select(n => n.Pitch).Order().ToList();
                    layout = sorted[sorted.Count / 2] >= 55 ? StaffLayout.Treble : StaffLayout.Bass;
                }
            }
        }
        return layout switch
        {
            StaffLayout.Grand => [Clef.Treble, Clef.Bass],
            StaffLayout.Bass => [Clef.Bass],
            _ => [Clef.Treble],
        };
    }

    /// <summary>
    /// Grand staff split. Notes that start and end together stay on one staff, chosen by their
    /// average pitch, so an F–A–C chord is not torn across the staves at middle C. Groups wider than
    /// an octave (both hands at once) are split note by note.
    /// </summary>
    static HashSet<TickNote> UpperStaffNotes(List<TickNote> notes)
    {
        var upper = new HashSet<TickNote>();
        foreach (var group in notes.GroupBy(n => (n.Start, n.End)))
        {
            int low = group.Min(n => n.Pitch), high = group.Max(n => n.Pitch);
            bool wide = high - low > 12;
            double average = group.Average(n => n.Pitch);
            foreach (var n in group)
                if (wide ? n.Pitch >= 60 : average >= 60) upper.Add(n);
        }
        return upper;
    }

    /// <summary>
    /// Transcribed notes usually stop a little before the next one starts, which would give tied 16ths and
    /// tiny rests everywhere. Close short gaps (under a quarter of the note and under a beat) up to the next
    /// note in the voice, or to the next beat after the last note.
    /// </summary>
    /// <param name="g">Ticks per beat.</param>
    static void CloseSmallGaps(List<Chord> voice, int g)
    {
        for (int i = 0; i < voice.Count; i++)
        {
            var c = voice[i];
            int boundary = i + 1 < voice.Count ? voice[i + 1].Start : (c.End + g - 1) / g * g;
            int gap = boundary - c.End;
            if (gap > 0 && gap < g && gap <= Math.Max(1, (c.End - c.Start) / 4)) c.End = boundary;
        }
    }

    /// <summary>
    /// Split notes into voices that never overlap. Notes that start and end together become chords, a note
    /// goes to the voice that became free most recently before it, and more overlap opens a new voice.
    /// </summary>
    static List<List<Chord>> AssignVoices(List<TickNote> notes, int maxVoices)
    {
        var voices = new List<List<Chord>>();
        foreach (var n in notes.OrderBy(n => n.Start).ThenByDescending(n => n.End - n.Start).ThenBy(n => n.Pitch))
        {
            // 1. Join a chord with the same start and (nearly) the same length.
            var chord = voices.Select(v => v[^1]).FirstOrDefault(c =>
                c.Start == n.Start && Math.Abs(c.End - n.End) <= Math.Max(1, (c.End - c.Start) / 4));
            if (chord is not null) { chord.Pitches.Add(n.Pitch); continue; }

            // 2. Append to the free voice that became free most recently (fewest rests).
            var free = voices.Where(v => v[^1].End <= n.Start).MaxBy(v => v[^1].End);
            if (free is not null) { free.Add(new Chord(n.Start, n.End, n.Pitch)); continue; }

            // 3. Open a new voice.
            if (voices.Count < maxVoices) { voices.Add([new Chord(n.Start, n.End, n.Pitch)]); continue; }

            // 4. Out of voices: cut short whichever voice frees up first, or join its chord.
            var victim = voices.MinBy(v => v[^1].End)!;
            var last = victim[^1];
            if (last.Start >= n.Start) { last.Pitches.Add(n.Pitch); continue; }
            last.End = n.Start;
            victim.Add(new Chord(n.Start, n.End, n.Pitch));
        }

        // Voice 1 should carry the most material (it is the one that is always written out).
        voices.Sort((a, b) => b.Sum(c => c.End - c.Start).CompareTo(a.Sum(c => c.End - c.Start)));
        return voices;
    }

    static List<IReadOnlyList<ScoreEvent>> LayOut(List<Chord> chords, int measureCount, int measureTicks,
        int g, int beatTicks, int beatsPerBar, IReadOnlyList<MusicalKey> keys, bool primary)
    {
        var measures = Enumerable.Range(0, measureCount).Select(_ => new List<ScoreEvent>()).ToList();

        void Emit(int absStart, int length, Chord? chord)
        {
            int pos = absStart, end = absStart + length;
            bool first = true;
            while (pos < end)
            {
                int m = pos / measureTicks;
                int inMeasure = pos - m * measureTicks;
                int pieceEnd = Math.Min(end, (m + 1) * measureTicks);
                var pitches = chord is null ? [] : chord.Pitches.Order().Select(p => ToPitch(p, keys[m])).ToList(); // each measure's key
                foreach (var (s, d, value) in Split(inMeasure, pieceEnd - pos, g, beatTicks, beatsPerBar))
                {
                    bool last = m * measureTicks + s + d >= end;
                    var kind = chord is not null ? EventKind.Note : primary ? EventKind.Rest : EventKind.Skip;
                    measures[m].Add(new ScoreEvent(kind, s, d, value, pitches,
                        TieFromPrevious: chord is not null && !first, TieToNext: chord is not null && !last));
                    first = false;
                }
                pos = pieceEnd;
            }
        }

        int cursor = 0;
        foreach (var c in chords.OrderBy(c => c.Start))
        {
            int start = Math.Max(c.Start, cursor);
            if (c.End <= start) continue;
            if (start > cursor) Emit(cursor, start - cursor, null);
            Emit(start, c.End - start, c);
            cursor = c.End;
        }
        if (cursor < measureCount * measureTicks) Emit(cursor, measureCount * measureTicks - cursor, null);

        // Tidy each measure: a measure with no notes becomes one whole-measure rest (voice 1) or is
        // omitted (other voices); runs of skips merge into one.
        var result = new List<IReadOnlyList<ScoreEvent>>(measureCount);
        foreach (var events in measures)
        {
            if (events.All(e => e.Kind != EventKind.Note))
            {
                result.Add(primary
                    ? [new ScoreEvent(EventKind.MeasureRest, 0, measureTicks, default, [], false, false)]
                    : []);
                continue;
            }
            var merged = new List<ScoreEvent>();
            foreach (var e in events)
            {
                if (e.Kind == EventKind.Skip && merged.Count > 0 && merged[^1].Kind == EventKind.Skip)
                    merged[^1] = merged[^1] with { Duration = merged[^1].Duration + e.Duration };
                else merged.Add(e);
            }
            result.Add(merged);
        }
        return result;
    }

    static ScorePitch ToPitch(int midi, MusicalKey key)
    {
        var (step, alter, octave) = key.Spell(midi);
        return new ScorePitch(midi, step, alter, octave);
    }

    /// <summary>
    /// Break a span inside one measure into note values that can be written, following the usual rules:
    /// a note that starts off the beat is tied at the next beat, and in 4/4 (or 12/8) nothing that starts
    /// inside the first half crosses the middle of the bar (unless it starts on beat 1).
    /// </summary>
    /// <param name="g">Ticks per quarter note.</param>
    public static IEnumerable<(int Start, int Duration, NoteValue Value)> Split(int start, int length, int g, int beatsPerBar) =>
        Split(start, length, g, g, beatsPerBar);

    /// <param name="g">Ticks per quarter note.</param>
    /// <param name="beatTicks">Ticks per beat: <paramref name="g"/>, or one and a half times it for a dotted-quarter beat.</param>
    public static IEnumerable<(int Start, int Duration, NoteValue Value)> Split(int start, int length, int g, int beatTicks, int beatsPerBar)
    {
        var values = Values(g, beatsPerBar * beatTicks);
        int pos = start, remaining = length;
        while (remaining > 0)
        {
            int limit = remaining;
            if (pos % beatTicks != 0) limit = Math.Min(limit, beatTicks - pos % beatTicks);
            else if (beatsPerBar == 4 && pos > 0 && pos < 2 * beatTicks) limit = Math.Min(limit, 2 * beatTicks - pos);

            var (ticks, value) = values.First(v => v.Ticks <= limit);
            yield return (pos, ticks, value);
            pos += ticks;
            remaining -= ticks;
        }
    }

    static readonly Dictionary<(int, int), List<(int Ticks, NoteValue Value)>> ValueCache = [];

    /// <summary>All note values (with up to two dots) that fit the grid, longest first.</summary>
    static List<(int Ticks, NoteValue Value)> Values(int g, int measureTicks)
    {
        lock (ValueCache)
        {
            if (ValueCache.TryGetValue((g, measureTicks), out var cached)) return cached;
            var list = new List<(int, NoteValue)>();
            (string Type, double Quarters, int Beams)[] bases =
            [
                ("whole", 4, 0), ("half", 2, 0), ("quarter", 1, 0), ("eighth", 0.5, 1), ("16th", 0.25, 2), ("32nd", 0.125, 3),
            ];
            foreach (var (type, quarters, beams) in bases)
            {
                for (int dots = 0; dots <= 2; dots++)
                {
                    double factor = dots switch { 0 => 1, 1 => 1.5, _ => 1.75 };
                    double ticks = quarters * g * factor;
                    if (ticks < 1 || ticks != Math.Floor(ticks) || ticks > measureTicks) continue;
                    list.Add(((int)ticks, new NoteValue(type, dots, beams)));
                }
            }
            list.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            ValueCache[(g, measureTicks)] = list;
            return list;
        }
    }
}
