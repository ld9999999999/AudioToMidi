namespace AudioToMidi.Core.Dsp;

public static class SlidingMedian
{
    /// <summary>
    /// Median filter with an odd kernel and reflected edges (same as scipy's default "reflect" mode).
    /// The window stays sorted, so each step needs only O(kernel) moves.
    /// </summary>
    public static void Apply(ReadOnlySpan<float> input, Span<float> output, int kernel)
    {
        int n = input.Length;
        if (n == 0) return;
        if (kernel <= 1) { input.CopyTo(output); return; }
        if ((kernel & 1) == 0) kernel++;
        int r = kernel / 2;

        // Reflected sample at index i (handles windows larger than the input too).
        float At(ReadOnlySpan<float> x, int i)
        {
            int period = 2 * x.Length;
            i %= period;
            if (i < 0) i += period;
            return i < x.Length ? x[i] : x[period - 1 - i];
        }

        Span<float> window = kernel <= 256 ? stackalloc float[kernel] : new float[kernel];
        for (int j = 0; j < kernel; j++) window[j] = At(input, j - r);
        window.Sort();

        for (int i = 0; i < n; i++)
        {
            output[i] = window[r];
            if (i == n - 1) break;

            float outgoing = At(input, i - r);
            float incoming = At(input, i + r + 1);

            int idx = window.BinarySearch(outgoing);
            if (idx < 0) idx = ~idx; // cannot happen, but stay safe
            // Remove outgoing, then slide incoming into its sorted position.
            while (idx > 0 && window[idx - 1] > incoming) { window[idx] = window[idx - 1]; idx--; }
            while (idx < kernel - 1 && window[idx + 1] < incoming) { window[idx] = window[idx + 1]; idx++; }
            window[idx] = incoming;
        }
    }
}
