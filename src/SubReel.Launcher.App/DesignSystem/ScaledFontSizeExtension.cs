using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace SubReel.Launcher.App.DesignSystem;

public sealed class FontScaleProvider : DependencyObject
{
    public static FontScaleProvider Instance { get; } = new();

    public static readonly DependencyProperty ScaleFactorProperty = DependencyProperty.Register(
        nameof(ScaleFactor), typeof(double), typeof(FontScaleProvider),
        new FrameworkPropertyMetadata(1d));

    public double ScaleFactor
    {
        get => (double)GetValue(ScaleFactorProperty);
        set => SetValue(ScaleFactorProperty, Math.Clamp(value, 0.85, 1.25));
    }
}

[MarkupExtensionReturnType(typeof(double))]
public sealed class ScaledFontSizeExtension(double baseFontSize) : MarkupExtension
{
    private static readonly IMultiValueConverter Converter = new FontScaleConverter();

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding
        {
            Converter = Converter,
            Mode = BindingMode.OneWay
        };
        binding.Bindings.Add(new Binding { Source = baseFontSize });
        binding.Bindings.Add(new Binding(nameof(FontScaleProvider.ScaleFactor)) { Source = FontScaleProvider.Instance });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class FontScaleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length != 2 || values[0] is not double baseSize || values[1] is not double scale)
                return DependencyProperty.UnsetValue;

            return Math.Round(baseSize * scale, MidpointRounding.AwayFromZero);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
