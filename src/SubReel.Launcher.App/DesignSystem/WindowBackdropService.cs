using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace SubReel.Launcher.App.DesignSystem;

public enum WindowBackdropMode { Mica, Acrylic, MicaAlt, Solid }

public interface IWindowBackdropService
{
    WindowBackdropMode Mode { get; }
    void SetMode(WindowBackdropMode mode);
    void Apply(Window window);
}

public sealed class WindowBackdropService : IWindowBackdropService
{
    public WindowBackdropMode Mode { get; private set; } = WindowBackdropMode.Mica;

    public void SetMode(WindowBackdropMode mode)
    {
        Mode = mode;
        foreach (Window window in Application.Current.Windows) Apply(window);
    }

    public void Apply(Window window)
    {
        var chrome = new WindowChrome { CaptionHeight = 62, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false };
        var handle = new WindowInteropHelper(window).Handle;
        var backdropType = Mode switch { WindowBackdropMode.Mica => 2, WindowBackdropMode.Acrylic => 3, WindowBackdropMode.MicaAlt => 4, _ => 1 };
        var transparent = false;
        var supportsBackdrop = Mode == WindowBackdropMode.MicaAlt
            ? OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            : OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (Mode != WindowBackdropMode.Solid && supportsBackdrop)
        {
            var darkMode = 1;
            transparent = DwmSetWindowAttribute(handle, 38, ref backdropType, sizeof(int)) == 0;
            _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        }
        if (transparent)
        {
            window.Background = Brushes.Transparent;
            chrome.GlassFrameThickness = new Thickness(-1);
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        }
        else window.Background = (Brush)Application.Current.FindResource("AppBackground");
        WindowChrome.SetWindowChrome(window, chrome);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
