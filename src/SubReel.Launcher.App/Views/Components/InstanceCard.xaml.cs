using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SubReel.Launcher.App.Views.Components;

public partial class InstanceCard : UserControl
{
    public static readonly DependencyProperty FavoriteCommandProperty =
        DependencyProperty.Register(
            nameof(FavoriteCommand),
            typeof(ICommand),
            typeof(InstanceCard));

    public ICommand? FavoriteCommand
    {
        get => (ICommand?)GetValue(FavoriteCommandProperty);
        set => SetValue(FavoriteCommandProperty, value);
    }

    public InstanceCard()
    {
        InitializeComponent();
    }
}
