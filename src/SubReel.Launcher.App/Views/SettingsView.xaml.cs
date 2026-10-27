using System.Windows.Controls;
using System.Windows;
using Microsoft.Win32;
using SubReel.Launcher.App.ViewModels;

namespace SubReel.Launcher.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void BrowseJavaPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = (DataContext as SettingsViewModel)?.JavaDialogTitle ?? "Choose Java runtime",
            Filter = (DataContext as SettingsViewModel)?.JavaDialogFilter ?? "Java runtime (java.exe; javaw.exe)|java.exe;javaw.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            JavaPathBox.Text = dialog.FileName;
    }

}
