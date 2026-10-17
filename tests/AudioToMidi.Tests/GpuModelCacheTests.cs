using AudioToMidi.Core.Separation;

namespace AudioToMidi.Tests;

/// <summary>The GPU model cache: made once per model file, made again when the file changes.</summary>
public sealed class GpuModelCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"a2m-cache-test-{Guid.NewGuid():N}");
    string Root => Path.Combine(_dir, "cache");

    public GpuModelCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Model(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_graph_is_prepared_once_and_then_read_from_the_cache()
    {
        var model = Model("m.onnx", "model");
        int prepared = 0;
        byte[] Prepare(string key) { prepared++; return [1, 2, 3]; }

        var cache = GpuModelCache.For(model, Root);
        Assert.Matches("^[0-9a-f]{32}$", cache.Key); // what CoreML takes as a cache key
        Assert.Equal(cache.GraphFile, cache.EnsureGraph(Prepare));
        Assert.Equal(cache.GraphFile, GpuModelCache.For(model, Root).EnsureGraph(Prepare));
        Assert.Equal(1, prepared);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(cache.GraphFile));
        Assert.Empty(Directory.GetFiles(cache.Directory, "*.tmp"));
        Assert.False(cache.IsReady); // not checked, and nothing from CoreML yet
    }

    [Fact]
    public void A_changed_model_file_gets_a_new_cache_and_the_old_one_goes()
    {
        var model = Model("m.onnx", "model");
        var other = Model("other.onnx", "other");
        var first = GpuModelCache.For(model, Root);
        first.EnsureGraph(_ => [1]);
        first.SaveCheckResult(GpuModelCache.Passed);
        GpuModelCache.For(other, Root).EnsureGraph(_ => [2]);

        File.WriteAllText(model, "a new version of the model");
        var second = GpuModelCache.For(model, Root);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Null(second.CheckResult);
        second.EnsureGraph(_ => [3]);
        Assert.False(Directory.Exists(first.Directory));
        Assert.True(File.Exists(GpuModelCache.For(other, Root).GraphFile)); // another model's cache stays
    }

    [Fact]
    public void Clearing_removes_the_check_and_the_graph()
    {
        var cache = GpuModelCache.For(Model("m.onnx", "model"), Root);
        using (cache.Lock())
        {
            cache.EnsureGraph(_ => [1]);
            cache.SaveCheckResult(GpuModelCache.Passed);
            Assert.Equal(GpuModelCache.Passed, cache.CheckResult);
            cache.Clear();
        }
        Assert.Null(cache.CheckResult);
        Assert.False(File.Exists(cache.GraphFile));
    }
}
