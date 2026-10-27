using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SubReel.Launcher.App.DesignSystem;

public sealed class IconGeometryConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> IconKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["home"] = "IconHomeGeometry",
        ["instances"] = "IconInstancesGeometry",
        ["mods"] = "IconSparklesGeometry",
        ["settings"] = "IconSettingsGeometry",
        ["news"] = "IconNewsGeometry",
        ["design-system"] = "IconLayersGeometry",
        ["⌂"] = "IconHomeGeometry",
        ["▣"] = "IconInstancesGeometry",
        ["✧"] = "IconSparklesGeometry",
        ["⚙"] = "IconSettingsGeometry",
        ["▤"] = "IconNewsGeometry",
        ["◈"] = "IconLayersGeometry",
        ["⌕"] = "IconSearchGeometry"
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value?.ToString();
        if (string.IsNullOrWhiteSpace(key))
            return Geometry.Empty;

        if (IconKeys.TryGetValue(key, out var resourceKey))
            key = resourceKey;

        return Application.Current.TryFindResource(key) as Geometry ?? Geometry.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
