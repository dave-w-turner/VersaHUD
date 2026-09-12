using Plugin.BLE.Abstractions.Contracts;

namespace VersaHUD.Controls;

public partial class BTDevicePicker : ContentView
{
    public static readonly BindableProperty BluetoothDeviceListItemsProperty =
    BindableProperty.Create(
        nameof(BluetoothDeviceListItems),
        typeof(System.Collections.ObjectModel.ObservableCollection<IDevice>),
        typeof(BTDevicePicker),
        defaultBindingMode: BindingMode.TwoWay);

    public System.Collections.ObjectModel.ObservableCollection<IDevice> BluetoothDeviceListItems
    {
        get => (System.Collections.ObjectModel.ObservableCollection<IDevice>)GetValue(BluetoothDeviceListItemsProperty);
        set => SetValue(BluetoothDeviceListItemsProperty, value);
    }

    public static readonly BindableProperty BluetoothDeviceListSelectedItemProperty =
    BindableProperty.Create(
        nameof(BluetoothDeviceListSelectedItem),
        typeof(object),
        typeof(BTDevicePicker),
        defaultBindingMode: BindingMode.TwoWay);

    public object? BluetoothDeviceListSelectedItem
    {
        get => GetValue(BluetoothDeviceListSelectedItemProperty);
        set => SetValue(BluetoothDeviceListSelectedItemProperty, value);
    }

    public static readonly BindableProperty IndicatorScannerRunningProperty =
    BindableProperty.Create(
        nameof(IndicatorScannerRunning),
        typeof(bool),
        typeof(BTDevicePicker),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool IndicatorScannerRunning
    {
        get => (bool)GetValue(IndicatorScannerRunningProperty);
        set => SetValue(IndicatorScannerRunningProperty, value);
    }

    public static readonly BindableProperty IndicatorScannerVisibleProperty =
    BindableProperty.Create(
        nameof(IndicatorScannerVisible),
        typeof(bool),
        typeof(BTDevicePicker),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool IndicatorScannerVisible
    {
        get => (bool)GetValue(IndicatorScannerVisibleProperty);
        set => SetValue(IndicatorScannerVisibleProperty, value);
    }
}
