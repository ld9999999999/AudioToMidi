using AudioToMidi.App.ViewModels;
using AudioToMidi.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AudioToMidi.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.Dispose();

            // Allow "AudioToMidi song.mp3" from a terminal or file association.
            if (desktop.Args is [var first, ..] && File.Exists(first))
                _ = vm.OpenAsync(first);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
