using System.Windows;
using System.Windows.Media;

namespace SubReel.Launcher.App.DesignSystem;

public interface IThemeService
{
    Color AccentColor { get; }
    void SetAccent(Color color);
}

public sealed class ThemeService : IThemeService
{
    public Color AccentColor => Application.Current.Resources["AccentColor"] is Color color ? color : Color.FromRgb(136, 117, 255);

    public void SetAccent(Color color)
    {
        Application.Current.Resources["AccentColor"] = color;
    }
}

public enum MotionLevel { Full, Reduced, Off }

public interface IMotionService
{
    MotionLevel Level { get; }
    double DurationMultiplier { get; }
    TimeSpan Duration(TimeSpan fullDuration);
    void SetLevel(MotionLevel level);
}

public sealed class MotionService : IMotionService
{
    public MotionLevel Level { get; private set; }
    public double DurationMultiplier => Level switch { MotionLevel.Full => 1, MotionLevel.Reduced => 0.5, _ => 0 };

    public MotionService()
    {
        Level = (RenderCapability.Tier >> 16) == 0 ? MotionLevel.Reduced : MotionLevel.Full;
    }

    public TimeSpan Duration(TimeSpan fullDuration) => TimeSpan.FromMilliseconds(fullDuration.TotalMilliseconds * DurationMultiplier);
    public void SetLevel(MotionLevel level) => Level = level;
}
