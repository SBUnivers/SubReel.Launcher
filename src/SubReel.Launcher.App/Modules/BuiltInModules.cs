using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Abstractions.Modules;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.ViewModels;
using SubReel.Launcher.App.DesignSystem;
using SubReel.Launcher.Rust.Abstractions.Models;
using SubReel.Launcher.Rust.Abstractions.Services;
using SubReel.Launcher.Rust.Core.Instances;

namespace SubReel.Launcher.App.Modules;

public sealed class HomeModule : ILauncherModule
{
    public string Id => "home";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<HomeViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("home", "Главная", "home", "Выбор версии Minecraft и запуск.", typeof(HomeViewModel))];
}

public sealed class InstancesModule : ILauncherModule
{
    public string Id => "instances";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<InstancesViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("instances", "Сборки", "instances", "Управление игровыми версиями и сборками Minecraft.", typeof(InstancesViewModel))];
}

public sealed class RustModule : ILauncherModule
{
    public string Id => "rust-clients";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<RustClientsViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("rust-clients", "Rust-клиенты", "instances", "Установка и запуск независимых клиентов Minecraft.", typeof(RustClientsViewModel))];
}

public sealed class ModsModule : ILauncherModule
{
    public string Id => "mods";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<ModsCatalogViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("mods", "Каталог", "mods", "Поиск модпаков в каталогах.", typeof(ModsCatalogViewModel))];
}

public sealed class SettingsModule : ILauncherModule
{
    public string Id => "settings";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<SettingsViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("settings", "Настройки", "settings", "Настройки лаунчера и обновления.", typeof(SettingsViewModel))];
}

public sealed class NewsModule : ILauncherModule
{
    public string Id => "news";
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient<ILauncherNewsService, PlatformLauncherNewsService>(client =>
            client.Timeout = TimeSpan.FromSeconds(15));
        services.AddTransient<NewsViewModel>();
    }
    public IEnumerable<NavItem> GetNavItems() => [new("news", "Новости", "news", "Новости и релизы лаунчера.", typeof(NewsViewModel))];
}

#if DEBUG
public sealed class DesignSystemModule : ILauncherModule
{
    public string Id => "design-system";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<DesignSystemViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("design-system", "Витрина UI", "design-system", "Компоненты темы и анимации (только Debug).", typeof(DesignSystemViewModel))];
}
#endif
