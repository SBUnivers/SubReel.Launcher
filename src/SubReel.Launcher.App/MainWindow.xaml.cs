using System.Windows;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.App.ViewModels;
using SubReel.Launcher.App.Views;
using SubReel.Launcher.Auth.Supabase;
using SubReel.Launcher.Abstractions.Game;

namespace SubReel.Launcher.App;

public partial class MainWindow : Window
{
    private readonly IWindowBackdropService _backdrop;
    private readonly ISubReelAuthService _subReelAuth;
    private readonly IGameAuthService _gameAuth;
    private readonly ISupabaseGameProfileService _gameProfileService;

    public MainWindow(MainViewModel viewModel, IWindowBackdropService backdrop, ISubReelAuthService subReelAuth, IGameAuthService gameAuth, ISupabaseGameProfileService gameProfileService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _backdrop = backdrop;
        _subReelAuth = subReelAuth;
        _gameAuth = gameAuth;
        _gameProfileService = gameProfileService;
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
        MaximizeButton.Content = isMaximized ? "❐" : "□";
        MaximizeButton.ToolTip = isMaximized ? "Восстановить" : "Развернуть";
    }

    private void AccountMenu_Click(object sender, RoutedEventArgs e)
    {
        var window = new AuthWindow(_subReelAuth, _gameAuth, _gameProfileService) { Owner = this };
        window.ShowDialog();
    }
}
