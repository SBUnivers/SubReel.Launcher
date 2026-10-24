using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubReel.Launcher.Abstractions.Settings;
using SubReel.Launcher.App.DesignSystem;

namespace SubReel.Launcher.App.ViewModels;

public partial class DesignSystemViewModel : ObservableObject
{
    private readonly IThemeService _theme;
    private readonly IMotionService _motion;
    private readonly ISettingsService _settings;

    [ObservableProperty] private bool _reducedMotion;
    [ObservableProperty] private double _sampleProgress = 62;
    [ObservableProperty] private string _sampleText = "SubReel Launcher";

    public IReadOnlyList<string> SampleItems { get; } = ["Карточка сборки", "Карточка новости", "Загрузка файлов"];

    public DesignSystemViewModel(IThemeService theme, IMotionService motion, ISettingsService settings)
    {
        _theme = theme;
        _motion = motion;
        _settings = settings;
        ReducedMotion = motion.Level != MotionLevel.Full;
    }

    [RelayCommand] private Task UseVioletAsync() => SetAccentAsync(Color.FromRgb(136, 117, 255));
    [RelayCommand] private Task UseCyanAsync() => SetAccentAsync(Color.FromRgb(64, 208, 255));
    [RelayCommand] private Task UseAmberAsync() => SetAccentAsync(Color.FromRgb(255, 190, 72));

    private async Task SetAccentAsync(Color color)
    {
        _theme.SetAccent(color);
        var settings = await _settings.LoadAsync();
        settings.AccentColor = color.ToString();
        await _settings.SaveAsync(settings);
    }

    partial void OnReducedMotionChanged(bool value)
    {
        _motion.SetLevel(value ? MotionLevel.Reduced : MotionLevel.Full);
        Application.Current.Resources["MotionLevel"] = _motion.Level;
        _ = PersistMotionAsync(value);
    }

    private async Task PersistMotionAsync(bool value)
    {
        var settings = await _settings.LoadAsync();
        settings.ReduceAnimations = value;
        await _settings.SaveAsync(settings);
    }
}
