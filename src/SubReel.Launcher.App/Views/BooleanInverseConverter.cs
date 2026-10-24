using System.Globalization;
using System.Windows.Data;

namespace SubReel.Launcher.App.Views;

public sealed class BooleanInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && !flag;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && !flag;
}
