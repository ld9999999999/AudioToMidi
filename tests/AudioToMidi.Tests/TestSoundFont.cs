using System.Text;

namespace AudioToMidi.Tests;

/// <summary>
/// Build a tiny SoundFont 2 in memory: one looped 440 Hz sine sample (root key A4) used by three presets:
/// program 0, program 33 tuned an octave up, and a drum kit (bank 128, program 0).
/// </summary>
static class TestSoundFont
{
    public const int SampleRate = 44000; // 440 Hz is exactly 100 samples a cycle, so the loop is seamless

    public static byte[] Build()
    {
        const int length = 4400, loopStart = 100;
        var smpl = new byte[(length + 46) * 2];
        for (int i = 0; i < length; i++)
            BitConverter.TryWriteBytes(smpl.AsSpan(i * 2), (short)(16000 * Math.Sin(2 * Math.PI * i / 100)));

        var phdr = new MemoryStream();
        Preset(phdr, "Sine", 0, 0, 0);
        Preset(phdr, "Octave", 33, 0, 1);
        Preset(phdr, "Kit", 0, 128, 2);
        Preset(phdr, "EOP", 0, 0, 3);
        var pbag = Records(4, [0, 0], [1, 0], [3, 0], [4, 0]);            // generator, modulator index of each zone
        var pgen = Records(4, [41, 0], [51, 12], [41, 0], [41, 0], [0, 0]); // instrument 0; +12 semitones coarse tune
        var inst = new MemoryStream();
        Name(inst, "Sine"); inst.Write(BitConverter.GetBytes((ushort)0));
        Name(inst, "EOI"); inst.Write(BitConverter.GetBytes((ushort)1));
        var ibag = Records(4, [0, 0], [2, 0]);
        var igen = Records(4, [54, 1], [53, 0], [0, 0]);                  // loop continuously; sample 0
        var shdr = new MemoryStream();
        Name(shdr, "Sine");
        foreach (var v in new[] { 0, length, loopStart, length, SampleRate }) shdr.Write(BitConverter.GetBytes(v));
        shdr.Write([69, 0]); shdr.Write(BitConverter.GetBytes((ushort)0)); shdr.Write(BitConverter.GetBytes((ushort)1));
        shdr.Write(new byte[46]); // terminal record

        var info = List("INFO", Chunk("ifil", [2, 0, 1, 0]), Chunk("isng", Encoding.ASCII.GetBytes("EMU8000\0")), Chunk("INAM", Encoding.ASCII.GetBytes("Test\0\0")));
        var sdta = List("sdta", Chunk("smpl", smpl));
        var pdta = List("pdta", Chunk("phdr", phdr.ToArray()), Chunk("pbag", pbag), Chunk("pmod", new byte[10]), Chunk("pgen", pgen),
            Chunk("inst", inst.ToArray()), Chunk("ibag", ibag), Chunk("imod", new byte[10]), Chunk("igen", igen), Chunk("shdr", shdr.ToArray()));
        var body = Encoding.ASCII.GetBytes("sfbk").Concat(info).Concat(sdta).Concat(pdta).ToArray();
        return Chunk("RIFF", body);
    }

    static void Name(Stream s, string name)
    {
        var bytes = new byte[20];
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        s.Write(bytes);
    }

    static void Preset(Stream s, string name, ushort preset, ushort bank, ushort bag)
    {
        Name(s, name);
        foreach (var v in new[] { preset, bank, bag }) s.Write(BitConverter.GetBytes(v));
        s.Write(new byte[12]); // library, genre, morphology
    }

    static byte[] Records(int size, params short[][] records) =>
        records.SelectMany(r => r.SelectMany(v => BitConverter.GetBytes(v))).ToArray() is var bytes && bytes.Length == records.Length * size
            ? bytes : throw new ArgumentException("record size");

    static byte[] Chunk(string id, byte[] data) =>
        [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(data.Length), .. data, .. data.Length % 2 == 1 ? new byte[1] : []];

    static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(c => c)]);
}
