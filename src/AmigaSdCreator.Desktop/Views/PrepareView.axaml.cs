using Avalonia.Controls;
using Avalonia.Interactivity;
using AmigaSdCreator.Core.Profiles;
using AmigaSdCreator.Ui.ViewModels;

namespace AmigaSdCreator.Desktop.Views;

/// <summary>
/// Choice tiles are RadioButtons generated per item; these handlers map them to the selection in
/// the view model (and restore the checked state when a tile is created again).
/// </summary>
public partial class PrepareView : UserControl
{
    public PrepareView() => InitializeComponent();

    private PrepareViewModel? Vm => DataContext as PrepareViewModel;
    private HardwareSelection? Hw => Vm?.Hardware;

    private static bool Checked(object? sender) => sender is RadioButton { IsChecked: true };

    private void OnPresetChecked(object? sender, RoutedEventArgs e)
    {
        if (Checked(sender) && Vm is { } vm && sender is Control { Tag: QuickPreset p } && vm.SelectedPreset != p) vm.SelectedPreset = p;
    }

    private void OnPresetLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: QuickPreset p } rb) rb.IsChecked = Vm?.SelectedPreset == p;
    }

    private void OnModelChecked(object? sender, RoutedEventArgs e)
    {
        if (Checked(sender) && Hw is { } hw && sender is Control { Tag: HardwareProfile p } && hw.SelectedProfile != p) hw.SelectedProfile = p;
    }

    private void OnModelLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: HardwareProfile p } rb) rb.IsChecked = Hw?.SelectedProfile == p;
    }

    private void OnInterfaceChecked(object? sender, RoutedEventArgs e)
    {
        if (Checked(sender) && Hw is { } hw && sender is Control { Tag: StorageInterfaceProfile i } && hw.SelectedInterface != i) hw.SelectedInterface = i;
    }

    private void OnInterfaceLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: StorageInterfaceProfile i } rb)
        {
            rb.IsChecked = Hw?.SelectedInterface == i;
            rb.IsEnabled = i.Status != VerificationStatus.Planned;
        }
    }

    private void OnUseChecked(object? sender, RoutedEventArgs e)
    {
        if (Checked(sender) && Hw is { } hw && sender is Control { Tag: IntendedUse u }) hw.SelectedUse = u;
    }

    private void OnUseLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: IntendedUse u } rb) rb.IsChecked = Hw?.SelectedUse == u;
    }
}
