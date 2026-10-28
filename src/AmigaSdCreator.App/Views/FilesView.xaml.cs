using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AmigaSdCreator.App.ViewModels;
using AmigaSdCreator.Core.FileSystems;

namespace AmigaSdCreator.App.Views;

/// <summary>
/// Drag and drop wiring. Items dragged from the Windows pane use the standard file-drop format,
/// so the Amiga pane accepts drops from Windows Explorer too. Amiga items use a private format
/// and can be dropped on the Windows pane.
/// </summary>
public partial class FilesView : UserControl
{
    private const string AmigaFormat = "AmigaSdCreator.AmigaEntries";
    private Point? _dragStart;

    public FilesView() => InitializeComponent();

    private FilesViewModel Vm => (FilesViewModel)DataContext;

    // ----- Windows pane -----

    private void OnWindowsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowsEntry entry) Vm.OpenWindowsEntry(entry);
    }

    private void OnWindowsPathKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm.LoadWindowsCommand.Execute(WindowsPathBox.Text);
    }

    private void OnWindowsDragMove(object sender, MouseEventArgs e)
    {
        if (!ShouldStartDrag(e) || WindowsList.SelectedItems.Count == 0) return;
        var paths = WindowsList.SelectedItems.Cast<WindowsEntry>().Where(x => !x.IsDrive).Select(x => x.FullPath).ToArray();
        if (paths.Length == 0) return;
        _dragStart = null;
        DragDrop.DoDragDrop(WindowsList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
    }

    private void OnWindowsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(AmigaFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(AmigaFormat) is AmigaEntry[] entries) await Vm.CopyToWindowsAsync(entries);
    }

    private async void OnCopyLeft(object sender, RoutedEventArgs e)
    {
        var entries = AmigaList.SelectedItems.Cast<AmigaEntry>().ToArray();
        if (entries.Length > 0) await Vm.CopyToWindowsAsync(entries);
    }

    // ----- Amiga pane -----

    private async void OnAmigaDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AmigaList.SelectedItem is AmigaEntry entry) await Vm.OpenAmigaEntryAsync(entry);
    }

    private void OnAmigaDragMove(object sender, MouseEventArgs e)
    {
        if (!ShouldStartDrag(e) || AmigaList.SelectedItems.Count == 0) return;
        var entries = AmigaList.SelectedItems.Cast<AmigaEntry>().ToArray();
        _dragStart = null;
        DragDrop.DoDragDrop(AmigaList, new DataObject(AmigaFormat, entries), DragDropEffects.Copy);
    }

    private void OnAmigaDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Vm.IsWritable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnAmigaDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Vm.CopyToAmigaAsync(paths);
    }

    private async void OnCopyRight(object sender, RoutedEventArgs e)
    {
        var paths = WindowsList.SelectedItems.Cast<WindowsEntry>().Where(x => !x.IsDrive).Select(x => x.FullPath).ToArray();
        if (paths.Length > 0) await Vm.CopyToAmigaAsync(paths);
    }

    private async void OnDelete(object sender, RoutedEventArgs e) =>
        await Vm.DeleteAsync(AmigaList.SelectedItems.Cast<AmigaEntry>().ToArray());

    private async void OnAmigaKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) await Vm.DeleteAsync(AmigaList.SelectedItems.Cast<AmigaEntry>().ToArray());
        else if (e.Key == Key.Back) Vm.AmigaUpCommand.Execute(null);
    }

    private void OnPartitionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.RemovedItems.Count > 0) Vm.OpenPartitionCommand.Execute(null);
    }

    // ----- Shared -----

    private void OnDragStartCandidate(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private bool ShouldStartDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start) return false;
        var d = e.GetPosition(this) - start;
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }
}
