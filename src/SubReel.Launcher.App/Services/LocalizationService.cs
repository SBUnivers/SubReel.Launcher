using System.Windows;
using System.Globalization;

namespace SubReel.Launcher.App.Services;

public interface ILocalizationService
{
    string Language { get; }
    CultureInfo Culture { get; }
    event EventHandler? LanguageChanged;
    string Get(string key, string fallback = "");
    void SetLanguage(string language);
}

public sealed class LocalizationService : ILocalizationService
{
    private const string LocaleResourceMarker = "Resources/Locales/";
    public string Language { get; private set; } = "ru-RU";
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public event EventHandler? LanguageChanged;

    public string Get(string key, string fallback = "") =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    public void SetLanguage(string language)
    {
        var normalized = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "ru-RU";
        if (Language == normalized && Application.Current?.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString.Contains($"/{normalized}.xaml", StringComparison.OrdinalIgnoreCase) == true) == true)
            return;

        var application = Application.Current;
        if (application is null) return;
        var dictionaries = application.Resources.MergedDictionaries;
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (dictionaries[index].Source?.OriginalString.Contains(LocaleResourceMarker, StringComparison.OrdinalIgnoreCase) == true)
                dictionaries.RemoveAt(index);
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/SubReel.Launcher.App;component/Resources/Locales/{normalized}.xaml", UriKind.Relative)
        });
        Language = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
