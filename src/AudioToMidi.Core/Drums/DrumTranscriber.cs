using AudioToMidi.Core.Audio;
using NWaves.Transforms;
using NWaves.Windows;

namespace AudioToMidi.Core.Drums;

/// <summary>
/// Find kick, snare and hi-hat hits in drum audio (a Demucs drum stem, or the percussive part from
/// HPSS) with non-negative matrix factorisation (NMF). The spectrogram is modelled as a sum of a few
/// spectral shapes, each switched on over time by an activation curve:
/// <list type="bullet">
/// <item>three drum shapes start as typical kick, snare and hi-hat spectra and are partly re-learned
/// from the recording, so they fit the actual kit but keep their identity (semi-adaptive NMF,
/// Dittmar and Gärtner 2014);</item>
/// <item>two free shapes soak up everything else (cymbals, toms, other instruments) and keep it out
/// of the drum activations.</item>
/// </list>
/// The slow factorisation runs once; <see cref="DrumAnalysis.Decode"/> can then pick hits from the
/// activation curves for any sensitivity almost instantly.
/// </summary>
public static class DrumTranscriber
{
    const int FftSize = 1024;
    const int Hop = 256;
    const int BandsPerOctave = 6;
    const double LowestHz = 30;
    const double HighestHz = 16000;

    const int Iterations = 60;
    const int FreeComponents = 2;

    /// <summary>How much of the starting shape a drum template keeps at the end (0 = fully re-learned).</summary>
    const double TemplateAnchor = 0.15;

    public static Task<DrumAnalysis> AnalyzeAsync(AudioData audio, AudioData? mix = null, CancellationToken ct = default) =>
        Task.Run(() => Analyze(audio, mix, ct), ct);

    /// <param name="audio">The drums: a drum stem or the percussive part of the mix.</param>
    /// <param name="mix">
    /// The full mix <paramref name="audio"/> came from, at the same sample rate. Hits must be loud
    /// compared with the mix, so a song without drums does not turn faint leftovers into a kit.
    /// Pass null when <paramref name="audio"/> is all there is.
    /// </param>
    public static DrumAnalysis Analyze(AudioData audio, AudioData? mix = null, CancellationToken ct = default)
    {
        var (v, centres, frames) = BandSpectrogram(audio, ct);
        int bands = centres.Length;
        int drums = DrumTemplates.Count;
        int k = drums + FreeComponents;

        // W: band x component, columns summing to 1. H: component x frame.
        var prior = new double[bands * drums];
        for (int c = 0; c < drums; c++)
        {
            double sum = 0;
            for (int b = 0; b < bands; b++) sum += prior[b * drums + c] = DrumTemplates[c](centres[b]);
            for (int b = 0; b < bands; b++) prior[b * drums + c] /= sum;
        }

        var w = new double[bands * k];
        var rng = new Random(12345);
        for (int b = 0; b < bands; b++)
        {
            for (int c = 0; c < drums; c++) w[b * k + c] = prior[b * drums + c];
            for (int c = drums; c < k; c++) w[b * k + c] = 0.5 + rng.NextDouble();
        }
        NormalizeColumns(w, null, bands, k, frames);

        // Drum activations start as each template's projection onto the spectrogram; free ones start small and random.
        var h = new double[k * frames];
        double mean = v.Average(x => (double)x);
        for (int t = 0; t < frames; t++)
        {
            for (int c = 0; c < drums; c++)
            {
                double s = 0;
                for (int b = 0; b < bands; b++) s += w[b * k + c] * v[t * bands + b];
                h[c * frames + t] = s + 1e-9;
            }
            for (int c = drums; c < k; c++) h[c * frames + t] = mean * (0.1 + 0.2 * rng.NextDouble());
        }

        var ratio = new double[frames * bands]; // V / WH
        for (int iter = 0; iter < Iterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            // Activations (KL multiplicative update). Columns of W sum to 1, so the denominator is 1.
            ComputeRatio(v, w, h, ratio, bands, k, frames);
            Parallel.For(0, frames, t =>
            {
                for (int c = 0; c < k; c++)
                {
                    double num = 0;
                    for (int b = 0; b < bands; b++) num += w[b * k + c] * ratio[t * bands + b];
                    h[c * frames + t] *= num;
                }
            });

            // Templates. Pull drum templates back toward their starting shape, less so as the iterations go on.
            ComputeRatio(v, w, h, ratio, bands, k, frames);
            var rowSum = new double[k];
            for (int c = 0; c < k; c++)
            {
                double s = 0;
                for (int t = 0; t < frames; t++) s += h[c * frames + t];
                rowSum[c] = s + 1e-12;
            }
            double alpha = Math.Max(TemplateAnchor, 1 - (double)iter / (Iterations * 0.6));
            Parallel.For(0, bands, b =>
            {
                for (int c = 0; c < k; c++)
                {
                    double num = 0;
                    for (int t = 0; t < frames; t++) num += ratio[t * bands + b] * h[c * frames + t];
                    double updated = w[b * k + c] * num / rowSum[c];
                    w[b * k + c] = c < drums ? alpha * prior[b * drums + c] + (1 - alpha) * updated : updated;
                }
            });
            NormalizeColumns(w, h, bands, k, frames);
        }

        var activations = new float[drums][];
        for (int c = 0; c < drums; c++)
        {
            activations[c] = new float[frames];
            for (int t = 0; t < frames; t++) activations[c][t] = (float)h[c * frames + t];
        }
        var mixV = mix is null ? v : BandSpectrogram(mix, ct).V;
        return new DrumAnalysis(activations, AttackCurves(v, w, bands, k, frames, drums),
            MixLevels(mixV, w, bands, k, drums), (double)audio.SampleRate / Hop);
    }

    /// <summary>Measure how loud the mix gets in each drum's bands: the 99th percentile over frames of the
    /// template-weighted spectrum, in the same unit as the drum's activation.</summary>
    static double[] MixLevels(float[] mixV, double[] w, int bands, int k, int drums)
    {
        int frames = mixV.Length / bands;
        var levels = new double[drums];
        var values = new double[frames];
        for (int c = 0; c < drums; c++)
        {
            for (int t = 0; t < frames; t++)
            {
                double s = 0;
                for (int b = 0; b < bands; b++) s += w[b * k + c] * mixV[t * bands + b];
                values[t] = s;
            }
            Array.Sort(values);
            levels[c] = frames > 0 ? values[(int)Math.Floor(0.99 * (frames - 1))] : 0;
        }
        return levels;
    }

    /// <summary>
    /// For each drum, measure how sharply the spectrum jumps up in that drum's bands at each frame, as log
    /// spectral flux weighted by the learned template. Activations rise slowly on a deep kick and wobble
    /// under a ringing cymbal. This curve peaks right at the attack, so it times hits and confirms they are real.
    /// </summary>
    static float[][] AttackCurves(float[] v, double[] w, int bands, int k, int frames, int drums)
    {
        double floor = 0.01 * v.Average(x => (double)x) + 1e-12;
        var log = new float[v.Length];
        for (int i = 0; i < v.Length; i++) log[i] = (float)Math.Log(v[i] + floor);

        var curves = new float[drums][];
        for (int c = 0; c < drums; c++)
        {
            var curve = curves[c] = new float[frames];
            for (int t = 1; t < frames; t++)
            {
                double s = 0;
                for (int b = 0; b < bands; b++)
                {
                    double rise = log[t * bands + b] - log[(t - 1) * bands + b];
                    if (rise > 0) s += w[b * k + c] * rise;
                }
                curve[t] = (float)s;
            }
        }
        return curves;
    }

    /// <summary>
    /// Starting spectral shapes (relative magnitude per band, by centre frequency), in
    /// <see cref="DrumAnalysis"/> component order: kick, snare, hi-hat.
    /// </summary>
    static readonly IReadOnlyList<Func<double, double>> DrumTemplates =
    [
        // Kick: a low thump around 50–100 Hz, a little beater click higher up.
        hz => Bump(hz, 60, 0.8) + 0.05 * Bump(hz, 3000, 1.5) + 0.002,
        // Snare: the drum body around 200 Hz plus the wires' broad noise, strongest at a few kHz.
        hz => 0.6 * Bump(hz, 200, 0.6) + Bump(hz, 3500, 1.4) + 0.01,
        // Hi-hat: almost nothing below 3 kHz, most energy from 6 kHz up.
        hz => 1 / (1 + Math.Exp(-4 * Math.Log2(hz / 6500))) + 0.001,
    ];

    /// <summary>Gaussian bump on a log-frequency axis, <paramref name="octaves"/> wide (standard deviation).</summary>
    static double Bump(double hz, double centre, double octaves)
    {
        double d = Math.Log2(hz / centre) / octaves;
        return Math.Exp(-0.5 * d * d);
    }

    static void ComputeRatio(float[] v, double[] w, double[] h, double[] ratio, int bands, int k, int frames)
    {
        Parallel.For(0, frames, t =>
        {
            for (int b = 0; b < bands; b++)
            {
                double wh = 0;
                for (int c = 0; c < k; c++) wh += w[b * k + c] * h[c * frames + t];
                ratio[t * bands + b] = v[t * bands + b] / (wh + 1e-12);
            }
        });
    }

    /// <summary>Scale each column of W to sum to 1 and its row of H by the inverse, leaving W·H unchanged.</summary>
    static void NormalizeColumns(double[] w, double[]? h, int bands, int k, int frames)
    {
        for (int c = 0; c < k; c++)
        {
            double sum = 0;
            for (int b = 0; b < bands; b++) sum += w[b * k + c];
            if (sum <= 0) continue;
            for (int b = 0; b < bands; b++) w[b * k + c] /= sum;
            if (h is not null)
                for (int t = 0; t < frames; t++) h[c * frames + t] *= sum;
        }
    }

    /// <summary>
    /// Magnitude spectrogram averaged into log-spaced bands, frame-major (v[t * bands + b]). Frames are
    /// centred: frame t covers the audio around t * hop.
    /// </summary>
    static (float[] V, double[] Centres, int Frames) BandSpectrogram(AudioData audio, CancellationToken ct)
    {
        int sr = audio.SampleRate;
        int pad = FftSize / 2;
        var padded = new float[audio.Samples.Length + 2 * pad];
        audio.Samples.CopyTo(padded, pad);

        var stft = new Stft(FftSize, Hop, WindowType.Hann, FftSize);
        var spectrum = stft.Direct(padded);
        ct.ThrowIfCancellationRequested();
        // Only frames centred inside the audio; the padding adds a few at the end.
        int frames = Math.Min(spectrum.Count, audio.Samples.Length / Hop + 1);

        // Band edges in FFT bins: log-spaced, merged where a band would hold no bin.
        double binHz = (double)sr / FftSize;
        int maxBin = Math.Min(FftSize / 2, (int)(Math.Min(HighestHz, sr / 2.0) / binHz));
        var edges = new List<int>();
        for (double hz = LowestHz; ; hz *= Math.Pow(2, 1.0 / BandsPerOctave))
        {
            int bin = Math.Min(maxBin, (int)Math.Round(hz / binHz));
            if (edges.Count == 0 || bin > edges[^1]) edges.Add(bin);
            if (bin >= maxBin) break;
        }
        int bands = edges.Count - 1;
        var centres = new double[bands];
        for (int b = 0; b < bands; b++) centres[b] = Math.Sqrt(Math.Max(1, edges[b]) * edges[b + 1]) * binHz;

        var v = new float[frames * bands];
        Parallel.For(0, frames, t =>
        {
            var (re, im) = spectrum[t];
            for (int b = 0; b < bands; b++)
            {
                double s = 0;
                for (int i = edges[b]; i < edges[b + 1]; i++) s += Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
                v[t * bands + b] = (float)(s / (edges[b + 1] - edges[b]));
            }
        });
        return (v, centres, frames);
    }
}
