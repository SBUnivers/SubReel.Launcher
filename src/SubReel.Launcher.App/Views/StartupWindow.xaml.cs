using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.App.Services;

namespace SubReel.Launcher.App.Views;

public partial class StartupWindow : Window
{
    private TaskCompletionSource<LauncherSettings?>? _setupCompletion;
    private LauncherSettings? _settings;
    private int _completedChecks;
    private ILocalizationService? _localization;
    private EventHandler? _languageChangedHandler;
    private readonly Dictionary<string, (string Message, StartupCheckState State)> _checks = [];
    private readonly HashSet<string> _completedCheckIds = [];

    public event EventHandler? LoginRequested;

    public StartupWindow() => InitializeComponent();

    public void UseLocalization(ILocalizationService localization)
    {
        if (_localization is not null && _languageChangedHandler is not null)
            _localization.LanguageChanged -= _languageChangedHandler;
        _localization = localization;
        _languageChangedHandler = (_, _) =>
        {
            foreach (var (check, result) in _checks)
                RenderCheck(check, result.Message, result.State);
        };
        localization.LanguageChanged += _languageChangedHandler;
        Closed -= StartupWindow_Closed;
        Closed += StartupWindow_Closed;
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string language })
                localization.SetLanguage(language);
        };
    }

    private void StartupWindow_Closed(object? sender, EventArgs e)
    {
        if (_localization is not null && _languageChangedHandler is not null)
            _localization.LanguageChanged -= _languageChangedHandler;
        _languageChangedHandler = null;
    }

    public void SetCheck(string check, string message, StartupCheckState state)
    {
        _checks[check] = (message, state);
        RenderCheck(check, message, state);
        if ((state is StartupCheckState.Passed or StartupCheckState.Warning or StartupCheckState.Failed) && _completedCheckIds.Add(check))
        {
            _completedChecks = Math.Min(3, _completedChecks + 1);
            StartupProgress.Value = _completedChecks;
            ContinueButton.IsEnabled = _completedChecks == 3;
        }
        if (check == "subreel")
            LoginButton.Visibility = state is StartupCheckState.Warning or StartupCheckState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderCheck(string check, string message, StartupCheckState state)
    {
        var (icon, status, color) = check switch
        {
            "files" => (FilesIcon, FilesStatus, GetBrush(state)),
            "license" => (LicenseIcon, LicenseStatus, GetBrush(state)),
            "subreel" => (SubReelIcon, SubReelStatus, GetBrush(state)),
            _ => throw new ArgumentOutOfRangeException(nameof(check))
        };
        icon.Data = (Geometry)FindResource(state switch
        {
            StartupCheckState.Passed => "IconCheckGeometry",
            StartupCheckState.Warning or StartupCheckState.Failed => "IconWarningGeometry",
            StartupCheckState.Running => "IconRefreshGeometry",
            _ => check switch
            {
                "files" => "IconFileGeometry",
                "license" => "IconLicenseGeometry",
                _ => "IconAccountGeometry"
            }
        });
        icon.Stroke = color;
        status.Text = TranslateCheck(check, message);
        status.Foreground = state == StartupCheckState.Failed ? (Brush)FindResource("ErrorBrush") : (Brush)FindResource("TextSecondary");
    }

    private string TranslateCheck(string check, string message)
    {
        var key = (check, message) switch
        {
            ("files", "Проверяю приложение и локальные папки…") => "Startup.Check.FilesRunning",
            ("files", "Файлы приложения на месте; папки данных готовы.") => "Startup.Check.FilesPassed",
            ("license", "Проверяю сохранённый профиль Minecraft…") => "Startup.Check.LicenseRunning",
            ("license", "Активен офлайн-профиль. Лицензия Minecraft Java этим профилем не подтверждается.") => "Startup.Check.Offline",
            ("license", "Microsoft-профиль не подключён. Лицензию можно проверить после входа.") => "Startup.Check.NoMicrosoft",
            ("license", "Проверка заняла слишком много времени. Её можно повторить в профиле.") => "Startup.Check.LicenseTimeout",
            ("subreel", "Проверяю сессию SubReel…") => "Startup.Check.SubReelRunning",
            ("subreel", "Проверка заняла слишком много времени. Проверь подключение.") => "Startup.Check.SubReelTimeout",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(key)) return _localization?.Get(key, message) ?? message;
        if (check == "license" && message.StartsWith("Профиль ", StringComparison.Ordinal) && message.EndsWith(" подтверждён через Minecraft Services.", StringComparison.Ordinal))
        {
            var username = message[8..^" подтверждён через Minecraft Services.".Length];
            return string.Format(_localization?.Culture ?? System.Globalization.CultureInfo.CurrentCulture,
                _localization?.Get("Startup.Check.LicensePassed", "Profile {0} verified through Minecraft Services.") ?? message, username);
        }
        if (check == "subreel" && message.StartsWith("Вход выполнен", StringComparison.Ordinal))
        {
            var email = message.StartsWith("Вход выполнен: ", StringComparison.Ordinal) ? message[15..] : string.Empty;
            var template = _localization?.Get("Startup.Check.SubReelPassed", "Signed in{0}") ?? message;
            return string.Format(_localization?.Culture ?? System.Globalization.CultureInfo.CurrentCulture, template, string.IsNullOrWhiteSpace(email) ? "." : $": {email}");
        }
        foreach (var (prefix, resourceKey) in new[]
        {
            ("Проверка завершилась с проблемой: ", "Startup.Check.FilesFailed"),
            ("Не удалось проверить профиль: ", "Startup.Check.LicenseFailed"),
            ("Не удалось проверить сессию: ", "Startup.Check.SubReelFailed")
        })
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                var template = _localization?.Get(resourceKey, prefix + "{0}") ?? prefix + "{0}";
                return string.Format(_localization?.Culture ?? System.Globalization.CultureInfo.CurrentCulture, template, message[prefix.Length..]);
            }
        }
        return message;
    }

    public async Task<LauncherSettings?> ShowWelcomeSetupAsync(LauncherSettings settings)
    {
        if (settings.HasCompletedWelcomeSetup)
        {
            ContinueButton.SetResourceReference(ContentControl.ContentProperty, "Startup.Opening");
            ContinueButton.IsEnabled = false;
            SkipButton.Visibility = Visibility.Collapsed;
            await Task.Delay(450);
            return null;
        }

        _settings = settings;
        ChecksPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        SkipButton.Visibility = Visibility.Visible;
        ContinueButton.SetResourceReference(ContentControl.ContentProperty, "Startup.SaveAndContinue");
        SubtitleText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "Startup.FirstRun");
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

    private Brush GetBrush(StartupCheckState state) => state switch
    {
        StartupCheckState.Passed => (Brush)FindResource("SuccessBrush"),
        StartupCheckState.Warning => (Brush)FindResource("WarningBrush"),
        StartupCheckState.Failed => (Brush)FindResource("ErrorBrush"),
        StartupCheckState.Running => (Brush)FindResource("AccentBrush"),
        _ => (Brush)FindResource("TextSecondary")
    };
}

public enum StartupCheckState { Pending, Running, Passed, Warning, Failed }
