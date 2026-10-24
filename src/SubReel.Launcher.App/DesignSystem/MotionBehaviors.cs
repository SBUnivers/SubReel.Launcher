using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.CompilerServices;

namespace SubReel.Launcher.App.DesignSystem;

public sealed class PageTransitionControl : ContentControl
{
    private ContentPresenter? _presenter;
    private Image? _snapshot;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _presenter = GetTemplateChild("PART_Content") as ContentPresenter;
        _snapshot = GetTemplateChild("PART_Snapshot") as Image;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        var image = _snapshot;
        BitmapSource? previousFrame = null;
        if (oldContent is not null && _presenter is { ActualWidth: > 0, ActualHeight: > 0 } presenter)
        {
            var dpi = VisualTreeHelper.GetDpi(presenter);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)(presenter.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)(presenter.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(presenter);
            previousFrame = bitmap;
        }

        base.OnContentChanged(oldContent, newContent);
        if (image is null || previousFrame is null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            image.Source = previousFrame;
            image.Opacity = 1;
            image.Visibility = Visibility.Visible;
            var milliseconds = Application.Current.Resources["MotionNormalMs"] is double value ? value : 250;
            var level = Application.Current.Resources["MotionLevel"] is MotionLevel configuredLevel ? configuredLevel : MotionLevel.Full;
            var multiplier = level switch { MotionLevel.Reduced => 0.5, MotionLevel.Off => 0, _ => 1 };
            var duration = TimeSpan.FromMilliseconds(milliseconds * multiplier);
            if (duration == TimeSpan.Zero)
            {
                image.Visibility = Visibility.Collapsed;
                image.Source = null;
                if (_presenter is not null) _presenter.Opacity = 1;
                return;
            }
            var fade = new DoubleAnimation(1, 0, duration)
            {
                EasingFunction = Application.Current.Resources["EaseOut"] as IEasingFunction
            };
            fade.Completed += (_, _) => { image.Visibility = Visibility.Collapsed; image.Source = null; };
            image.BeginAnimation(OpacityProperty, fade);

            if (_presenter is not null)
            {
                _presenter.Opacity = 0;
                var translate = new TranslateTransform(0, 10);
                _presenter.RenderTransform = translate;
                var enter = new DoubleAnimation(0, 1, duration) { EasingFunction = Application.Current.Resources["EaseOut"] as IEasingFunction };
                var slide = new DoubleAnimation(10, 0, duration) { EasingFunction = Application.Current.Resources["EaseOut"] as IEasingFunction };
                enter.Completed += (_, _) => { _presenter.BeginAnimation(OpacityProperty, null); _presenter.Opacity = 1; };
                _presenter.BeginAnimation(OpacityProperty, enter);
                translate.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        });
    }
}

public static class MotionBehaviors
{
    private sealed class StaggerSubscription(ItemsControl owner)
    {
        public EventHandler GeneratorChanged { get; } = (_, _) => StaggerContainers(owner);
        public RoutedEventHandler Loaded { get; } = (_, _) => StaggerContainers(owner);
    }

    private static readonly ConditionalWeakTable<ItemsControl, StaggerSubscription> StaggerSubscriptions = new();
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached("HoverLift", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnHoverLiftChanged));
    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached("FadeIn", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnFadeInChanged));
    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached("Stagger", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnStaggerChanged));
    public static readonly DependencyProperty ShimmerProperty = DependencyProperty.RegisterAttached("Shimmer", typeof(bool), typeof(MotionBehaviors), new PropertyMetadata(false, OnShimmerChanged));

    public static void SetHoverLift(DependencyObject obj, bool value) => obj.SetValue(HoverLiftProperty, value);
    public static bool GetHoverLift(DependencyObject obj) => (bool)obj.GetValue(HoverLiftProperty);
    public static void SetFadeIn(DependencyObject obj, bool value) => obj.SetValue(FadeInProperty, value);
    public static bool GetFadeIn(DependencyObject obj) => (bool)obj.GetValue(FadeInProperty);
    public static void SetStagger(DependencyObject obj, bool value) => obj.SetValue(StaggerProperty, value);
    public static bool GetStagger(DependencyObject obj) => (bool)obj.GetValue(StaggerProperty);
    public static void SetShimmer(DependencyObject obj, bool value) => obj.SetValue(ShimmerProperty, value);
    public static bool GetShimmer(DependencyObject obj) => (bool)obj.GetValue(ShimmerProperty);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.MouseEnter += HoverEnter;
            element.MouseLeave += HoverLeave;
            element.CacheMode = new BitmapCache();
        }
        else { element.MouseEnter -= HoverEnter; element.MouseLeave -= HoverLeave; element.CacheMode = null; }
    }

    private static void HoverEnter(object sender, System.Windows.Input.MouseEventArgs e) => AnimateLift((FrameworkElement)sender, -8, 1.03);
    private static void HoverLeave(object sender, System.Windows.Input.MouseEventArgs e) => AnimateLift((FrameworkElement)sender, 0, 1);

    private static void AnimateLift(FrameworkElement element, double y, double scale)
    {
        var group = element.RenderTransform as TransformGroup;
        if (group is null) { group = new TransformGroup(); group.Children.Add(new ScaleTransform(1, 1)); group.Children.Add(new TranslateTransform()); element.RenderTransform = group; }
        if (group.Children[0] is ScaleTransform scaler && group.Children[1] is TranslateTransform mover)
        {
            var ms = Application.Current.Resources["MotionFastMs"] is double duration ? duration : 150;
            var ease = Application.Current.Resources["EaseInOut"] as IEasingFunction;
            scaler.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            scaler.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            mover.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }
    }

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue) element.Loaded += FadeLoaded; else element.Loaded -= FadeLoaded;
    }
    private static void FadeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= FadeLoaded;
        if (!AnimationsEnabled) { element.Opacity = 1; return; }
        element.Opacity = 0;
        var move = new TranslateTransform(0, 8); element.RenderTransform = move;
        var animation = new DoubleAnimation(0, 1, Duration(TimeSpan.FromMilliseconds(250))) { EasingFunction = Application.Current.Resources["EaseOut"] as IEasingFunction };
        animation.Completed += (_, _) => { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Duration(TimeSpan.FromMilliseconds(250))) { EasingFunction = Application.Current.Resources["EaseOut"] as IEasingFunction });
    }

    private static void OnStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items) return;
        if ((bool)e.NewValue)
        {
            if (StaggerSubscriptions.TryGetValue(items, out _)) return;
            var subscription = new StaggerSubscription(items);
            StaggerSubscriptions.Add(items, subscription);
            items.ItemContainerGenerator.StatusChanged += subscription.GeneratorChanged;
            items.Loaded += subscription.Loaded;
        }
        else RemoveStagger(items);
    }
    private static void RemoveStagger(ItemsControl items)
    {
        if (!StaggerSubscriptions.TryGetValue(items, out var subscription)) return;
        items.ItemContainerGenerator.StatusChanged -= subscription.GeneratorChanged;
        items.Loaded -= subscription.Loaded;
        StaggerSubscriptions.Remove(items);
    }
    private static void StaggerContainers(ItemsControl owner)
    {
        if (owner.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;
        for (var i = 0; i < owner.Items.Count; i++)
        {
            if (owner.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            container.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(i * 40);
            container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Duration(TimeSpan.FromMilliseconds(250))) { BeginTime = delay, EasingFunction = Application.Current.Resources["EaseOut"] as IEasingFunction });
        }
    }

    private static void OnShimmerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue)
        {
            if (!AnimationsEnabled) { element.Opacity = 0.65; return; }
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 0.8, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; }
    }

    private static bool AnimationsEnabled => Application.Current.Resources["MotionLevel"] is not MotionLevel.Off;
    private static Duration Duration(TimeSpan full) => TimeSpan.FromMilliseconds(full.TotalMilliseconds * (Application.Current.Resources["MotionLevel"] is MotionLevel.Reduced ? 0.5 : 1));
}
