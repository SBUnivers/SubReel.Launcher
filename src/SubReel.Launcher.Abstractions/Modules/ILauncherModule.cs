using Microsoft.Extensions.DependencyInjection;

namespace SubReel.Launcher.Abstractions.Modules;

/// <summary>Registration contract shared by built-in modules and future extensions.</summary>
public interface ILauncherModule
{
    string Id { get; }
    void ConfigureServices(IServiceCollection services);
    IEnumerable<NavItem> GetNavItems();
}

public sealed record NavItem(
    string Id,
    string Title,
    string Icon,
    string Description,
    Type? ViewModelType = null);
