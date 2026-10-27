using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.App.ViewModels;
using SubReel.Launcher.App.Views;
using SubReel.Launcher.Auth.Supabase;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.App.Services;

namespace SubReel.Launcher.App;

public partial class MainWindow : Window
{
    private readonly IWindowBackdropService _backdrop;
    private readonly ISubReelAuthService _subReelAuth;
    private readonly IGameAuthService _gameAuth;
    private readonly ISupabaseGameProfileService _gameProfileService;
    private readonly ILocalizationService _localization;

    public MainWindow(MainViewModel viewModel, IWindowBackdropService backdrop, ISubReelAuthService subReelAuth, IGameAuthService gameAuth, ISupabaseGameProfileService gameProfileService, ILocalizationService localization)
    {
        InitializeComponent();
        DataContext = viewModel;
        _backdrop = backdrop;
        _subReelAuth = subReelAuth;
        _gameAuth = gameAuth;
        _gameProfileService = gameProfileService;
        _localization = localization;
        _localization.LanguageChanged += (_, _) => UpdateWindowButtons();
        StateChanged += (_, _) => UpdateWindowButtons();
        UpdateWindowButtons();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _backdrop.Apply(this);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowButtons()
    {
        if (MaximizeButton is null) return;
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = (Geometry)FindResource(isMaximized ? "IconRestoreGeometry" : "IconMaximizeGeometry");
        MaximizeButton.ToolTip = _localization.Get(isMaximized ? "Shell.Restore" : "Shell.Maximize", isMaximized ? "Восстановить" : "Развернуть");
        AutomationProperties.SetName(MaximizeButton, _localization.Get(isMaximized ? "Shell.RestoreWindow" : "Shell.MaximizeWindow", isMaximized ? "Восстановить окно" : "Развернуть окно"));
    }

    private void AccountMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new AuthWindow(_subReelAuth, _gameAuth, _gameProfileService, _localization) { Owner = this };
        window.ShowDialog();
    }
}
