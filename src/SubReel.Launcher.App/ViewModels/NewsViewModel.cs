using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.App.Services;

namespace SubReel.Launcher.App.ViewModels;

public partial class NewsViewModel : ObservableObject
{
    private readonly ILauncherNewsService _news;
    private readonly ILocalizationService _localization;
    private string _statusKey = "News.Loading";
    private string _statusFallback = "Загружаю новости…";
    private int _loadedCount;
    public ObservableCollection<LauncherRelease> Releases { get; } = [];
    public LauncherRelease? FeaturedRelease => Releases.FirstOrDefault();
    public IEnumerable<LauncherRelease> OtherReleases => Releases.Skip(1);
    public bool IsEmpty => Releases.Count == 0 && !IsBusy;
    public string StatusText
    {
        get
        {
            var text = _localization.Get(_statusKey, _statusFallback);
            return _statusKey == "News.Loaded" ? string.Format(_localization.Culture, text, _loadedCount) : text;
        }
    }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private LauncherRelease? _selectedRelease;
    [ObservableProperty] private bool _isReading;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public NewsViewModel(ILauncherNewsService news, ILocalizationService localization)
    {
        _news = news;
        _localization = localization;
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(FeaturedRelease));
            OnPropertyChanged(nameof(OtherReleases));
            OnPropertyChanged(nameof(SelectedRelease));
        };
        Releases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            if (!_news.IsConfigured) { SetStatus("News.NotConfigured", "Новости пока не настроены. Проверьте Platform:ApiBaseUrl в appsettings.json."); return; }
            var items = await _news.GetLatestAsync();
            Releases.Clear();
            foreach (var item in items) Releases.Add(item);
            OnPropertyChanged(nameof(FeaturedRelease));
            OnPropertyChanged(nameof(OtherReleases));
            _loadedCount = items.Count;
            SetStatus(items.Count == 0 ? "News.NoUpdates" : "News.Loaded", items.Count == 0 ? "Пока нет опубликованных обновлений." : $"Загружено обновлений: {items.Count}");
        }
        catch (HttpRequestException) { SetStatus("News.NetworkError", "Не удалось связаться с SubReel Platform. Проверь интернет и доступность сайта."); }
        catch (JsonException) { SetStatus("News.InvalidData", "Сайт вернул некорректные данные новостей. Попробуй обновить позже."); }
        catch (Exception) { SetStatus("News.LoadError", "Не удалось загрузить новости. Попробуй обновить позже."); }
        finally { IsBusy = false; }
    }

    private void SetStatus(string key, string fallback)
    {
        _statusKey = key;
        _statusFallback = fallback;
        OnPropertyChanged(nameof(StatusText));
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
