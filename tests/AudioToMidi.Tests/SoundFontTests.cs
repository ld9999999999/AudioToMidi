using System.IO.Compression;
using AudioToMidi.Core.Synth;
using AudioToMidi.Core.Transcription;
using MeltySynth;
using Xunit.Abstractions;

namespace AudioToMidi.Tests;

public class SoundFontTests(ITestOutputHelper output)
{
    const int Rate = 48000;

    static SoundFont Font() => new(new MemoryStream(TestSoundFont.Build()));

    /// <summary>Start a note, render for a while, and return the left channel.</summary>
    static float[] Play(IInstrument instrument, int pitch, double seconds = 0.5, float velocity = 0.8f)
    {
        instrument.NoteOn(pitch, velocity);
        return Render(instrument, seconds);
    }

    static float[] Render(IInstrument instrument, double seconds)
    {
        var left = new float[(int)(seconds * Rate)];
        var right = new float[left.Length];
        for (int i = 0; i < left.Length; i += 256)
        {
            int n = Math.Min(256, left.Length - i);
            instrument.Render(left.AsSpan(i, n), right.AsSpan(i, n));
        }
        return left;
    }

    /// <summary>Measure frequency from rising zero crossings in the second half, after the attack.</summary>
    static double Frequency(float[] audio)
    {
        int from = audio.Length / 2, crossings = 0, first = -1, last = -1;
        for (int i = from + 1; i < audio.Length; i++)
            if (audio[i - 1] < 0 && audio[i] >= 0)
            {
                if (first < 0) first = i;
                last = i;
                crossings++;
            }
        return (crossings - 1) * (double)Rate / (last - first);
    }

    [Fact]
    public void Plays_a_program_at_the_notes_pitch()
    {
        var font = Font();
        Assert.Equal(440, Frequency(Play(new SoundFontInstrument(font, Rate, 0), 69)), 1.0);
        Assert.Equal(880, Frequency(Play(new SoundFontInstrument(font, Rate, 0), 81)), 2.0);
        Assert.Equal(880, Frequency(Play(new SoundFontInstrument(font, Rate, 33), 69)), 2.0); // program 33 is an octave up
    }

    [Fact]
    public void Plays_the_drum_kit_on_the_percussion_channel()
    {
        var kit = new SoundFontInstrument(Font(), Rate, 0, percussion: true);
        var audio = Play(kit, 36);
        Assert.True(audio.Max(Math.Abs) > 0.01, "the kit sounds");
    }

    [Fact]
    public void Note_off_lets_the_note_end()
    {
        var instrument = new SoundFontInstrument(Font(), Rate, 0);
        Play(instrument, 69, 0.2);
        instrument.NoteOff(69);
        var after = Render(instrument, 2.0);
        Assert.True(after.AsSpan(after.Length - 4800).ToArray().Max(Math.Abs) < 1e-3, "silent once released");
    }

    [Fact]
    public void The_mixer_plays_tracks_and_drums_from_a_soundfont_and_back()
    {
        var player = new NotePlayer(new PolySynth(Rate));
        player.SetTracks([0, 33]);
        player.SetNotes([new DetectedNote(0, 2, 69, 0.8f) { Track = 0 }, new DetectedNote(0, 2, 69, 0.8f) { Track = 1 }], null);
        player.SetDrums([new DetectedNote(0.05, 0.2, 36, 0.9f)]);
        player.Configure(0, double.MaxValue, false, 1);
        player.SetSoundFont(Font());
        player.Play(0);

        float[] Render(double seconds)
        {
            var left = new float[(int)(seconds * Rate)];
            var right = new float[left.Length];
            for (int i = 0; i < left.Length; i += 256)
            {
                int n = Math.Min(256, left.Length - i);
                player.Render(left.AsSpan(i, n), right.AsSpan(i, n));
            }
            return left;
        }

        Render(0.3);
        var levels = player.ReadLevels();
        Assert.True(levels.Tracks[0] > 0.01 && levels.Tracks[1] > 0.01 && levels.Drums > 0.01, $"tracks {string.Join(", ", levels.Tracks)}, drums {levels.Drums}");

        player.SetMix(0, 1, 0.5, mute: true, solo: false); // only program 33 (an octave up) is left
        player.SetMix(NotePlayer.DrumChannel, 0, 0.5, false, false);
        Render(0.3);
        Assert.Equal(880, Frequency(Render(0.5)), 2.0);

        player.SetSoundFont(null); // back to the built-in synth: still sounding, new notes on it
        Assert.Null(player.SoundFont);
        player.Play(0);
        Render(0.3);
        Assert.True(player.ReadLevels().Tracks[1] > 0.01);
    }

    [Fact]
    public void A_zip_is_unpacked_to_its_largest_soundfont()
    {
        var dir = Directory.CreateTempSubdirectory("a2m-sf-");
        try
        {
            var zip = Path.Combine(dir.FullName, "fonts.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = archive.CreateEntry("docs/README.txt").Open()) w.Write("hello"u8);
                using (var w = archive.CreateEntry("Small/small.sf2").Open()) w.Write(new byte[10]);
                using (var w = archive.CreateEntry("Font/Test.sf2").Open()) w.Write(TestSoundFont.Build());
            }
            var target = Path.Combine(dir.FullName, "fonts");
            var path = SoundFonts.Import(zip, target);
            Assert.Equal(Path.Combine(target, "Test.sf2"), path);
            Assert.Equal(3, SoundFonts.Load(path).Presets.Count);
            Assert.Equal(path, SoundFonts.Import(path, target)); // a SoundFont is used where it is
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>An .sf3 (MuseScore's, when installed) loads with every sample decoded. Skipped without one.</summary>
    [Fact]
    public void Decodes_an_sf3()
    {
        const string path = "/Applications/MuseScore 4.app/Contents/Resources/sound/MS Basic.sf3";
        if (!File.Exists(path)) { output.WriteLine("No MuseScore 4 SoundFont here; skipping."); return; }
        var font = SoundFonts.Load(path);
        Assert.True(font.Presets.Count > 128);
        Assert.All(font.SampleHeaders, h => Assert.True(h.Start <= h.StartLoop && h.StartLoop <= h.EndLoop && h.EndLoop <= h.End && h.End <= font.WaveData.Length));
        var strings = Play(new SoundFontInstrument(font, Rate, 48), 60);
        Assert.True(strings.Max(Math.Abs) > 0.01, "strings sound");
    }
}
