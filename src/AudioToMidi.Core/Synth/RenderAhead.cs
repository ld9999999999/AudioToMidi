namespace AudioToMidi.Core.Synth;

/// <summary>Render stereo audio, overwriting both spans (same length).</summary>
public delegate void StereoRenderer(Span<float> left, Span<float> right);

/// <summary>
/// Render audio ahead of the device on a separate thread, into a ring buffer that the device's callback
/// copies from. When a render runs late (the UI holds the mixer's lock, or a burst of notes arrives), it
/// uses up some of this lead and the device never runs dry, which would be heard as a click. The cost is
/// that changes such as a fader move or play and stop are heard <see cref="Lead"/> later. There is one
/// reader and one writer, so call <see cref="Read"/> from only one thread at a time (the device callback).
/// </summary>
public sealed class RenderAhead : IDisposable
{
    const int Block = 256;

    readonly StereoRenderer _render;
    readonly float[] _left, _right;
    readonly int _mask, _leadFrames, _sampleRate;
    readonly Thread _thread;
    readonly AutoResetEvent _consumed = new(false);
    long _written, _read; // frames, only ever growing; each written by one side
    volatile bool _stopping;
    int _starved;

    /// <param name="lead">How far ahead to render, in seconds.</param>
    public RenderAhead(StereoRenderer render, int sampleRate, double lead = 0.08)
    {
        _render = render;
        _sampleRate = sampleRate;
        _leadFrames = Math.Max(Block, (int)(lead * sampleRate));
        int capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(_leadFrames + Block));
        _left = new float[capacity];
        _right = new float[capacity];
        _mask = capacity - 1;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Playback render" };
        _thread.Start();
    }

    /// <summary>How far ahead audio is rendered, in seconds.</summary>
    public double Lead => (double)_leadFrames / _sampleRate;

    /// <summary>Seconds of audio rendered but not yet read, which is how far the device lags the renderer.</summary>
    public double Buffered => (double)(Volatile.Read(ref _written) - Volatile.Read(ref _read)) / _sampleRate;

    /// <summary>How many times <see cref="Read"/> found too little audio and filled the rest with silence.</summary>
    public int Starved => _starved;

    /// <summary>Copy out the next audio, with silence for any part not rendered yet. This never blocks.</summary>
    public void Read(Span<float> left, Span<float> right)
    {
        long read = _read;
        int n = (int)Math.Min(left.Length, Volatile.Read(ref _written) - read);
        int at = (int)(read & _mask), first = Math.Min(n, _left.Length - at);
        _left.AsSpan(at, first).CopyTo(left);
        _right.AsSpan(at, first).CopyTo(right);
        _left.AsSpan(0, n - first).CopyTo(left[first..]);
        _right.AsSpan(0, n - first).CopyTo(right[first..]);
        if (n < left.Length)
        {
            left[n..].Clear();
            right[n..].Clear();
            _starved++;
        }
        Volatile.Write(ref _read, read + n);
        _consumed.Set();
    }

    void Run()
    {
        var left = new float[Block];
        var right = new float[Block];
        while (!_stopping)
        {
            long written = _written;
            if (written - Volatile.Read(ref _read) + Block > _leadFrames)
            {
                _consumed.WaitOne(5); // wait until the device reads some
                continue;
            }
            _render(left, right);
            int at = (int)(written & _mask); // capacity is a multiple of Block, so a block never wraps
            left.CopyTo(_left.AsSpan(at));
            right.CopyTo(_right.AsSpan(at));
            Volatile.Write(ref _written, written + Block);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _consumed.Set();
        _thread.Join(500);
        _consumed.Dispose();
    }
}
