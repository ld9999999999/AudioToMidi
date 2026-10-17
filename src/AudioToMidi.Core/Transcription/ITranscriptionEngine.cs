using AudioToMidi.Core.Audio;

namespace AudioToMidi.Core.Transcription;

public interface ITranscriptionEngine
{
    string Name { get; }

    /// <summary>Sample rate the engine expects its input at.</summary>
    int SampleRate { get; }

    /// <summary>Run the slow analysis step. The result can be decoded many times with different settings.</summary>
    Task<IAnalysis> AnalyzeAsync(AudioData audio, IProgress<double>? progress = null, CancellationToken ct = default);
}

public interface IAnalysis
{
    /// <summary>Turn the raw analysis into notes. This is fast and runs whenever a setting changes.</summary>
    IReadOnlyList<DetectedNote> Decode(NoteSettings settings);
}
