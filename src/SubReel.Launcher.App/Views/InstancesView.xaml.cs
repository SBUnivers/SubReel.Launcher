using System.Windows;
using System.Windows.Controls;

namespace SubReel.Launcher.App.Views;

public partial class InstancesView : UserControl
{
    public InstancesView()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.InstancesViewModel viewModel)
                await viewModel.LoadCommand.ExecuteAsync(null);
        };
    }

    private void OnInstancesLayoutSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        var isCompact = e.NewSize.Width < 760;

        InstancesLayout.ColumnDefinitions[0].Width =
            isCompact ? new GridLength(1, GridUnitType.Star) : new GridLength(1.2, GridUnitType.Star);
        InstancesLayout.ColumnDefinitions[1].Width =
            isCompact ? new GridLength(0) : new GridLength(16);
        InstancesLayout.ColumnDefinitions[2].Width =
            isCompact ? new GridLength(0) : new GridLength(0.8, GridUnitType.Star);

        Grid.SetRow(EditorPanel, isCompact ? 1 : 0);
        Grid.SetColumn(EditorPanel, isCompact ? 0 : 2);
    }
}
