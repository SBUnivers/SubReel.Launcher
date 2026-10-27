using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SubReel.Launcher.App.DesignSystem;

public interface IThemeService
{
    Color AccentColor { get; }
    void SetAccent(Color color);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Color DefaultAccent = Color.FromRgb(136, 117, 255);
    private readonly DispatcherTimer _accentTimer;
    private Color _accentStart;
    private Color _accentTarget;
    private DateTime _accentTransitionStarted;
    private double _accentTransitionDurationMs;

    public ThemeService()
    {
        _accentTimer = new DispatcherTimer(DispatcherPriority.Render, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _accentTimer.Tick += OnAccentTimerTick;
    }

    public Color AccentColor => Application.Current.Resources["AccentColor"] is Color color ? color : DefaultAccent;

    public void SetAccent(Color color)
    {
        var resources = Application.Current.Resources;
        var current = AccentColor;
        if (current == color)
        {
            _accentTimer.Stop();
            return;
        }

        var motionLevel = resources["MotionLevel"] is MotionLevel level ? level : MotionLevel.Full;
        _accentTransitionDurationMs = motionLevel switch
        {
            MotionLevel.Reduced => 125,
            MotionLevel.Off => 0,
            _ => 250
        };
        if (_accentTransitionDurationMs <= 0)
        {
            _accentTimer.Stop();
            resources["AccentColor"] = color;
            return;
        }

        _accentStart = current;
        _accentTarget = color;
        _accentTransitionStarted = DateTime.UtcNow;
        _accentTimer.Stop();
        _accentTimer.Start();
    }

    private void OnAccentTimerTick(object? sender, EventArgs e)
    {
        var resources = Application.Current.Resources;
        if (resources["MotionLevel"] is MotionLevel.Off)
        {
            _accentTimer.Stop();
            resources["AccentColor"] = _accentTarget;
            return;
        }

        var progress = Math.Clamp((DateTime.UtcNow - _accentTransitionStarted).TotalMilliseconds / _accentTransitionDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        resources["AccentColor"] = Interpolate(_accentStart, _accentTarget, eased);
        if (progress >= 1)
        {
            _accentTimer.Stop();
            resources["AccentColor"] = _accentTarget;
        }
    }

    private static Color Interpolate(Color from, Color to, double amount) => Color.FromArgb(
        Interpolate(from.A, to.A, amount),
        Interpolate(from.R, to.R, amount),
        Interpolate(from.G, to.G, amount),
        Interpolate(from.B, to.B, amount));

    private static byte Interpolate(byte from, byte to, double amount) =>
        (byte)Math.Clamp((int)Math.Round(from + (to - from) * amount), byte.MinValue, byte.MaxValue);
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
