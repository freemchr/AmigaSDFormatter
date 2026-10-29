using System.Windows;
using System.Windows.Threading;
using AmigaSdCreator.App.Services;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Ui.ViewModels;

namespace AmigaSdCreator.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppServices.Initialise(new WpfPlatform());
        AppServices.ApplyTheme(AppServices.Current.Settings.Theme);
        AppServices.Current.Log.Info($"Started (elevated: {AppServices.Current.Disks.IsElevated})");

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainViewModel.Instance is { } vm)
        {
            vm.Files.Dispose();
            vm.Devices.Dispose();
        }
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppServices.Current?.Log.Error("Unhandled error", e.Exception);
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to the log.", "Amiga SD Creator", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
