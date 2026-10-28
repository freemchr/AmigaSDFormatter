using System.Windows;
using Microsoft.Win32;

namespace AmigaSdCreator.App.Services;

public static class Dialogs
{
    public const string ImageFilter = "Disk images (*.img;*.hdf;*.img.gz)|*.img;*.hdf;*.gz|All files (*.*)|*.*";

    public static string? OpenFile(string filter, string title)
    {
        var d = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    public static string? SaveFile(string filter, string title, string defaultName)
    {
        var d = new SaveFileDialog { Filter = filter, Title = title, FileName = defaultName, OverwritePrompt = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    public static string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderName : null;
    }

    public static bool Confirm(string message, string title = "Amiga SD Creator") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static void Error(string message, string title = "Amiga SD Creator") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(string message, string title = "Amiga SD Creator") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
