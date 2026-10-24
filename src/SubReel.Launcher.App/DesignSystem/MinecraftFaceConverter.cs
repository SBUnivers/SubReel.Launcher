using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SubReel.Launcher.App.DesignSystem;

public sealed class MinecraftFaceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path) return null;
        if (!File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var face = new CroppedBitmap(bitmap, new Int32Rect(8, 8, 8, 8));
            face.Freeze();
            var drawing = new DrawingGroup();
            drawing.Children.Add(new ImageDrawing(face, new Rect(0, 0, 8, 8)));
            if (bitmap.PixelHeight >= 64)
            {
                var hat = new CroppedBitmap(bitmap, new Int32Rect(40, 8, 8, 8));
                hat.Freeze();
                drawing.Children.Add(new ImageDrawing(hat, new Rect(0, 0, 8, 8)));
            }
            drawing.Freeze();
            var result = new DrawingImage(drawing);
            result.Freeze();
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
