using Microsoft.Extensions.DependencyInjection;
using SubReel.Launcher.Abstractions.Modules;
using SubReel.Launcher.App.Services;
using SubReel.Launcher.App.ViewModels;
using SubReel.Launcher.App.DesignSystem;

namespace SubReel.Launcher.App.Modules;

public sealed class HomeModule : ILauncherModule
{
    public string Id => "home";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<HomeViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("home", "Главная", "⌂", "Выбор версии Minecraft и запуск.", typeof(HomeViewModel))];
}

public sealed class InstancesModule : ILauncherModule
{
    public string Id => "instances";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<InstancesViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("instances", "Сборки", "▣", "Управление игровыми версиями и сборками Minecraft.", typeof(InstancesViewModel))];
}

public sealed class ModsModule : ILauncherModule
{
    public string Id => "mods";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<ModsCatalogViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("mods", "Каталог", "✧", "Поиск модпаков в каталогах.", typeof(ModsCatalogViewModel))];
}

public sealed class SettingsModule : ILauncherModule
{
    public string Id => "settings";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<SettingsViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("settings", "Настройки", "⚙", "Настройки лаунчера и обновления.", typeof(SettingsViewModel))];
}

public sealed class NewsModule : ILauncherModule
{
    public string Id => "news";
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient<ILauncherNewsService, SupabaseLauncherNewsService>();
        services.AddTransient<NewsViewModel>();
    }
    public IEnumerable<NavItem> GetNavItems() => [new("news", "Новости", "▤", "Новости и релизы лаунчера.", typeof(NewsViewModel))];
}

#if DEBUG
public sealed class DesignSystemModule : ILauncherModule
{
    public string Id => "design-system";
    public void ConfigureServices(IServiceCollection services) => services.AddTransient<DesignSystemViewModel>();
    public IEnumerable<NavItem> GetNavItems() => [new("design-system", "Витрина UI", "◈", "Компоненты темы и анимации (только Debug).", typeof(DesignSystemViewModel))];
}
#endif
