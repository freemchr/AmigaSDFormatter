using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AmigaSdCreator.Desktop.Services;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Ui.ViewModels;

namespace AmigaSdCreator.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnUnhandled;
            AppServices.Initialise(new AvaloniaPlatform());
            AppServices.ApplyTheme(AppServices.Current.Settings.Theme);
            AppServices.Current.Log.Info($"Started on Linux (root: {AppServices.Current.Disks.IsElevated})");

            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                vm.Files.Dispose();
                vm.Devices.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppServices.Current?.Log.Error("Unhandled error", e.Exception);
        _ = Dialogs.Error($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to the log.");
        e.Handled = true;
    }
}
