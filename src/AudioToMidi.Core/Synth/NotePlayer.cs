using AudioToMidi.Core.Notation;
using AudioToMidi.Core.Transcription;
using MeltySynth;

namespace AudioToMidi.Core.Synth;

/// <summary>
/// Play transcribed notes through a small stereo mixer with one instrument per track (each with its own
/// General MIDI program), a drum kit and the original recording, each with its own gain, pan, mute and solo.
/// Instruments come from the built-in synth and drum kit or from a SoundFont (<see cref="SetSoundFont"/>).
/// Note timing is sample-accurate, and the player supports a playback speed, looping over a range and a
/// metronome on the song's beat grid. Control methods are called from the UI thread;
/// <see cref="Render(Span{float}, Span{float})"/> runs on the audio thread.
/// </summary>
public sealed class NotePlayer
{
    /// <summary>Channel number of the drum kit and of the original recording in <see cref="SetMix"/>.</summary>
    public const int DrumChannel = -1, SourceChannel = -2;

    /// <param name="Bend">True for a pitch bend of <see cref="Velocity"/> semitones.</param>
    readonly record struct NoteEvent(double Time, int Pitch, float Velocity, bool On, int Channel, bool Bend = false);

    sealed class Channel
    {
        public IInstrument Instrument = null!;
        public int Program;
        public float Gain = 1, Pan = 0.5f, Peak;
        public bool Mute, Solo;
        public readonly HashSet<int> Sounding = [];
    }

    readonly object _gate = new();
    readonly int _sampleRate;
    readonly float _synthVolume;
    readonly Channel _drumChannel = new(), _sourceChannel = new();
    Channel[] _tracks;
    SoundFont? _font;
    float[] _scratchLeft = new float[4096], _scratchRight = new float[4096];

    NoteEvent[] _notes = [], _drumHits = [], _events = [];
    double[] _beats = [];      // metronome clicks (song seconds)
    bool[] _downbeats = [];
    float[]? _source;
    int _sourceRate = 1;
    int _nextEvent, _nextBeat;
    double _position, _rangeStart, _rangeEnd = double.MaxValue;
    double _rate = 1;
    bool _playing, _loop;
    float _masterPeak;

    // Metronome click state (audio thread only).
    int _clickSamples;
    float _clickFreq, _clickPhase;

    /// <summary>Create a player with one track played by <paramref name="synth"/>. New tracks get synths at the same volume.</summary>
    public NotePlayer(PolySynth synth)
    {
        _sampleRate = synth.SampleRate;
        _synthVolume = synth.Volume;
        _tracks = [new Channel { Instrument = synth }];
        _drumChannel.Instrument = new DrumKit(_sampleRate);
    }

    public int SampleRate => _sampleRate;

    public bool IsPlaying { get { lock (_gate) return _playing; } }

    /// <summary>Song time in seconds.</summary>
    public double Position { get { lock (_gate) return _position; } }

    /// <summary>Pitches currently held on any track, for lighting up keys.</summary>
    public int[] SoundingPitches { get { lock (_gate) return [.. _tracks.SelectMany(t => t.Sounding).Distinct()]; } }

    public bool Metronome { get; set; }
    public float MetronomeVolume { get; set; } = 0.5f;

    /// <summary>The SoundFont the tracks and drums play from, or null for the built-in synth and drum kit.</summary>
    public SoundFont? SoundFont { get { lock (_gate) return _font; } }

    /// <summary>
    /// Play every track with this SoundFont's General MIDI instruments and the drums with its kit. Pass null to
    /// go back to the built-in synth (with the closest sound to each program) and drum kit.
    /// </summary>
    public void SetSoundFont(SoundFont? font)
    {
        lock (_gate)
        {
            if (font == _font) return;
            _font = font;
            foreach (var t in _tracks)
            {
                Release(t);
                t.Instrument = CreateInstrument(t.Program);
            }
            _drumChannel.Instrument = font is null ? new DrumKit(_sampleRate) : new SoundFontInstrument(font, _sampleRate, 0, percussion: true);
        }
    }

    /// <summary>Set one instrument per track, playing these General MIDI programs (0-based), and remove any tracks beyond the list.</summary>
    public void SetTracks(IReadOnlyList<int> programs)
    {
        lock (_gate)
        {
            var tracks = new Channel[Math.Max(1, programs.Count)];
            for (int i = 0; i < tracks.Length; i++)
            {
                int program = i < programs.Count ? programs[i] : 0;
                var ch = tracks[i] = i < _tracks.Length ? _tracks[i] : new Channel { Instrument = CreateInstrument(program) };
                ch.Program = program;
                switch (ch.Instrument)
                {
                    case PolySynth synth: synth.Patch = SynthPatch.ForProgram(program); break;
                    case SoundFontInstrument font: font.Program = program; break;
                }
            }
            for (int i = tracks.Length; i < _tracks.Length; i++) Release(_tracks[i]);
            _tracks = tracks;
        }
    }

    IInstrument CreateInstrument(int program) => _font is null
        ? new PolySynth(_sampleRate) { Volume = _synthVolume, Patch = SynthPatch.ForProgram(program) }
        : new SoundFontInstrument(_font, _sampleRate, program);

    /// <summary>Replace the notes, for example after a settings change. Playback continues from the same position.</summary>
    public void SetNotes(IEnumerable<DetectedNote> notes, BarGrid? grid)
    {
        var events = new List<NoteEvent>();
        var list = notes.ToList();
        foreach (var n in list)
        {
            float velocity = Math.Max(0.15f, n.Velocity / 127f);
            events.Add(new NoteEvent(n.Start, n.Pitch, velocity, true, n.Track));
            events.Add(new NoteEvent(Math.Max(n.Start + 0.01, n.End), n.Pitch, 0, false, n.Track));
        }
        // Add pitch bends as MIDI would: only on notes that sound alone on their track, and back to centre as each note ends.
        foreach (var track in list.GroupBy(n => n.Track))
            foreach (var n in PitchBend.Playable(track))
            {
                foreach (var b in n.Bends!) events.Add(new NoteEvent(Math.Clamp(b.Time, n.Start, n.End), 0, b.Semitones, false, n.Track, Bend: true));
                events.Add(new NoteEvent(Math.Max(n.Start + 0.01, n.End), 0, 0, false, n.Track, Bend: true));
            }

        var beats = new List<double>();
        var downbeats = new List<bool>();
        if (grid is not null)
        {
            double end = events.Count > 0 ? events.Max(e => e.Time) + grid.BarSeconds : 0;
            for (long b = (long)Math.Floor(grid.ToBeats(0)); ; b++)
            {
                double t = grid.ToSeconds(b);
                if (t > end) break;
                if (t < 0) continue;
                beats.Add(t);
                downbeats.Add(((b % grid.BeatsPerBar) + grid.BeatsPerBar) % grid.BeatsPerBar == 0);
            }
        }

        lock (_gate)
        {
            _notes = [.. events];
            _beats = [.. beats];
            _downbeats = [.. downbeats];
            Merge();
        }
    }

    /// <summary>Set the drum hits, as General MIDI drum notes played on the drum kit.</summary>
    public void SetDrums(IEnumerable<DetectedNote> hits)
    {
        var events = hits.SelectMany(h => new[]
        {
            new NoteEvent(h.Start, h.Pitch, Math.Max(0.2f, h.Velocity / 127f), true, DrumChannel),
            new NoteEvent(Math.Max(h.Start + 0.05, h.End), h.Pitch, 0, false, DrumChannel), // stop a SoundFont's looped drum sounds
        }).ToArray();
        lock (_gate)
        {
            _drumHits = events;
            Merge();
        }
    }

    /// <summary>Set the original recording (mono) to play in time with the notes, or null for none.</summary>
    public void SetSource(float[]? samples, int sampleRate)
    {
        lock (_gate)
        {
            _source = samples;
            _sourceRate = Math.Max(1, sampleRate);
        }
    }

    /// <summary>Set a channel's mix. The channel is a track index, <see cref="DrumChannel"/> or <see cref="SourceChannel"/>.</summary>
    /// <param name="gain">Linear, 1 = as is.</param>
    /// <param name="pan">0 = left, 0.5 = centre, 1 = right.</param>
    public void SetMix(int channel, double gain, double pan, bool mute, bool solo)
    {
        lock (_gate)
        {
            var ch = channel == DrumChannel ? _drumChannel : channel == SourceChannel ? _sourceChannel
                : channel >= 0 && channel < _tracks.Length ? _tracks[channel] : null;
            if (ch is null) return;
            ch.Gain = (float)Math.Max(0, gain);
            ch.Pan = (float)Math.Clamp(pan, 0, 1);
            ch.Mute = mute;
            ch.Solo = solo;
        }
    }

    /// <summary>Return the peak levels (linear) since the last call for each track, the drums, the recording and the mix.</summary>
    public (float[] Tracks, float Drums, float Source, float Master) ReadLevels()
    {
        lock (_gate)
        {
            var tracks = _tracks.Select(t => t.Peak).ToArray();
            var result = (tracks, _drumChannel.Peak, _sourceChannel.Peak, _masterPeak);
            foreach (var t in _tracks) t.Peak = 0;
            _drumChannel.Peak = _sourceChannel.Peak = _masterPeak = 0;
            return result;
        }
    }

    /// <param name="rate">Playback speed: 1 = song tempo, 0.5 = half speed.</param>
    public void Configure(double rangeStart, double rangeEnd, bool loop, double rate)
    {
        lock (_gate)
        {
            _rangeStart = Math.Max(0, rangeStart);
            _rangeEnd = rangeEnd > _rangeStart ? rangeEnd : double.MaxValue;
            _loop = loop;
            _rate = Math.Clamp(rate, 0.1, 4);
        }
    }

    public void Play(double from)
    {
        lock (_gate)
        {
            ReleaseAll();
            Seek(Math.Clamp(from, _rangeStart, _rangeEnd == double.MaxValue ? double.MaxValue : _rangeEnd));
            _playing = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _playing = false;
            ReleaseAll();
        }
    }

    /// <summary>
    /// Render from where <see cref="Play"/> started until playback stops at the range's end, plus
    /// <paramref name="tail"/> seconds for notes to ring out, as for writing the mix to a file. Use it on a
    /// separate player that no audio device is playing.
    /// </summary>
    public (float[] Left, float[] Right) RenderUntilStopped(double tail = 2, CancellationToken cancel = default)
    {
        const int block = 4096;
        var left = new List<float>();
        var right = new List<float>();
        var l = new float[block];
        var r = new float[block];
        int tailBlocks = (int)Math.Ceiling(tail * _sampleRate / block);
        while (tailBlocks > 0)
        {
            cancel.ThrowIfCancellationRequested();
            Render(l, r);
            left.AddRange(l);
            right.AddRange(r);
            if (!IsPlaying) tailBlocks--;
        }
        return ([.. left], [.. right]);
    }

    /// <summary>Render the mix, folded to mono, into <paramref name="output"/>. Pan is ignored.</summary>
    public void Render(Span<float> output) => Render(output, output, mono: true);

    /// <summary>Render the stereo mix into <paramref name="left"/> and <paramref name="right"/> (same length).</summary>
    public void Render(Span<float> left, Span<float> right) => Render(left, right, mono: false);

    void Render(Span<float> left, Span<float> right, bool mono)
    {
        left.Clear();
        if (!mono) right.Clear();
        double sampleRate = _sampleRate;
        int done = 0;
        lock (_gate)
        {
            while (_playing && done < left.Length)
            {
                double secondsPerSample = _rate / sampleRate;
                double blockEnd = _position + (left.Length - done) * secondsPerSample;
                double limit = Math.Min(blockEnd, _rangeEnd);

                // Find the next note event or range end that falls before the block ends.
                double nextTime = _nextEvent < _events.Length ? _events[_nextEvent].Time : double.MaxValue;
                if (nextTime < limit)
                {
                    int samples = Math.Clamp((int)Math.Round((nextTime - _position) / secondsPerSample), 0, left.Length - done);
                    RenderSpan(left.Slice(done, samples), right.Slice(done, samples), mono, secondsPerSample);
                    done += samples;
                    _position = nextTime;
                    Dispatch(_events[_nextEvent++]);
                    continue;
                }

                if (blockEnd >= _rangeEnd)
                {
                    int samples = Math.Clamp((int)Math.Round((_rangeEnd - _position) / secondsPerSample), 0, left.Length - done);
                    RenderSpan(left.Slice(done, samples), right.Slice(done, samples), mono, secondsPerSample);
                    done += samples;
                    ReleaseAll();
                    if (_loop && _rangeEnd != double.MaxValue) Seek(_rangeStart);
                    else { _position = _rangeEnd; _playing = false; }
                    continue;
                }

                // Past the last note of an open-ended range, stop once everything has been released.
                if (_nextEvent >= _events.Length && _tracks.All(t => t.Sounding.Count == 0) && _rangeEnd == double.MaxValue &&
                    (_events.Length == 0 || _position > _events[^1].Time + 1))
                {
                    if (_loop) Seek(_rangeStart);
                    else _playing = false;
                    continue;
                }

                RenderSpan(left[done..], right[done..], mono, secondsPerSample);
                _position = blockEnd;
                done = left.Length;
            }
            // Once stopped, let release tails ring out.
            if (done < left.Length) RenderSpan(left[done..], right[done..], mono, 0);
        }
    }

    void Dispatch(NoteEvent e)
    {
        if (e.Channel == DrumChannel)
        {
            if (e.On) _drumChannel.Instrument.NoteOn(e.Pitch, e.Velocity);
            else _drumChannel.Instrument.NoteOff(e.Pitch);
            return;
        }
        var ch = _tracks[Math.Clamp(e.Channel, 0, _tracks.Length - 1)];
        if (e.Bend) ch.Instrument.PitchBend(e.Velocity);
        else if (e.On) { ch.Instrument.NoteOn(e.Pitch, e.Velocity); ch.Sounding.Add(e.Pitch); }
        else if (ch.Sounding.Remove(e.Pitch)) ch.Instrument.NoteOff(e.Pitch);
    }

    /// <summary>Mix every channel for a span, plus any metronome clicks that fall inside it.</summary>
    void RenderSpan(Span<float> left, Span<float> right, bool mono, double secondsPerSample)
    {
        int n = left.Length;
        if (n == 0) return;
        if (_scratchLeft.Length < n) { _scratchLeft = new float[n]; _scratchRight = new float[n]; } // only if the host changes buffer size
        Span<float> bufferLeft = _scratchLeft.AsSpan(0, n), bufferRight = _scratchRight.AsSpan(0, n);
        bool anySolo = _tracks.Any(t => t.Solo) || _drumChannel.Solo || _sourceChannel.Solo;

        foreach (var ch in _tracks.Append(_drumChannel))
        {
            ch.Instrument.Render(bufferLeft, bufferRight); // muted channels still run, so their notes end on time
            Mix(bufferLeft, bufferRight, ch, left, right, mono, anySolo);
        }
        var buffer = bufferLeft;

        if (_source is { } source && secondsPerSample > 0)
        {
            double step = secondsPerSample * _sourceRate, index = _position * _sourceRate;
            for (int i = 0; i < n; i++, index += step)
            {
                int k = (int)index;
                float f = (float)(index - k);
                buffer[i] = k >= 0 && k + 1 < source.Length ? source[k] * (1 - f) + source[k + 1] * f : 0;
            }
            Mix(buffer, buffer, _sourceChannel, left, right, mono, anySolo);
        }

        if (Metronome || _clickSamples > 0)
        {
            double t = _position;
            for (int i = 0; i < n; i++, t += secondsPerSample)
            {
                if (Metronome && secondsPerSample > 0 && _nextBeat < _beats.Length && t >= _beats[_nextBeat])
                {
                    _clickFreq = _downbeats[_nextBeat] ? 1760 : 1320; // accent on beat 1
                    _clickSamples = (int)(0.03f * _sampleRate);
                    _clickPhase = 0;
                    _nextBeat++;
                }
                if (_clickSamples > 0)
                {
                    float env = _clickSamples / (0.03f * _sampleRate);
                    float click = MetronomeVolume * 0.4f * env * env * MathF.Sin(_clickPhase);
                    left[i] += click;
                    if (!mono) right[i] += click;
                    _clickPhase += 2 * MathF.PI * _clickFreq / _sampleRate;
                    _clickSamples--;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            _masterPeak = Math.Max(_masterPeak, Math.Abs(left[i]));
            if (!mono) _masterPeak = Math.Max(_masterPeak, Math.Abs(right[i]));
        }
    }

    /// <summary>
    /// Add a channel's audio at its gain and pan. Panning works as a balance control: both sides play at full
    /// level in the centre, and the far side fades out towards the edge. Mono folds the two sides together.
    /// </summary>
    static void Mix(ReadOnlySpan<float> inLeft, ReadOnlySpan<float> inRight, Channel ch, Span<float> left, Span<float> right, bool mono, bool anySolo)
    {
        float gain = ch.Mute || (anySolo && !ch.Solo) ? 0 : ch.Gain;
        if (gain == 0) return;
        float peak = ch.Peak;
        float gl = gain * Math.Min(1, 2 * (1 - ch.Pan)), gr = gain * Math.Min(1, 2 * ch.Pan);
        for (int i = 0; i < inLeft.Length; i++)
        {
            float l = inLeft[i], r = inRight[i];
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)) * gain);
            if (mono) left[i] += 0.5f * (l + r) * gain;
            else
            {
                left[i] += l * gl;
                right[i] += r * gr;
            }
        }
        ch.Peak = peak;
    }

    void Merge()
    {
        var all = new NoteEvent[_notes.Length + _drumHits.Length];
        _notes.CopyTo(all, 0);
        _drumHits.CopyTo(all, _notes.Length);
        // Put note-offs before note-ons at the same instant, so a repeated note is struck again.
        Array.Sort(all, (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.On.CompareTo(b.On));
        _events = all;
        Seek(_position);
    }

    void Seek(double time)
    {
        _position = time;
        _nextEvent = LowerBound(_events, time);
        _nextBeat = 0;
        while (_nextBeat < _beats.Length && _beats[_nextBeat] < time - 1e-9) _nextBeat++;
    }

    void ReleaseAll()
    {
        foreach (var t in _tracks) Release(t);
        _drumChannel.Instrument.AllNotesOff();
    }

    static void Release(Channel ch)
    {
        foreach (var p in ch.Sounding) ch.Instrument.NoteOff(p);
        ch.Sounding.Clear();
        ch.Instrument.PitchBend(0); // so a stop or jump during a bend leaves the next notes in tune
    }

    static int LowerBound(NoteEvent[] events, double time)
    {
        int lo = 0, hi = events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (events[mid].Time < time) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
