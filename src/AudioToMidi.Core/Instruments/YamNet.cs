using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioToMidi.Core.Instruments;

/// <param name="Scores">Per frame: 521 AudioSet class probabilities.</param>
/// <param name="Embeddings">Per frame: 1024-dimensional embedding (what the instrument head classifies).</param>
/// <param name="Levels">Per frame: RMS level of the audio under the frame window.</param>
public sealed record YamNetOutput(float[][] Scores, float[][] Embeddings, float[] Levels)
{
    public int Frames => Scores.Length;

    /// <summary>
    /// Average the per-frame vectors, weighted by level, over frames within <paramref name="rangeDb"/> of
    /// the loudest one, so silence and faint bleed do not dilute the result. Return null if all frames are silent.
    /// </summary>
    public float[]? Pool(Func<YamNetOutput, float[][]> select, double rangeDb = 30)
    {
        var vectors = select(this);
        if (Frames == 0) return null;
        float max = Levels.Max();
        if (max <= 1e-5f) return null;
        float floor = max * (float)Math.Pow(10, -rangeDb / 20);

        var sum = new double[vectors[0].Length];
        double total = 0;
        for (int f = 0; f < Frames; f++)
        {
            if (Levels[f] < floor) continue;
            for (int i = 0; i < sum.Length; i++) sum[i] += vectors[f][i] * Levels[f];
            total += Levels[f];
        }
        return sum.Select(v => (float)(v / total)).ToArray();
    }
}

/// <summary>
/// Google's YAMNet audio event classifier (AudioSet, 521 classes, Apache-2.0), ONNX export
/// (andrelgomes/yamnet-onnx). Input is 16 kHz mono of any length; output is one frame per 0.48 s,
/// each covering 0.96 s of audio.
/// </summary>
public sealed class YamNet : IDisposable
{
    public const int SampleRate = 16000;
    public const int HopSamples = 7680;     // 0.48 s
    public const int WindowSamples = 15360; // 0.96 s

    static readonly Lazy<string[]> Names = new(LoadClassNames);
    public static IReadOnlyList<string> ClassNames => Names.Value;

    readonly InferenceSession _session;

    public YamNet()
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(ReadResource("yamnet.onnx"), options);
    }

    public YamNetOutput Run(float[] samples16k)
    {
        // YAMNet needs at least one full window.
        var x = samples16k.Length >= WindowSamples ? samples16k : [.. samples16k, .. new float[WindowSamples - samples16k.Length]];
        var input = new DenseTensor<float>(x, [x.Length]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("waveform", input)], ["output_0", "output_1"]);
        var scores = ToRows(results.First(r => r.Name == "output_0").AsTensor<float>());
        var embeddings = ToRows(results.First(r => r.Name == "output_1").AsTensor<float>());

        var levels = new float[scores.Length];
        for (int f = 0; f < levels.Length; f++)
        {
            int start = f * HopSamples, end = Math.Min(x.Length, start + WindowSamples);
            double sum = 0;
            for (int i = start; i < end; i++) sum += x[i] * x[i];
            levels[f] = (float)Math.Sqrt(sum / Math.Max(1, end - start));
        }
        return new YamNetOutput(scores, embeddings, levels);
    }

    static float[][] ToRows(Tensor<float> t)
    {
        int rows = t.Dimensions[0], cols = t.Dimensions[1];
        var flat = t.ToDenseTensor().Buffer.Span;
        var result = new float[rows][];
        for (int r = 0; r < rows; r++) result[r] = flat.Slice(r * cols, cols).ToArray();
        return result;
    }

    static string[] LoadClassNames()
    {
        // index,mid,display_name   (display_name may be quoted and contain commas)
        using var reader = new StreamReader(new MemoryStream(ReadResource("yamnet_class_map.csv")));
        reader.ReadLine();
        var names = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            int second = line.IndexOf(',', line.IndexOf(',') + 1);
            names.Add(line[(second + 1)..].Trim().Trim('"'));
        }
        return names.ToArray();
    }

    internal static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AudioToMidi.Core.{name}")
                           ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
