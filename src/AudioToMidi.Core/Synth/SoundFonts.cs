using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MeltySynth;
using NVorbis;

namespace AudioToMidi.Core.Synth;

/// <summary>
/// Find and load General MIDI SoundFonts. Both .sf2 and .sf3 (SoundFont 2 with Ogg Vorbis–compressed samples,
/// as MuseScore ships them) work; .sf3 samples are decoded to 16-bit when loaded.
/// </summary>
public static class SoundFonts
{
    /// <summary>The app's own folder for SoundFonts, searched first.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioToMidi", "soundfonts");

    public static bool IsSoundFont(string path) => Path.GetExtension(path).ToLowerInvariant() is ".sf2" or ".sf3";

    /// <summary>
    /// List SoundFonts on this computer, best first: the app's folder, then the usual system places (macOS's
    /// Sounds/Banks, Linux's FluidR3_GM and similar). In each folder .sf2 files come before .sf3, which
    /// take longer to load and much more memory once decoded. MuseScore's own fonts are left out of the
    /// automatic choice because they are tuned for its player, and MeltySynth plays some of their layered
    /// instruments far too quietly. They can still be chosen by hand.
    /// </summary>
    public static IEnumerable<string> Installed()
    {
        foreach (var dir in SearchFolders())
        {
            List<string> files;
            try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Where(IsSoundFont).ToList() : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files.OrderBy(f => Path.GetExtension(f).Equals(".sf3", StringComparison.OrdinalIgnoreCase))
                                   .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return f;
        }
    }

    static IEnumerable<string> SearchFolders()
    {
        yield return Folder;
        if (OperatingSystem.IsMacOS())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Audio", "Sounds", "Banks");
        else if (!OperatingSystem.IsWindows())
        {
            yield return "/usr/share/sounds/sf2";
            yield return "/usr/share/soundfonts";
            yield return "/usr/share/sounds/sf3";
        }
    }

    /// <summary>
    /// Return the SoundFont to use for <paramref name="path"/>. That is the file itself, or for a .zip (a common
    /// way to share SoundFonts) the largest .sf2 or .sf3 inside it, extracted into <paramref name="folder"/>
    /// (default <see cref="Folder"/>).
    /// </summary>
    public static string Import(string path, string? folder = null)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return path;
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.Where(e => IsSoundFont(e.Name)).MaxBy(e => e.Length)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no .sf2 or .sf3 in it.");
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, entry.Name); // use the file name only, ignoring folders inside the archive
        if (!File.Exists(target) || new FileInfo(target).Length != entry.Length)
        {
            var part = target + ".part";
            entry.ExtractToFile(part, overwrite: true);
            File.Move(part, target, overwrite: true);
        }
        return target;
    }

    /// <summary>Load an .sf2 or .sf3, and throw if the file is not a SoundFont.</summary>
    public static SoundFont Load(string path) =>
        IsCompressed(path) ? new SoundFont(new MemoryStream(Decompress(File.ReadAllBytes(path)))) : new SoundFont(path);

    /// <summary>Check for an .sf3, whose version (the INFO list's ifil) is 3.</summary>
    static bool IsCompressed(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[Math.Min(4096, stream.Length)];
        stream.ReadExactly(head);
        int at = head.AsSpan().IndexOf("ifil"u8);
        return at >= 0 && at + 10 <= head.Length && BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(at + 8)) >= 3;
    }

    const int HeaderSize = 46, Padding = 46; // sample header record size; silent frames SoundFont 2 needs after each sample
    const ushort Vorbis = 0x10;              // sample type flag of a compressed sample

    /// <summary>
    /// Turn an .sf3 into an .sf2. In an .sf3 each sample is an Ogg Vorbis stream whose header start and end
    /// are byte offsets into the sample data, and whose loop points count frames from its own start. Each
    /// sample is decoded in parallel straight into its place in the new sample data, followed by silence, and
    /// the headers are rewritten to match. Everything else is copied unchanged.
    /// </summary>
    public static byte[] Decompress(byte[] sf3)
    {
        var lists = Lists(sf3);
        var (smplAt, smplSize) = Chunk(sf3, lists["sdta"], "smpl");
        var (shdrAt, shdrSize) = Chunk(sf3, lists["pdta"], "shdr");
        int count = shdrSize / HeaderSize - 1; // the last record only ends the list

        // Find each distinct sample's frame count, then where it goes.
        var sources = new Dictionary<(uint Start, uint End, bool Compressed), long>(); // → frames
        var keys = new (uint, uint, bool)[count];
        for (int i = 0; i < count; i++)
        {
            var h = sf3.AsSpan(shdrAt + i * HeaderSize, HeaderSize);
            uint start = U32(h, 20), end = U32(h, 24);
            bool compressed = (BinaryPrimitives.ReadUInt16LittleEndian(h[44..]) & Vorbis) != 0;
            var key = keys[i] = (start, end, compressed);
            if (sources.ContainsKey(key)) continue;
            if (!compressed) sources[key] = Math.Max(0, (long)end - start);
            else
            {
                using var reader = OpenVorbis(sf3, smplAt, smplSize, start, end);
                sources[key] = reader.TotalSamples;
            }
        }
        var offsets = new Dictionary<(uint, uint, bool), long>(); // → first frame in the new sample data
        long frames = 0;
        foreach (var (key, length) in sources)
        {
            offsets[key] = frames;
            frames += length + Padding;
        }
        if (frames * 2 > int.MaxValue - 1024) throw new InvalidDataException("The SoundFont's samples are too large to decode.");

        // Write RIFF sfbk with INFO (version set to 2.4), an sdta holding only smpl, and pdta with new headers.
        var (infoAt, infoSize) = lists["INFO"];
        var (pdtaAt, pdtaSize) = lists["pdta"];
        int smplBytes = (int)(frames * 2);
        int sdtaSize = 4 + 8 + smplBytes;
        var output = new byte[12 + 8 + infoSize + 8 + sdtaSize + 8 + pdtaSize];
        var o = output.AsSpan();
        int at = 0;
        void Id(string id) { Encoding.ASCII.GetBytes(id, output.AsSpan(at, 4)); at += 4; }
        void Size(int size) { BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at, 4), size); at += 4; }

        Id("RIFF"); Size(output.Length - 8); Id("sfbk");
        Id("LIST"); Size(infoSize);
        int infoOut = at;
        sf3.AsSpan(infoAt, infoSize).CopyTo(o[at..]); at += infoSize;
        var (ifilAt, _) = Chunk(output, (infoOut, infoSize), "ifil");
        BinaryPrimitives.WriteUInt16LittleEndian(o[ifilAt..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(o[(ifilAt + 2)..], 4);

        Id("LIST"); Size(sdtaSize); Id("sdta"); Id("smpl"); Size(smplBytes);
        int dataOut = at;
        at += smplBytes; // zero-filled, so the padding between samples is already silent

        Id("LIST"); Size(pdtaSize);
        int pdtaOut = at;
        sf3.AsSpan(pdtaAt, pdtaSize).CopyTo(o[at..]);
        int shdrOut = pdtaOut + (shdrAt - pdtaAt);
        for (int i = 0; i < count; i++)
        {
            var h = output.AsSpan(shdrOut + i * HeaderSize, HeaderSize);
            var key = keys[i];
            long start = offsets[key], length = sources[key];
            long loopStart = U32(h, 28), loopEnd = U32(h, 32);
            if (!key.Item3) { loopStart -= key.Item1; loopEnd -= key.Item1; } // loop points were absolute in the old data
            WriteU32(h, 20, start);
            WriteU32(h, 24, start + length);
            WriteU32(h, 28, start + Math.Clamp(loopStart, 0, length));
            WriteU32(h, 32, start + Math.Clamp(loopEnd, 0, length));
            BinaryPrimitives.WriteUInt16LittleEndian(h[44..], (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(h[44..]) & ~Vorbis));
        }

        Parallel.ForEach(sources, source =>
        {
            var ((start, end, compressed), length) = (source.Key, source.Value);
            var target = output.AsSpan(dataOut + (int)(offsets[source.Key] * 2), (int)length * 2);
            if (!compressed)
            {
                if (smplAt + (long)end * 2 <= smplAt + smplSize) sf3.AsSpan(smplAt + (int)start * 2, (int)length * 2).CopyTo(target);
                return;
            }
            using var reader = OpenVorbis(sf3, smplAt, smplSize, start, end);
            int channels = reader.Channels;
            var buffer = new float[4096 * channels];
            long done = 0;
            while (done < length)
            {
                // Never ask for more than the stream holds, because NVorbis 0.10.5 can hang reading past the end.
                int want = (int)Math.Min(4096, length - done) * channels;
                int got = reader.ReadSamples(buffer, 0, want) / channels;
                if (got <= 0) break;
                for (int k = 0; k < got; k++)
                {
                    short s = (short)Math.Clamp(MathF.Round(buffer[k * channels] * 32767), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(target[(int)((done + k) * 2)..], s);
                }
                done += got;
            }
        });
        return output;
    }

    static VorbisReader OpenVorbis(byte[] file, int smplAt, int smplSize, uint start, uint end)
    {
        if (end <= start || end > smplSize) throw new InvalidDataException($"A compressed sample's data ({start}–{end}) is outside the sample chunk.");
        return new VorbisReader(new MemoryStream(file, smplAt + (int)start, (int)(end - start), writable: false), closeOnDispose: true);
    }

    /// <summary>Find the file's top-level LIST chunks by type, with where each one's contents start (after the type) and its size including the type.</summary>
    static Dictionary<string, (int At, int Size)> Lists(byte[] file)
    {
        if (file.Length < 12 || Encoding.ASCII.GetString(file, 0, 4) != "RIFF" || Encoding.ASCII.GetString(file, 8, 4) != "sfbk")
            throw new InvalidDataException("Not a SoundFont (no RIFF sfbk header).");
        var lists = new Dictionary<string, (int, int)>();
        for (int at = 12; at + 12 <= file.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            if (size < 0 || at + 8L + size > file.Length) throw new InvalidDataException("The SoundFont is truncated.");
            if (Encoding.ASCII.GetString(file, at, 4) == "LIST") lists[Encoding.ASCII.GetString(file, at + 8, 4)] = (at + 8, size);
            at += 8 + size + (size & 1);
        }
        foreach (var name in new[] { "INFO", "sdta", "pdta" })
            if (!lists.ContainsKey(name)) throw new InvalidDataException($"The SoundFont has no {name} list.");
        return lists;
    }

    /// <summary>Find a sub-chunk of a LIST (as <see cref="Lists"/> returns it), with where its data starts and its size.</summary>
    static (int At, int Size) Chunk(byte[] file, (int At, int Size) list, string id)
    {
        for (int at = list.At + 4; at + 8 <= list.At + list.Size;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            if (Encoding.ASCII.GetString(file, at, 4) == id) return (at + 8, size);
            at += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"The SoundFont has no {id} chunk.");
    }

    static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    static void WriteU32(Span<byte> s, int at, long value) => BinaryPrimitives.WriteUInt32LittleEndian(s[at..], (uint)value);
}
