using System.ComponentModel;

namespace VersaHUD.Controls;

public partial class MemoryIndicator : ContentView, INotifyPropertyChanged
{
    public static MemoryIndicator CurrentInstance { get; private set; }

    public MemoryIndicator()
    {
        InitializeComponent();

        CurrentInstance = this;
    }

    private static int CalculateRamPercentage(int availableBytes)
    {
        if (availableBytes <= 0) return 0;

        if (availableBytes >= 32768) return 100;

        double calculatedFraction = (double)availableBytes / 32768.0;
        int percentage = (int)Math.Round(calculatedFraction * 100.0);

        return Math.Clamp(percentage, 0, 100);
    }

    public void UpdateMemoryHardwareGauge(int availableBytes)
    {
        if (BatteryFillBlock == null) return;

        int freeRamPercent = CalculateRamPercentage(availableBytes);

        if (freeRamPercent < 0) freeRamPercent = 0;
        if (freeRamPercent > 100) freeRamPercent = 100;

        double targetFillWidth = (freeRamPercent * 56.0) / 100.0;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (BatteryFillBlock == null) return;

            BatteryFillBlock.WidthRequest = targetFillWidth;

            RamStatusTextLabel = $"{freeRamPercent}% ({availableBytes} / 32768 Bytes Free)";

            if (freeRamPercent > 75)
                RamIndicatorColorBrush = Color.FromArgb("#107C41");
            else if (freeRamPercent > 45)
                RamIndicatorColorBrush = Color.FromArgb("#D83B01");
            else
                RamIndicatorColorBrush = Color.FromArgb("#A80000");
        });

        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }
}
