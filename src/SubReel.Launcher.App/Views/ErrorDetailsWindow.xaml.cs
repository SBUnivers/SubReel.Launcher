using System.Windows;

namespace SubReel.Launcher.App.Views;

public partial class ErrorDetailsWindow : Window
{
    public ErrorDetailsWindow(string details)
    {
        InitializeComponent();
        Details.Text = details;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Details.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
