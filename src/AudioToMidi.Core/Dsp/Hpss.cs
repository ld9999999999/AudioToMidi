using NWaves.Transforms;
using NWaves.Windows;

namespace AudioToMidi.Core.Dsp;

/// <param name="Harmonic">Audio with percussion removed (what gets transcribed).</param>
/// <param name="Percussive">The removed drum/transient part.</param>
/// <param name="OnsetEnvelope">Spectral flux of the percussive part, one value per STFT frame (frame t is centred at t / EnvelopeRate seconds). Used for tempo.</param>
/// <param name="LowOnsetEnvelope">The same below 200 Hz: mostly kick drum, which marks where the beat is.</param>
public sealed record HpssResult(float[] Harmonic, float[] Percussive, float[] OnsetEnvelope, double EnvelopeRate, float[] LowOnsetEnvelope);

/// <summary>
/// Split audio into harmonic and percussive parts by median filtering (Fitzgerald 2010), with the
/// separation margin from Driedger et al. 2014. Sustained tones are smooth across time and drum
/// hits are smooth across frequency, so each median filter keeps one and suppresses the other.
/// </summary>
public static class Hpss
{
    /// <param name="margin">
    /// 1 gives a plain soft-mask split. Higher values remove more percussion, along with the unclear
    /// leftover, at the cost of some note attacks. 1.5–3 works well for drum-heavy music.
    /// </param>
    public static HpssResult Separate(float[] samples, int sampleRate, double margin = 2.0,
        int fftSize = 2048, int hopSize = 512, int harmonicKernel = 31, int percussiveKernel = 31,
        CancellationToken ct = default)
    {
        // Centre the frames: pad half a window on both sides so frame t is centred on sample t * hop,
        // and the signal edges are covered by fully overlapping windows (otherwise the inverse STFT
        // divides by a near-zero window sum there and blows up).
        int pad = fftSize / 2;
        var padded = new float[samples.Length + 2 * pad];
        samples.CopyTo(padded, pad);

        var stft = new Stft(fftSize, hopSize, WindowType.Hann, fftSize);
        var spectrum = stft.Direct(padded);
        int frames = spectrum.Count;
        int bins = spectrum[0].Item1.Length;

        // Magnitudes, frame-major: mag[t * bins + k]
        var mag = new float[frames * bins];
        Parallel.For(0, frames, t =>
        {
            var (re, im) = spectrum[t];
            for (int k = 0; k < bins; k++)
                mag[t * bins + k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
        });
        ct.ThrowIfCancellationRequested();

        var harm = new float[frames * bins];
        var perc = new float[frames * bins];

        // Harmonic enhancement: median across time, per frequency bin.
        Parallel.For(0, bins, new ParallelOptions { CancellationToken = ct }, k =>
        {
            var col = new float[frames];
            var outCol = new float[frames];
            for (int t = 0; t < frames; t++) col[t] = mag[t * bins + k];
            SlidingMedian.Apply(col, outCol, harmonicKernel);
            for (int t = 0; t < frames; t++) harm[t * bins + k] = outCol[t];
        });

        // Percussive enhancement: median across frequency, per frame.
        Parallel.For(0, frames, new ParallelOptions { CancellationToken = ct }, t =>
            SlidingMedian.Apply(mag.AsSpan(t * bins, bins), perc.AsSpan(t * bins, bins), percussiveKernel));

        // Soft (Wiener, power 2) masks with margin. With margin > 1 the masks no longer sum to 1;
        // the leftover "residual" is discarded from both outputs.
        var harmSpec = new List<(float[], float[])>(frames);
        var percSpec = new List<(float[], float[])>(frames);
        float m2 = (float)(margin * margin);
        const float eps = 1e-10f;
        var percMag = harm; // reuse: the harmonic median is not needed after this loop
        for (int t = 0; t < frames; t++)
        {
            var (re, im) = spectrum[t];
            var hre = new float[bins]; var him = new float[bins];
            var pre = new float[bins]; var pim = new float[bins];
            for (int k = 0; k < bins; k++)
            {
                float h2 = harm[t * bins + k]; h2 *= h2;
                float p2 = perc[t * bins + k]; p2 *= p2;
                float mh = h2 / (h2 + m2 * p2 + eps);
                float mp = p2 / (p2 + m2 * h2 + eps);
                hre[k] = re[k] * mh; him[k] = im[k] * mh;
                pre[k] = re[k] * mp; pim[k] = im[k] * mp;
                percMag[t * bins + k] = mag[t * bins + k] * mp;
            }
            harmSpec.Add((hre, him));
            percSpec.Add((pre, pim));
        }
        ct.ThrowIfCancellationRequested();

        var harmonic = Unpad(stft.Inverse(harmSpec, true), pad, samples.Length);
        var percussive = Unpad(stft.Inverse(percSpec, true), pad, samples.Length);

        // Onset envelope: half-wave rectified log-spectral flux of the percussive part. Magnitudes are
        // normalised to roughly signal amplitude first so the log stays mild: with strong compression a
        // quiet hi-hat scores as high as a loud snare and the beat accents that define tempo are lost.
        float norm = 10f / (fftSize / 4f);
        for (int i = 0; i < percMag.Length; i++) percMag[i] = MathF.Log(1 + norm * percMag[i]);
        var envelope = new float[frames];
        var low = new float[frames];
        int lowBins = (int)Math.Ceiling(200.0 * fftSize / sampleRate);
        Parallel.For(1, frames, t =>
        {
            float sum = 0, lowSum = 0;
            for (int k = 0; k < bins; k++)
            {
                float diff = percMag[t * bins + k] - percMag[(t - 1) * bins + k];
                if (diff <= 0) continue;
                sum += diff;
                if (k < lowBins) lowSum += diff;
            }
            envelope[t] = sum;
            low[t] = lowSum;
        });

        return new HpssResult(harmonic, percussive, envelope, (double)sampleRate / hopSize, low);
    }

    static float[] Unpad(float[] x, int offset, int length)
    {
        var y = new float[length];
        Array.Copy(x, offset, y, 0, Math.Clamp(x.Length - offset, 0, length));
        return y;
    }
}
