using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SubReel.Launcher.Abstractions.Settings;

namespace SubReel.Launcher.App.Views;

public partial class StartupWindow : Window
{
    private TaskCompletionSource<LauncherSettings?>? _setupCompletion;
    private LauncherSettings? _settings;
    private int _completedChecks;

    public event EventHandler? LoginRequested;

    public StartupWindow() => InitializeComponent();

    public void SetCheck(string check, string message, StartupCheckState state)
    {
        var (icon, status, color) = check switch
        {
            "files" => (FilesIcon, FilesStatus, GetBrush(state)),
            "license" => (LicenseIcon, LicenseStatus, GetBrush(state)),
            "subreel" => (SubReelIcon, SubReelStatus, GetBrush(state)),
            _ => throw new ArgumentOutOfRangeException(nameof(check))
        };
        icon.Text = state switch
        {
            StartupCheckState.Running => "…",
            StartupCheckState.Passed => "✓",
            StartupCheckState.Warning => "!",
            StartupCheckState.Failed => "×",
            _ => "·"
        };
        icon.Foreground = color;
        status.Text = message;
        status.Foreground = state == StartupCheckState.Failed ? new SolidColorBrush(Color.FromRgb(255, 132, 145)) : new SolidColorBrush(Color.FromRgb(169, 164, 185));
        if (state is StartupCheckState.Passed or StartupCheckState.Warning or StartupCheckState.Failed)
        {
            _completedChecks = Math.Min(3, _completedChecks + 1);
            StartupProgress.Value = _completedChecks;
            ContinueButton.IsEnabled = _completedChecks == 3;
        }
        LoginButton.Visibility = SubReelIcon.Text is "!" or "×" ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task<LauncherSettings?> ShowWelcomeSetupAsync(LauncherSettings settings)
    {
        if (settings.HasCompletedWelcomeSetup)
        {
            ContinueButton.Content = "Открываю лаунчер…";
            ContinueButton.IsEnabled = false;
            SkipButton.Visibility = Visibility.Collapsed;
            await Task.Delay(450);
            return null;
        }

        _settings = settings;
        ChecksPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        SkipButton.Visibility = Visibility.Visible;
        ContinueButton.Content = "Сохранить и открыть лаунчер";
        SubtitleText.Text = "Первый запуск — несколько быстрых настроек";
        SelectChoice(LanguageBox, settings.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "ru-RU");
        SelectChoice(MemoryBox, settings.DefaultMemoryMb.ToString());
        ReduceMotionBox.IsChecked = settings.ReduceAnimations;

        _setupCompletion = new TaskCompletionSource<LauncherSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return await _setupCompletion.Task;
    }

    private void Login_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke(this, EventArgs.Empty);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            DialogResult = true;
            return;
        }
        ApplySetupSettings();
        _setupCompletion?.TrySetResult(_settings);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) _settings.HasCompletedWelcomeSetup = true;
        _setupCompletion?.TrySetResult(_settings);
    }

    private void ApplySetupSettings()
    {
        if (_settings is null) return;
        if (LanguageBox.SelectedItem is ComboBoxItem language && language.Tag is string languageTag)
            _settings.Language = languageTag;
        if (MemoryBox.SelectedItem is ComboBoxItem memory && int.TryParse(memory.Tag?.ToString(), out var memoryMb))
            _settings.DefaultMemoryMb = memoryMb;
        _settings.ReduceAnimations = ReduceMotionBox.IsChecked == true;
        _settings.HasCompletedWelcomeSetup = true;
    }

    private static void SelectChoice(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static Brush GetBrush(StartupCheckState state) => state switch
    {
        StartupCheckState.Passed => new SolidColorBrush(Color.FromRgb(112, 225, 174)),
        StartupCheckState.Warning => new SolidColorBrush(Color.FromRgb(255, 199, 111)),
        StartupCheckState.Failed => new SolidColorBrush(Color.FromRgb(255, 132, 145)),
        StartupCheckState.Running => new SolidColorBrush(Color.FromRgb(171, 153, 255)),
        _ => new SolidColorBrush(Color.FromRgb(169, 164, 185))
    };
}

public enum StartupCheckState { Pending, Running, Passed, Warning, Failed }
