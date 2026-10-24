using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SubReel.Launcher.App.ViewModels;

namespace SubReel.Launcher.App.Views;

public partial class NewsView : UserControl
{
    private NewsViewModel? _viewModel;

    public NewsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SyncReader(_viewModel?.IsReading == true, false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as NewsViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncReader(_viewModel?.IsReading == true, false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewsViewModel.IsReading))
            Dispatcher.BeginInvoke(new Action(() => SyncReader(_viewModel?.IsReading == true, true)));
    }

    private void SyncReader(bool reading, bool animate)
    {
        var incoming = reading ? ReaderLayer : NewsLayer;
        var outgoing = reading ? NewsLayer : ReaderLayer;
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        outgoing.IsHitTestVisible = false;
        if (!animate)
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            outgoing.Visibility = Visibility.Collapsed;
            SetOffset(incoming, 0);
            SetOffset(outgoing, reading ? -12 : 12);
            return;
        }
        Animate(outgoing, UIElement.OpacityProperty, outgoing.Opacity, 0);
        AnimateOffset(outgoing, Offset(outgoing), reading ? -12 : 12);
        var fade = Animate(incoming, UIElement.OpacityProperty, 0, 1);
        AnimateOffset(incoming, reading ? 14 : -12, 0);
        fade.Completed += (_, _) => outgoing.Visibility = Visibility.Collapsed;
    }

    private static DoubleAnimation Animate(UIElement target, DependencyProperty property, double from, double to)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        target.BeginAnimation(property, animation);
        return animation;
    }
    private static double Offset(UIElement element) => element.RenderTransform is TranslateTransform translate ? translate.Y : 0;
    private static void SetOffset(UIElement element, double value) { if (element.RenderTransform is TranslateTransform translate) translate.Y = value; }
    private static void AnimateOffset(UIElement element, double from, double to)
    {
        if (element.RenderTransform is not TranslateTransform translate) return;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(320))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
