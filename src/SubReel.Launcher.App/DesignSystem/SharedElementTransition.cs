using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SubReel.Launcher.App.DesignSystem;

/// <summary>Reusable shared-element overlay. Screen-specific pages own the expanded content.</summary>
public sealed class SharedElementTransition
{
    private readonly Canvas _overlay;
    private readonly FrameworkElement _card;
    private readonly Border _snapshot;
    private readonly double _scaleX;
    private readonly double _scaleY;
    private readonly TimeSpan _duration;

    private SharedElementTransition(Canvas overlay, FrameworkElement card, Border snapshot, Rect origin, double scaleX, double scaleY, TimeSpan duration)
    {
        _overlay = overlay; _card = card; _snapshot = snapshot; _origin = origin; _scaleX = scaleX; _scaleY = scaleY; _duration = duration;
    }

    public static SharedElementTransition? Create(FrameworkElement card, Canvas overlay)
    {
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0 || overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0) return null;
        var rect = card.TransformToVisual(overlay).TransformBounds(new Rect(card.RenderSize));
        var dpi = VisualTreeHelper.GetDpi(card);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)(card.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(card.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(card);
        var snapshot = new Border { Width = rect.Width, Height = rect.Height, CornerRadius = new CornerRadius(18), ClipToBounds = true, Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill }, IsHitTestVisible = false, RenderTransformOrigin = new Point(0, 0) };
        Canvas.SetLeft(snapshot, rect.Left); Canvas.SetTop(snapshot, rect.Top);
        overlay.Children.Add(snapshot);
        card.Visibility = Visibility.Hidden;
        var time = Application.Current.Resources["MotionSlowMs"] is double ms ? ms : 400;
        var duration = Application.Current.Resources["MotionLevel"] is MotionLevel.Off ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Application.Current.Resources["MotionLevel"] is MotionLevel.Reduced ? time * 0.5 : time);
        return new SharedElementTransition(overlay, card, snapshot, rect, overlay.ActualWidth / rect.Width, overlay.ActualHeight / rect.Height, duration);
    }

    public Task ExpandAsync()
    {
        var transform = new ScaleTransform(1, 1);
        _snapshot.RenderTransform = transform;
        return AnimateAsync(transform, 1, _scaleX, 1, _scaleY, Canvas.GetLeft(_snapshot), 0, Canvas.GetTop(_snapshot), 0);
    }

    public async Task CollapseAsync()
    {
        var transform = _snapshot.RenderTransform as ScaleTransform ?? new ScaleTransform(_scaleX, _scaleY);
        _snapshot.RenderTransform = transform;
        await AnimateAsync(transform, _scaleX, 1, _scaleY, 1, 0, _originLeft, 0, _originTop);
        _overlay.Children.Remove(_snapshot);
        _card.Visibility = Visibility.Visible;
    }

    private double _originLeft => _origin.Left;
    private double _originTop => _origin.Top;
    private readonly Rect _origin;

    private Task AnimateAsync(ScaleTransform transform, double fromX, double toX, double fromY, double toY, double fromLeft, double toLeft, double fromTop, double toTop)
    {
        if (_duration == TimeSpan.Zero) { transform.ScaleX = toX; transform.ScaleY = toY; Canvas.SetLeft(_snapshot, toLeft); Canvas.SetTop(_snapshot, toTop); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ease = Application.Current.Resources["EaseInOut"] as IEasingFunction;
        var x = new DoubleAnimation(fromX, toX, _duration) { EasingFunction = ease };
        var y = new DoubleAnimation(fromY, toY, _duration) { EasingFunction = ease };
        var left = new DoubleAnimation(fromLeft, toLeft, _duration) { EasingFunction = ease };
        var top = new DoubleAnimation(fromTop, toTop, _duration) { EasingFunction = ease };
        y.Completed += (_, _) => completion.TrySetResult();
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, x);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        _snapshot.BeginAnimation(Canvas.LeftProperty, left);
        _snapshot.BeginAnimation(Canvas.TopProperty, top);
        return completion.Task;
    }
}
