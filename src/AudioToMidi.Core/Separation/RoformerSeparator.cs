using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioToMidi.Core.Separation;

/// <summary>
/// Six-stem separation with BS-RoFormer "SW", through an ONNX export whose graph works on spectrograms
/// (elicwhite/bs-roformer-sw-6stem-onnx). The STFT and inverse STFT are done here, matching
/// <c>torch.stft</c>/<c>torch.istft</c> (n_fft 2048, hop 512, periodic Hann window, centred with reflect
/// padding). The graph was traced for 4 s chunks, so the song is processed in chunks that overlap by a
/// quarter and are cross-faded. On macOS it can run on the GPU through CoreML (see <see cref="CoreMlGraph"/>).
/// </summary>
public sealed class RoformerSeparator : IStemSeparator
{
    public const int Rate = 44100;
    public const int ChunkSamples = 176400; // 4 s, fixed by the trace
    public const int FftSize = 2048;
    public const int Hop = 512;
    public const int Bins = FftSize / 2 + 1;
    public const int ChunkFrames = ChunkSamples / Hop + 1; // 345
    const int ModelStems = 6; // bass, drums, other, vocals, guitar, piano

    static readonly float[] Window = Enumerable.Range(0, FftSize)
        .Select(n => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FftSize))).ToArray();

    readonly InferenceSession _session;

    /// <param name="threads">ONNX Runtime threads on the CPU; 0 = use all cores.</param>
    /// <param name="gpu">On macOS, run on the GPU through CoreML after a one-off check against the CPU,
    /// and fall back to the CPU when that is not possible.</param>
    public RoformerSeparator(string modelPath, int threads = 0, bool gpu = false)
    {
        InferenceSession? session = null;
        if (gpu && OperatingSystem.IsMacOS())
        {
            try
            {
                session = OpenOnGpu(modelPath, threads, out var problem);
                Device = session is null ? $"CPU ({problem})" : "GPU";
            }
            catch (Exception e)
            {
                Device = $"CPU (the GPU could not load the model: {e.Message})";
            }
        }
        _session = session ?? OpenOnCpu(modelPath, threads);
    }

    /// <summary>Where the model runs: "CPU" or "GPU", with the reason when the GPU was asked for but not used.</summary>
    public string Device { get; } = "CPU";

    static InferenceSession OpenOnCpu(string modelPath, int threads)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (threads > 0) options.IntraOpNumThreads = threads;
        return new InferenceSession(modelPath, options);
    }

    // GPU checks that failed in this process, per cache key. They are not tried again until the next launch.
    static readonly Dictionary<string, string> FailedGpuChecks = [];

    /// <summary>True when the model can start on the GPU from the cache, without the first-time preparation.</summary>
    public static bool IsReadyOnGpu(string modelPath) => GpuModelCache.For(modelPath).IsReady;

    /// <summary>
    /// Open the model on the GPU, or return null (with the reason) when its output does not match the CPU's
    /// or it is not faster. CoreML is limited to the CPU and GPU: its CPU kernels gave wrong stems for this
    /// model, and the Neural Engine computes in half precision. The rewritten graph, CoreML's compiled model
    /// and the check's result are cached (<see cref="GpuModelCache"/>), so only the first launch does that work.
    /// </summary>
    static InferenceSession? OpenOnGpu(string modelPath, int threads, out string? problem)
    {
        var cache = GpuModelCache.For(modelPath);
        lock (FailedGpuChecks)
            if (FailedGpuChecks.TryGetValue(cache.Key, out problem)) return null;
        // A wrong result does not change between launches; "not faster" might, so only that is tried again.
        if (cache.CheckResult is { } earlier && earlier != GpuModelCache.Passed && earlier != NotFaster)
        {
            problem = earlier;
            return null;
        }

        using var _ = cache.Lock();
        InferenceSession gpu;
        try
        {
            gpu = OpenCoreMl(cache);
        }
        catch (OnnxRuntimeException)
        {
            // Most likely a cache left half-written when the app or the Mac stopped part-way: build it again.
            cache.Clear();
            gpu = OpenCoreMl(cache);
        }
        if (cache.CheckResult == GpuModelCache.Passed)
        {
            problem = null;
            return gpu;
        }
        try
        {
            problem = CheckAgainstCpu(gpu, modelPath, threads);
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
        if (problem != NotFaster) cache.SaveCheckResult(problem ?? GpuModelCache.Passed);
        if (problem is null) return gpu;
        lock (FailedGpuChecks) FailedGpuChecks[cache.Key] = problem;
        gpu.Dispose();
        return null;
    }

    const string NotFaster = "the GPU was not faster";

    /// <summary>A CoreML session on the cached rewritten graph, with CoreML's compiled model cached beside it.</summary>
    static InferenceSession OpenCoreMl(GpuModelCache cache)
    {
        string graph = cache.EnsureGraph(key => CoreMlGraph.Prepare(cache.ModelPath, key));
        // Errors only: CoreML reports at warning level that ONNX Runtime keeps a few shape ops itself.
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        // Fusing MatMul+Add into Gemm makes CoreML embed transposed weights as text, which is very slow to load.
        options.AddSessionConfigEntry("optimization.disable_specified_optimizers", "MatMulAddFusion");
        options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
        {
            ["ModelFormat"] = "MLProgram",
            ["MLComputeUnits"] = "CPUAndGPU",
            ["RequireStaticInputShapes"] = "1",
            ["ModelCacheDirectory"] = cache.CoreMlDirectory,
        });
        return new InferenceSession(graph, options);
    }

    /// <summary>Run a test chunk on both, and return null when the GPU agrees with the CPU and is faster.</summary>
    static string? CheckAgainstCpu(InferenceSession gpu, string modelPath, int threads)
    {
        var (re, im) = Spectrogram(TestSignal(0), TestSignal(1));
        using var cpu = OpenOnCpu(modelPath, threads);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var expected = Run(cpu, re, im);
        var cpuTime = clock.Elapsed;
        Run(gpu, re, im); // the first run sets things up
        clock.Restart();
        var actual = Run(gpu, re, im);
        var gpuTime = clock.Elapsed;

        double maxDiff = 0, maxRef = 0;
        for (int i = 0; i < expected.Re.Length; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(actual.Re[i] - expected.Re[i]), Math.Abs(actual.Im[i] - expected.Im[i])));
            maxRef = Math.Max(maxRef, Math.Max(Math.Abs(expected.Re[i]), Math.Abs(expected.Im[i])));
        }
        if (!(maxDiff <= 1e-2 * maxRef)) return "the GPU's output did not match the CPU's";
        if (gpuTime > cpuTime * 0.8) return NotFaster;
        return null;
    }

    /// <summary>4 s of chords with a little noise, as test input.</summary>
    static float[] TestSignal(int channel)
    {
        var x = new float[ChunkSamples];
        var rng = new Random(17 + channel);
        double[] notes = [110, 220, 277.2, 329.6, 440, 659.3, 880, 1760];
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Rate, env = Math.Exp(-2 * (t % 1));
            double v = 0;
            foreach (var f in notes) v += Math.Sin(2 * Math.PI * f * (1 + 0.002 * channel) * t) / notes.Length;
            x[i] = (float)(0.5 * env * v + 0.02 * (rng.NextDouble() - 0.5));
        }
        return x;
    }

    public int SampleRate => Rate;

    public StemSet Separate(float[] left, float[] right, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int total = left.Length;
        int overlap = ChunkSamples / 4;
        int stride = ChunkSamples - overlap;
        int chunks = Math.Max(1, (int)Math.Ceiling((double)Math.Max(0, total - overlap) / stride));

        var stems = new float[ModelStems][];
        for (int s = 0; s < ModelStems; s++) stems[s] = new float[total];
        var weight = new float[total];

        (float[] Re, float[] Im) SpectrogramOf(int c)
        {
            int start = c * stride, length = Math.Min(ChunkSamples, total - start);
            var l = new float[ChunkSamples];
            var r = new float[ChunkSamples];
            left.AsSpan(start, length).CopyTo(l);
            right.AsSpan(start, length).CopyTo(r);
            return Spectrogram(l, r);
        }

        // Fade in and out over the overlap, except at the very start and end of the song.
        void AddToStems(int c, float[] outRe, float[] outIm)
        {
            int start = c * stride, length = Math.Min(ChunkSamples, total - start);
            Parallel.For(0, ModelStems, s =>
            {
                var mono = MonoStem(outRe, outIm, s);
                var dst = stems[s];
                for (int i = 0; i < length; i++) dst[start + i] += mono[i] * Fade(i, c, chunks, overlap);
            });
            for (int i = 0; i < length; i++) weight[start + i] += Fade(i, c, chunks, overlap);
        }

        // Run the model on one chunk while the next one's spectrogram and the previous one's audio are
        // worked out alongside, so the transforms do not hold up the model.
        var next = Task.Run(() => SpectrogramOf(0), ct);
        var adding = Task.CompletedTask;
        for (int c = 0; c < chunks; c++)
        {
            ct.ThrowIfCancellationRequested();
            var (specRe, specIm) = next.GetAwaiter().GetResult();
            int following = c + 1;
            if (following < chunks) next = Task.Run(() => SpectrogramOf(following), ct);
            var (outRe, outIm) = Run(specRe, specIm);
            adding.GetAwaiter().GetResult(); // chunks overlap, so add them one at a time
            int chunk = c;
            adding = Task.Run(() =>
            {
                AddToStems(chunk, outRe, outIm);
                progress?.Report((double)(chunk + 1) / chunks);
            }, ct);
        }
        adding.GetAwaiter().GetResult();

        foreach (var st in stems)
            for (int i = 0; i < total; i++) st[i] = weight[i] > 1e-6f ? st[i] / weight[i] : 0;
        return new StemSet(Drums: stems[1], Bass: stems[0], Other: stems[2], Vocals: stems[3], Rate,
            Guitar: stems[4], Piano: stems[5]);
    }

    static float Fade(int i, int chunk, int chunks, int overlap)
    {
        float w = 1;
        if (chunk > 0 && i < overlap) w = (i + 1f) / (overlap + 1f);
        if (chunk < chunks - 1 && i >= ChunkSamples - overlap) w = Math.Min(w, (ChunkSamples - i) / (overlap + 1f));
        return w;
    }

    /// <summary>Run the graph: [1, 2, Bins, T] spectrogram in, [1, 6, 2, Bins, T] masked spectrograms out.</summary>
    public (float[] Re, float[] Im) Run(float[] specRe, float[] specIm) => Run(_session, specRe, specIm);

    static (float[] Re, float[] Im) Run(InferenceSession session, float[] specRe, float[] specIm)
    {
        var shape = new[] { 1, 2, Bins, ChunkFrames };
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("spec_real", new DenseTensor<float>(specRe, shape)),
            NamedOnnxValue.CreateFromTensor("spec_imag", new DenseTensor<float>(specIm, shape)),
        };
        using var results = session.Run(inputs);
        var re = results.First(x => x.Name == "out_spec_real").AsTensor<float>().ToArray();
        var im = results.First(x => x.Name == "out_spec_imag").AsTensor<float>().ToArray();
        return (re, im);
    }

    /// <summary>Turn one stem's two channels back into audio, averaged to mono.</summary>
    static float[] MonoStem(float[] re, float[] im, int stem)
    {
        int plane = Bins * ChunkFrames;
        var a = InverseSpectrogram(re.AsSpan((stem * 2) * plane, plane), im.AsSpan((stem * 2) * plane, plane), ChunkSamples);
        var b = InverseSpectrogram(re.AsSpan((stem * 2 + 1) * plane, plane), im.AsSpan((stem * 2 + 1) * plane, plane), ChunkSamples);
        for (int i = 0; i < a.Length; i++) a[i] = 0.5f * (a[i] + b[i]);
        return a;
    }

    /// <summary>Compute both channels' STFT in the graph's layout [channel, bin, frame].</summary>
    public static (float[] Re, float[] Im) Spectrogram(float[] left, float[] right)
    {
        int plane = Bins * ChunkFrames;
        var re = new float[2 * plane];
        var im = new float[2 * plane];
        Parallel.Invoke(
            () => Stft(left, re.AsSpan(0, plane), im.AsSpan(0, plane)),
            () => Stft(right, re.AsSpan(plane, plane), im.AsSpan(plane, plane)));
        return (re, im);
    }

    /// <summary>
    /// <c>torch.stft(x, 2048, 512, window=hann, center=True, pad_mode="reflect")</c>, one-sided, written
    /// bin-major: [bin * frames + frame]. Frames = x.Length / Hop + 1.
    /// </summary>
    public static void Stft(float[] x, Span<float> re, Span<float> im)
    {
        int frames = x.Length / Hop + 1;
        int pad = FftSize / 2;
        var buffer = new Complex[FftSize];
        for (int t = 0; t < frames; t++)
        {
            for (int n = 0; n < FftSize; n++)
            {
                int j = t * Hop + n - pad;
                if (j < 0) j = -j;                         // reflect, edge sample not repeated
                if (j >= x.Length) j = 2 * (x.Length - 1) - j;
                buffer[n] = new Complex(x[Math.Clamp(j, 0, x.Length - 1)] * Window[n], 0);
            }
            Fft(buffer, inverse: false);
            for (int k = 0; k < Bins; k++)
            {
                re[k * frames + t] = (float)buffer[k].Real;
                im[k * frames + t] = (float)buffer[k].Imaginary;
            }
        }
    }

    /// <summary><c>torch.istft(..., center=True, length)</c> for a one-sided, bin-major spectrogram.</summary>
    public static float[] InverseSpectrogram(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int length)
    {
        int frames = re.Length / Bins;
        int pad = FftSize / 2;
        int span = FftSize + Hop * (frames - 1);
        var sum = new double[span];
        var envelope = new double[span];
        var buffer = new Complex[FftSize];
        for (int t = 0; t < frames; t++)
        {
            for (int k = 0; k < Bins; k++) buffer[k] = new Complex(re[k * frames + t], im[k * frames + t]);
            for (int k = Bins; k < FftSize; k++) buffer[k] = Complex.Conjugate(buffer[FftSize - k]); // Hermitian
            buffer[0] = new Complex(buffer[0].Real, 0);
            buffer[FftSize / 2] = new Complex(buffer[FftSize / 2].Real, 0);
            Fft(buffer, inverse: true);
            int at = t * Hop;
            for (int n = 0; n < FftSize; n++)
            {
                sum[at + n] += buffer[n].Real / FftSize * Window[n];
                envelope[at + n] += Window[n] * Window[n];
            }
        }
        var y = new float[length];
        for (int i = 0; i < length; i++)
        {
            int j = i + pad;
            if (j < span && envelope[j] > 1e-11) y[i] = (float)(sum[j] / envelope[j]);
        }
        return y;
    }

    /// <summary>In-place radix-2 FFT (no scaling either way).</summary>
    static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= step;
                }
            }
        }
    }

    public void Dispose() => _session.Dispose();
}
