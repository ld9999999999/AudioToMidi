using System.Diagnostics;
using System.Globalization;
using System.Text;
using NWaves.Operations;
using NWaves.Signals;

namespace AudioToMidi.Core.Audio;

public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// Convert any media file ffmpeg can read into 32-bit float audio at a fixed sample rate and
/// channel count, going through a temporary WAV file.
/// </summary>
public static class FfmpegDecoder
{
    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "AudioToMidi");
    static string ExeName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>
    /// Find ffmpeg. Look at the explicit setting, then FFMPEG_PATH, next to the app, on PATH, and
    /// finally in common install folders, since GUI apps on macOS do not inherit the shell PATH.
    /// </summary>
    public static string? Locate(string? configuredPath = null)
    {
        IEnumerable<string?> Candidates()
        {
            yield return configuredPath;
            yield return Environment.GetEnvironmentVariable("FFMPEG_PATH");
            yield return Path.Combine(AppContext.BaseDirectory, ExeName);
            yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", ExeName);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (dir.Length > 0) yield return Path.Combine(dir, ExeName);
            if (OperatingSystem.IsMacOS())
            {
                yield return "/opt/homebrew/bin/ffmpeg";
                yield return "/usr/local/bin/ffmpeg";
                yield return "/opt/local/bin/ffmpeg";
            }
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                yield return @"C:\ffmpeg\bin\ffmpeg.exe";
                yield return @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
            }
        }

        return Candidates().FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <summary>
    /// Decode <paramref name="inputPath"/> to mono float audio at <paramref name="sampleRate"/>.
    /// If ffmpeg is missing and the input is already a WAV file, read and resample it directly.
    /// </summary>
    public static async Task<AudioData> DecodeAsync(string inputPath, int sampleRate, string? ffmpegPath = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (channels, rate) = await DecodeChannelsAsync(inputPath, sampleRate, 1, ffmpegPath, progress, ct);
        return new AudioData(channels[0], rate);
    }

    /// <summary>
    /// Decode to <paramref name="channels"/> separate channels at <paramref name="sampleRate"/>
    /// (1 = mono mix, 2 = stereo; a mono source is copied to both sides).
    /// </summary>
    public static async Task<(float[][] Channels, int SampleRate)> DecodeChannelsAsync(string inputPath, int sampleRate,
        int channels, string? ffmpegPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Input file not found.", inputPath);

        var ffmpeg = Locate(ffmpegPath);
        if (ffmpeg is null)
        {
            if (Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                return FitChannels(WavReader.ReadChannels(inputPath), sampleRate, channels);
            throw new FfmpegNotFoundException(
                "ffmpeg was not found. Set its location in Settings, set FFMPEG_PATH, place it next to the app, or install it " +
                (OperatingSystem.IsMacOS() ? "(brew install ffmpeg)." : "(winget install ffmpeg)."));
        }

        Directory.CreateDirectory(TempDir);
        var tempWav = Path.Combine(TempDir, $"decode_{Guid.NewGuid():N}.wav");
        try
        {
            await RunAsync(ffmpeg, inputPath, tempWav, sampleRate, channels, progress, ct);
            return WavReader.ReadChannels(tempWav);
        }
        finally
        {
            TryDelete(tempWav);
        }
    }

    /// <summary>Fallback for WAV input: resample and match the channel count without ffmpeg.</summary>
    static (float[][], int) FitChannels((float[][] Channels, int SampleRate) wav, int sampleRate, int channels)
    {
        var src = wav.Channels.Select(c => Resample(new AudioData(c, wav.SampleRate), sampleRate).Samples).ToArray();
        if (src.Length == channels) return (src, sampleRate);
        var mono = new float[src[0].Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = src.Average(c => c[i]);
        return (Enumerable.Range(0, channels).Select(_ => channels == 1 ? mono : (float[])mono.Clone()).ToArray(), sampleRate);
    }

    static async Task RunAsync(string ffmpeg, string input, string output, int sampleRate, int channels,
        IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-nostdin", "-hide_banner", "-nostats", "-y",
                     "-i", input,
                     "-vn", "-ac", channels.ToString(CultureInfo.InvariantCulture),
                     "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                     "-c:a", "pcm_f32le", "-f", "wav",
                     "-progress", "pipe:1",
                     output,
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        await using var _ = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { /* already exited */ } });

        double totalSeconds = 0;
        var stderr = new StringBuilder();

        // Read both streams concurrently so neither pipe buffer can fill up and stall ffmpeg.
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                stderr.AppendLine(line);
                var i = line.IndexOf("Duration: ", StringComparison.Ordinal);
                if (i >= 0 && totalSeconds == 0 &&
                    TimeSpan.TryParse(line.AsSpan(i + 10, 11), CultureInfo.InvariantCulture, out var d))
                    totalSeconds = d.TotalSeconds;
            }
        });
        var outTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                // Newer builds write "out_time_us"; "out_time_ms" is also in microseconds despite its name.
                if ((line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms=")) && totalSeconds > 0 &&
                    long.TryParse(line.AsSpan(12), out var us))
                    progress?.Report(Math.Clamp(us / 1e6 / totalSeconds, 0, 1));
            }
        });

        await Task.WhenAll(errTask, outTask);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed (exit code {p.ExitCode}):\n{Tail(stderr.ToString(), 15)}");
        progress?.Report(1);
    }

    /// <summary>Remove temp files left behind by a crash or forced quit.</summary>
    public static void CleanupStaleTempFiles(TimeSpan olderThan)
    {
        if (!Directory.Exists(TempDir)) return;
        foreach (var f in Directory.EnumerateFiles(TempDir))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > olderThan)
                TryDelete(f);
    }

    public static AudioData Resample(AudioData audio, int sampleRate)
    {
        if (audio.SampleRate == sampleRate) return audio;
        var resampled = Operation.Resample(new DiscreteSignal(audio.SampleRate, audio.Samples), sampleRate);
        return new AudioData(resampled.Samples, sampleRate);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
}
