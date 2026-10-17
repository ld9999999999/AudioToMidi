using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using AudioToMidi.Core.Dsp;

namespace AudioToMidi.Core.Notation;

/// <summary>Write a <see cref="Score"/> as MusicXML 4.0 (score-partwise), readable by MuseScore, Dorico, Sibelius and Finale.</summary>
public static class MusicXmlWriter
{
    public static void Write(string path, Score score) => Write(path, [score]);

    /// <summary>Write one MusicXML part per score. The parts must have the same number of measures.</summary>
    public static void Write(string path, IReadOnlyList<Score> parts)
    {
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        ToDocument(parts).Save(writer);
    }

    public static XDocument ToDocument(Score score) => ToDocument([score]);

    public static XDocument ToDocument(IReadOnlyList<Score> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("No parts to write.", nameof(parts));
        if (parts.Any(p => p.MeasureCount != parts[0].MeasureCount))
            throw new ArgumentException("All parts must have the same number of measures.", nameof(parts));

        var partList = new XElement("part-list");
        var partElements = new List<XElement>();
        for (int i = 0; i < parts.Count; i++)
        {
            string id = $"P{i + 1}";
            var score = parts[i];
            partList.Add(new XElement("score-part", new XAttribute("id", id),
                new XElement("part-name", score.PartName),
                new XElement("score-instrument", new XAttribute("id", $"{id}-I1"), new XElement("instrument-name", score.PartName)),
                new XElement("midi-instrument", new XAttribute("id", $"{id}-I1"),
                    new XElement("midi-channel", i < 9 ? i + 1 : Math.Min(16, i + 2)), // skip channel 10 (drums)
                    new XElement("midi-program", Math.Clamp(score.MidiProgram, 0, 127) + 1))));
            partElements.Add(Part(score, id, withTempo: i == 0));
        }

        var root = new XElement("score-partwise", new XAttribute("version", "4.0"),
            new XElement("work", new XElement("work-title", parts[0].Title)),
            new XElement("identification",
                new XElement("encoding",
                    new XElement("software", "AudioToMidi"),
                    new XElement("encoding-date", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))),
            partList);
        foreach (var p in partElements) root.Add(p);

        return new XDocument(new XDeclaration("1.0", "UTF-8", "no"),
            new XDocumentType("score-partwise", "-//Recordare//DTD MusicXML 4.0 Partwise//EN",
                "http://www.musicxml.org/dtds/partwise.dtd", null),
            root);
    }

    static XElement Part(Score score, string id, bool withTempo)
    {
        string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        var part = new XElement("part", new XAttribute("id", id));
        for (int m = 0; m < score.MeasureCount; m++)
        {
            var measure = new XElement("measure", new XAttribute("number", m + 1));
            if (m == 0) measure.Add(Attributes(score));
            else if (score.KeyOf(m).Fifths != score.KeyOf(m - 1).Fifths)
                measure.Add(new XElement("attributes", Key(score.KeyOf(m))));
            if (withTempo)
            {
                // A tempo mark counts the meter's beats (dotted quarters in 6/8); <sound> wants quarter notes.
                var tempos = score.Tempos.Count > 0 ? score.Tempos : [new TempoMark(0, Math.Round(score.Bpm))];
                foreach (var mark in tempos.Where(t => t.Measure == m))
                {
                    var metronome = new XElement("metronome", new XElement("beat-unit", "quarter"));
                    if (score.Meter.IsCompound) metronome.Add(new XElement("beat-unit-dot"));
                    metronome.Add(new XElement("per-minute", Num(mark.Bpm)));
                    measure.Add(new XElement("direction", new XAttribute("placement", "above"),
                        new XElement("direction-type", metronome),
                        new XElement("staff", 1),
                        new XElement("sound", new XAttribute("tempo", Num(mark.Bpm * score.Meter.BeatQuarters)))));
                }
                if (m == 0 && score.Swing > Swing.Straight) measure.Add(SwingMark(score.Swing));
            }

            foreach (var h in score.Harmonies.Where(h => h.Measure == m)) measure.Add(Harmony(h));

            bool firstVoice = true;
            foreach (var voice in score.Voices)
            {
                var events = voice.Measures[m];
                if (events.Count == 0) continue;
                if (!firstVoice) measure.Add(new XElement("backup", new XElement("duration", score.MeasureTicks)));
                firstVoice = false;

                var beams = Beams(events, score.BeatTicks);
                int written = 0;
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.Kind == EventKind.Skip)
                    {
                        measure.Add(new XElement("forward", new XElement("duration", e.Duration),
                            new XElement("voice", voice.Number), new XElement("staff", voice.Staff)));
                    }
                    else
                    {
                        foreach (var note in NoteElements(e, voice, beams[i])) measure.Add(note);
                    }
                    written += e.Duration;
                }
                // Pad so every voice spans the whole measure; keeps the backup arithmetic exact.
                if (written < score.MeasureTicks)
                    measure.Add(new XElement("forward", new XElement("duration", score.MeasureTicks - written),
                        new XElement("voice", voice.Number), new XElement("staff", voice.Staff)));
            }

            if (m == score.MeasureCount - 1)
                measure.Add(new XElement("barline", new XAttribute("location", "right"), new XElement("bar-style", "light-heavy")));
            else if (score.KeyOf(m + 1).Fifths != score.KeyOf(m).Fifths) // a double bar before a change of key
                measure.Add(new XElement("barline", new XAttribute("location", "right"), new XElement("bar-style", "light-light")));
            part.Add(measure);
        }

        return part;
    }

    /// <summary>
    /// Write a "Swing" mark over straight eighths, with the long-short ratio for playback (2:1 for a
    /// triplet shuffle; 0.62 of the beat is 31:19).
    /// </summary>
    static XElement SwingMark(double swing)
    {
        int first = (int)Math.Round(swing * 100), second = 100 - first, d = Gcd(first, second);
        return new XElement("direction", new XAttribute("placement", "above"),
            new XElement("direction-type", new XElement("words", "Swing")),
            new XElement("staff", 1),
            new XElement("sound", new XElement("swing",
                new XElement("first", first / d), new XElement("second", second / d), new XElement("swing-type", "eighth"))));
        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }

    /// <summary>A chord symbol, placed by its offset from the start of the measure.</summary>
    static XElement Harmony(ScoreHarmony h)
    {
        var root = new XElement("root", new XElement("root-step", h.RootStep));
        if (h.RootAlter != 0) root.Add(new XElement("root-alter", h.RootAlter));
        var harmony = new XElement("harmony", new XAttribute("print-frame", "no"), root,
            new XElement("kind", new XAttribute("text", h.Text), h.Kind));
        if (h.BassStep is { } step)
        {
            var bass = new XElement("bass", new XElement("bass-step", step));
            if (h.BassAlter != 0) bass.Add(new XElement("bass-alter", h.BassAlter));
            harmony.Add(bass);
        }
        if (h.Offset != 0) harmony.Add(new XElement("offset", h.Offset));
        return harmony;
    }

    static XElement Attributes(Score score)
    {
        var attributes = new XElement("attributes",
            new XElement("divisions", score.Divisions),
            Key(score.KeyOf(0)),
            new XElement("time", new XElement("beats", score.Meter.Beats), new XElement("beat-type", score.Meter.BeatType)));
        if (score.Staves.Count > 1) attributes.Add(new XElement("staves", score.Staves.Count));
        for (int s = 0; s < score.Staves.Count; s++)
        {
            var (sign, line) = score.Staves[s] == Clef.Treble ? ("G", 2) : ("F", 4);
            attributes.Add(new XElement("clef", new XAttribute("number", s + 1), new XElement("sign", sign), new XElement("line", line)));
        }
        return attributes;
    }

    static XElement Key(MusicalKey key) =>
        new("key", new XElement("fifths", key.Fifths), new XElement("mode", key.Minor ? "minor" : "major"));

    static IEnumerable<XElement> NoteElements(ScoreEvent e, ScoreVoice voice, IReadOnlyList<(int Number, string Kind)> beams)
    {
        if (e.Kind is EventKind.Rest or EventKind.MeasureRest)
        {
            var rest = new XElement("note",
                e.Kind == EventKind.MeasureRest ? new XElement("rest", new XAttribute("measure", "yes")) : new XElement("rest"),
                new XElement("duration", e.Duration),
                new XElement("voice", voice.Number));
            if (e.Kind == EventKind.Rest) AddValue(rest, e.Value);
            rest.Add(new XElement("staff", voice.Staff));
            yield return rest;
            yield break;
        }

        for (int i = 0; i < e.Pitches.Count; i++)
        {
            var p = e.Pitches[i];
            var pitch = new XElement("pitch", new XElement("step", p.Step));
            if (p.Alter != 0) pitch.Add(new XElement("alter", p.Alter));
            pitch.Add(new XElement("octave", p.Octave));

            var note = new XElement("note");
            if (i > 0) note.Add(new XElement("chord"));
            note.Add(pitch, new XElement("duration", e.Duration));
            if (e.TieFromPrevious) note.Add(new XElement("tie", new XAttribute("type", "stop")));
            if (e.TieToNext) note.Add(new XElement("tie", new XAttribute("type", "start")));
            note.Add(new XElement("voice", voice.Number));
            AddValue(note, e.Value);
            note.Add(new XElement("staff", voice.Staff));
            if (i == 0)
                foreach (var (number, kind) in beams)
                    note.Add(new XElement("beam", new XAttribute("number", number), kind));
            if (e.TieFromPrevious || e.TieToNext)
            {
                var notations = new XElement("notations");
                if (e.TieFromPrevious) notations.Add(new XElement("tied", new XAttribute("type", "stop")));
                if (e.TieToNext) notations.Add(new XElement("tied", new XAttribute("type", "start")));
                note.Add(notations);
            }
            yield return note;
        }
    }

    static void AddValue(XElement note, NoteValue value)
    {
        note.Add(new XElement("type", value.Type));
        for (int d = 0; d < value.Dots; d++) note.Add(new XElement("dot"));
    }

    /// <summary>
    /// Beam consecutive flagged notes within the same beat (three eighths in compound meters). Secondary
    /// beams (16ths, 32nds) join neighbouring notes that have them; a lone one gets a hook pointing into the group.
    /// </summary>
    static List<IReadOnlyList<(int Number, string Kind)>> Beams(IReadOnlyList<ScoreEvent> events, int beatTicks)
    {
        var result = events.Select(_ => (IReadOnlyList<(int, string)>)[]).ToList();
        int i = 0;
        while (i < events.Count)
        {
            if (!Beamable(events[i])) { i++; continue; }
            int beat = events[i].Start / beatTicks;
            int j = i;
            while (j + 1 < events.Count && Beamable(events[j + 1]) && events[j + 1].Start / beatTicks == beat) j++;

            if (j > i)
            {
                var group = Enumerable.Range(i, j - i + 1).ToList();
                var perNote = group.ToDictionary(k => k, _ => new List<(int, string)>());
                int maxLevel = group.Max(k => events[k].Value.Beams);
                for (int level = 1; level <= maxLevel; level++)
                {
                    int a = 0;
                    while (a < group.Count)
                    {
                        if (events[group[a]].Value.Beams < level) { a++; continue; }
                        int b = a;
                        while (b + 1 < group.Count && events[group[b + 1]].Value.Beams >= level) b++;
                        if (level == 1 || b > a)
                        {
                            for (int k = a; k <= b; k++)
                                perNote[group[k]].Add((level, k == a ? "begin" : k == b ? "end" : "continue"));
                        }
                        else
                        {
                            perNote[group[a]].Add((level, a == 0 ? "forward hook" : "backward hook"));
                        }
                        a = b + 1;
                    }
                }
                foreach (var k in group) result[k] = perNote[k];
            }
            i = j + 1;
        }
        return result;
    }

    static bool Beamable(ScoreEvent e) => e.Kind == EventKind.Note && e.Value.Beams > 0;
}
