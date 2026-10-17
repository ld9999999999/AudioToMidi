using System.Collections.Concurrent;

namespace AudioToMidi.Core.Synth;

/// <summary>
/// A small synthesized drum kit for playback: a kick (a falling sine), a snare (a tone plus noise), and closed and
/// open hi-hats (bright noise; a closed hat cuts off an open one). It plays General MIDI drum notes 35/36 (kick),
/// 37–40 (snare), 42/44 (closed hat) and 46 (open hat); any other note sounds as a closed hat. Like
/// <see cref="PolySynth"/>, <see cref="NoteOn"/> may be called from any thread and <see cref="Render"/> never allocates.
/// </summary>
public sealed class DrumKit(int sampleRate) : IInstrument
{
    enum Kind { Kick, Snare, ClosedHat, OpenHat }

    sealed class Hit
    {
        public Kind Kind;
        public float Velocity, Phase, Time;
        public bool Active;
        public uint Noise = 22222;
        public float HighPassState, LastNoise;
    }

    readonly Hit[] _hits = Enumerable.Range(0, 12).Select(_ => new Hit()).ToArray();
    readonly ConcurrentQueue<(int Pitch, float Velocity)> _events = new();
    readonly float _sampleRate = sampleRate;

    public float Volume { get; set; } = 0.6f;

    public void NoteOn(int pitch, float velocity = 0.8f) => _events.Enqueue((pitch, Math.Clamp(velocity, 0f, 1f)));

    public void AllOff()
    {
        while (_events.TryDequeue(out _)) { }
        foreach (var h in _hits) h.Active = false;
    }

    void IInstrument.NoteOff(int pitch) { } // hits ring out on their own
    void IInstrument.AllNotesOff() { }

    void IInstrument.Render(Span<float> left, Span<float> right)
    {
        Render(left);
        left.CopyTo(right);
    }

    /// <summary>Render mono samples, overwriting <paramref name="output"/>.</summary>
    public void Render(Span<float> output)
    {
        while (_events.TryDequeue(out var e))
        {
            var kind = e.Pitch switch
            {
                35 or 36 => Kind.Kick,
                >= 37 and <= 40 => Kind.Snare,
                46 => Kind.OpenHat,
                _ => Kind.ClosedHat,
            };
            if (kind == Kind.ClosedHat) foreach (var h in _hits) if (h.Active && h.Kind == Kind.OpenHat) h.Active = false; // choke
            var hit = _hits.FirstOrDefault(h => !h.Active) ?? _hits.MaxBy(h => h.Time)!;
            hit.Kind = kind; hit.Velocity = e.Velocity; hit.Phase = 0; hit.Time = 0; hit.Active = true;
        }

        output.Clear();
        float dt = 1 / _sampleRate;
        foreach (var h in _hits)
        {
            if (!h.Active) continue;
            for (int i = 0; i < output.Length; i++)
            {
                float t = h.Time, v;
                switch (h.Kind)
                {
                    case Kind.Kick:
                    {
                        float freq = 48 + 110 * MathF.Exp(-t * 28);
                        h.Phase += 2 * MathF.PI * freq * dt;
                        v = MathF.Sin(h.Phase) * MathF.Exp(-t * 9) + 0.25f * MathF.Exp(-t * 300) * Noise(h); // body + click
                        if (t > 0.5f) h.Active = false;
                        break;
                    }
                    case Kind.Snare:
                    {
                        h.Phase += 2 * MathF.PI * 190 * dt;
                        v = 0.45f * MathF.Sin(h.Phase) * MathF.Exp(-t * 22) + 0.6f * HighPass(h, Noise(h)) * MathF.Exp(-t * 14);
                        if (t > 0.35f) h.Active = false;
                        break;
                    }
                    case Kind.ClosedHat:
                        v = 0.35f * HighPass(h, Noise(h)) * MathF.Exp(-t * 60);
                        if (t > 0.12f) h.Active = false;
                        break;
                    default:
                        v = 0.3f * HighPass(h, Noise(h)) * MathF.Exp(-t * 7);
                        if (t > 0.7f) h.Active = false;
                        break;
                }
                output[i] += v * (0.3f + 0.7f * h.Velocity);
                h.Time += dt;
                if (!h.Active) break;
            }
        }
        for (int i = 0; i < output.Length; i++) output[i] = MathF.Tanh(output[i] * Volume);
    }

    static float Noise(Hit h)
    {
        h.Noise ^= h.Noise << 13; h.Noise ^= h.Noise >> 17; h.Noise ^= h.Noise << 5;
        return h.Noise / (float)uint.MaxValue * 2 - 1;
    }

    /// <summary>A one-pole high-pass, for the metallic hiss of cymbals and snare wires.</summary>
    static float HighPass(Hit h, float x)
    {
        float y = 0.85f * (h.HighPassState + x - h.LastNoise);
        h.HighPassState = y;
        h.LastNoise = x;
        return y;
    }
}
