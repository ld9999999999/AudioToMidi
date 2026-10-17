using AudioToMidi.Core.Audio;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioToMidi.Core.Transcription;

/// <summary>
/// Polyphonic transcription with Spotify's basic-pitch model (ICASSP 2022), run through ONNX Runtime.
/// As in basic_pitch/inference.py, the audio is cut into overlapping 2 s windows, each window gives
/// per-frame note and onset probabilities for 88 piano keys, and the overlaps are trimmed and stitched.
/// </summary>
public sealed class BasicPitchEngine : ITranscriptionEngine, IDisposable
{
    public const int AudioSampleRate = 22050;
    public const int FftHop = 256;
    public const int AudioNSamples = AudioSampleRate * 2 - FftHop;       // 43844 samples per window
    public const int AnnotNFrames = 172;                                 // output frames per window
    public const int AnnotationsFps = AudioSampleRate / FftHop;          // 86 (integer division, as upstream)
    public const int NOverlappingFrames = 30;
    public const int OverlapLength = NOverlappingFrames * FftHop;        // 7680
    public const int HopSize = AudioNSamples - OverlapLength;            // 36164
    public const int NotePitches = 88;
    /// <summary>Pitch contour bins, three per semitone, starting at A0.</summary>
    public const int ContourBins = 264, ContourBinsPerSemitone = 3;

    const string InputName = "serving_default_input_2:0";
    const string ContourOutput = "StatefulPartitionedCall:0";
    const string NoteOutput = "StatefulPartitionedCall:1";
    const string OnsetOutput = "StatefulPartitionedCall:2";
    const int BatchSize = 8;

    readonly InferenceSession _session;

    public string Name => "basic-pitch (polyphonic)";
    public int SampleRate => AudioSampleRate;

    public BasicPitchEngine()
    {
        using var stream = typeof(BasicPitchEngine).Assembly.GetManifestResourceStream("AudioToMidi.Core.nmp.onnx")
                           ?? throw new InvalidOperationException("Embedded basic-pitch model (nmp.onnx) is missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(ms.ToArray(), options);
    }

    public Task<IAnalysis> AnalyzeAsync(AudioData audio, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (audio.SampleRate != AudioSampleRate)
            throw new ArgumentException($"basic-pitch needs {AudioSampleRate} Hz audio, got {audio.SampleRate} Hz.");
        return Task.Run(() => (IAnalysis)Run(audio.Samples, progress, ct), ct);
    }

    BasicPitchAnalysis Run(float[] audio, IProgress<double>? progress, CancellationToken ct)
    {
        // Pad the start by half an overlap so the first trimmed window lines up with t = 0.
        int padded = OverlapLength / 2 + audio.Length;
        int windows = Math.Max(1, (padded + HopSize - 1) / HopSize);

        int trim = NOverlappingFrames / 2;
        int keptPerWindow = AnnotNFrames - 2 * trim; // 142
        int totalFrames = (int)Math.Floor(audio.Length * ((double)AnnotationsFps / AudioSampleRate));
        var note = new float[totalFrames * NotePitches];
        var onset = new float[totalFrames * NotePitches];
        var contour = new byte[totalFrames * ContourBins]; // 0..1 stored as 0..255, a quarter of the memory of floats

        for (int w0 = 0; w0 < windows; w0 += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int batch = Math.Min(BatchSize, windows - w0);
            var input = new DenseTensor<float>([batch, AudioNSamples, 1]);
            var buffer = input.Buffer.Span;
            for (int b = 0; b < batch; b++)
            {
                int start = (w0 + b) * HopSize - OverlapLength / 2; // in un-padded audio coordinates
                for (int i = 0; i < AudioNSamples; i++)
                {
                    int src = start + i;
                    buffer[b * AudioNSamples + i] = src >= 0 && src < audio.Length ? audio[src] : 0f;
                }
            }

            using var results = _session.Run(
                [NamedOnnxValue.CreateFromTensor(InputName, input)],
                [NoteOutput, OnsetOutput, ContourOutput]);
            var noteOut = results.First(r => r.Name == NoteOutput).AsTensor<float>();
            var onsetOut = results.First(r => r.Name == OnsetOutput).AsTensor<float>();
            var contourOut = results.First(r => r.Name == ContourOutput).AsTensor<float>();

            for (int b = 0; b < batch; b++)
            {
                for (int f = 0; f < keptPerWindow; f++)
                {
                    int dst = (w0 + b) * keptPerWindow + f;
                    if (dst >= totalFrames) break;
                    for (int p = 0; p < NotePitches; p++)
                    {
                        note[dst * NotePitches + p] = noteOut[b, f + trim, p];
                        onset[dst * NotePitches + p] = onsetOut[b, f + trim, p];
                    }
                    for (int k = 0; k < ContourBins; k++)
                        contour[dst * ContourBins + k] = (byte)Math.Clamp(Math.Round(contourOut[b, f + trim, k] * 255), 0, 255);
                }
            }

            progress?.Report((double)(w0 + batch) / windows);
        }

        return new BasicPitchAnalysis(note, onset, totalFrames) { Contour = contour };
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Raw model output for a whole file, with per-frame probabilities for 88 keys (A0 to C8).</summary>
public sealed class BasicPitchAnalysis(float[] note, float[] onset, int frames) : IAnalysis
{
    public float[] Note { get; } = note;
    public float[] Onset { get; } = onset;
    public int Frames { get; } = frames;

    /// <summary>Pitch strength per frame in <see cref="BasicPitchEngine.ContourBins"/> bins of a third of a semitone from A0, scaled to 0..255, or null when not kept.</summary>
    public byte[]? Contour { get; init; }

    public IReadOnlyList<DetectedNote> Decode(NoteSettings s) =>
        BasicPitchDecoder.Decode(Note, Onset, Frames, s.OnsetThreshold, s.FrameThreshold,
            minNoteFrames: (int)Math.Round(s.MinNoteMs / 1000 * BasicPitchEngine.AudioSampleRate / BasicPitchEngine.FftHop),
            melodiaTrick: s.MelodiaTrick, lowestNote: s.LowestNote, highestNote: s.HighestNote,
            contour: s.PitchBends ? Contour : null);
}
