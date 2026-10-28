using System.ComponentModel;
using System.Windows;
using AmigaSdCreator.App.ViewModels;

namespace AmigaSdCreator.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Fit the window to the home screen once, then let it be resized freely; other pages scroll.
        ContentRendered += (_, _) =>
        {
            SizeToContent = SizeToContent.Manual;
            MaxHeight = double.PositiveInfinity;
        };
        MaxHeight = SystemParameters.WorkArea.Height;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: true } &&
            MessageBox.Show(this, "A disk operation is still running. Closing now can leave the card incomplete.\n\nClose anyway?",
                "Amiga SD Card Creator", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
