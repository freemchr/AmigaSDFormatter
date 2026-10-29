using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Ui.ViewModels;

namespace AmigaSdCreator.Desktop.Views;

/// <summary>
/// Drag and drop wiring. Drags between the panes stay inside the app: the transfer carries a private
/// marker format and the dragged items are kept here. The Amiga pane also accepts files dropped from
/// the desktop's file manager.
/// </summary>
public partial class FilesView : UserControl
{
    private static readonly DataFormat<string> LocalFormat = DataFormat.CreateStringApplicationFormat("AmigaSdCreator.LocalPaths");
    private static readonly DataFormat<string> AmigaFormat = DataFormat.CreateStringApplicationFormat("AmigaSdCreator.AmigaEntries");
    private const double DragThreshold = 4;
    private Point? _dragStart;
    private string[] _draggedPaths = [];
    private AmigaEntry[] _draggedEntries = [];

    public FilesView()
    {
        InitializeComponent();

        WindowsList.DoubleTapped += OnWindowsDoubleTapped;
        WindowsPathBox.KeyDown += OnWindowsPathKey;
        WindowsList.AddHandler(PointerPressedEvent, OnDragStartCandidate, RoutingStrategies.Tunnel);
        WindowsList.AddHandler(PointerMovedEvent, OnWindowsDragMove, RoutingStrategies.Tunnel);
        WindowsList.AddHandler(DragDrop.DragOverEvent, OnWindowsDragOver);
        WindowsList.AddHandler(DragDrop.DropEvent, OnWindowsDrop);

        AmigaList.DoubleTapped += OnAmigaDoubleTapped;
        AmigaList.KeyDown += OnAmigaKey;
        AmigaList.AddHandler(PointerPressedEvent, OnDragStartCandidate, RoutingStrategies.Tunnel);
        AmigaList.AddHandler(PointerMovedEvent, OnAmigaDragMove, RoutingStrategies.Tunnel);
        AmigaList.AddHandler(DragDrop.DragOverEvent, OnAmigaDragOver);
        AmigaList.AddHandler(DragDrop.DropEvent, OnAmigaDrop);

        PartitionBox.SelectionChanged += OnPartitionChanged;
    }

    private FilesViewModel Vm => (FilesViewModel)DataContext!;

    private string[] SelectedLocalPaths() =>
        WindowsList.SelectedItems?.OfType<WindowsEntry>().Where(x => !x.IsDrive).Select(x => x.FullPath).ToArray() ?? [];

    private AmigaEntry[] SelectedAmigaEntries() => AmigaList.SelectedItems?.OfType<AmigaEntry>().ToArray() ?? [];

    // ----- Local pane -----

    private void OnWindowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowsEntry entry) Vm.OpenWindowsEntry(entry);
    }

    private void OnWindowsPathKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm.LoadWindowsCommand.Execute(WindowsPathBox.Text);
    }

    private async void OnWindowsDragMove(object? sender, PointerEventArgs e)
    {
        if (!ShouldStartDrag(e)) return;
        var paths = SelectedLocalPaths();
        if (paths.Length == 0) return;
        _dragStart = null;
        _draggedPaths = paths;
        await DragDrop.DoDragDropAsync(e, Marker(LocalFormat), DragDropEffects.Copy);
        _draggedPaths = [];
    }

    private void OnWindowsDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(AmigaFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowsDrop(object? sender, DragEventArgs e)
    {
        var entries = _draggedEntries;
        if (e.DataTransfer.Contains(AmigaFormat) && entries.Length > 0) await Vm.CopyToWindowsAsync(entries);
    }

    private async void OnCopyLeft(object? sender, RoutedEventArgs e)
    {
        var entries = SelectedAmigaEntries();
        if (entries.Length > 0) await Vm.CopyToWindowsAsync(entries);
    }

    // ----- Amiga pane -----

    private async void OnAmigaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (AmigaList.SelectedItem is AmigaEntry entry) await Vm.OpenAmigaEntryAsync(entry);
    }

    private async void OnAmigaDragMove(object? sender, PointerEventArgs e)
    {
        if (!ShouldStartDrag(e)) return;
        var entries = SelectedAmigaEntries();
        if (entries.Length == 0) return;
        _dragStart = null;
        _draggedEntries = entries;
        await DragDrop.DoDragDropAsync(e, Marker(AmigaFormat), DragDropEffects.Copy);
        _draggedEntries = [];
    }

    private void OnAmigaDragOver(object? sender, DragEventArgs e)
    {
        bool files = e.DataTransfer.Contains(LocalFormat) || e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files && Vm.IsWritable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnAmigaDrop(object? sender, DragEventArgs e)
    {
        string[] paths = e.DataTransfer.Contains(LocalFormat)
            ? _draggedPaths
            : e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        if (paths.Length > 0) await Vm.CopyToAmigaAsync(paths);
    }

    private async void OnCopyRight(object? sender, RoutedEventArgs e)
    {
        var paths = SelectedLocalPaths();
        if (paths.Length > 0) await Vm.CopyToAmigaAsync(paths);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e) => await Vm.DeleteAsync(SelectedAmigaEntries());

    private async void OnAmigaKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) await Vm.DeleteAsync(SelectedAmigaEntries());
        else if (e.Key == Key.Back) Vm.AmigaUpCommand.Execute(null);
    }

    private void OnPartitionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.RemovedItems.Count > 0) Vm.OpenPartitionCommand.Execute(null);
    }

    // ----- Shared -----

    private static DataTransfer Marker(DataFormat<string> format)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(format, format.Identifier));
        return data;
    }

    private void OnDragStartCandidate(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
    }

    private bool ShouldStartDrag(PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        var d = e.GetPosition(this) - start;
        return Math.Abs(d.X) > DragThreshold || Math.Abs(d.Y) > DragThreshold;
    }
}
