namespace AudioToMidi.Core.Transcription;

/// <summary>
/// A port of basic_pitch/note_creation.py (output_to_notes_polyphonic and its helpers).
/// </summary>
public static class BasicPitchDecoder
{
    const int MidiOffset = 21;
    const int Pitches = BasicPitchEngine.NotePitches;
    const int MaxFreqIdx = Pitches - 1;
    const int EnergyTolerance = 11;

    /// <param name="contour">basic-pitch's pitch contour (see <see cref="BasicPitchAnalysis.Contour"/>). When given, notes whose
    /// pitch clearly moves get <see cref="DetectedNote.Bends"/>.</param>
    public static List<DetectedNote> Decode(float[] frames, float[] onsets, int nFrames,
        double onsetThreshold, double frameThreshold, int minNoteFrames,
        bool inferOnsets = true, bool melodiaTrick = true, int lowestNote = 21, int highestNote = 108, byte[]? contour = null)
    {
        var notes = new List<DetectedNote>();
        if (nFrames < 3) return notes;

        float onsetThresh = (float)onsetThreshold, frameThresh = (float)frameThreshold;
        int minIdx = Math.Clamp(lowestNote - MidiOffset, 0, MaxFreqIdx);
        int maxIdx = Math.Clamp(highestNote - MidiOffset, 0, MaxFreqIdx);

        // constrain_frequency: work on copies with out-of-range pitches set to zero.
        var frame = (float[])frames.Clone();
        var onset = (float[])onsets.Clone();
        for (int t = 0; t < nFrames; t++)
            for (int p = 0; p < Pitches; p++)
                if (p < minIdx || p > maxIdx) { frame[t * Pitches + p] = 0; onset[t * Pitches + p] = 0; }

        if (inferOnsets) onset = InferOnsets(onset, frame, nFrames);

        // Find onset peaks (local maxima over time) above the threshold, and process the latest first.
        var peaks = new List<(int T, int P)>();
        for (int t = 1; t < nFrames - 1; t++)
            for (int p = 0; p < Pitches; p++)
            {
                float v = onset[t * Pitches + p];
                if (v >= onsetThresh && v > onset[(t - 1) * Pitches + p] && v > onset[(t + 1) * Pitches + p])
                    peaks.Add((t, p));
            }
        // Match upstream, which walks np.where's row-major order in reverse: time descending, then pitch descending.
        peaks.Sort((a, b) => a.T != b.T ? b.T.CompareTo(a.T) : b.P.CompareTo(a.P));

        var remaining = (float[])frame.Clone();

        foreach (var (start, p) in peaks)
        {
            if (start >= nFrames - 1) continue;
            int i = start + 1, k = 0;
            while (i < nFrames - 1 && k < EnergyTolerance)
            {
                k = remaining[i * Pitches + p] < frameThresh ? k + 1 : 0;
                i++;
            }
            i -= k;
            if (i - start <= minNoteFrames) continue;

            ClearRange(remaining, start, i, p);
            notes.Add(new DetectedNote(start, i, p + MidiOffset, Mean(frame, start, i, p)));
        }

        if (melodiaTrick)
        {
            // Upstream repeatedly takes the strongest remaining cell. Energy only ever drops to zero, so
            // visiting cells from strongest to weakest by their original value, and skipping cells cleared
            // since, gives the same sequence much faster.
            var candidates = new List<int>();
            for (int idx = 0; idx < remaining.Length; idx++)
                if (remaining[idx] > frameThresh) candidates.Add(idx);
            var snapshot = (float[])remaining.Clone();
            candidates.Sort((a, b) => snapshot[b].CompareTo(snapshot[a]));

            foreach (var idx in candidates)
            {
                if (remaining[idx] != snapshot[idx] || remaining[idx] <= frameThresh) continue;
                int mid = idx / Pitches, p = idx % Pitches;
                remaining[idx] = 0;

                int i = mid + 1, k = 0;
                while (i < nFrames - 1 && k < EnergyTolerance)
                {
                    k = remaining[i * Pitches + p] < frameThresh ? k + 1 : 0;
                    ClearCell(remaining, i, p);
                    i++;
                }
                int end = i - 1 - k;

                i = mid - 1; k = 0;
                while (i > 0 && k < EnergyTolerance)
                {
                    k = remaining[i * Pitches + p] < frameThresh ? k + 1 : 0;
                    ClearCell(remaining, i, p);
                    i--;
                }
                int begin = i + 1 + k;

                if (end - begin <= minNoteFrames) continue;
                notes.Add(new DetectedNote(begin, end, p + MidiOffset, Mean(frame, begin, end, p)));
            }
        }

        if (contour is not null) notes = JoinGlides(notes, onsets, contour, nFrames, onsetThresh);

        // Convert frame indexes to seconds, and add bends from the contour.
        var times = FrameTimes(nFrames + 1);
        for (int n = 0; n < notes.Count; n++)
        {
            var e = notes[n];
            notes[n] = e with
            {
                Start = times[(int)e.Start], End = times[(int)e.End],
                Bends = contour is null ? null : Bends(contour, nFrames, (int)e.Start, (int)e.End, e.Pitch, times),
            };
        }
        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return notes;
    }

    /// <summary>
    /// Join notes that basic-pitch cut out of one bend or slide. The model turns a bent or slid string into
    /// steps (E, F, F♯ for an E bent up a whole tone). A note that starts as the one before it ends (within two
    /// frames), a semitone or two away, continues that note when it has no attack of its own (the model's
    /// onset is below the threshold) or the contour slides between them. It is then joined to the earlier
    /// note, so the bend shows in the contour, up to two semitones from where the note began (the reach of a
    /// MIDI bend).
    /// </summary>
    static List<DetectedNote> JoinGlides(List<DetectedNote> notes, float[] onsets, byte[] contour, int nFrames, float onsetThreshold)
    {
        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        var joined = new bool[sorted.Count];
        var result = new List<DetectedNote>(sorted.Count);
        for (int a = 0; a < sorted.Count; a++)
        {
            if (joined[a]) continue;
            var head = sorted[a];
            var last = head;
            for (bool extended = true; extended;)
            {
                extended = false;
                for (int b = a + 1; b < sorted.Count && sorted[b].Start <= last.End + 2; b++)
                {
                    var next = sorted[b];
                    if (joined[b] || next.Start < last.End - 2) continue;
                    int step = Math.Abs(next.Pitch - last.Pitch);
                    if (step is < 1 or > 2 || Math.Abs(next.Pitch - head.Pitch) > 2) continue;
                    bool unstruck = Attack(onsets, nFrames, (int)next.Start, next.Pitch - MidiOffset) < onsetThreshold;
                    if (!unstruck && !Continuous(contour, nFrames, (int)next.Start, last.Pitch, next.Pitch)) continue;
                    joined[b] = true;
                    last = next;
                    extended = true;
                    break;
                }
            }
            result.Add(last.End > head.End ? head with { End = last.End } : head);
        }
        return result;
    }

    /// <summary>
    /// Check whether the contour's peak slides from one pitch to the other around a frame, never jumping more
    /// than two bins (two thirds of a semitone) between frames, as a bent or slid string does. A newly picked
    /// note jumps.
    /// </summary>
    static bool Continuous(byte[] contour, int nFrames, int t, int from, int to)
    {
        const int Bins = BasicPitchEngine.ContourBins, Per = BasicPitchEngine.ContourBinsPerSemitone;
        int lo = Math.Max(0, (Math.Min(from, to) - MidiOffset) * Per - 3), hi = Math.Min(Bins - 1, (Math.Max(from, to) - MidiOffset) * Per + 3);
        int previous = -1;
        for (int f = Math.Max(0, t - 4); f <= Math.Min(nFrames - 1, t + 4); f++)
        {
            int peak = lo;
            for (int k = lo; k <= hi; k++) if (contour[f * Bins + k] > contour[f * Bins + peak]) peak = k;
            if (contour[f * Bins + peak] < 40) return false; // no pitch to follow
            if (previous >= 0 && Math.Abs(peak - previous) > 2) return false;
            previous = peak;
        }
        return true;
    }

    /// <summary>Return the model's strongest onset for a pitch around a frame.</summary>
    static float Attack(float[] onsets, int nFrames, int t, int p)
    {
        float most = 0;
        for (int f = Math.Max(0, t - 1); f <= Math.Min(nFrames - 1, t + 1); f++) most = Math.Max(most, onsets[f * Pitches + p]);
        return most;
    }

    /// <summary>Contour bins on either side of a note's pitch that a bend can reach (25 is about eight semitones), as in get_pitch_bends.</summary>
    const int BendReach = 25;
    /// <summary>
    /// The smallest bend kept, in contour bins (one semitone). Smaller movements are usually vibrato or the
    /// attack, while real bends are a half or whole step.
    /// </summary>
    const int MinBend = 3;

    /// <summary>
    /// Follow the note's pitch frame by frame, as get_pitch_bends does. Take the contour's peak near the
    /// note's pitch, weighted towards it (a Gaussian of 5 bins), in thirds of a semitone, then smooth it with
    /// a median over 5 frames to remove single-frame jumps (to a chord's other notes, for example). Return
    /// the points where it changes, or null when it never moves by <see cref="MinBend"/> bins.
    /// </summary>
    static List<PitchBend>? Bends(byte[] contour, int nFrames, int from, int to, int pitch, double[] times)
    {
        const int Bins = BasicPitchEngine.ContourBins, PerSemitone = BasicPitchEngine.ContourBinsPerSemitone;
        int centre = (pitch - MidiOffset) * PerSemitone;
        if (to - from < 3 || centre < 0 || centre >= Bins) return null;
        var raw = new int[to - from];
        for (int t = from; t < Math.Min(to, nFrames); t++)
        {
            double best = -1;
            int at = 0;
            for (int k = Math.Max(0, centre - BendReach); k <= Math.Min(Bins - 1, centre + BendReach); k++)
            {
                double d = k - centre, value = contour[t * Bins + k] * Math.Exp(-0.5 * d * d / 25);
                if (value > best) { best = value; at = k - centre; }
            }
            raw[t - from] = at;
        }
        var smooth = new int[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var window = raw[Math.Max(0, i - 2)..Math.Min(raw.Length, i + 3)];
            Array.Sort(window);
            smooth[i] = window[window.Length / 2];
        }
        if (smooth.All(b => Math.Abs(b) < MinBend)) return null;

        var bends = new List<PitchBend>();
        for (int i = 0; i < smooth.Length; i++)
            if (i == 0 || smooth[i] != smooth[i - 1]) bends.Add(new PitchBend(times[from + i], smooth[i] / (float)PerSemitone));
        return bends;
    }

    /// <summary>Add onsets wherever note activation jumps up, scaled to the onset range (get_infered_onsets).</summary>
    static float[] InferOnsets(float[] onsets, float[] frames, int nFrames, int nDiff = 2)
    {
        var diff = new float[frames.Length];
        float maxDiff = 0, maxOnset = 0;
        for (int t = 0; t < nFrames; t++)
            for (int p = 0; p < Pitches; p++)
            {
                int idx = t * Pitches + p;
                maxOnset = Math.Max(maxOnset, onsets[idx]);
                if (t < nDiff) continue;
                float d = float.MaxValue;
                for (int n = 1; n <= nDiff; n++)
                    d = Math.Min(d, frames[idx] - frames[(t - n) * Pitches + p]);
                d = Math.Max(d, 0);
                diff[idx] = d;
                maxDiff = Math.Max(maxDiff, d);
            }

        var result = new float[onsets.Length];
        float scale = maxDiff > 0 ? maxOnset / maxDiff : 0;
        for (int i = 0; i < result.Length; i++)
            result[i] = Math.Max(onsets[i], diff[i] * scale);
        return result;
    }

    static void ClearRange(float[] e, int from, int to, int p)
    {
        for (int t = from; t < to; t++) ClearCell(e, t, p);
    }

    static void ClearCell(float[] e, int t, int p)
    {
        e[t * Pitches + p] = 0;
        if (p < MaxFreqIdx) e[t * Pitches + p + 1] = 0;
        if (p > 0) e[t * Pitches + p - 1] = 0;
    }

    static float Mean(float[] frame, int from, int to, int p)
    {
        if (to <= from) return 0;
        double sum = 0;
        for (int t = from; t < to; t++) sum += frame[t * Pitches + p];
        return (float)(sum / (to - from));
    }

    /// <summary>model_frames_to_time: return frame times, corrected for the drift from stitching windows.</summary>
    public static double[] FrameTimes(int nFrames)
    {
        const double hopSeconds = (double)BasicPitchEngine.FftHop / BasicPitchEngine.AudioSampleRate;
        double windowOffset = hopSeconds * (BasicPitchEngine.AnnotNFrames -
                                            (double)BasicPitchEngine.AudioNSamples / BasicPitchEngine.FftHop) + 0.0018;
        var times = new double[nFrames];
        for (int i = 0; i < nFrames; i++)
            times[i] = i * hopSeconds - windowOffset * Math.Floor((double)i / BasicPitchEngine.AnnotNFrames);
        return times;
    }
}
