namespace VersaHUD.Controls;

public partial class MemoryIndicator : ContentView
{
    public static readonly BindableProperty RamStatusTextLabelProperty =
    BindableProperty.Create(
        nameof(RamStatusTextLabel),
        typeof(string),
        typeof(MemoryIndicator),
        defaultValue: "Waiting for Telemetry...",
        defaultBindingMode: BindingMode.TwoWay);

    public string RamStatusTextLabel
    {
        get => (string)GetValue(RamStatusTextLabelProperty);
        set => SetValue(RamStatusTextLabelProperty, value);
    }

    public static readonly BindableProperty RamIndicatorColorBrushProperty =
        BindableProperty.Create(
            nameof(RamIndicatorColorBrush),
            typeof(Color),
            typeof(MemoryIndicator),
            defaultValue: Colors.DarkGreen,
            defaultBindingMode: BindingMode.TwoWay);

    public Color RamIndicatorColorBrush
    {
        get => (Color)GetValue(RamIndicatorColorBrushProperty);
        set => SetValue(RamIndicatorColorBrushProperty, value);
    }
}