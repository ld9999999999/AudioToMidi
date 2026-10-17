using System.IO.Compression;
using System.Security.Cryptography;

namespace AudioToMidi.Core.Separation;

/// <summary>Where a model is installed from. <see cref="Location"/> is a URL when <see cref="Remote"/>, else a file path.</summary>
/// <param name="Archive">The source is a model archive (.zip) that holds the model file.</param>
/// <param name="Bytes">Expected size of the download, when known.</param>
public sealed record ModelSource(string Location, bool Remote, bool Archive, long? Bytes = null);

/// <summary>
/// Install separation models: download or copy them to their default location through a .part file, so
/// an interrupted install never looks complete. The source is the publisher's URL unless one is given: a
/// URL or file path (file:// URLs too) to the model file itself (.onnx), to a model archive (.zip), or to
/// a folder that holds the model file under its usual name.
/// </summary>
public static class ModelInstaller
{
    /// <summary>A connection that delivers nothing for this long is dropped and the download resumed.</summary>
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public static Task InstallAsync(StemModel model, string? source = null, IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        InstallAsync(model, source, model.DefaultPath, progress, ct);

    public static async Task InstallAsync(StemModel model, string? source, string destination, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var from = Resolve(model, source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        if (from is { Remote: true, Archive: true })
        {
            // Download the archive (90% of the progress), then take the model out of it.
            var archive = destination + ".zip.part";
            await DownloadAsync(from.Location, archive, null, Scale(progress, 0, 0.9), ct);
            await ExtractAsync(archive, model, part, Scale(progress, 0.9, 1), ct);
            File.Delete(archive);
        }
        else if (from.Remote) await DownloadAsync(from.Location, part, from.Bytes, progress, ct);
        else if (from.Archive) await ExtractAsync(from.Location, model, part, progress, ct);
        else await CopyAsync(from.Location, part, progress, ct);
        File.Move(part, destination, overwrite: true);
    }

    /// <summary>True when the source is fetched over the network (the publisher's URL when empty).</summary>
    public static bool IsRemote(string? source) =>
        string.IsNullOrWhiteSpace(source) || Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>Work out what a source points at, and throw when it cannot hold the model.</summary>
    public static ModelSource Resolve(StemModel model, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return new(model.DownloadUrl, true, false, model.DownloadBytes);
        source = source.Trim();
        var names = model.OtherFileNames.Prepend(model.FileName).ToList();

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var path = uri.AbsolutePath;
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new(uri.AbsoluteUri, true, true);
            if (path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                CheckName(model, names, Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]));
                return new(uri.AbsoluteUri, true, false);
            }
            var folder = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
            return new(new Uri(folder, Uri.EscapeDataString(model.FileName)).AbsoluteUri, true, false);
        }

        // A local path, or a file:// URL to one.
        string local = uri is { IsFile: true } ? uri.LocalPath
            : source.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), source[2..])
            : Path.GetFullPath(source);
        if (Directory.Exists(local))
        {
            var found = names.SelectMany(n => new[] { Path.Combine(local, n), Path.Combine(local, "models", n) }).FirstOrDefault(File.Exists);
            return found is null
                ? throw new FileNotFoundException($"{local} has no {model.FileName}.")
                : new(found, false, false);
        }
        if (!File.Exists(local)) throw new FileNotFoundException($"{local} does not exist.");
        if (local.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new(local, false, true);
        CheckName(model, names, Path.GetFileName(local));
        return new(local, false, false);
    }

    /// <summary>A model file must keep its name, so that one model is not installed in place of another.</summary>
    static void CheckName(StemModel model, List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{name} is not the {model.Name} model, which is named {model.FileName}.");
    }

    static async Task CopyAsync(string from, string to, IProgress<double>? progress, CancellationToken ct)
    {
        await using var source = File.OpenRead(from);
        await using var target = File.Create(to);
        await CopyWithProgressAsync(source, target, source.Length, progress, null, ct);
    }

    /// <summary>Take the model out of an archive, and check it against the archive's SHA256SUMS when that lists it.</summary>
    static async Task ExtractAsync(string archive, StemModel model, string to, IProgress<double>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var names = model.OtherFileNames.Prepend(model.FileName).ToList();
        var entry = names.Select(n => zip.Entries.FirstOrDefault(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                        .FirstOrDefault(e => e is not null)
                    ?? throw new FileNotFoundException($"The archive has no {model.FileName}.");

        string? expected = null;
        if (zip.Entries.FirstOrDefault(e => e.FullName == "SHA256SUMS") is { } sums)
        {
            using var reader = new StreamReader(sums.Open());
            expected = (await reader.ReadToEndAsync(ct)).Split('\n')
                .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == entry.Name)?[0];
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = entry.Open())
        await using (var target = File.Create(to))
            await CopyWithProgressAsync(source, target, entry.Length, progress, hash, ct);
        if (expected is not null && !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(to);
            throw new InvalidDataException($"{entry.Name} in the archive does not match its checksum: the archive is damaged.");
        }
    }

    static async Task CopyWithProgressAsync(Stream source, Stream target, long total, IProgress<double>? progress,
        IncrementalHash? hash, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            hash?.AppendData(buffer, 0, read);
            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }
    }

    static IProgress<double>? Scale(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new SyncProgress(f => progress.Report(from + (to - from) * f));

    sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Download <paramref name="url"/> into <paramref name="part"/>, retrying and resuming after a stall or a broken connection.</summary>
    static async Task DownloadAsync(string url, string part, long? bytes, IProgress<double>? progress, CancellationToken ct)
    {
        // No overall timeout: a slow connection may need a long time. A stalled one is caught per read below.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (await DownloadPartAsync(http, url, part, bytes, progress, ct)) return;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TimeoutException && attempt < attempts)
            {
                // Try again from where it stopped.
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    /// <summary>
    /// Download into <paramref name="part"/>, continuing a partial file when the server supports ranges.
    /// Return true when the file is complete, and throw on a stall or a broken connection.
    /// </summary>
    static async Task<bool> DownloadPartAsync(HttpClient http, string url, string part, long? bytes, IProgress<double>? progress,
        CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && (bytes is null || have == bytes))
                return true; // already complete
            response.EnsureSuccessStatusCode();
            bool resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed) have = 0; // the server sent the whole file
            long? total = response.Content.Headers.ContentLength is { } length ? have + length : bytes;

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var target = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);
            var buffer = new byte[1 << 20];
            long done = have;
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
            if (total is { } expected && done != expected) throw new IOException($"Download incomplete: {done} of {expected} bytes.");
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The download stalled (no data for {StallTimeout.TotalSeconds:F0} s).");
        }
    }
}
