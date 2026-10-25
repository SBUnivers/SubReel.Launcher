using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Navigation;

namespace SubReel.Launcher.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void SaveTranslationKey_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel viewModel)
            await viewModel.SaveTranslationKeyAsync(TranslationKeyBox.Password);
        TranslationKeyBox.Clear();
    }

    private void OpenDeepL_Click(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
