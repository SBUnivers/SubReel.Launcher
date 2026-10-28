using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Rust.Abstractions.Paths;
using SubReel.Launcher.Rust.Abstractions.Services;
using SubReel.Launcher.Rust.Core.Instances;
using SubReel.Launcher.Rust.Infrastructure.Catalog;
using SubReel.Launcher.Rust.Infrastructure.Installers;
using SubReel.Launcher.Rust.Infrastructure.Launching;
using SubReel.Launcher.Rust.Infrastructure.Paths;
using SubReel.Launcher.Rust.Infrastructure.Persistence;

namespace SubReel.Launcher.Rust.Infrastructure;

public static class RustServiceCollectionExtensions
{
    public static IServiceCollection AddRustSubsystem(this IServiceCollection services, string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        services.AddSingleton<IRustPaths>(_ => new RustPaths(launcherRoot));
        services.AddSingleton<IRustInstanceRepository, JsonRustInstanceRepository>();
        services.AddSingleton<IRustInstallationRepository, JsonRustInstallationRepository>();
        services.AddSingleton<IRustProcessStarter, RustProcessStarter>();
        services.AddSingleton<IRustClientLauncher, PommeClientLauncher>();
        services.AddSingleton<RustInstanceService>();
        services.AddHttpClient<IRustClientCatalog, PommeReleaseCatalog>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SubReelLauncher/0.1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        services.AddHttpClient<IRustClientInstaller, PommeClientInstaller>(client =>
            client.Timeout = TimeSpan.FromMinutes(5));
        return services;
    }
}
