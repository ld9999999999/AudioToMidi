using AudioToMidi.App.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioToMidi.App.ViewModels;

/// <summary>
/// A channel strip in the mixer: the original recording, a track, or the drums. Its fader, pan, mute and
/// solo set how it plays; <see cref="Level"/> is its meter.
/// </summary>
public partial class MixerStrip(string name, Color color, string? fixedInstrument = null) : ObservableObject
{
    public string Name { get; } = name;
    public string Caption => Name.ToUpperInvariant();
    public IBrush ColorBrush { get; } = new SolidColorBrush(color);

    /// <summary>What plays, for strips without a choice of instrument (the recording, the drum kit).</summary>
    public string? FixedInstrument { get; } = fixedInstrument;
    public bool HasProgramChoice => FixedInstrument is null;

    /// <summary>Fader position, 0..1 (see <see cref="Fader.Gain"/>).</summary>
    [ObservableProperty] private double _volume = Fader.UnityPosition;
    /// <summary>0 = left, 0.5 = centre, 1 = right.</summary>
    [ObservableProperty] private double _pan = 0.5;
    [ObservableProperty] private bool _mute;
    [ObservableProperty] private bool _solo;
    /// <summary>Peak level for the meter, linear.</summary>
    [ObservableProperty] private double _level;

    public string VolumeText => Fader.DbText(Volume);
    public virtual bool CanSplit => false;

    /// <summary>Raised when the volume, pan, mute or solo setting changes.</summary>
    public event Action<MixerStrip>? MixChanged;

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        MixChanged?.Invoke(this);
    }

    partial void OnPanChanged(double value) => MixChanged?.Invoke(this);
    partial void OnMuteChanged(bool value) => MixChanged?.Invoke(this);
    partial void OnSoloChanged(bool value) => MixChanged?.Invoke(this);

    /// <summary>Copy another strip's settings, used when the tracks are rebuilt.</summary>
    public void CopyMix(MixerStrip other)
    {
        Volume = other.Volume;
        Pan = other.Pan;
        Mute = other.Mute;
        Solo = other.Solo;
    }
}
