using System.Windows;
using System.Windows.Controls;
using AmigaSdCreator.App.ViewModels;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.App.Views;

/// <summary>
/// Choice tiles are RadioButtons generated per item; these handlers map them to the selection in
/// the view model (and restore the checked state when a step is shown again).
/// </summary>
public partial class PrepareView : UserControl
{
    public PrepareView() => InitializeComponent();

    private HardwareSelection? Hw => (DataContext as PrepareViewModel)?.Hardware;

    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is PrepareViewModel vm && sender is FrameworkElement { Tag: QuickPreset p } && vm.SelectedPreset != p) vm.SelectedPreset = p;
    }

    private void OnPresetLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: QuickPreset p } rb) rb.IsChecked = (DataContext as PrepareViewModel)?.SelectedPreset == p;
    }

    private void OnModelChecked(object sender, RoutedEventArgs e)
    {
        if (Hw is { } hw && sender is FrameworkElement { Tag: HardwareProfile p } && hw.SelectedProfile != p) hw.SelectedProfile = p;
    }

    private void OnModelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: HardwareProfile p } rb) rb.IsChecked = Hw?.SelectedProfile == p;
    }

    private void OnInterfaceChecked(object sender, RoutedEventArgs e)
    {
        if (Hw is { } hw && sender is FrameworkElement { Tag: StorageInterfaceProfile i } && hw.SelectedInterface != i) hw.SelectedInterface = i;
    }

    private void OnInterfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: StorageInterfaceProfile i } rb)
        {
            rb.IsChecked = Hw?.SelectedInterface == i;
            rb.IsEnabled = i.Status != VerificationStatus.Planned;
        }
    }

    private void OnUseChecked(object sender, RoutedEventArgs e)
    {
        if (Hw is { } hw && sender is FrameworkElement { Tag: IntendedUse u }) hw.SelectedUse = u;
    }

    private void OnUseLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: IntendedUse u } rb) rb.IsChecked = Hw?.SelectedUse == u;
    }
}
