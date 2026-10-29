using System.Runtime.Versioning;
using Avalonia;

// This front end is the Linux build; Windows uses the WPF app.
[assembly: SupportedOSPlatform("linux")]

namespace AmigaSdCreator.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
