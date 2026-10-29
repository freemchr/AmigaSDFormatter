namespace AmigaSdCreator.Ui.Services;

/// <summary>Dialogs shown by the view models; implemented by the host UI (see <see cref="IDialogService"/>).</summary>
public static class Dialogs
{
    public const string ImageFilter = "Disk images (*.img;*.hdf;*.img.gz)|*.img;*.hdf;*.gz|All files (*.*)|*.*";
    private const string AppTitle = "Amiga SD Creator";

    private static IDialogService Service => AppServices.Platform.Dialogs;

    public static Task<string?> OpenFile(string filter, string title) => Service.OpenFileAsync(filter, title);

    /// <summary>The chosen file is handed back to the desktop user once the caller has written it.</summary>
    public static Task<string?> SaveFile(string filter, string title, string defaultName) => Service.SaveFileAsync(filter, title, defaultName);

    public static Task<string?> PickFolder(string title) => Service.PickFolderAsync(title);

    public static Task<bool> Confirm(string message, string title = AppTitle) => Service.ConfirmAsync(message, title);

    public static Task Error(string message, string title = AppTitle) => Service.ErrorAsync(message, title);

    public static Task Info(string message, string title = AppTitle) => Service.InfoAsync(message, title);
}
