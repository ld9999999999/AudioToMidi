using AudioToMidi.Core.Audio;
using NWaves.Features;

namespace AudioToMidi.Core.Transcription;

/// <summary>
/// Monophonic transcription that finds the pitch of each frame with YIN, then splits the pitch curve into notes.
/// It suits single melody lines (voice, bass, lead) and is much lighter than basic-pitch.
/// </summary>
public sealed class YinEngine : ITranscriptionEngine
{
    public const int Rate = 22050;
    public const int FrameSize = 2048;
    public const int Hop = 256;

    public string Name => "YIN (monophonic)";
    public int SampleRate => Rate;

    public double MinFrequency { get; init; } = 40;
    public double MaxFrequency { get; init; } = 2000;
    public double YinThreshold { get; init; } = 0.2;

    public Task<IAnalysis> AnalyzeAsync(AudioData audio, IProgress<double>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var x = audio.Samples;
            int frames = Math.Max(0, (x.Length - FrameSize) / Hop + 1);
            var pitch = new float[frames];
            var rmsDb = new float[frames];
            int done = 0;

            Parallel.For(0, frames, new ParallelOptions { CancellationToken = ct }, f =>
            {
                int start = f * Hop;
                double sum = 0;
                for (int i = start; i < start + FrameSize; i++) sum += x[i] * x[i];
                rmsDb[f] = (float)(10 * Math.Log10(sum / FrameSize + 1e-12));
                pitch[f] = Pitch.FromYin(x, audio.SampleRate, start, start + FrameSize,
                    (float)MinFrequency, (float)MaxFrequency, (float)YinThreshold);
                if (Interlocked.Increment(ref done) % 2000 == 0) progress?.Report((double)done / frames);
            });

            progress?.Report(1);
            return (IAnalysis)new YinAnalysis(pitch, rmsDb, (double)Hop / audio.SampleRate,
                (double)FrameSize / 2 / audio.SampleRate, MinFrequency, MaxFrequency);
        }, ct);
}

/// <param name="Pitch">Hz per frame (0 or out of range means no pitch).</param>
/// <param name="RmsDb">Frame loudness in dBFS.</param>
public sealed record YinAnalysis(float[] Pitch, float[] RmsDb, double HopSeconds, double FrameCenterOffset,
    double MinFrequency, double MaxFrequency) : IAnalysis
{
    const int SmoothFrames = 5;       // median filter on the pitch curve to remove brief octave jumps
    const int PitchChangeFrames = 3;  // a new pitch must hold this long before it splits the note
    const float ReattackDb = 6;       // loudness jump that starts a repeated note of the same pitch

    public IReadOnlyList<DetectedNote> Decode(NoteSettings s)
    {
        int n = Pitch.Length;
        var midi = new float[n];
        for (int i = 0; i < n; i++)
        {
            bool voiced = RmsDb[i] >= s.SilenceThresholdDb && Pitch[i] >= MinFrequency && Pitch[i] <= MaxFrequency;
            midi[i] = voiced ? (float)(69 + 12 * Math.Log2(Pitch[i] / 440.0)) : float.NaN;
        }

        // Smooth with a median over pitched neighbours only.
        var smooth = new int[n];
        var buf = new List<float>(SmoothFrames);
        for (int i = 0; i < n; i++)
        {
            if (float.IsNaN(midi[i])) { smooth[i] = -1; continue; }
            buf.Clear();
            for (int j = i - SmoothFrames / 2; j <= i + SmoothFrames / 2; j++)
                if (j >= 0 && j < n && !float.IsNaN(midi[j])) buf.Add(midi[j]);
            buf.Sort();
            smooth[i] = (int)Math.Round(buf[buf.Count / 2]);
        }

        var notes = new List<DetectedNote>();
        int noteStart = -1, notePitch = -1;
        double loudSum = 0;
        int loudCount = 0;

        void Close(int endFrame)
        {
            if (noteStart >= 0 && endFrame > noteStart)
            {
                double meanDb = loudSum / Math.Max(1, loudCount);
                float amp = (float)Math.Clamp((meanDb + 60) / 60, 0.05, 1); // -60 dBFS..0 dBFS -> 0..1
                notes.Add(new DetectedNote(FrameTime(noteStart), FrameTime(endFrame), notePitch, amp));
            }
            noteStart = -1; notePitch = -1; loudSum = 0; loudCount = 0;
        }

        for (int i = 0; i < n; i++)
        {
            int p = smooth[i];
            if (p < 0) { Close(i); continue; }

            if (noteStart < 0) { noteStart = i; notePitch = p; }
            else if (p != notePitch)
            {
                // Switch only if the new pitch holds steady; otherwise treat it as vibrato or jitter.
                int hold = 0;
                while (hold < PitchChangeFrames && i + hold < n && smooth[i + hold] == p) hold++;
                if (hold >= PitchChangeFrames) { Close(i); noteStart = i; notePitch = p; }
            }
            else if (i >= 2 && RmsDb[i] - RmsDb[i - 2] > ReattackDb && i - noteStart > 2)
            {
                Close(i); noteStart = i; notePitch = p; // repeated note
            }

            loudSum += RmsDb[i];
            loudCount++;
        }
        Close(n);

        return notes.Where(e => e.Duration * 1000 >= s.MinNoteMs && e.Pitch >= s.LowestNote && e.Pitch <= s.HighestNote)
            .ToList();
    }

    double FrameTime(int frame) => frame * HopSeconds + FrameCenterOffset;
}
