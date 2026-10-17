using System.Buffers.Binary;

namespace AudioToMidi.Core.Audio;

/// <summary>
/// Small WAV reader for 8/16/24/32-bit PCM and 32/64-bit float files.
/// </summary>
public static class WavReader
{
    const ushort FormatPcm = 1;
    const ushort FormatFloat = 3;
    const ushort FormatExtensible = 0xFFFE;

    public static AudioData Read(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Read the file and mix it down to mono.</summary>
    public static AudioData Read(byte[] bytes)
    {
        var (channels, rate) = ReadChannels(bytes);
        if (channels.Length == 1) return new AudioData(channels[0], rate);
        var mono = new float[channels[0].Length];
        float scale = 1f / channels.Length;
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            foreach (var c in channels) sum += c[i];
            mono[i] = sum * scale;
        }
        return new AudioData(mono, rate);
    }

    public static (float[][] Channels, int SampleRate) ReadChannels(string path) => ReadChannels(File.ReadAllBytes(path));

    /// <summary>Read each channel separately.</summary>
    public static (float[][] Channels, int SampleRate) ReadChannels(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        ushort format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        ReadOnlySpan<byte> data = default;
        bool haveFmt = false, haveData = false;

        int pos = 12;
        while (pos + 8 <= span.Length)
        {
            var id = span.Slice(pos, 4);
            // ffmpeg writes 0xFFFFFFFF as the size when it cannot seek back, so clamp to what's there.
            long size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 4, 4));
            int body = pos + 8;
            int len = (int)Math.Min(size, span.Length - body);

            if (id.SequenceEqual("fmt "u8))
            {
                var fmt = span.Slice(body, len);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible && fmt.Length >= 26)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // first 2 bytes of the SubFormat GUID
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = span.Slice(body, len);
                haveData = true;
            }

            pos = body + len + (len & 1); // chunks are word aligned
        }

        if (!haveFmt || !haveData) throw new InvalidDataException("WAV file is missing a fmt or data chunk.");
        if (channels == 0) throw new InvalidDataException("WAV file has zero channels.");

        int bytesPerSample = bits / 8;
        int frameBytes = bytesPerSample * channels;
        int frames = data.Length / frameBytes;
        var result = new float[channels][];
        for (int c = 0; c < channels; c++) result[c] = new float[frames];

        for (int f = 0; f < frames; f++)
        {
            var frame = data.Slice(f * frameBytes, frameBytes);
            for (int c = 0; c < channels; c++)
                result[c][f] = ReadSample(frame.Slice(c * bytesPerSample, bytesPerSample), format, bits);
        }

        return (result, sampleRate);
    }

    static float ReadSample(ReadOnlySpan<byte> s, ushort format, ushort bits) => (format, bits) switch
    {
        (FormatFloat, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
        (FormatFloat, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
        (FormatPcm, 8) => (s[0] - 128) / 128f,
        (FormatPcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        (FormatPcm, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
        (FormatPcm, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        _ => throw new NotSupportedException($"Unsupported WAV sample format {format} with {bits} bits."),
    };

    /// <summary>Write a mono 32-bit float WAV, handy for tests and for listening to intermediate signals.</summary>
    public static void WriteFloat(string path, AudioData audio)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = audio.Samples.Length * 4;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write(FormatFloat); w.Write((ushort)1);
        w.Write(audio.SampleRate); w.Write(audio.SampleRate * 4); w.Write((ushort)4); w.Write((ushort)32);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var x in audio.Samples) w.Write(x);
    }

    /// <summary>Write a stereo 32-bit float WAV. Both channels must have the same length.</summary>
    public static void WriteFloat(string path, float[] left, float[] right, int sampleRate)
    {
        if (left.Length != right.Length) throw new ArgumentException("Both channels need the same length.");
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = left.Length * 8;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write(FormatFloat); w.Write((ushort)2);
        w.Write(sampleRate); w.Write(sampleRate * 8); w.Write((ushort)8); w.Write((ushort)32);
        w.Write("data"u8); w.Write(dataBytes);
        for (int i = 0; i < left.Length; i++) { w.Write(left[i]); w.Write(right[i]); }
    }
}
