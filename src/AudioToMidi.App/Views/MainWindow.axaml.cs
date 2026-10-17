using AudioToMidi.App.ViewModels;
using AudioToMidi.Core.Synth;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AudioToMidi.App.Views;

public partial class MainWindow : Window
{
    static readonly FilePickerFileType MediaFiles = new("Audio and video")
    {
        Patterns =
        [
            "*.mp3", "*.wav", "*.flac", "*.ogg", "*.opus", "*.m4a", "*.aac", "*.aif", "*.aiff", "*.wma",
            "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi",
        ],
    };

    MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Roll.KeyPressed += (pitch, velocity) => Vm.PreviewNoteOn(pitch, velocity);
        Roll.KeyReleased += pitch => Vm.PreviewNoteOff(pitch);
        Roll.ClipClicked += OnClipClicked;
    }

    /// <summary>A repeat clicked in the pattern lane selects its clip; right-click offers to save it.</summary>
    void OnClipClicked(int lane, int pattern, bool rightButton)
    {
        Vm.SelectClip(lane, pattern);
        if (!rightButton || !Vm.HasSelectedClip) return;
        var save = new MenuItem { Header = $"Save {Vm.SelectedClipText} as MIDI…" };
        save.Click += OnExportClipClick;
        var menu = new MenuFlyout { Placement = PlacementMode.Pointer };
        menu.Items.Add(save);
        menu.ShowAt(Roll, showAtPointer: true);
    }

    async void OnExportClipClick(object? sender, RoutedEventArgs e)
    {
        var path = await SaveAsync("Save clip as MIDI", Vm.SelectedClipSuffix, new FilePickerFileType("MIDI file") { Patterns = ["*.mid"] });
        if (path is not null) Vm.ExportClip(path);
    }

    async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open audio or video",
            AllowMultiple = false,
            FileTypeFilter = [MediaFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = await FolderAsync(Vm.LastFolder),
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await Vm.OpenAsync(path);
    }

    async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var path = await SaveAsync("Export MIDI", ".mid", new FilePickerFileType("MIDI file") { Patterns = ["*.mid"] });
        if (path is not null) Vm.ExportMidi(path);
    }

    async void OnExportScoreClick(object? sender, RoutedEventArgs e)
    {
        var path = await SaveAsync("Export sheet music", ".musicxml", new FilePickerFileType("MusicXML") { Patterns = ["*.musicxml"] });
        if (path is not null) Vm.ExportScore(path);
    }

    async void OnExportAudioClick(object? sender, RoutedEventArgs e)
    {
        var path = await SaveAsync("Save transcribed audio", ".transcribed.wav", new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] });
        if (path is not null) Vm.ExportSeparatedAudio(path);
    }

    async void OnExportMixClick(object? sender, RoutedEventArgs e)
    {
        var path = await SaveAsync("Save the mix", ".mix.wav", new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] });
        if (path is not null) await Vm.ExportMixAsync(path);
    }

    async void OnSaveProjectClick(object? sender, RoutedEventArgs e)
    {
        var path = await SaveAsync("Save project", ".a2m", new FilePickerFileType("AudioToMidi project") { Patterns = ["*.a2m"] });
        if (path is not null) await Vm.SaveProjectAsync(path);
    }

    async void OnBrowseFfmpegClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate ffmpeg",
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Vm.FfmpegPath = path;
    }

    async void OnBrowseModelSourceClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install the model from",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Model or model archive") { Patterns = ["*.onnx", "*.zip"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Vm.ModelSource = path;
    }

    async void OnBrowseSoundFontClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a SoundFont",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SoundFont, or the .zip it came in") { Patterns = ["*.sf2", "*.sf3", "*.zip"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await Vm.ChooseSoundFontAsync(path);
    }

    async void OnOpenSoundFontFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SoundFonts.Folder);
        await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(SoundFonts.Folder));
    }

    async Task<string?> SaveAsync(string title, string suffix, FilePickerFileType type)
    {
        var source = Vm.SourcePath;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = source is null ? "transcription" + suffix : Path.GetFileNameWithoutExtension(source) + suffix,
            DefaultExtension = suffix.TrimStart('.').Split('.').Last(),
            FileTypeChoices = [type],
            SuggestedStartLocation = await FolderAsync(source is null ? Vm.LastFolder : Path.GetDirectoryName(source)),
        });
        return file?.TryGetLocalPath();
    }

    async Task<IStorageFolder?> FolderAsync(string? path) =>
        string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : await StorageProvider.TryGetFolderFromPathAsync(path);

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await Vm.OpenAsync(path);
    }
}
