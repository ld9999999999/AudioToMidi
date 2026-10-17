using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AudioToMidi.Core.Separation;

namespace AudioToMidi.Tests;

public sealed class ModelInstallerTests : IDisposable
{
    static readonly StemModel Model = new("test model", "test_model.onnx", "http://127.0.0.1:1/unused", 0, 4, ["test_model_fp32.onnx"]);
    readonly string _dir = Directory.CreateTempSubdirectory("a2m-installer-").FullName;
    readonly byte[] _content = RandomNumberGenerator.GetBytes(3_000_000);

    string Destination => Path.Combine(_dir, "installed", Model.FileName);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A model archive: models/ and SHA256SUMS.</summary>
    string Archive(byte[] content, string? checksum = null)
    {
        var path = Path.Combine(_dir, checksum is null ? "models.zip" : "damaged.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var s = zip.CreateEntry("models/" + Model.FileName).Open()) s.Write(content);
        using (var w = new StreamWriter(zip.CreateEntry("SHA256SUMS").Open()))
            w.Write($"{checksum ?? Convert.ToHexStringLower(SHA256.HashData(content))}  {Model.FileName}\n");
        return path;
    }

    async Task AssertInstalls(string source)
    {
        await ModelInstaller.InstallAsync(Model, source, Destination);
        Assert.Equal(_content, File.ReadAllBytes(Destination));
        Assert.False(File.Exists(Destination + ".part"));
        File.Delete(Destination);
    }

    [Fact]
    public async Task Installs_from_a_model_file_by_path_or_file_url()
    {
        var file = Write(Model.FileName, _content);
        await AssertInstalls(file);
        await AssertInstalls(new Uri(file).AbsoluteUri);
    }

    [Fact]
    public async Task Installs_from_a_folder_or_its_models_subfolder()
    {
        Write(Path.Combine("unzipped", "models", Model.FileName), _content);
        await AssertInstalls(Path.Combine(_dir, "unzipped"));
        Write(Path.Combine("flat", Model.FileName), _content);
        await AssertInstalls(Path.Combine(_dir, "flat"));
    }

    [Fact]
    public async Task Installs_from_an_archive_and_checks_its_checksum()
    {
        await AssertInstalls(Archive(_content));

        var damaged = Archive(_content, checksum: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelInstaller.InstallAsync(Model, damaged, Destination));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task Refuses_a_model_file_with_another_name()
    {
        var other = Write("htdemucs_6s_fp16weights.onnx", _content);
        await Assert.ThrowsAsync<ArgumentException>(() => ModelInstaller.InstallAsync(Model, other, Destination));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ModelInstaller.InstallAsync(Model, Path.Combine(_dir, "missing"), Destination));
    }

    [Fact]
    public async Task Downloads_from_a_folder_url_a_file_url_or_an_archive_url()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["/models/" + Model.FileName] = _content,
            ["/models.zip"] = File.ReadAllBytes(Archive(_content)),
        };
        int port = FreePort();
        using var server = new HttpListener { Prefixes = { $"http://127.0.0.1:{port}/" } };
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext context;
                try { context = await server.GetContextAsync(); }
                catch (Exception) { return; }
                if (files.TryGetValue(context.Request.Url!.AbsolutePath, out var body))
                {
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                }
                else context.Response.StatusCode = 404;
                context.Response.Close();
            }
        });

        string root = $"http://127.0.0.1:{port}";
        Assert.Equal($"{root}/models/{Model.FileName}", ModelInstaller.Resolve(Model, $"{root}/models").Location);
        await AssertInstalls($"{root}/models");
        await AssertInstalls($"{root}/models/{Model.FileName}");
        await AssertInstalls($"{root}/models.zip");
        Assert.False(File.Exists(Destination + ".zip.part"));
    }

    [Fact]
    public void An_empty_source_is_the_publisher()
    {
        var source = ModelInstaller.Resolve(StemModel.SixStem, " ");
        Assert.Equal(StemModel.SixStem.DownloadUrl, source.Location);
        Assert.True(source.Remote);
        Assert.True(ModelInstaller.IsRemote(null));
        Assert.False(ModelInstaller.IsRemote("/tmp/models.zip"));
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
