namespace AudioToMidi.Core.Separation;

/// <summary>Mono stems at <see cref="SampleRate"/>. <see cref="Guitar"/> and <see cref="Piano"/> only from a 6-stem model.</summary>
public sealed record StemSet(float[] Drums, float[] Bass, float[] Other, float[] Vocals, int SampleRate,
    float[]? Guitar = null, float[]? Piano = null)
{
    public bool HasGuitarAndPiano => Guitar is not null && Piano is not null;
}

public enum StemArchitecture { HtDemucs, BsRoformer }

/// <summary>A downloadable instrument separation model.</summary>
/// <param name="Stems">4: drums, bass, other, vocals. 6: also guitar and piano (other then keeps less).</param>
public sealed record StemModel(string Name, string FileName, string DownloadUrl, long DownloadBytes, int Stems,
    IReadOnlyList<string> OtherFileNames, StemArchitecture Architecture = StemArchitecture.HtDemucs)
{
    /// <summary>HT-Demucs (Meta), ONNX export by StemSplitio. Code MIT; the weights are for personal and research use (models/LICENSES.md).</summary>
    public static readonly StemModel FourStem = new("4 stems", "htdemucs_fp16weights.onnx",
        "https://huggingface.co/StemSplitio/htdemucs-onnx/resolve/main/htdemucs_fp16weights.onnx", 165_612_636, 4,
        ["htdemucs.onnx"]);

    /// <summary>HT-Demucs 6-source (Meta), ONNX export by StemSplitio. Code MIT; the weights are for personal and research use.</summary>
    public static readonly StemModel SixStem = new("6 stems", "htdemucs_6s_fp16weights.onnx",
        "https://huggingface.co/StemSplitio/htdemucs-6s-onnx/resolve/main/htdemucs_6s_fp16weights.onnx", 136_428_532, 6,
        ["htdemucs_6s.onnx"]);

    /// <summary>
    /// BS-RoFormer "SW" 6-stem, ONNX export by elicwhite (code MIT). The weights have no licence: users may
    /// download them for their own experiments, but they must never be distributed (models/LICENSES.md).
    /// </summary>
    public static readonly StemModel Roformer = new("6 stems, BS-RoFormer", "bs_roformer_sw_6stem_fp16.onnx",
        "https://huggingface.co/elicwhite/bs-roformer-sw-6stem-onnx/resolve/main/bs_roformer_sw_6stem_fp16.onnx", 352_778_874, 6,
        ["bs_roformer_sw_6stem_fp32.onnx"], StemArchitecture.BsRoformer);

    public static readonly IReadOnlyList<StemModel> All = [FourStem, SixStem, Roformer];

    /// <summary>
    /// Pick the model for a number of stems. For 6, use BS-RoFormer when it is preferred and installed
    /// (it separates better but is slower), otherwise HT-Demucs.
    /// </summary>
    public static StemModel Choose(int stems, bool preferRoformer) =>
        stems != 6 ? FourStem
        : preferRoformer && DemucsSeparator.LocateModel(null, Roformer) is not null ? Roformer
        : SixStem;

    /// <summary>Where the app downloads this model to.</summary>
    public string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioToMidi", "models", FileName);

    /// <summary>Open the model file with the separator for its architecture.</summary>
    /// <param name="threads">ONNX Runtime threads; 0 = use all cores.</param>
    /// <param name="gpu">Run on the GPU where the model supports it (BS-RoFormer on macOS).</param>
    public IStemSeparator Open(string path, int threads = 0, bool gpu = false) => Architecture switch
    {
        StemArchitecture.BsRoformer => new RoformerSeparator(path, threads, gpu),
        _ => new DemucsSeparator(path, threads),
    };
}

/// <summary>Split a stereo mix into instrument stems.</summary>
public interface IStemSeparator : IDisposable
{
    /// <summary>Sample rate the separator takes and returns.</summary>
    int SampleRate { get; }

    /// <summary>Separate a stereo mix at <see cref="SampleRate"/> into mono stems.</summary>
    StemSet Separate(float[] left, float[] right, IProgress<double>? progress = null, CancellationToken ct = default);
}
