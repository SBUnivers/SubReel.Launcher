using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SubReel.Launcher.App.DesignSystem;
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

    private void OnFeaturedCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isCompact = e.NewSize.Width < 620;
        FeaturedCardGrid.RowDefinitions.Clear();
        FeaturedCardGrid.ColumnDefinitions.Clear();
        if (isCompact)
        {
            FeaturedCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FeaturedCardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            FeaturedCardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumnSpan(FeaturedTint, 1);
            Grid.SetColumnSpan(FeaturedImage, 1);
            Grid.SetColumn(FeaturedVersionArt, 0);
            Grid.SetRow(FeaturedVersionArt, 0);
            Grid.SetColumn(FeaturedArtGlow, 0);
            Grid.SetRow(FeaturedArtGlow, 0);
            Grid.SetColumn(FeaturedTextPanel, 0);
            Grid.SetRow(FeaturedTextPanel, 1);
            FeaturedVersionArt.FontSize = 48;
            FeaturedVersionArt.HorizontalAlignment = HorizontalAlignment.Right;
            FeaturedVersionArt.Margin = new Thickness(0, 8, 20, 0);
            FeaturedArtGlow.Visibility = Visibility.Collapsed;
        }
        else
        {
            FeaturedCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            FeaturedCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            Grid.SetColumnSpan(FeaturedTint, 2);
            Grid.SetColumnSpan(FeaturedImage, 2);
            Grid.SetColumn(FeaturedVersionArt, 1);
            Grid.SetRow(FeaturedVersionArt, 0);
            Grid.SetColumn(FeaturedArtGlow, 1);
            Grid.SetRow(FeaturedArtGlow, 0);
            Grid.SetColumn(FeaturedTextPanel, 0);
            Grid.SetRow(FeaturedTextPanel, 0);
            FeaturedVersionArt.FontSize = 88;
            FeaturedVersionArt.HorizontalAlignment = HorizontalAlignment.Center;
            FeaturedVersionArt.Margin = new Thickness(0);
            FeaturedArtGlow.Visibility = Visibility.Visible;
        }
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
        var motionLevel = Application.Current.Resources["MotionLevel"] is MotionLevel configuredLevel
            ? configuredLevel
            : MotionLevel.Full;
        var durationMultiplier = motionLevel switch
        {
            MotionLevel.Reduced => 0.5,
            MotionLevel.Off => 0,
            _ => 1
        };
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        outgoing.IsHitTestVisible = false;
        if (!animate || durationMultiplier == 0)
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            outgoing.Visibility = Visibility.Collapsed;
            SetOffset(incoming, 0);
            SetOffset(outgoing, reading ? -12 : 12);
            return;
        }
        Animate(outgoing, UIElement.OpacityProperty, outgoing.Opacity, 0, TimeSpan.FromMilliseconds(300 * durationMultiplier));
        AnimateOffset(outgoing, Offset(outgoing), reading ? -12 : 12, TimeSpan.FromMilliseconds(320 * durationMultiplier));
        var fade = Animate(incoming, UIElement.OpacityProperty, 0, 1, TimeSpan.FromMilliseconds(300 * durationMultiplier));
        AnimateOffset(incoming, reading ? 14 : -12, 0, TimeSpan.FromMilliseconds(320 * durationMultiplier));
        fade.Completed += (_, _) => outgoing.Visibility = Visibility.Collapsed;
    }

    private static DoubleAnimation Animate(UIElement target, DependencyProperty property, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation(from, to, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        target.BeginAnimation(property, animation);
        return animation;
    }
    private static double Offset(UIElement element) => element.RenderTransform is TranslateTransform translate ? translate.Y : 0;
    private static void SetOffset(UIElement element, double value) { if (element.RenderTransform is TranslateTransform translate) translate.Y = value; }
    private static void AnimateOffset(UIElement element, double from, double to, TimeSpan duration)
    {
        if (element.RenderTransform is not TranslateTransform translate) return;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, to, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
