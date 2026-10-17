using AudioToMidi.Core.Dsp;
using AudioToMidi.Core.Harmony;
using AudioToMidi.Core.Transcription;

namespace AudioToMidi.Core.Notation;

/// <summary>
/// Guess which beat starts the bar. Chords change on downbeats more than anywhere else, and bass notes
/// and long notes tend to start there, so each beat position in the bar is scored by the chord changes
/// and the notes that fall on it.
/// </summary>
public static class DownbeatEstimator
{
    /// <summary>Weight of the chord changes against the notes. Chord changes are the stronger clue.</summary>
    const double ChordWeight = 2;

    /// <returns>0-based index of the detected beat that is beat 1 (in 0..beatsPerBar-1).</returns>
    public static int Estimate(IReadOnlyList<DetectedNote> notes, TempoEstimate tempo, int beatsPerBar)
    {
        if (beatsPerBar <= 1 || notes.Count == 0) return 0;
        var map = tempo.Map;
        double beat = map.BeatSeconds;
        int Position(long b) => (int)(((b % beatsPerBar) + beatsPerBar) % beatsPerBar);

        var onsets = new double[beatsPerBar];
        foreach (var n in notes)
        {
            double beats = map.ToBeat(n.Start);
            long nearest = (long)Math.Round(beats);
            if (Math.Abs(beats - nearest) > 0.2) continue; // off-beat notes say nothing about the bar
            onsets[Position(nearest)] += Math.Min(n.Duration / beat, 4) * (0.5 + n.Amplitude) * (n.Pitch < 55 ? 2 : 1);
        }

        // Where chords change, weighted by how long the new chord lasts (up to a bar).
        var changes = new double[beatsPerBar];
        var chords = ChordRecognizer.Recognize(notes, new BarGrid(map, 1, 0));
        for (int i = 1; i < chords.Count; i++)
        {
            if (chords[i].Chord is null || chords[i - 1].Chord is null) continue;
            double start = map.ToBeat(chords[i].Start);
            changes[Position((long)Math.Round(start))] += Math.Min(map.ToBeat(chords[i].End) - start, beatsPerBar);
        }

        double onsetTotal = onsets.Sum(), changeTotal = changes.Sum();
        var scores = new double[beatsPerBar];
        for (int p = 0; p < beatsPerBar; p++)
            scores[p] = (onsetTotal > 0 ? onsets[p] / onsetTotal : 0) + ChordWeight * (changeTotal > 0 ? changes[p] / changeTotal : 0);

        int best = 0;
        for (int i = 1; i < beatsPerBar; i++)
            if (scores[i] > scores[best] * 1.0001) best = i;
        return best;
    }
}
