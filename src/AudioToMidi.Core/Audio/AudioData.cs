namespace AudioToMidi.Core.Audio;

/// <summary>Mono audio samples in the range [-1, 1].</summary>
public sealed record AudioData(float[] Samples, int SampleRate)
{
    public double Duration => (double)Samples.Length / SampleRate;
}
