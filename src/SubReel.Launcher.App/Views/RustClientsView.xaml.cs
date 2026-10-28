using System.Windows.Controls;

namespace SubReel.Launcher.App.Views;

public partial class RustClientsView : UserControl
{
    public RustClientsView() => InitializeComponent();

    private void OnPanelsSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (PanelsGrid.ColumnDefinitions.Count < 3) return;
        var compact = e.NewSize.Width < 850;
        PanelsGrid.ColumnDefinitions[0].Width = compact ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) : new System.Windows.GridLength(1.1, System.Windows.GridUnitType.Star);
        PanelsGrid.ColumnDefinitions[1].Width = compact ? new System.Windows.GridLength(0) : new System.Windows.GridLength(16);
        PanelsGrid.ColumnDefinitions[2].Width = compact ? new System.Windows.GridLength(0) : new System.Windows.GridLength(0.9, System.Windows.GridUnitType.Star);
        PanelsGrid.RowDefinitions.Clear();
        PanelsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        if (compact)
        {
            PanelsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetColumn(InstancePanel, 0);
            System.Windows.Controls.Grid.SetRow(InstancePanel, 1);
            InstancePanel.Margin = new System.Windows.Thickness(0, 14, 0, 0);
        }
        else
        {
            System.Windows.Controls.Grid.SetColumn(InstancePanel, 2);
            System.Windows.Controls.Grid.SetRow(InstancePanel, 0);
            InstancePanel.Margin = new System.Windows.Thickness(0);
        }
    }
}
