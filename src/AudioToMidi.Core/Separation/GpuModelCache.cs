using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AudioToMidi.Core.Separation;

/// <summary>
/// What it takes to run a model on the GPU through CoreML, kept on disk so the work is done once per
/// model file:
/// <list type="bullet">
///   <item>the rewritten graph (<see cref="CoreMlGraph"/>);</item>
///   <item>CoreML's compiled model, which ONNX Runtime writes to <see cref="CoreMlDirectory"/>;</item>
///   <item>the result of the check against the CPU.</item>
/// </list>
/// Building them reads and writes more than a gigabyte, which can take minutes on a busy disk. The cache
/// lives in ~/Library/Caches, which Time Machine skips. A new model file, ONNX Runtime
/// version or macOS version gets a new cache, and the old one for the same file is removed.
/// </summary>
public sealed class GpuModelCache
{
    /// <summary>Raise when <see cref="CoreMlGraph"/>'s rewrites change, so cached graphs are made again.</summary>
    const int RewriteVersion = 1;

    /// <summary>Written to <see cref="CheckFile"/> when the GPU's output matched the CPU's.</summary>
    public const string Passed = "passed";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "AudioToMidi", "coreml");

    GpuModelCache(string modelPath, string directory, string key)
    {
        ModelPath = modelPath;
        Directory = directory;
        Key = key;
    }

    /// <summary>Get the cache for a model file as it is now (its size and date are part of the key).</summary>
    public static GpuModelCache For(string modelPath, string? root = null)
    {
        var file = new FileInfo(modelPath);
        string identity = string.Join("|", file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, RewriteVersion,
            typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version, Environment.OSVersion.Version);
        // CoreML needs the key alphanumeric and at most 64 characters.
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32].ToLowerInvariant();
        return new GpuModelCache(file.FullName, Path.Combine(root ?? DefaultRoot, key), key);
    }

    public string ModelPath { get; }

    public string Directory { get; }

    /// <summary>Also the COREML_CACHE_KEY written into the rewritten graph, which names CoreML's cache.</summary>
    public string Key { get; }

    /// <summary>The rewritten graph.</summary>
    public string GraphFile => Path.Combine(Directory, "graph.onnx");

    /// <summary>For ONNX Runtime's ModelCacheDirectory option.</summary>
    public string CoreMlDirectory => Path.Combine(Directory, "coreml");

    string CheckFile => Path.Combine(Directory, "check.txt");

    string SourceFile => Path.Combine(Directory, "source.txt");

    /// <summary>True when the GPU can start from the cache alone.</summary>
    public bool IsReady => File.Exists(GraphFile) && CheckResult == Passed
        && System.IO.Directory.Exists(CoreMlDirectory) && System.IO.Directory.EnumerateFileSystemEntries(CoreMlDirectory).Any();

    /// <summary>The check's result from an earlier run: <see cref="Passed"/>, why the GPU is not used, or null.</summary>
    public string? CheckResult
    {
        get
        {
            try { return File.Exists(CheckFile) ? File.ReadAllText(CheckFile).Trim() : null; }
            catch (IOException) { return null; }
        }
    }

    public void SaveCheckResult(string result)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomically(CheckFile, Encoding.UTF8.GetBytes(result));
    }

    /// <summary>Get the rewritten graph's path, and make the graph with <paramref name="prepare"/> the first time.</summary>
    public string EnsureGraph(Func<string, byte[]> prepare)
    {
        if (File.Exists(GraphFile)) return GraphFile;
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(SourceFile, ModelPath);
        RemoveStale();
        WriteAtomically(GraphFile, prepare(Key));
        return GraphFile;
    }

    /// <summary>Remove everything cached, as when a stop part-way through left something that will not load.</summary>
    public void Clear()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>
    /// Hold the cache for this process while it is built or checked, so another process waits its turn
    /// before writing the same files.
    /// </summary>
    public IDisposable Lock()
    {
        // Keep the lock file beside the cache, so Clear() leaves it alone.
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Directory)!);
        string path = Directory + ".lock";
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromMinutes(10)) { Thread.Sleep(250); }
        }
    }

    /// <summary>Remove caches made earlier for the same model file (before it changed, or under another ONNX Runtime or macOS).</summary>
    void RemoveStale()
    {
        var root = System.IO.Directory.GetParent(Directory)!;
        foreach (var other in root.EnumerateDirectories())
        {
            if (other.FullName == Directory) continue;
            string source = Path.Combine(other.FullName, "source.txt");
            try
            {
                // A cache without a source file may belong to a process that just started, so leave it alone.
                if (File.Exists(source) && File.ReadAllText(source) is var model && (model == ModelPath || !File.Exists(model)))
                {
                    other.Delete(recursive: true);
                    File.Delete(other.FullName + ".lock");
                }
            }
            catch (IOException) { } // in use by another process: try again next time
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Write to a temporary file and rename it, so a stop part-way through never leaves half a file.</summary>
    static void WriteAtomically(string path, byte[] bytes)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
