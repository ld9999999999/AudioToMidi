using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioToMidi.Core.Separation;

/// <summary>
/// Music source separation with Meta's HT-Demucs (v4), through an ONNX export that has the STFT built
/// into the graph (StemSplitio/htdemucs-onnx, MIT). Input is 44.1 kHz stereo. The song is processed in
/// 7.8 s windows that overlap by a quarter and are cross-faded linearly.
/// </summary>
public sealed class DemucsSeparator : IStemSeparator
{
    public const int SampleRate = 44100;
    public const int SegmentSamples = 343980; // 7.8 s

    int IStemSeparator.SampleRate => SampleRate;

    readonly InferenceSession _session;

    /// <param name="threads">ONNX Runtime threads for this separator; 0 = use all cores.</param>
    public DemucsSeparator(string modelPath, int threads = 0)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (threads > 0) options.IntraOpNumThreads = threads;
        _session = new InferenceSession(modelPath, options);
        // Output is (1, stems, 2, samples); a dynamic stem axis is resolved on the first run.
        int declared = _session.OutputMetadata.First().Value.Dimensions is [_, var n, ..] ? n : -1;
        Stems = declared > 0 ? declared : 0;
    }

    /// <summary>4 or 6. Zero until the first run when the model does not declare it.</summary>
    public int Stems { get; private set; }

    /// <summary>
    /// Find a model file. For the 4-stem model, look at an explicit path, AUDIOTOMIDI_DEMUCS_MODEL, the
    /// download location, a models folder next to the app, or (for development runs) a models folder in a
    /// parent directory. Look for other models in the same places, except the environment variable.
    /// </summary>
    public static string? LocateModel(string? configuredPath = null, StemModel? model = null)
    {
        model ??= StemModel.FourStem;
        IEnumerable<string?> Candidates()
        {
            yield return configuredPath;
            if (model == StemModel.FourStem) yield return Environment.GetEnvironmentVariable("AUDIOTOMIDI_DEMUCS_MODEL");
            yield return model.DefaultPath;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                foreach (var name in model.OtherFileNames.Prepend(model.FileName))
                    yield return Path.Combine(dir.FullName, "models", name);
        }
        return Candidates().FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <summary>Separate a stereo mix (44.1 kHz) into four or six mono stems.</summary>
    public StemSet Separate(float[] left, float[] right, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int total = left.Length;
        int overlap = SegmentSamples / 4;
        int stride = SegmentSamples - overlap;
        int chunks = Math.Max(1, (total + stride - 1) / stride);

        // Linear fade in/out over the overlap, flat in between.
        var window = new float[SegmentSamples];
        for (int i = 0; i < SegmentSamples; i++)
        {
            float w = 1;
            if (i < overlap) w = (float)i / (overlap - 1);
            if (i >= SegmentSamples - overlap) w = (float)(SegmentSamples - 1 - i) / (overlap - 1);
            window[i] = w;
        }

        float[][]? stems = null;
        var weight = new float[total];

        for (int c = 0; c < chunks; c++)
        {
            ct.ThrowIfCancellationRequested();
            int start = c * stride;
            int length = Math.Min(SegmentSamples, total - start);

            var input = new DenseTensor<float>([1, 2, SegmentSamples]); // zero padded past the end
            var buf = input.Buffer.Span;
            left.AsSpan(start, length).CopyTo(buf);
            right.AsSpan(start, length).CopyTo(buf[SegmentSamples..]);

            using var results = _session.Run([NamedOnnxValue.CreateFromTensor("mix", input)]);
            // (1, S, 2, N): drums, bass, other, vocals[, guitar, piano]; each stereo pair is mixed down to mono.
            var tensor = results.First().AsTensor<float>();
            if (stems is null)
            {
                Stems = tensor.Dimensions[1];
                if (Stems is not (4 or 6)) throw new InvalidDataException($"Expected a 4- or 6-stem Demucs model, got {Stems} outputs.");
                stems = new float[Stems][];
                for (int s = 0; s < Stems; s++) stems[s] = new float[total];
            }
            var output = tensor.ToDenseTensor().Buffer.Span;

            for (int s = 0; s < stems.Length; s++)
            {
                var dst = stems[s];
                var l = output.Slice((s * 2) * SegmentSamples, SegmentSamples);
                var r = output.Slice((s * 2 + 1) * SegmentSamples, SegmentSamples);
                for (int i = 0; i < length; i++)
                    dst[start + i] += 0.5f * (l[i] + r[i]) * window[i];
            }
            for (int i = 0; i < length; i++) weight[start + i] += window[i];
            progress?.Report((double)(c + 1) / chunks);
        }

        // The first and last samples have zero window weight; leave them silent to avoid dividing by ~0.
        foreach (var st in stems!)
            for (int i = 0; i < total; i++) st[i] = weight[i] > 1e-3f ? st[i] / weight[i] : 0;
        return stems.Length == 6
            ? new StemSet(stems[0], stems[1], stems[2], stems[3], SampleRate, Guitar: stems[4], Piano: stems[5])
            : new StemSet(stems[0], stems[1], stems[2], stems[3], SampleRate);
    }

    public void Dispose() => _session.Dispose();
}
