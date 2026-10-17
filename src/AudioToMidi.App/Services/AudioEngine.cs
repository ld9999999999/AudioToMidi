using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioToMidi.Core.Synth;
using MeltySynth;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace AudioToMidi.App.Services;

/// <summary>
/// Drive the <see cref="NotePlayer"/> (the mixer) and the piano roll's <see cref="Keyboard"/>, either on the
/// default output device through PortAudio or, with <see cref="OpenSilent"/>, on a silent background clock so
/// playback still moves when there is no device or audio is off. The mixer renders ahead on its own thread
/// (<see cref="RenderAhead"/>), and the device callback only copies that audio and adds the keyboard. This way
/// the keys respond at once, and a slow render or a GC pause can't starve the device and cause a click.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    readonly PaStream? _stream;
    readonly PaStream.Callback? _callback; // kept referenced so the GC cannot collect it while native code calls it
    readonly Thread? _silentThread;
    volatile bool _stopSilent;
    readonly RenderAhead _ahead;
    float[] _left = new float[4096], _right = new float[4096], _keysLeft = new float[4096], _keysRight = new float[4096];
    float[] _stereo = new float[8192];
    volatile IInstrument _keyboard;

    /// <summary>
    /// Frames the device asks for at a time, about 11 ms. This lets the callback ride out a much longer pause
    /// than CoreAudio's default of 64 frames, such as a GC, which stops every .NET thread including the callback.
    /// </summary>
    const int DeviceFrames = 512;

    /// <summary>The keyboard's built-in synth, played when it has no SoundFont.</summary>
    public PolySynth Synth { get; }

    /// <summary>What the piano roll's keys play: <see cref="Synth"/>, or a SoundFont's instrument.</summary>
    public IInstrument Keyboard => _keyboard;

    /// <summary>Play the keyboard from a SoundFont's General MIDI program (0-based), or on <see cref="Synth"/> when the font is null.</summary>
    public void SetKeyboardSound(SoundFont? font, int program)
    {
        if (font is null) _keyboard = Synth;
        else if (_keyboard is SoundFontInstrument current && current.SoundFont == font) current.Program = program;
        else _keyboard = new SoundFontInstrument(font, Synth.SampleRate, program);
    }
    public NotePlayer Player { get; }
    public bool IsSilent => _stream is null;

    /// <summary>Gain on everything, 1 = as is.</summary>
    public float MasterVolume { get; set; } = 1;

    /// <summary>Mixer audio rendered but not heard yet, in seconds: the playhead is this far behind the mixer.</summary>
    public double Buffered => _ahead.Buffered;

    int _late, _lastFrames, _gaps;
    long _lastStart, _longestGapTicks;
    readonly double _sampleRate;

    /// <summary>
    /// Counters for tracking down clicks: how often the mixer fell behind the device, how many callbacks were
    /// held up for more than two buffers or took longer than the audio they made, the device's buffer size,
    /// and the longest wait between callbacks.
    /// </summary>
    public (int Starved, int Gaps, int Late, int Frames, double LongestGapMs) Health =>
        (_ahead.Starved, _gaps, _late, _lastFrames, _longestGapTicks * 1000.0 / Stopwatch.Frequency);

    AudioEngine(int device, DeviceInfo info)
    {
        Synth = new PolySynth((int)info.defaultSampleRate);
        _keyboard = Synth;
        Player = new NotePlayer(new PolySynth((int)info.defaultSampleRate));
        _sampleRate = info.defaultSampleRate;
        _ahead = new RenderAhead(Player.Render, (int)info.defaultSampleRate);
        _callback = Render;
        var output = new StreamParameters
        {
            device = device,
            channelCount = 2,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = Math.Max(info.defaultLowOutputLatency, 2.0 * DeviceFrames / info.defaultSampleRate),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        _stream = new PaStream(null, output, info.defaultSampleRate, DeviceFrames, StreamFlags.ClipOff, _callback, null);
        _stream.Start();
    }

    AudioEngine(int sampleRate)
    {
        Synth = new PolySynth(sampleRate);
        _keyboard = Synth;
        Player = new NotePlayer(new PolySynth(sampleRate));
        _sampleRate = sampleRate;
        _ahead = new RenderAhead(Player.Render, sampleRate);
        _silentThread = new Thread(SilentLoop) { IsBackground = true, Name = "Silent audio clock" };
        _silentThread.Start();
    }

    /// <summary>Open the default output device. Throw if there is none.</summary>
    public static AudioEngine Open()
    {
        PortAudio.Initialize();
        try
        {
            int device = PortAudio.DefaultOutputDevice;
            if (device == PortAudio.NoDevice) throw new InvalidOperationException("No audio output device found.");
            return new AudioEngine(device, PortAudio.GetDeviceInfo(device));
        }
        catch
        {
            PortAudio.Terminate();
            throw;
        }
    }

    public static AudioEngine OpenSilent() => new(44100);

    StreamCallbackResult Render(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time,
        StreamCallbackFlags flags, IntPtr userData)
    {
        long start = Stopwatch.GetTimestamp();
        int n = (int)frames;
        long period = (long)(n / _sampleRate * Stopwatch.Frequency);
        if (_lastStart != 0)
        {
            long gap = start - _lastStart; // a callback held up before it started (e.g. by a GC pause) shows here
            _longestGapTicks = Math.Max(_longestGapTicks, gap);
            if (gap > 2 * period) _gaps++;
        }
        _lastStart = start;
        _lastFrames = n;
        RenderStereo(n);
        Marshal.Copy(_stereo, 0, output, n * 2);
        if (Stopwatch.GetTimestamp() - start > period) _late++;
        return StreamCallbackResult.Continue;
    }

    /// <summary>Write the already rendered mix and the keyboard into <see cref="_stereo"/>, interleaved.</summary>
    void RenderStereo(int n)
    {
        if (_left.Length < n) // only if the host changes buffer size
        {
            _left = new float[n]; _right = new float[n]; _keysLeft = new float[n]; _keysRight = new float[n]; _stereo = new float[n * 2];
        }
        _ahead.Read(_left.AsSpan(0, n), _right.AsSpan(0, n));
        _keyboard.Render(_keysLeft.AsSpan(0, n), _keysRight.AsSpan(0, n));
        float gain = MasterVolume;
        for (int i = 0; i < n; i++)
        {
            _stereo[2 * i] = MathF.Tanh((_left[i] + _keysLeft[i]) * gain);
            _stereo[2 * i + 1] = MathF.Tanh((_right[i] + _keysRight[i]) * gain);
        }
    }

    void SilentLoop()
    {
        const int block = 256;
        var clock = Stopwatch.StartNew();
        long rendered = 0;
        while (!_stopSilent)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * Synth.SampleRate);
            while (rendered + block <= due) { RenderStereo(block); rendered += block; }
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        if (_stream is null)
        {
            _stopSilent = true;
            _silentThread?.Join(500);
            _ahead.Dispose();
            return;
        }
        try
        {
            _stream.Stop();
            _stream.Dispose(); // the stream must close before PortAudio terminates
        }
        finally
        {
            PortAudio.Terminate();
            _ahead.Dispose();
        }
    }
}
