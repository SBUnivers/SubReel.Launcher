using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Services;

namespace SubReel.Launcher.App.ViewModels;

public partial class NewsViewModel : ObservableObject
{
    private readonly ILauncherNewsService _news;
    public ObservableCollection<LauncherRelease> Releases { get; } = [];
    public LauncherRelease? FeaturedRelease => Releases.FirstOrDefault();
    public IEnumerable<LauncherRelease> OtherReleases => Releases.Skip(1);
    [ObservableProperty] private string _statusText = "Загружаю новости…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private LauncherRelease? _selectedRelease;
    [ObservableProperty] private bool _isReading;

    public NewsViewModel(ILauncherNewsService news)
    {
        _news = news;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            if (!_news.IsConfigured) { StatusText = "Новости пока не настроены. Добавьте адрес Supabase и publishable key в appsettings.json."; return; }
            var items = await _news.GetLatestAsync();
            Releases.Clear();
            foreach (var item in items) Releases.Add(item);
            OnPropertyChanged(nameof(FeaturedRelease));
            OnPropertyChanged(nameof(OtherReleases));
            StatusText = items.Count == 0 ? "Пока нет опубликованных обновлений." : $"Загружено обновлений: {items.Count}";
        }
        catch (Exception ex) { StatusText = $"Не удалось загрузить новости: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenArticle(LauncherRelease? release)
    {
        if (release is null) return;
        SelectedRelease = release;
        IsReading = true;
    }

    [RelayCommand]
    private void CloseArticle() => IsReading = false;

    [RelayCommand]
    private static void OpenRelease(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com") return;
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
}
