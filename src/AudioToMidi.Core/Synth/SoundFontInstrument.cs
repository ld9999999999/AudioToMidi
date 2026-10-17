using System.Collections.Concurrent;
using MeltySynth;

namespace AudioToMidi.Core.Synth;

/// <summary>
/// One mixer channel played from a SoundFont, either a General MIDI program or the drum kit. Each channel
/// has its own MeltySynth synthesizer, so each track keeps its own level, pan and meter. Like <see cref="PolySynth"/>,
/// note methods may be called from any thread and take effect at the next <see cref="Render"/>.
/// </summary>
public sealed class SoundFontInstrument : IInstrument
{
    enum Kind { On, Off, AllOff, Program, Bend }

    readonly record struct Event(Kind Kind, int Value, int Velocity = 0);

    /// <summary>
    /// MeltySynth's master volume. General MIDI SoundFonts are balanced for one synth playing a whole song,
    /// so this brings a single part to about the level of the built-in synth.
    /// </summary>
    const float Volume = 0.5f;

    readonly Synthesizer _synth;
    readonly int _channel;
    readonly ConcurrentQueue<Event> _events = new();
    int _program;

    /// <param name="program">General MIDI program, 0-based. Ignored for <paramref name="percussion"/>.</param>
    /// <param name="percussion">Play the SoundFont's drum kit (MIDI channel 10) instead.</param>
    public SoundFontInstrument(SoundFont font, int sampleRate, int program, bool percussion = false)
    {
        _synth = new Synthesizer(font, new SynthesizerSettings(sampleRate) { MaximumPolyphony = 64 }) { MasterVolume = Volume };
        _channel = percussion ? _synth.PercussionChannel : 0;
        SoundFont = font;
        IsPercussion = percussion;
        Program = program;
    }

    public SoundFont SoundFont { get; }
    public bool IsPercussion { get; }

    /// <summary>General MIDI program (0-based). Applies to notes started after the change.</summary>
    public int Program
    {
        get => _program;
        set
        {
            _program = Math.Clamp(value, 0, 127);
            if (!IsPercussion) _events.Enqueue(new Event(Kind.Program, _program));
        }
    }

    public void NoteOn(int pitch, float velocity) =>
        _events.Enqueue(new Event(Kind.On, pitch, Math.Clamp((int)MathF.Round(velocity * 127), 1, 127)));

    public void NoteOff(int pitch) => _events.Enqueue(new Event(Kind.Off, pitch));

    public void AllNotesOff() => _events.Enqueue(new Event(Kind.AllOff, 0));

    /// <summary>Bend within the General MIDI default range of ±2 semitones.</summary>
    public void PitchBend(float semitones) =>
        _events.Enqueue(new Event(Kind.Bend, Math.Clamp(8192 + (int)MathF.Round(semitones * 4096), 0, 16383)));

    public void Render(Span<float> left, Span<float> right)
    {
        while (_events.TryDequeue(out var e))
        {
            switch (e.Kind)
            {
                case Kind.On: _synth.NoteOn(_channel, e.Value, e.Velocity); break;
                case Kind.Off: _synth.NoteOff(_channel, e.Value); break;
                case Kind.AllOff: _synth.NoteOffAll(_channel, immediate: false); break;
                case Kind.Program: _synth.ProcessMidiMessage(_channel, 0xC0, e.Value, 0); break;
                case Kind.Bend: _synth.ProcessMidiMessage(_channel, 0xE0, e.Value & 0x7F, e.Value >> 7); break;
            }
        }
        _synth.Render(left, right);
    }
}
