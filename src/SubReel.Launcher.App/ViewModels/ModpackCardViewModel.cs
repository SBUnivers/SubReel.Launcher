using CommunityToolkit.Mvvm.ComponentModel;
using SubReel.Launcher.Abstractions.Modpacks;

namespace SubReel.Launcher.App.ViewModels;

public partial class ModpackCardViewModel(ModpackSummary project) : ObservableObject
{
    public ModpackSummary Project { get; } = project;
    [ObservableProperty] private string? _translatedTitle;
    [ObservableProperty] private string? _translatedDescription;
    public string DisplayTitle => TranslatedTitle ?? Project.Title;
    public string DisplayDescription => TranslatedDescription ?? Project.Description;
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string? IconUrl => Project.IconUrl;
    public string? AuthorLabel => string.IsNullOrWhiteSpace(Project.Author) ? null : $"Автор: {Project.Author}";
    public string CategoriesLabel => Project.CategoriesLabel;
    public string DownloadsLabel => Project.DownloadsLabel;
    partial void OnTranslatedTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnTranslatedDescriptionChanged(string? value) => OnPropertyChanged(nameof(DisplayDescription));
}
