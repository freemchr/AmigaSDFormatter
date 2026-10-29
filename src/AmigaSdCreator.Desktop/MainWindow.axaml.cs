using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Ui.ViewModels;

namespace AmigaSdCreator.Desktop;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        // Development aid: ASDC_SCREENSHOT=out.png [ASDC_PAGE=Files] renders the window to a PNG and exits.
        if (Environment.GetEnvironmentVariable("ASDC_SCREENSHOT") is { Length: > 0 } shot)
            Opened += async (_, _) => await SaveScreenshotAsync(shot, Environment.GetEnvironmentVariable("ASDC_PAGE"));
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainViewModel { IsBusy: true }) return;
        e.Cancel = true;
        if (await Dialogs.Confirm("A disk operation is still running. Closing now can leave the card incomplete.\n\nClose anyway?"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async Task SaveScreenshotAsync(string path, string? page)
    {
        if (page is { Length: > 0 } && DataContext is MainViewModel vm) vm.NavigateCommand.Execute(page);
        await Task.Delay(2500);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(this);
            bitmap.Save(path);
        }, DispatcherPriority.Background);
        _closeConfirmed = true;
        Close();
    }
}
