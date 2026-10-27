using System.Windows;
using System.Windows.Controls;

namespace SubReel.Launcher.App.Views.Components;

public partial class MemoryAllocationControl : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MemoryAllocationControl), new FrameworkPropertyMetadata(
            4096d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty AutomationLabelProperty = DependencyProperty.Register(
        nameof(AutomationLabel), typeof(string), typeof(MemoryAllocationControl), new PropertyMetadata("Объём оперативной памяти, мегабайты"));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string AutomationLabel { get => (string)GetValue(AutomationLabelProperty); set => SetValue(AutomationLabelProperty, value); }

    public MemoryAllocationControl() => InitializeComponent();
}
