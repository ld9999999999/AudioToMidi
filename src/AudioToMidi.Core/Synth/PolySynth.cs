using System.Collections.Concurrent;

namespace AudioToMidi.Core.Synth;

/// <summary>
/// A small polyphonic synth for previewing notes. It mixes two detuned saw waves, a square wave and a sub
/// oscillator (smoothed with PolyBLEP to avoid aliasing), then shapes them with a resonant low-pass filter
/// that has its own envelope, and a volume envelope. <see cref="NoteOn"/>/<see cref="NoteOff"/> may be called from any thread;
/// <see cref="Render"/> runs on the audio thread and never allocates.
/// </summary>
public sealed class PolySynth : IInstrument
{
    readonly struct Event(int pitch, float velocity, bool on, bool allOff = false, bool bend = false)
    {
        public readonly int Pitch = pitch;
        public readonly float Velocity = velocity; // semitones for a bend
        public readonly bool On = on, AllOff = allOff, Bend = bend;
    }

    readonly Voice[] _voices;
    readonly ConcurrentQueue<Event> _events = new();
    readonly float _sampleRate;
    long _age;
    float _bend = 1; // frequency factor (audio thread)

    public PolySynth(int sampleRate, int maxVoices = 16)
    {
        _sampleRate = sampleRate;
        _voices = Enumerable.Range(0, maxVoices).Select(_ => new Voice()).ToArray();
    }

    public int SampleRate => (int)_sampleRate;

    /// <summary>Applies to notes started after the change.</summary>
    public SynthPatch Patch { get; set; } = SynthPatch.SynthKeys;

    /// <summary>Master volume, 0 to 1.</summary>
    public float Volume { get; set; } = 0.5f;

    /// <summary>Voices currently sounding, including release tails.</summary>
    public int ActiveVoices => _voices.Count(v => v.Active);

    public void NoteOn(int pitch, float velocity = 0.8f) => _events.Enqueue(new Event(pitch, Math.Clamp(velocity, 0f, 1f), true));
    public void NoteOff(int pitch) => _events.Enqueue(new Event(pitch, 0, false));
    public void PitchBend(float semitones) => _events.Enqueue(new Event(0, semitones, false, bend: true));
    public void AllNotesOff() => _events.Enqueue(new Event(0, 0, false, allOff: true));

    /// <summary>Render mono samples, overwriting <paramref name="output"/>.</summary>
    public void Render(Span<float> output)
    {
        while (_events.TryDequeue(out var e))
        {
            if (e.Bend) _bend = MathF.Pow(2, e.Velocity / 12);
            else if (e.AllOff) { foreach (var v in _voices) v.Release(); }
            else if (e.On) Allocate(e.Pitch).Start(e.Pitch, e.Velocity, Patch, _sampleRate, ++_age);
            else foreach (var v in _voices) if (v.Active && v.Pitch == e.Pitch && !v.Releasing) v.Release();
        }

        output.Clear();
        foreach (var v in _voices)
            if (v.Active) v.Render(output, _sampleRate, _bend);

        float gain = Volume * 0.35f;
        for (int i = 0; i < output.Length; i++)
            output[i] = MathF.Tanh(output[i] * gain); // gentle limiter, so chords never clip hard
    }

    void IInstrument.Render(Span<float> left, Span<float> right)
    {
        Render(left);
        left.CopyTo(right);
    }

    /// <summary>Reuse the voice already playing this pitch, else a free voice, else take the oldest one.</summary>
    Voice Allocate(int pitch)
    {
        foreach (var v in _voices) if (v.Active && v.Pitch == pitch) return v;
        foreach (var v in _voices) if (!v.Active) return v;
        Voice oldest = _voices[0];
        foreach (var v in _voices) if (v.Age < oldest.Age) oldest = v;
        return oldest;
    }

    sealed class Voice
    {
        public int Pitch;
        public long Age;
        public bool Active, Releasing;

        SynthPatch _p = SynthPatch.SynthKeys;
        float _velocity, _freq;
        double _phase1, _phase2, _phase3, _phaseSub;
        readonly Envelope _amp = new(), _filter = new();
        float _ic1, _ic2; // filter state

        public void Start(int pitch, float velocity, SynthPatch patch, float sampleRate, long age)
        {
            bool retrigger = Active && Pitch == pitch;
            Pitch = pitch; Age = age; Active = true; Releasing = false;
            _p = patch; _velocity = velocity;
            _freq = 440f * MathF.Pow(2, (pitch - 69) / 12f);
            if (!retrigger) { _phase1 = 0; _phase2 = 0.37; _phase3 = 0.11; _phaseSub = 0; _ic1 = _ic2 = 0; }
            _amp.Start(patch.AmpAttack, patch.AmpDecay, patch.AmpSustain, patch.AmpRelease, sampleRate, fromCurrent: retrigger);
            _filter.Start(patch.FilterAttack, patch.FilterDecay, patch.FilterSustain, patch.FilterRelease, sampleRate, fromCurrent: retrigger);
        }

        public void Release()
        {
            if (!Active) return;
            Releasing = true;
            _amp.Release();
            _filter.Release();
        }

        public void Render(Span<float> output, float sampleRate, float bend)
        {
            float detune = MathF.Pow(2, _p.DetuneCents / 1200f), freq = _freq * bend;
            double inc1 = freq / detune / sampleRate, inc2 = freq * detune / sampleRate;
            double inc3 = freq / sampleRate, incSub = freq / 2 / sampleRate;
            float keyTrack = MathF.Pow(_freq / 261.6f, _p.KeyTracking);
            float k = 2 - 2 * Math.Clamp(_p.Resonance, 0f, 0.95f); // filter damping
            float level = 0.25f + 0.75f * _velocity;

            for (int i = 0; i < output.Length; i++)
            {
                float env = _amp.Next();
                if (_amp.Done) { Active = false; return; }
                float fenv = _filter.Next();

                float saw = Saw(ref _phase1, inc1) + Saw(ref _phase2, inc2);
                float x = _p.SawLevel * 0.5f * saw + _p.SquareLevel * Square(ref _phase3, inc3) + _p.SubLevel * Sine(ref _phaseSub, incSub);

                // Harder notes open the filter more, so they sound brighter.
                float cutoff = _p.Cutoff * keyTrack * MathF.Pow(2, _p.FilterEnvOctaves * fenv * (0.5f + 0.5f * _velocity));
                cutoff = Math.Clamp(cutoff, 20f, sampleRate * 0.45f);
                float g = MathF.Tan(MathF.PI * cutoff / sampleRate);

                // State-variable filter (Zavalishin's TPT form), low-pass output.
                float hp = (x - (k + g) * _ic1 - _ic2) / (1 + g * (k + g));
                float bp = g * hp + _ic1;
                float lp = g * bp + _ic2;
                _ic1 = g * hp + bp;
                _ic2 = g * bp + lp;

                output[i] += lp * env * level;
            }
        }

        static float PolyBlep(double t, double dt)
        {
            if (t < dt) { t /= dt; return (float)(t + t - t * t - 1); }
            if (t > 1 - dt) { t = (t - 1) / dt; return (float)(t * t + t + t + 1); }
            return 0;
        }

        static float Saw(ref double phase, double inc)
        {
            float v = (float)(2 * phase - 1) - PolyBlep(phase, inc);
            phase += inc; if (phase >= 1) phase -= 1;
            return v;
        }

        static float Square(ref double phase, double inc)
        {
            float v = (phase < 0.5 ? 1f : -1f) + PolyBlep(phase, inc) - PolyBlep((phase + 0.5) % 1, inc);
            phase += inc; if (phase >= 1) phase -= 1;
            return v;
        }

        static float Sine(ref double phase, double inc)
        {
            float v = MathF.Sin((float)(2 * Math.PI * phase));
            phase += inc; if (phase >= 1) phase -= 1;
            return v;
        }
    }

    /// <summary>Linear attack, exponential decay and release.</summary>
    sealed class Envelope
    {
        enum Stage { Idle, Attack, Decay, Sustain, Release }
        Stage _stage = Stage.Idle;
        float _value, _attackStep, _decayCoef, _releaseCoef, _sustain;

        public bool Done => _stage == Stage.Idle;

        public void Start(float attack, float decay, float sustain, float release, float sampleRate, bool fromCurrent)
        {
            if (!fromCurrent) _value = 0;
            _attackStep = 1f / Math.Max(1, attack * sampleRate);
            _decayCoef = Coefficient(decay, sampleRate);
            _releaseCoef = Coefficient(release, sampleRate);
            _sustain = sustain;
            _stage = Stage.Attack;
        }

        public void Release()
        {
            if (_stage != Stage.Idle) _stage = Stage.Release;
        }

        public float Next()
        {
            switch (_stage)
            {
                case Stage.Attack:
                    _value += _attackStep;
                    if (_value >= 1) { _value = 1; _stage = Stage.Decay; }
                    break;
                case Stage.Decay:
                    _value = _sustain + (_value - _sustain) * _decayCoef;
                    if (_value - _sustain < 1e-4f) { _value = _sustain; _stage = _sustain > 0 ? Stage.Sustain : Stage.Release; }
                    break;
                case Stage.Release:
                    _value *= _releaseCoef;
                    if (_value < 1e-4f) { _value = 0; _stage = Stage.Idle; }
                    break;
            }
            return _value;
        }

        // Fall to about 0.1% of the starting distance after `seconds`.
        static float Coefficient(float seconds, float sampleRate) =>
            MathF.Exp(MathF.Log(1e-3f) / Math.Max(1, seconds * sampleRate));
    }
}
