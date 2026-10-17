using System.IO.Compression;
using System.Text;
using AudioToMidi.Core.Separation;
using Xunit.Abstractions;

namespace AudioToMidi.Tests;

/// <summary>
/// Check the BS-RoFormer separator against the PyTorch reference output saved next to the model
/// (bs_roformer_sw_6stem_reference.npz). Skipped when either is missing.
/// </summary>
public class RoformerTests(ITestOutputHelper output)
{
    static string ReferencePath => Path.Combine(Path.GetDirectoryName(StemModel.Roformer.DefaultPath)!, "bs_roformer_sw_6stem_reference.npz");

    [Fact]
    public void Stft_model_and_inverse_match_pytorch()
    {
        var model = DemucsSeparator.LocateModel(null, StemModel.Roformer);
        if (model is null || !File.Exists(ReferencePath)) { output.WriteLine("BS-RoFormer model or reference not installed; skipping."); return; }
        var npz = Npz.Read(ReferencePath);
        var audio = npz["input_audio"];          // 2 x 176400
        int n = RoformerSeparator.ChunkSamples;
        var left = audio.Data[..n];
        var right = audio.Data[n..(2 * n)];

        var (re, im) = RoformerSeparator.Spectrogram(left, right);
        Compare("STFT real", re, npz["spec_real"].Data, 1e-3);
        Compare("STFT imag", im, npz["spec_imag"].Data, 1e-3);

        using var separator = new RoformerSeparator(model);
        var (outRe, outIm) = separator.Run(npz["spec_real"].Data, npz["spec_imag"].Data);
        Compare("model real", outRe, npz["out_spec_real"].Data, 2e-2);
        Compare("model imag", outIm, npz["out_spec_imag"].Data, 2e-2);

        // Inverse STFT of the reference output against the reference audio, every stem and channel.
        int plane = RoformerSeparator.Bins * RoformerSeparator.ChunkFrames;
        var all = new float[12 * n];
        for (int k = 0; k < 12; k++)
            RoformerSeparator.InverseSpectrogram(npz["out_spec_real"].Data.AsSpan(k * plane, plane), npz["out_spec_imag"].Data.AsSpan(k * plane, plane), n)
                .CopyTo(all, k * n);
        Compare("inverse STFT", all, npz["ref_audio"].Data, 1e-3);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        separator.Run(npz["spec_real"].Data, npz["spec_imag"].Data);
        output.WriteLine($"one 4 s chunk: {timer.Elapsed.TotalSeconds:F2} s");
    }

    [Fact]
    public void Model_on_the_gpu_matches_pytorch()
    {
        var model = DemucsSeparator.LocateModel(null, StemModel.Roformer);
        if (model is null || !File.Exists(ReferencePath) || !OperatingSystem.IsMacOS()) { output.WriteLine("Needs macOS with the BS-RoFormer model and reference; skipping."); return; }
        var npz = Npz.Read(ReferencePath);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (var first = new RoformerSeparator(model, gpu: true))
        {
            output.WriteLine($"loaded and checked on {first.Device} in {timer.Elapsed.TotalSeconds:F1} s");
            Assert.Equal("GPU", first.Device);
        }
        // Later loads start from the cache and skip the rewrite, the CoreML compile and the CPU check.
        Assert.True(RoformerSeparator.IsReadyOnGpu(model));
        timer.Restart();
        using var separator = new RoformerSeparator(model, gpu: true);
        output.WriteLine($"loaded again from the cache in {timer.Elapsed.TotalSeconds:F1} s");
        Assert.Equal("GPU", separator.Device);

        var (outRe, outIm) = separator.Run(npz["spec_real"].Data, npz["spec_imag"].Data);
        Compare("model real", outRe, npz["out_spec_real"].Data, 2e-2);
        Compare("model imag", outIm, npz["out_spec_imag"].Data, 2e-2);
        timer.Restart();
        separator.Run(npz["spec_real"].Data, npz["spec_imag"].Data);
        output.WriteLine($"one 4 s chunk: {timer.Elapsed.TotalSeconds:F2} s");
    }

    void Compare(string what, float[] actual, float[] expected, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        double maxDiff = 0, maxRef = 1e-9;
        for (int i = 0; i < actual.Length; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(actual[i] - expected[i]));
            maxRef = Math.Max(maxRef, Math.Abs(expected[i]));
        }
        output.WriteLine($"{what}: max diff {maxDiff:E2} of max value {maxRef:E2} ({maxDiff / maxRef:E2} relative)");
        Assert.True(maxDiff / maxRef < tolerance, $"{what} differs: {maxDiff / maxRef:E2} relative");
    }
}

/// <summary>Minimal .npz reader: little-endian float32/float64/int64 arrays in C order.</summary>
static class Npz
{
    public sealed record Array(int[] Shape, float[] Data);

    public static Dictionary<string, Array> Read(string path)
    {
        var result = new Dictionary<string, Array>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            result[Path.GetFileNameWithoutExtension(entry.Name)] = Parse(ms.ToArray());
        }
        return result;
    }

    static Array Parse(byte[] b)
    {
        int major = b[6];
        int headerLength = major == 1 ? BitConverter.ToUInt16(b, 8) : BitConverter.ToInt32(b, 8);
        int start = (major == 1 ? 10 : 12) + headerLength;
        var header = Encoding.ASCII.GetString(b, major == 1 ? 10 : 12, headerLength);
        string descr = header.Split("'descr':")[1].Split('\'')[1];
        var shapeText = header.Split("'shape':")[1].Split('(')[1].Split(')')[0];
        var shape = shapeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        int count = shape.Aggregate(1, (a, x) => a * x);
        var data = new float[count];
        for (int i = 0; i < count; i++)
            data[i] = descr switch
            {
                "<f4" => BitConverter.ToSingle(b, start + 4 * i),
                "<f8" => (float)BitConverter.ToDouble(b, start + 8 * i),
                "<i8" => BitConverter.ToInt64(b, start + 8 * i),
                _ => throw new NotSupportedException(descr),
            };
        return new Array(shape, data);
    }
}
