namespace VersaHUD;

public partial class MainPage : ContentPage
{
    #region FrontBatteryFields
    public static readonly BindableProperty FrontVoltsTextLabelProperty =
        BindableProperty.Create(
            nameof(FrontVoltsTextLabel),
            typeof(string),
            typeof(MainPage),
            defaultValue: "0.00 V",
            defaultBindingMode: BindingMode.TwoWay);

    public string FrontVoltsTextLabel
    {
        get => (string)GetValue(FrontVoltsTextLabelProperty);
        set => SetValue(FrontVoltsTextLabelProperty, value);
    }

    public static readonly BindableProperty FrontVoltsTextLabelColorProperty =
    BindableProperty.Create(
        nameof(FrontVoltsTextLabelColorValue),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#10B981"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color FrontVoltsTextLabelColorValue
    {
        get => (Color)GetValue(FrontVoltsTextLabelColorProperty);
        set => SetValue(FrontVoltsTextLabelColorProperty, value);
    }

    public static readonly BindableProperty FrontPercentTextLabelProperty =
    BindableProperty.Create(
        nameof(FrontPercentTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "0%",
        defaultBindingMode: BindingMode.TwoWay);

    public string FrontPercentTextLabel
    {
        get => (string)GetValue(FrontPercentTextLabelProperty);
        set => SetValue(FrontPercentTextLabelProperty, value);
    }

    public static readonly BindableProperty ProgressFrontValueProperty =
    BindableProperty.Create(
        nameof(ProgressFrontValue),
        typeof(double),
        typeof(MainPage),
        defaultValue: (double)0,
        defaultBindingMode: BindingMode.TwoWay);

    public double ProgressFrontValue
    {
        get => (double)GetValue(ProgressFrontValueProperty);
        set => SetValue(ProgressFrontValueProperty, value);
    }

    public static readonly BindableProperty ProgressFrontColorValueProperty =
    BindableProperty.Create(
        nameof(ProgressFrontColorValue),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#10B981"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color ProgressFrontColorValue
    {
        get => (Color)GetValue(ProgressFrontColorValueProperty);
        set => SetValue(ProgressFrontColorValueProperty, value);
    }

    public static readonly BindableProperty FrontIconTextLabelProperty =
    BindableProperty.Create(
        nameof(FrontIconTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "❌",
        defaultBindingMode: BindingMode.TwoWay);

    public string FrontIconTextLabel
    {
        get => (string)GetValue(FrontIconTextLabelProperty);
        set => SetValue(FrontIconTextLabelProperty, value);
    }
    #endregion

    #region BackBatteryFields
    public static readonly BindableProperty BackVoltsTextLabelProperty =
    BindableProperty.Create(
        nameof(BackVoltsTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "0.00 V",
        defaultBindingMode: BindingMode.TwoWay);

    public string BackVoltsTextLabel
    {
        get => (string)GetValue(BackVoltsTextLabelProperty);
        set => SetValue(BackVoltsTextLabelProperty, value);
    }

    public static readonly BindableProperty BackVoltsTextLabelColorProperty =
    BindableProperty.Create(
        nameof(BackVoltsTextLabelColorValue),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#3B82F6"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color BackVoltsTextLabelColorValue
    {
        get => (Color)GetValue(BackVoltsTextLabelColorProperty);
        set => SetValue(BackVoltsTextLabelColorProperty, value);
    }

    public static readonly BindableProperty BackPercentTextLabelProperty =
    BindableProperty.Create(
        nameof(BackPercentTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "0%",
        defaultBindingMode: BindingMode.TwoWay);

    public string BackPercentTextLabel
    {
        get => (string)GetValue(BackPercentTextLabelProperty);
        set => SetValue(BackPercentTextLabelProperty, value);
    }

    public static readonly BindableProperty ProgressBackValueProperty =
    BindableProperty.Create(
        nameof(ProgressBackValue),
        typeof(double),
        typeof(MainPage),
        defaultValue: (double)0,
        defaultBindingMode: BindingMode.TwoWay);

    public double ProgressBackValue
    {
        get => (double)GetValue(ProgressBackValueProperty);
        set => SetValue(ProgressBackValueProperty, value);
    }

    public static readonly BindableProperty ProgressBackColorValueProperty =
    BindableProperty.Create(
        nameof(ProgressBackColorValue),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#3B82F6"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color ProgressBackColorValue
    {
        get => (Color)GetValue(ProgressBackColorValueProperty);
        set => SetValue(ProgressBackColorValueProperty, value);
    }

    public static readonly BindableProperty BackIconTextLabelProperty =
    BindableProperty.Create(
        nameof(BackIconTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "❌",
        defaultBindingMode: BindingMode.TwoWay);

    public string BackIconTextLabel
    {
        get => (string)GetValue(BackIconTextLabelProperty);
        set => SetValue(BackIconTextLabelProperty, value);
    }
    #endregion

    #region CrossChargingFields
    public static readonly BindableProperty CrossChargeStatusTextLabelProperty =
    BindableProperty.Create(
        nameof(CrossChargeStatusTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultBindingMode: BindingMode.TwoWay);

    public string CrossChargeStatusTextLabel
    {
        get => (string)GetValue(CrossChargeStatusTextLabelProperty);
        set => SetValue(CrossChargeStatusTextLabelProperty, value);
    }

    public static readonly BindableProperty CrossChargeStatusTextLabelColorProperty =
    BindableProperty.Create(
        nameof(CrossChargeStatusTextLabelColorValue),
        typeof(Color),
        typeof(MainPage),
        defaultBindingMode: BindingMode.TwoWay);

    public Color CrossChargeStatusTextLabelColorValue
    {
        get => (Color)GetValue(CrossChargeStatusTextLabelColorProperty);
        set => SetValue(CrossChargeStatusTextLabelColorProperty, value);
    }

    public static readonly BindableProperty CrossChargeStatusLayoutVisibleProperty =
    BindableProperty.Create(
        nameof(CrossChargeStatusLayoutVisible),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool CrossChargeStatusLayoutVisible
    {
        get => (bool)GetValue(CrossChargeStatusLayoutVisibleProperty);
        set => SetValue(CrossChargeStatusLayoutVisibleProperty, value);
    }
    #endregion

    #region HeaderFields
    public static readonly BindableProperty CloudWanTelemetryStatusTextLabelProperty =
        BindableProperty.Create(
            nameof(CloudWanTelemetryStatusTextLabel),
            typeof(string),
            typeof(MainPage),
            defaultValue: "☁️ CLOUD LINK CHECKING...",
            defaultBindingMode: BindingMode.TwoWay);

    public string CloudWanTelemetryStatusTextLabel
    {
        get => (string)GetValue(CloudWanTelemetryStatusTextLabelProperty);
        set => SetValue(CloudWanTelemetryStatusTextLabelProperty, value);
    }

    public static readonly BindableProperty CloudWanTelemetryStatusTextColorProperty =
    BindableProperty.Create(
        nameof(CloudWanTelemetryStatusTextColor),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#F59E0B"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color CloudWanTelemetryStatusTextColor
    {
        get => (Color)GetValue(CloudWanTelemetryStatusTextColorProperty);
        set => SetValue(CloudWanTelemetryStatusTextColorProperty, value);
    }

    #endregion

    #region NetworkStatusFields
    public static readonly BindableProperty BorderBluetoothStatusBackgroundColorProperty =
        BindableProperty.Create(
            nameof(BorderBluetoothStatusBackgroundColor),
            typeof(Color),
            typeof(MainPage),
            defaultValue: Color.Parse("#2B221A"),
            defaultBindingMode: BindingMode.TwoWay);

    public Color BorderBluetoothStatusBackgroundColor
    {
        get => (Color)GetValue(BorderBluetoothStatusBackgroundColorProperty);
        set => SetValue(BorderBluetoothStatusBackgroundColorProperty, value);
    }

    public static readonly BindableProperty BorderBluetoothStatusStrokeColorProperty =
        BindableProperty.Create(
            nameof(BorderBluetoothStatusStrokeColor),
            typeof(Color),
            typeof(MainPage),
            defaultValue: Color.Parse("#F59E0B"),
            defaultBindingMode: BindingMode.TwoWay);

    public Color BorderBluetoothStatusStrokeColor
    {
        get => (Color)GetValue(BorderBluetoothStatusStrokeColorProperty);
        set => SetValue(BorderBluetoothStatusStrokeColorProperty, value);
    }

    public static readonly BindableProperty DotTextLabelProperty =
        BindableProperty.Create(
            nameof(DotTextLabel),
            typeof(string),
            typeof(MainPage),
            defaultValue: "🟡",
            defaultBindingMode: BindingMode.TwoWay);

    public string DotTextLabel
    {
        get => (string)GetValue(DotTextLabelProperty);
        set => SetValue(DotTextLabelProperty, value);
    }

    public static readonly BindableProperty BluetoothStatusTextLabelProperty =
        BindableProperty.Create(
            nameof(BluetoothStatusTextLabel),
            typeof(string),
            typeof(MainPage),
            defaultValue: "SEARCHING FOR VEHICLE MODULE...",
            defaultBindingMode: BindingMode.TwoWay);

    public string BluetoothStatusTextLabel
    {
        get => (string)GetValue(BluetoothStatusTextLabelProperty);
        set => SetValue(BluetoothStatusTextLabelProperty, value);
    }

    public static readonly BindableProperty BluetoothStatusTextLabelColorProperty =
    BindableProperty.Create(
        nameof(BluetoothStatusTextLabelColor),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#F59E0B"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color BluetoothStatusTextLabelColor
    {
        get => (Color)GetValue(BluetoothStatusTextLabelColorProperty);
        set => SetValue(BluetoothStatusTextLabelColorProperty, value);
    }

    public static readonly BindableProperty BluetoothSignalTextLabelProperty =
    BindableProperty.Create(
        nameof(BluetoothSignalTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultBindingMode: BindingMode.TwoWay);

    public string BluetoothSignalTextLabel
    {
        get => (string)GetValue(BluetoothSignalTextLabelProperty);
        set => SetValue(BluetoothSignalTextLabelProperty, value);
    }

    public static readonly BindableProperty BluetoothSignalTextLabelColorProperty =
    BindableProperty.Create(
        nameof(BluetoothSignalTextLabelColor),
        typeof(Color),
        typeof(MainPage),
        defaultValue: Color.Parse("#8E8E93"),
        defaultBindingMode: BindingMode.TwoWay);

    public Color BluetoothSignalTextLabelColor
    {
        get => (Color)GetValue(BluetoothSignalTextLabelColorProperty);
        set => SetValue(BluetoothSignalTextLabelColorProperty, value);
    }

    public static readonly BindableProperty BluetoothSignalVisibleProperty =
    BindableProperty.Create(
        nameof(BluetoothSignalVisible),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool BluetoothSignalVisible
    {
        get => (bool)GetValue(BluetoothSignalVisibleProperty);
        set => SetValue(BluetoothSignalVisibleProperty, value);
    }

    public static readonly BindableProperty BorderNetworkStatusVisibleProperty =
        BindableProperty.Create(
            nameof(BorderNetworkStatusVisible),
            typeof(bool),
            typeof(MainPage),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public bool BorderNetworkStatusVisible
    {
        get => (bool)GetValue(BorderNetworkStatusVisibleProperty);
        set => SetValue(BorderNetworkStatusVisibleProperty, value);
    }

    public static readonly BindableProperty LayoutReconnectingVisibleProperty =
    BindableProperty.Create(
        nameof(LayoutReconnectingVisible),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool LayoutReconnectingVisible
    {
        get => (bool)GetValue(LayoutReconnectingVisibleProperty);
        set => SetValue(LayoutReconnectingVisibleProperty, value);
    }

    public static readonly BindableProperty ReconnectingTextLabelProperty =
    BindableProperty.Create(
        nameof(ReconnectingTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultBindingMode: BindingMode.TwoWay);

    public string ReconnectingTextLabel
    {
        get => (string)GetValue(ReconnectingTextLabelProperty);
        set => SetValue(ReconnectingTextLabelProperty, value);
    }

    public static readonly BindableProperty VehicleIPTextLabelProperty =
    BindableProperty.Create(
        nameof(VehicleIPTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "0.0.0.0",
        defaultBindingMode: BindingMode.TwoWay);

    public string VehicleIPTextLabel
    {
        get => (string)GetValue(VehicleIPTextLabelProperty);
        set => SetValue(VehicleIPTextLabelProperty, value);
    }

    public static readonly BindableProperty ActiveTransportChannelTextLabelProperty =
    BindableProperty.Create(
        nameof(ActiveTransportChannelTextLabel),
        typeof(string),
        typeof(MainPage),
        defaultValue: "INITIALIZING NETWORK POOLS...",
        defaultBindingMode: BindingMode.TwoWay);

    public string ActiveTransportChannelTextLabel
    {
        get => (string)GetValue(ActiveTransportChannelTextLabelProperty);
        set => SetValue(ActiveTransportChannelTextLabelProperty, value);
    }
    #endregion

    #region UserControlsFields
    public static readonly BindableProperty LayoutPasswordInitVisibleProperty =
        BindableProperty.Create(
            nameof(LayoutPasswordInitVisible),
            typeof(bool),
            typeof(MainPage),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public bool LayoutPasswordInitVisible
    {
        get => (bool)GetValue(LayoutPasswordInitVisibleProperty);
        set => SetValue(LayoutPasswordInitVisibleProperty, value);
    }

    public static readonly BindableProperty BluetoothDevicePickerVisibleProperty =
    BindableProperty.Create(
        nameof(BluetoothDevicePickerVisible),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool BluetoothDevicePickerVisible
    {
        get => (bool)GetValue(BluetoothDevicePickerVisibleProperty);
        set => SetValue(BluetoothDevicePickerVisibleProperty, value);
    }
    #endregion

    #region ButtonFields
    public static readonly BindableProperty ManualScanButtonVisibleProperty =
    BindableProperty.Create(
        nameof(ManualScanButtonVisible),
        typeof(bool),
        typeof(MainPage),
        defaultValue: true,
        defaultBindingMode: BindingMode.TwoWay);

    public bool ManualScanButtonVisible
    {
        get => (bool)GetValue(ManualScanButtonVisibleProperty);
        set => SetValue(ManualScanButtonVisibleProperty, value);
    }

    public static readonly BindableProperty AdminNavigationButtonEnabledProperty =
    BindableProperty.Create(
        nameof(AdminNavigationButtonEnabled),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool AdminNavigationButtonEnabled
    {
        get => (bool)GetValue(AdminNavigationButtonEnabledProperty);
        set => SetValue(AdminNavigationButtonEnabledProperty, value);
    }

    public static readonly BindableProperty LockButtonEnabledProperty =
    BindableProperty.Create(
        nameof(LockButtonEnabled),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool LockButtonEnabled
    {
        get => (bool)GetValue(LockButtonEnabledProperty);
        set => SetValue(LockButtonEnabledProperty, value);
    }

    public static readonly BindableProperty UnLockButtonEnabledProperty =
    BindableProperty.Create(
        nameof(UnLockButtonEnabled),
        typeof(bool),
        typeof(MainPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool UnLockButtonEnabled
    {
        get => (bool)GetValue(UnLockButtonEnabledProperty);
        set => SetValue(UnLockButtonEnabledProperty, value);
    }

    #endregion
}
