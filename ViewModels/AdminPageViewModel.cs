using VersaHUD.Controls;

namespace VersaHUD;

public partial class AdminPage : ContentPage
{
    public static readonly BindableProperty MasterPasswordEntryTextProperty =
    BindableProperty.Create(
         nameof(MasterPasswordEntryText),
         typeof(string),
         typeof(AdminPage),
         defaultBindingMode: BindingMode.TwoWay);

    public string MasterPasswordEntryText
    {
        get => (string)GetValue(MasterPasswordEntryTextProperty);
        set => SetValue(MasterPasswordEntryTextProperty, value);
    }

    public static readonly BindableProperty APPasswordTextLabelProperty =
    BindableProperty.Create(
        nameof(APPasswordTextLabel),
        typeof(string),
        typeof(AdminPage),
        defaultValue: "CURRENT AP PASSWORD: NONE",
        defaultBindingMode: BindingMode.TwoWay);

    public string APPasswordTextLabel
    {
        get => (string)GetValue(APPasswordTextLabelProperty);
        set => SetValue(APPasswordTextLabelProperty, value);
    }

    public static readonly BindableProperty WifiAPEntryTextProperty =
    BindableProperty.Create(
         nameof(WifiAPEntryText),
         typeof(string),
         typeof(AdminPage),
         defaultValue: "Loading...",
         defaultBindingMode: BindingMode.TwoWay);

    public string WifiAPEntryText
    {
        get => (string)GetValue(WifiAPEntryTextProperty);
        set => SetValue(WifiAPEntryTextProperty, value);
    }

    public static readonly BindableProperty BluetoothNameTextProperty =
    BindableProperty.Create(
         nameof(BluetoothNameText),
         typeof(string),
         typeof(AdminPage),
         defaultValue: "Loading...",
         defaultBindingMode: BindingMode.TwoWay);

    public string BluetoothNameText
    {
        get => (string)GetValue(BluetoothNameTextProperty);
        set => SetValue(BluetoothNameTextProperty, value);
    }

    public static readonly BindableProperty RouterPasswordTextProperty =
    BindableProperty.Create(
         nameof(RouterPasswordText),
         typeof(string),
         typeof(AdminPage),
         defaultValue: "Loading...",
         defaultBindingMode: BindingMode.TwoWay);

    public string RouterPasswordText
    {
        get => (string)GetValue(RouterPasswordTextProperty);
        set => SetValue(RouterPasswordTextProperty, value);
    }

    public static readonly BindableProperty RouterPasswordEnabledProperty =
    BindableProperty.Create(
        nameof(RouterPasswordEnabled),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool RouterPasswordEnabled
    {
        get => (bool)GetValue(RouterPasswordEnabledProperty);
        set => SetValue(RouterPasswordEnabledProperty, value);
    }

    public static readonly BindableProperty LayoutUnconfiguredRouterVisibleProperty =
    BindableProperty.Create(
        nameof(LayoutUnconfiguredRouterVisible),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool LayoutUnconfiguredRouterVisible
    {
        get => (bool)GetValue(LayoutUnconfiguredRouterVisibleProperty);
        set => SetValue(LayoutUnconfiguredRouterVisibleProperty, value);
    }

    public static readonly BindableProperty LayoutConfiguredRouterVisibleProperty =
    BindableProperty.Create(
        nameof(LayoutConfiguredRouterVisible),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool LayoutConfiguredRouterVisible
    {
        get => (bool)GetValue(LayoutConfiguredRouterVisibleProperty);
        set => SetValue(LayoutConfiguredRouterVisibleProperty, value);
    }

    public static readonly BindableProperty RouterSSIDTextLabelProperty =
    BindableProperty.Create(
        nameof(RouterSSIDTextLabel),
        typeof(string),
        typeof(AdminPage),
        defaultValue: "Loading...",
        defaultBindingMode: BindingMode.TwoWay);

    public string RouterSSIDTextLabel
    {
        get => (string)GetValue(RouterSSIDTextLabelProperty);
        set => SetValue(RouterSSIDTextLabelProperty, value);
    }

    public static readonly BindableProperty RouterSSIDEntryTextProperty =
    BindableProperty.Create(
         nameof(RouterSSIDEntryText),
         typeof(string),
         typeof(AdminPage),
         defaultValue: "Loading...",
         defaultBindingMode: BindingMode.TwoWay);

    public string RouterSSIDEntryText
    {
        get => (string)GetValue(RouterSSIDEntryTextProperty);
        set => SetValue(RouterSSIDEntryTextProperty, value);
    }

    public static readonly BindableProperty RouterSSIDEnabledProperty =
    BindableProperty.Create(
        nameof(RouterPasswordEnabledProperty),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool RouterSSIDEnabled
    {
        get => (bool)GetValue(RouterSSIDEnabledProperty);
        set => SetValue(RouterSSIDEnabledProperty, value);
    }

    public static readonly BindableProperty CloudflareHostEntryTextProperty =
    BindableProperty.Create(
         nameof(CloudflareHostEntryText),
         typeof(string),
         typeof(AdminPage),
         defaultBindingMode: BindingMode.TwoWay);

    public string CloudflareHostEntryText
    {
        get => (string)GetValue(CloudflareHostEntryTextProperty);
        set => SetValue(CloudflareHostEntryTextProperty, value);
    }

    public static readonly BindableProperty CloudflareClientIDEntryTextProperty =
    BindableProperty.Create(
         nameof(CloudflareClientIDEntryText),
         typeof(string),
         typeof(AdminPage),
         defaultBindingMode: BindingMode.TwoWay);

    public string CloudflareClientIDEntryText
    {
        get => (string)GetValue(CloudflareClientIDEntryTextProperty);
        set => SetValue(CloudflareClientIDEntryTextProperty, value);
    }

    public static readonly BindableProperty CloudflareClientSecretEntryTextProperty =
    BindableProperty.Create(
         nameof(CloudflareClientSecretEntryText),
         typeof(string),
         typeof(AdminPage),
         defaultBindingMode: BindingMode.TwoWay);

    public string CloudflareClientSecretEntryText
    {
        get => (string)GetValue(CloudflareClientSecretEntryTextProperty);
        set => SetValue(CloudflareClientSecretEntryTextProperty, value);
    }

    public static readonly BindableProperty DebugTerminalTextLabelProperty =
    BindableProperty.Create(
        nameof(DebugTerminalTextLabel),
        typeof(string),
        typeof(AdminPage),
        defaultValue: "[SYS] Terminal initialized. Awaiting vehicle telemetry frames...",
        defaultBindingMode: BindingMode.TwoWay);

    public string DebugTerminalTextLabel
    {
        get => (string)GetValue(DebugTerminalTextLabelProperty);
        set => SetValue(DebugTerminalTextLabelProperty, value);
    }

    public static readonly BindableProperty SwitchAutoScrollToggledProperty =
    BindableProperty.Create(
        nameof(SwitchAutoScrollToggled),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: true,
        defaultBindingMode: BindingMode.TwoWay);

    public bool SwitchAutoScrollToggled
    {
        get => (bool)GetValue(SwitchAutoScrollToggledProperty);
        set => SetValue(SwitchAutoScrollToggledProperty, value);
    }

    public static readonly BindableProperty SwitchRemoteTelemetryToggledProperty =
    BindableProperty.Create(
        nameof(SwitchRemoteTelemetryToggled),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: true,
        defaultBindingMode: BindingMode.TwoWay);

    public bool SwitchRemoteTelemetryToggled
    {
        get => (bool)GetValue(SwitchRemoteTelemetryToggledProperty);
        set => SetValue(SwitchRemoteTelemetryToggledProperty, value);
    }

    public static readonly BindableProperty SwitchDebugLogsToggledProperty =
    BindableProperty.Create(
        nameof(SwitchDebugLogsToggled),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool SwitchDebugLogsToggled
    {
        get => (bool)GetValue(SwitchDebugLogsToggledProperty);
        set => SetValue(SwitchDebugLogsToggledProperty, value);
    }

    public static readonly BindableProperty RebootLockoutShellVisibleProperty =
    BindableProperty.Create(
        nameof(RebootLockoutShellVisible),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool RebootLockoutShellVisible
    {
        get => (bool)GetValue(RebootLockoutShellVisibleProperty);
        set => SetValue(RebootLockoutShellVisibleProperty, value);
    }

    public static readonly BindableProperty ButtonLinkToRouterEnabledProperty =
    BindableProperty.Create(
        nameof(ButtonLinkToRouterEnabled),
        typeof(bool),
        typeof(AdminPage),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay);

    public bool ButtonLinkToRouterEnabled
    {
        get => (bool)GetValue(ButtonLinkToRouterEnabledProperty);
        set => SetValue(ButtonLinkToRouterEnabledProperty, value);
    }
}
