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
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Infrastructure.Paths;
using SubReel.Launcher.Infrastructure.Modpacks;
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

        StartupWindow? startupWindow = null;
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

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            startupWindow = new StartupWindow();
            MainWindow = startupWindow;
            startupWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);

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
            var moduleDefinitions = modules
                .Select(module => (Module: module, NavigationItems: module.GetNavItems().ToArray()))
                .ToArray();
            ValidateModuleDefinitions(moduleDefinitions);

            var builder = Host.CreateApplicationBuilder();
            builder.ConfigureContainer(
                new DefaultServiceProviderFactory(new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                }),
                _ => { });

            builder.Services.AddSingleton<IAppPaths>(appPaths);
            builder.Services.AddSingleton<ISettingsService, JsonSettingsService>();
            builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
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
            builder.Services.AddHttpClient("LoaderMetadata", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReelLauncher/0.1.0");
            });
            builder.Services.AddHttpClient<IModpackCatalog, ModrinthModpackCatalog>(client =>
            {
                client.BaseAddress = new Uri("https://api.modrinth.com/v2/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReelLauncher/0.1.0 (+https://github.com/SBUnivers/SubReel.Launcher)");
            });
            builder.Services.AddHttpClient<ICurseForgeApiClient, CurseForgeProxyApiClient>(client =>
            {
                client.BaseAddress = new Uri(builder.Configuration["Platform:CurseForgeProxyUrl"] ?? "https://www.subreel.ru/api/launcher/curseforge/");
                client.Timeout = TimeSpan.FromMinutes(5);
            });
            builder.Services.AddTransient<IModpackCatalog, CurseForgeModpackCatalog>();
            builder.Services.AddTransient<ICurseForgeModpackInstaller, CurseForgeModpackInstaller>();
            builder.Services.AddSingleton<ILauncherSessionProvider, LauncherSessionProvider>();
            builder.Services.AddHttpClient<IModpackInstaller, ModrinthModpackInstaller>(client =>
            {
                client.BaseAddress = new Uri("https://api.modrinth.com/v2/");
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReelLauncher/0.1.0 (+https://github.com/SBUnivers/SubReel.Launcher)");
            });
            builder.Services.AddHttpClient<IModpackTextTranslator, DeepLModpackTextTranslator>(client =>
            {
                client.BaseAddress = new Uri(builder.Configuration["Platform:ApiBaseUrl"] ?? "https://www.subreel.ru/api/launcher/");
                client.Timeout = TimeSpan.FromSeconds(25);
            });
            builder.Services.AddSingleton<IGameService, CmlLibGameService>();
            builder.Services.AddSingleton<IJavaRuntimeInspector, JavaRuntimeInspector>();
            builder.Services.AddSingleton<IGameLoaderCatalog, CmlLibGameLoaderCatalog>();
            builder.Services.AddHttpClient("SupabaseAuth", client => client.Timeout = TimeSpan.FromSeconds(20));
            builder.Services.AddSingleton<ISubReelAuthService, SupabaseAuthService>();
            builder.Services.AddSingleton<ISupabaseGameProfileService, SupabaseGameProfileService>();
            builder.Services.AddHttpClient<IPlatformHealthService, PlatformHealthService>(client =>
                client.Timeout = TimeSpan.FromSeconds(10));
            builder.Services.AddSingleton<IDiagnosticBundleService, DiagnosticBundleService>();
            builder.Services.AddSingleton<IGameAuthService>(sp => new MicrosoftGameAuthService(
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

            foreach (var definition in moduleDefinitions)
            {
                definition.Module.ConfigureServices(builder.Services);
                builder.Services.AddSingleton<ILauncherModule>(definition.Module);
            }

            builder.Services.AddSingleton<INavigationService, NavigationService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();

            await _host.StartAsync();

            try
            {
                var recoveredInstallCount = await _host.Services.GetRequiredService<InstanceService>()
                    .RecoverInterruptedModpackInstallsAsync();
                if (recoveredInstallCount > 0)
                    Log.Warning("Удалено незавершённых установок модпаков после прошлого аварийного завершения: {Count}", recoveredInstallCount);
            }
            catch (Exception recoveryException)
            {
                Log.Warning(recoveryException, "Не удалось полностью очистить незавершённые установки; запуск лаунчера продолжится");
            }

            var navigation = _host.Services
                .GetRequiredService<INavigationService>();

            var settings = await _host.Services.GetRequiredService<ISettingsService>().LoadAsync();
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            localization.SetLanguage(settings.Language);
            startupWindow.UseLocalization(localization);
            FontScaleProvider.Instance.ScaleFactor = settings.TextScale;
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

            startupWindow.SetCheck("files", "Проверяю приложение и локальные папки…", StartupCheckState.Running);
            await Dispatcher.Yield(DispatcherPriority.Background);
            try
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                    throw new FileNotFoundException("Не найден исполняемый файл лаунчера.");
                appPaths.EnsureCreated();
                var requiredDirectories = new[] { appPaths.Root, appPaths.Auth, appPaths.Cache, appPaths.Instances, appPaths.Shared };
                var missingDirectory = requiredDirectories.FirstOrDefault(path => !Directory.Exists(path));
                if (missingDirectory is not null) throw new IOException($"Не удалось подготовить папку {missingDirectory}.");
                startupWindow.SetCheck("files", "Файлы приложения на месте; папки данных готовы.", StartupCheckState.Passed);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Начальная проверка файлов лаунчера завершилась с проблемой");
                startupWindow.SetCheck("files", $"Проверка завершилась с проблемой: {ex.Message}", StartupCheckState.Failed);
            }

            var gameAuth = _host.Services.GetRequiredService<IGameAuthService>();
            startupWindow.SetCheck("license", "Проверяю сохранённый профиль Minecraft…", StartupCheckState.Running);
            await Dispatcher.Yield(DispatcherPriority.Background);
            try
            {
                using var gameCheckTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var gameSession = await gameAuth.TryRestoreSessionAsync(gameCheckTimeout.Token);
                if (gameSession is not null)
                    startupWindow.SetCheck("license", $"Профиль {gameSession.Username} подтверждён через Minecraft Services.", StartupCheckState.Passed);
                else if (gameAuth.ActiveAccount?.Type == LauncherAccountType.Offline)
                    startupWindow.SetCheck("license", "Активен офлайн-профиль. Лицензия Minecraft Java этим профилем не подтверждается.", StartupCheckState.Warning);
                else
                    startupWindow.SetCheck("license", "Microsoft-профиль не подключён. Лицензию можно проверить после входа.", StartupCheckState.Warning);
            }
            catch (OperationCanceledException)
            {
                startupWindow.SetCheck("license", "Проверка заняла слишком много времени. Её можно повторить в профиле.", StartupCheckState.Warning);
            }
            catch (Exception ex)
            {
                Log.Information(ex, "Проверка лицензии Minecraft при запуске не завершилась");
                startupWindow.SetCheck("license", $"Не удалось проверить профиль: {ex.Message}", StartupCheckState.Warning);
            }

            var subReelAuth = _host.Services.GetRequiredService<ISubReelAuthService>();
            async Task CheckSubReelSessionAsync()
            {
                startupWindow.SetCheck("subreel", "Проверяю сессию SubReel…", StartupCheckState.Running);
                await Dispatcher.Yield(DispatcherPriority.Background);
                try
                {
                    using var sessionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var authRestore = await subReelAuth.RestoreSessionAsync(sessionTimeout.Token);
                    if (authRestore.Success && authRestore.Session is { } session)
                        startupWindow.SetCheck("subreel", $"Вход выполнен{(string.IsNullOrWhiteSpace(session.Email) ? "." : $": {session.Email}")}", StartupCheckState.Passed);
                    else
                        startupWindow.SetCheck("subreel", authRestore.Message, StartupCheckState.Warning);
                }
                catch (OperationCanceledException)
                {
                    startupWindow.SetCheck("subreel", "Проверка заняла слишком много времени. Проверь подключение.", StartupCheckState.Warning);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Не удалось проверить сессию SubReel при запуске");
                    startupWindow.SetCheck("subreel", $"Не удалось проверить сессию: {ex.Message}", StartupCheckState.Warning);
                }
            }

            startupWindow.LoginRequested += async (_, _) =>
            {
                try
                {
                    var authWindow = new AuthWindow(subReelAuth, gameAuth,
                        _host.Services.GetRequiredService<ISupabaseGameProfileService>(),
                        _host.Services.GetRequiredService<ILocalizationService>()) { Owner = startupWindow };
                    authWindow.ShowDialog();
                    await CheckSubReelSessionAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Не удалось открыть вход SubReel из приветственного экрана");
                    startupWindow.SetCheck("subreel", ex.Message, StartupCheckState.Warning);
                }
            };

            await CheckSubReelSessionAsync();
            var completedSetup = await startupWindow.ShowWelcomeSetupAsync(settings);
            if (completedSetup is not null)
            {
                settings = completedSetup;
                await _host.Services.GetRequiredService<ISettingsService>().SaveAsync(settings);
                _host.Services.GetRequiredService<ILocalizationService>().SetLanguage(settings.Language);
            }
            if (settings.ReduceAnimations)
            {
                _host.Services.GetRequiredService<IMotionService>().SetLevel(MotionLevel.Reduced);
                Application.Current.Resources["MotionLevel"] = MotionLevel.Reduced;
            }

            foreach (var definition in moduleDefinitions)
            {
                foreach (var item in definition.NavigationItems)
                {
                    var navigationItem = new NavigationItem(item.Id, item.Title, item.Icon, item.Description);
                    navigation.Register(navigationItem, item.ViewModelType is null
                        ? null
                        : () => _host.Services.GetRequiredService(item.ViewModelType));
                }
            }

            navigation.NavigateTo("home");

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            startupWindow.Close();

            Log.Information("SubReel Launcher запущен");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Не удалось запустить SubReel Launcher");
            startupWindow?.Close();

            MessageBox.Show(
                $"Не удалось запустить лаунчер.\n\n{ex.Message}",
                "SubReel Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    private static void ValidateModuleDefinitions(
        IReadOnlyCollection<(ILauncherModule Module, NavItem[] NavigationItems)> definitions)
    {
        var invalidModule = definitions.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Module.Id));
        if (invalidModule.Module is not null)
            throw new InvalidOperationException("У модуля лаунчера должен быть задан непустой идентификатор.");

        var duplicateModuleId = definitions
            .GroupBy(x => x.Module.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateModuleId is not null)
            throw new InvalidOperationException($"Идентификатор модуля '{duplicateModuleId.Key}' зарегистрирован несколько раз.");

        var navigationItems = definitions.SelectMany(x => x.NavigationItems).ToArray();
        if (navigationItems.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id)))
            throw new InvalidOperationException("Каждый пункт навигации модуля должен иметь непустой идентификатор.");

        var duplicateNavigationId = navigationItems
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNavigationId is not null)
            throw new InvalidOperationException($"Идентификатор страницы '{duplicateNavigationId.Key}' зарегистрирован несколькими модулями.");
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
