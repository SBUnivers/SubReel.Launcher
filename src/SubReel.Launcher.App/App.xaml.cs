using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SubReel.Launcher.App.Modules;
using SubReel.Launcher.App.Navigation;
using SubReel.Launcher.App.ViewModels;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Infrastructure.Game;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Launching;
using SubReel.Launcher.Infrastructure.Persistence;
using SubReel.Launcher.Auth;
using SubReel.Launcher.Auth.Supabase;
using SubReel.Launcher.App.Services;
using Velopack;
using SubReel.Launcher.Abstractions.Modules;
using SubReel.Launcher.Abstractions.Paths;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.Infrastructure.Paths;
using SubReel.Launcher.App.Views;
using SubReel.Launcher.App.DesignSystem;
using System.Windows.Media;

namespace SubReel.Launcher.App;

public partial class App : Application
{
    private IHost? _host;
    private int _errorDialogOpen;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var appPaths = new LocalAppPaths();
            appPaths.EnsureCreated();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(appPaths.Logs, "launcher-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger();

            var modules = new ILauncherModule[]
            {
                new HomeModule(),
                new InstancesModule(),
                new ModsModule(),
                new NewsModule(),
                new SettingsModule()
            };
#if DEBUG
            modules = [.. modules, new DesignSystemModule()];
#endif

            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton<IAppPaths>(appPaths);
            builder.Services.AddSingleton<ISettingsService, JsonSettingsService>();
            builder.Services.AddSingleton<IThemeService, ThemeService>();
            builder.Services.AddSingleton<IMotionService, MotionService>();
            builder.Services.AddSingleton<IWindowBackdropService, WindowBackdropService>();
            builder.Services.AddSingleton<IInstanceRepository, JsonInstanceRepository>();
            builder.Services.AddSingleton<InstanceService>();
            builder.Services.AddSingleton<ILaunchOrchestrator, LaunchOrchestrator>();
            builder.Services.AddHttpClient("Minecraft", client => client.Timeout = TimeSpan.FromMinutes(10))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxConnectionsPerServer = 16, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
            builder.Services.AddHttpClient<IGameVersionCatalog, CmlLibVersionCatalog>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReelLauncher/0.1.0");
            });
            builder.Services.AddSingleton<IGameService, CmlLibGameService>();
            builder.Services.AddHttpClient("SupabaseAuth", client => client.Timeout = TimeSpan.FromSeconds(20));
            builder.Services.AddSingleton<ISubReelAuthService, SupabaseAuthService>();
            builder.Services.AddSingleton<ISupabaseGameProfileService, SupabaseGameProfileService>();
            builder.Services.AddSingleton<IGameAuthService>(sp => new MicrosoftGameAuthService(
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Authentication:MicrosoftClientId"],
                sp.GetRequiredService<IAppPaths>().AccountsFile,
                sp.GetRequiredService<IAppPaths>().LauncherAccountsFile,
                Path.Combine(sp.GetRequiredService<IAppPaths>().Cache, "skins"),
                sp.GetRequiredService<IHttpClientFactory>()));
            builder.Services.AddSingleton<IUpdateService>(sp => new VelopackUpdateService(
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Updates:FeedUrl"],
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Updates:GitHubRepository"],
                bool.TryParse(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Updates:IncludePrereleases"], out var includePrereleases) && includePrereleases));

            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();

            foreach (var module in modules)
            {
                module.ConfigureServices(builder.Services);
                builder.Services.AddSingleton<ILauncherModule>(module);
            }

            builder.Services.AddSingleton<INavigationService, NavigationService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();

            await _host.StartAsync();

            var navigation = _host.Services
                .GetRequiredService<INavigationService>();

            var settings = await _host.Services.GetRequiredService<ISettingsService>().LoadAsync();
            var gameAuth = _host.Services.GetRequiredService<IGameAuthService>();
            gameAuth.ConfigureMicrosoftClientId(settings.MicrosoftClientId is null
                ? _host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Authentication:MicrosoftClientId"]
                : settings.MicrosoftClientId);
            try
            {
                if (ColorConverter.ConvertFromString(settings.AccentColor) is Color accentColor)
                    _host.Services.GetRequiredService<IThemeService>().SetAccent(accentColor);
            }
            catch (FormatException ex) { Log.Warning(ex, "В настройках указан некорректный цвет акцента; используется тема по умолчанию"); }
            var motion = _host.Services.GetRequiredService<IMotionService>();
            if (settings.ReduceAnimations) motion.SetLevel(MotionLevel.Reduced);
            Application.Current.Resources["MotionLevel"] = motion.Level;
            if (Enum.TryParse<WindowBackdropMode>(settings.WindowBackdrop, true, out var backdropMode))
                _host.Services.GetRequiredService<IWindowBackdropService>().SetMode(backdropMode);
            foreach (var module in _host.Services.GetServices<ILauncherModule>())
            {
                foreach (var item in module.GetNavItems())
                {
                    var navigationItem = new NavigationItem(item.Id, item.Title, item.Icon, item.Description);
                    navigation.Register(navigationItem, item.ViewModelType is null
                        ? null
                        : () => _host.Services.GetRequiredService(item.ViewModelType));
                }
            }

            navigation.NavigateTo("home");

            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            await mainViewModel.RestoreAccountAsync();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            Log.Information("SubReel Launcher запущен");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Не удалось запустить SubReel Launcher");

            MessageBox.Show(
                $"Не удалось запустить лаунчер.\n\n{ex.Message}",
                "SubReel Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Необработанная ошибка интерфейса");
        ShowError(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Необработанная ошибка домена приложения; процесс завершится");
            Dispatcher.BeginInvoke(() => ShowError(exception));
        }
        else Log.Fatal("Необработанная ошибка домена приложения: {ErrorObject}", e.ExceptionObject);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение фоновой задачи");
        e.SetObserved();
        Dispatcher.BeginInvoke(() => ShowError(e.Exception));
    }

    private void ShowError(Exception exception)
    {
        if (Interlocked.Exchange(ref _errorDialogOpen, 1) != 0) return;
        try
        {
            new ErrorDetailsWindow($"{exception.GetType().FullName}: {exception.Message}\n\n{exception}")
                { Owner = MainWindow }.ShowDialog();
        }
        catch (Exception dialogException)
        {
            Log.Error(dialogException, "Не удалось показать окно диагностики");
            MessageBox.Show(exception.Message, "Ошибка SubReel Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Interlocked.Exchange(ref _errorDialogOpen, 0); }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при остановке лаунчера");
            }
            finally
            {
                _host.Dispose();
            }
        }

        await Log.CloseAndFlushAsync();

        base.OnExit(e);
    }
}
