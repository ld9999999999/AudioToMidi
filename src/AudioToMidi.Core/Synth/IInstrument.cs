namespace AudioToMidi.Core.Synth;

/// <summary>
/// A sound source the mixer (<see cref="NotePlayer"/>) plays notes on: the built-in <see cref="PolySynth"/>
/// and <see cref="DrumKit"/>, or a <see cref="SoundFontInstrument"/>. Note methods may be called from any
/// thread and take effect at the next render; <see cref="Render"/> runs on the audio thread.
/// </summary>
public interface IInstrument
{
    void NoteOn(int pitch, float velocity);
    void NoteOff(int pitch);
    /// <summary>Release every note and let it ring out.</summary>
    void AllNotesOff();
    /// <summary>Bend every note, current and future, by this many semitones (0 = in tune), as a MIDI channel does.</summary>
    void PitchBend(float semitones) { }
    /// <summary>Render stereo, overwriting both spans (same length). Mono instruments write the same signal to both.</summary>
    void Render(Span<float> left, Span<float> right);
}
