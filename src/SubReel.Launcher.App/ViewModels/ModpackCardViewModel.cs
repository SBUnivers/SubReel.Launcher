using CommunityToolkit.Mvvm.ComponentModel;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.App.Services;

namespace SubReel.Launcher.App.ViewModels;

public partial class ModpackCardViewModel(ModpackSummary project, ILocalizationService localization) : ObservableObject
{
    public ModpackSummary Project { get; } = project;
    [ObservableProperty] private string? _translatedTitle;
    [ObservableProperty] private string? _translatedDescription;
    public string DisplayTitle => TranslatedTitle ?? Project.Title;
    public string DisplayDescription => TranslatedDescription ?? Project.Description;
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string? IconUrl => Project.IconUrl;
    public string? AuthorLabel => string.IsNullOrWhiteSpace(Project.Author) ? null : string.Format(localization.Culture, localization.Get("Mods.AuthorLabel", "Author: {0}"), Project.Author);
    public string CategoriesLabel => Project.CategoriesLabel;
    public string DownloadsLabel => string.Format(localization.Culture,
        localization.Get("Mods.DownloadsLabel", "{0} downloads"), Project.Downloads.ToString("N0", localization.Culture));
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(AuthorLabel));
        OnPropertyChanged(nameof(DownloadsLabel));
    }
    partial void OnTranslatedTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnTranslatedDescriptionChanged(string? value) => OnPropertyChanged(nameof(DisplayDescription));
}
