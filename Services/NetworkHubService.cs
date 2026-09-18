using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VersaHUD.Controls;

namespace VersaHUD.Services;

public class NetworkHubService
{
    private readonly IBluetoothLE? _ble;
    private readonly IAdapter _adapter;
    private IDevice? _targetDevice;
    private ICharacteristic? _rxCharacteristic;
    private ICharacteristic? _txCharacteristic;

    private CancellationTokenSource? _rssiLoopCts;
    private CancellationTokenSource? _wifiTelemetryCts;
    private CancellationTokenSource? _passwordVerificationCts;

    private const string DeviceCacheKey = "LastConnectedBleId";

    private readonly Guid ServiceUuid = Guid.Parse("19B10000-E8F2-537E-4F1D-223A12345678");
    private readonly Guid RxCharUuid = Guid.Parse("19B10001-E8F2-537E-4F1D-223A12345678");
    private readonly Guid TxCharUuid = Guid.Parse("19B10002-E8F2-537E-4F1D-223A12345678");
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(1500),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(2500)
    };

    private int _WANFailureCount = 0;
    private bool _hasShownNoRadioAlert = false;
    private bool _isAppInForeground = true;
    private bool? _isWANReportedOnline = false;
    private static readonly SemaphoreSlim _wifiRadarLockoutMutedGate = new(1, 1);
    private static DateTime _lastWifiHandshakeTimestamp = DateTime.MinValue;
    private bool _bLECommunicationProvisioned = false;
    private bool _isTelemetryActive = false;
    private readonly Lock _rssiLock = new();
    public bool IsConnecting { get; private set; } = false;
    private const int TRANSPORT_FLAPPING_COOLDOWN_SECONDS = 30;
    private const int DEBOUNCE_COOLDOWN_MILLISECONDS = 3500;
    private const int MIN_PASS_RSSI_VALUE = -75;

    private CancellationTokenSource? _autoConnectLoopCts;
    private CancellationTokenSource? _reconnectLoopCts = null;

    public event Action<int> OnRssiUpdated;
    public event Action<string>? OnTelemetryReceived;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<bool>? OnAuthorizationRequestComplete;

    public System.Collections.ObjectModel.ObservableCollection<IDevice> DiscoveredDevices { get; } = [];
    public int ActiveRssi { get; set; } = -100;
    public bool IsRebootingWatchdogActive { get; set; } = false;
    public string CloudflareHost { get; set; } = Preferences.Default.Get("CloudflareHostKey", string.Empty);
    public string CloudflareClientId { get; set; } = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
    public string CloudflareClientSecret { get; set; } = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);
    public bool IsUsingWifiTransportMode { get; set; } = false;
    public bool IsUsingLocalApMode { get; set; } = false;
    public bool IsUsingCloudWanMode { get; set; } = false;
    public bool IsBluetoothConnected => CrossBluetoothLE.Current.IsOn && _targetDevice != null &&
                               _targetDevice.State == DeviceState.Connected;
    public bool? IsWANReportedOnline
    {
        get
        {
            if (_isWANReportedOnline == null)
            {
                if (Preferences.Default.ContainsKey("IsWANReportedOnline"))
                {
                    _isWANReportedOnline = Preferences.Default.Get<bool>("IsWANReportedOnline", false);
                }
                else
                {
                    _isWANReportedOnline = null;
                }
            }
            return _isWANReportedOnline;
        }
        set
        {
            _isWANReportedOnline = value;
            if (value.HasValue)
            {
                Preferences.Default.Set<bool>("IsWANReportedOnline", value.Value);
            }
            else
            {
                Preferences.Default.Remove("IsWANReportedOnline");
            }
        }
    }
    public bool IsWifiTelemetryDead { get; set; } = false;
    public bool IsPromptingForMasterPassword { get; set; } = false;
    public DateTime LastReportedWANLinkState { get; set; } = DateTime.MinValue;
    public bool IsAuthorized { get; set; } = false;
    public bool WaitingForAuthorization { get; set; } = false;
    public DateTime LastTransportSwitchTimestamp = DateTime.MinValue;
    public static bool HasPhysicalWifiConnection { get; private set; } = Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);
    public bool IsMonitorActive { get; private set; } = false;
    public int ReconnectCountdown { get; private set; } = 0;

    public NetworkHubService()
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;

        _adapter.DeviceDisconnected += async (s, e) =>
        {
            if (!(IsUsingWifiTransportMode || IsUsingLocalApMode))
                OnConnectionStateChanged?.Invoke(false);

            await App.Log("--> [BLE SIGNAL LOST]: Vehicle out of range. Initializing safe background failover watch...");
        };

        _adapter.DeviceDiscovered += async (s, args) =>
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (!string.IsNullOrEmpty(args.Device.Name) && !DiscoveredDevices.Any(d => d.Id == args.Device.Id))
                {
                    DiscoveredDevices.Add(args.Device);
                }
            });
        };

        Connectivity.Current.ConnectivityChanged += OnSystemWirelessHardwareStateChanged;
#if ANDROID
        global::VersaHUD.BootReceiver.OnBLEStateChange += BootReceiver_OnBLEStateChange;
#endif
        OnConnectionStateChanged?.Invoke(IsBluetoothConnected);
    }

    private void BootReceiver_OnBLEStateChange(bool bleOn)
    {
        if (bleOn)
        {
            App.NetworkService.IsUsingCloudWanMode = false;
            App.NetworkService.IsUsingWifiTransportMode = false;
            App.NetworkService.IsUsingLocalApMode = false;
            _reconnectLoopCts?.Cancel();
        }

        OnConnectionStateChanged?.Invoke(bleOn);

        if (!(IsMonitorActive || bleOn))
        {
            StartConnectionSupervisor();
        }
    }

    public async Task<bool> AutoConnectAsync()
    {
        if (IsConnecting)
            return false;
        
        IsConnecting = true;
                
        if (ReconnectCountdown > 0)
        {
            _reconnectLoopCts?.Cancel();
        }        

        var activeProfiles = Connectivity.Current.ConnectionProfiles;
        bool hasPhysicalWifiInterface = activeProfiles.Contains(ConnectionProfile.WiFi) &&
                                       (Connectivity.Current.NetworkAccess != NetworkAccess.None || Connectivity.Current.NetworkAccess == NetworkAccess.Unknown);
        bool phoneHasInternetAccess = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

        bool allRadiosOff = !(CrossBluetoothLE.Current.IsOn || hasPhysicalWifiInterface || phoneHasInternetAccess);

        if (allRadiosOff)
        {
            if (!_hasShownNoRadioAlert)
            {
                _hasShownNoRadioAlert = true;

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await MainPage.CurrentInstance.DisplayAlertAsync(
                        "Network Error",
                        "No active network interfaces detected. Please enable Bluetooth, Wi-Fi, or Cellular data to continue.",
                        "OK");
                });
            }

            IsUsingCloudWanMode = false;
            IsUsingLocalApMode = false;
            IsUsingWifiTransportMode = false;
            IsWifiTelemetryDead = false;
            IsConnecting = false;
            _passwordVerificationCts?.Cancel();

            while (WaitingForAuthorization)
            {
                await Task.Delay(500);
            }

            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
        else
        {
            _hasShownNoRadioAlert = false;
        }

        if (!(IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode || IsUsingCloudWanMode))
            OnConnectionStateChanged?.Invoke(false);

        var secondsSinceLastTransportSwitch = (DateTime.UtcNow - LastTransportSwitchTimestamp).TotalSeconds;

        try
        { 
            if (!IsBluetoothConnected && CrossBluetoothLE.Current.IsOn)
            {
                string cachedId = Preferences.Default.Get(DeviceCacheKey, string.Empty);

                if (!string.IsNullOrEmpty(cachedId))
                {
                    await App.Log($"--> [CACHE HIT]: Reconnecting straight to historical device: {cachedId}");
                    Guid deviceGuid = Guid.Parse(cachedId);

                    await Task.Delay(2500);
                    _targetDevice = await _adapter.ConnectToKnownDeviceAsync(deviceGuid);

                    if (IsBluetoothConnected)
                    {
                        LastTransportSwitchTimestamp = DateTime.MinValue;
                        IsUsingCloudWanMode = false;
                        await ProvisionBLECommunication();
                    }

                    _passwordVerificationCts?.Cancel();
                }
                else
                {
                    IsConnecting = false;
                    return false;
                }
            }
            else if (IsBluetoothConnected)
            {
                IsUsingCloudWanMode = false;

                if (!_bLECommunicationProvisioned)
                {
                    if (!(IsUsingWifiTransportMode || IsUsingLocalApMode))
                    {
                        if (!IsAuthorized)
                            WaitingForAuthorization = false;

                        await ProvisionBLECommunication();

                        if (hasPhysicalWifiInterface)
                            LastTransportSwitchTimestamp = DateTime.MinValue;
                    }
                    else if ((secondsSinceLastTransportSwitch >= TRANSPORT_FLAPPING_COOLDOWN_SECONDS &&
                        ActiveRssi >= MIN_PASS_RSSI_VALUE && (IsUsingWifiTransportMode || IsUsingLocalApMode)) || IsWifiTelemetryDead)
                    {
                        await App.Log("--> [AUTO-CONNECT]: BLE signal strength is acceptable. Using BLE transport.");

                        await ManageWifiTelemetryPollingLifecycle(false);
                        _txCharacteristic?.ValueUpdated -= NativeCharacteristic_ValueUpdated;
                        _txCharacteristic?.ValueUpdated += NativeCharacteristic_ValueUpdated;

                        IsUsingWifiTransportMode = false;
                        IsUsingLocalApMode = false;
                        _bLECommunicationProvisioned = true;

                        OnConnectionStateChanged?.Invoke(true);
                        LastTransportSwitchTimestamp = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [BLE RECOVERY TRACK CHOKE]: {ex.Message}");
        }
        
        if (hasPhysicalWifiInterface && ((IsBluetoothConnected && ActiveRssi <= MIN_PASS_RSSI_VALUE) || !IsBluetoothConnected))
        {
            string lastKnownIp = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
            if (!string.IsNullOrEmpty(lastKnownIp) && (lastKnownIp == "0.0.0.0" || lastKnownIp == "192.168.4.1" || lastKnownIp.Equals("STA_HOTSPOT")) && !IsUsingLocalApMode)
            {
                var currentSubnet = GetCurrentWifiSubnetBase();

                if (!string.IsNullOrEmpty(currentSubnet) && currentSubnet == "192.168.4.")
                {
                    lastKnownIp = "192.168.4.1";
                    IsUsingLocalApMode = true;
                    IsWifiTelemetryDead = false;
                    await App.Log("--> [AUTO-CONNECT]: Vehicle node is in hotspot mode. Attempting to connect over Wi-Fi transport...");
                }
            }

            if (!string.IsNullOrEmpty(lastKnownIp) && !lastKnownIp.Equals("0.0.0.0") && !lastKnownIp.Equals("STA_HOTSPOT") && !IsWifiTelemetryDead)
            {
                if (secondsSinceLastTransportSwitch >= TRANSPORT_FLAPPING_COOLDOWN_SECONDS)
                {
                    
                    var debounceResult = await VerifyWifiHealthWithDebounceAsync(lastKnownIp);
                    bool isWifiServerActive = ActiveRssi < MIN_PASS_RSSI_VALUE && debounceResult || !(IsBluetoothConnected && debounceResult);

                    if (isWifiServerActive)
                    {
                        if (!(IsUsingWifiTransportMode || IsUsingLocalApMode))
                        {
                            await App.Log("--> [AUTO-CONNECT]: Evaluating network transport route preference to Wifi route...");

                            if (IsUsingLocalApMode)
                                IsUsingWifiTransportMode = false;
                            else
                                IsUsingWifiTransportMode = true;

                            IsUsingCloudWanMode = false;
                            _txCharacteristic?.ValueUpdated -= NativeCharacteristic_ValueUpdated;
                            _bLECommunicationProvisioned = false;
                            LastTransportSwitchTimestamp = DateTime.UtcNow;
                            WaitingForAuthorization = false;

                            await App.Log("--> [FAILOVER SUCCESS]: Vehicle node discovered live over Wi-Fi Subnet. Engaging Wi-Fi transport channels!");

                            IsConnecting = false;
                            OnConnectionStateChanged?.Invoke(false);

                            if (IsAuthorized)
                            {
                                _ = ManageWifiTelemetryPollingLifecycle(true);
                            }

                            return true;
                        }
                        else if (IsAuthorized)
                        {
                            _ = ManageWifiTelemetryPollingLifecycle(true);
                        }
                    }
                    else if (!debounceResult)
                    {
                        IsUsingWifiTransportMode = false;
                        IsUsingLocalApMode = false;
                        await ManageWifiTelemetryPollingLifecycle(false);

                        OnConnectionStateChanged?.Invoke(IsBluetoothConnected);
                    }
                }
            }
        }
        else if (!hasPhysicalWifiInterface && (IsUsingWifiTransportMode || IsUsingLocalApMode))
        {
            IsUsingWifiTransportMode = false;
            IsUsingLocalApMode = false;
            await ManageWifiTelemetryPollingLifecycle(false);
        }

        if (!IsBluetoothConnected && IsWifiTelemetryDead && hasPhysicalWifiInterface && (IsUsingWifiTransportMode || IsUsingLocalApMode))
        {
            await App.Log("--> [AUTO-CONNECT]: No bluetooth available. Retaining WIFI connection regardless of empty telemetry.");
            IsWifiTelemetryDead = false;
        }
        else if (!(IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode || IsUsingCloudWanMode))
        {
            IsWifiTelemetryDead = false;
        }

        if (IsBluetoothConnected && !(IsUsingWifiTransportMode || IsUsingLocalApMode))
        {
            IsConnecting = false;
            return true;
        }

        var minutesSinceWANStatusReported = (DateTime.UtcNow - LastReportedWANLinkState).TotalMinutes;

        if (!(IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode || IsUsingCloudWanMode) && phoneHasInternetAccess &&
            ((IsWANReportedOnline ?? true) || minutesSinceWANStatusReported >= 20))
        {
            IsWANReportedOnline = await VerifyTrueInternetRouteToHostAsync();

            if (IsWANReportedOnline ?? true)
            {
                await App.Log("--> [AUTO-CONNECT SUCCESS]: Bluetooth and Wifi off, but Internet path to Cloudflare verified live. Activating Cloud WAN fallback...");

                _txCharacteristic?.ValueUpdated -= NativeCharacteristic_ValueUpdated;
                _bLECommunicationProvisioned = false;
                _isTelemetryActive = false;
                IsWifiTelemetryDead = false;
                IsUsingWifiTransportMode = false;
                IsUsingLocalApMode = false;
                IsUsingCloudWanMode = true;

                App.NetworkService.LastReportedWANLinkState = DateTime.UtcNow;

                await ManageWifiTelemetryPollingLifecycle(false);
                OnConnectionStateChanged?.Invoke(false);
            }
        }
        else if (IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode)
        {
            IsConnecting = false;
            return true;
        }

        if (IsUsingCloudWanMode)
        {
            if ((IsAuthorized || WaitingForAuthorization) && !IsWifiTelemetryDead)
            {
                await ManageCloudFlareTelemetryPollingLifecycle();
            }

            if (!IsWifiTelemetryDead)
            {
                IsConnecting = false;
                return true;
            }

            await Task.Delay(_isAppInForeground ? 10000 : 30000);
        }

        _passwordVerificationCts?.Cancel();

        IsUsingWifiTransportMode = false;
        IsUsingLocalApMode = false;
        IsUsingCloudWanMode = false;
        IsWANReportedOnline = false;
        IsWifiTelemetryDead = false;

        IsConnecting = false;
        OnConnectionStateChanged?.Invoke(false);

        return false;
    }

    public void StartConnectionSupervisor()
    {
        if (IsMonitorActive)
            return;

        IsMonitorActive = true;

        _reconnectLoopCts?.Cancel();
        _autoConnectLoopCts?.Cancel();
        _autoConnectLoopCts?.Dispose();
        _autoConnectLoopCts = new();

        var token = _autoConnectLoopCts.Token;

        _ = Task.Run(async () =>
        {            
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    if (!await AutoConnectAsync())
                    {
                        IsMonitorActive = false;

                        _reconnectLoopCts = new();
                        var reconnectToken = _reconnectLoopCts.Token;

                        await Task.Run(async () =>
                        {
                            for (int i = 30; i >= 0; i--)
                            {
                                ReconnectCountdown = i;

                                if (i > 0)
                                {
                                    await Task.Delay(1000);
                                }

                                if (reconnectToken.IsCancellationRequested || token.IsCancellationRequested)
                                {
                                    ReconnectCountdown = 0;
                                    OnConnectionStateChanged?.Invoke(IsBluetoothConnected);

                                    while (IsConnecting)
                                    {
                                        await Task.Delay(1000);
                                    }

                                    _reconnectLoopCts?.Dispose();
                                    _reconnectLoopCts = null;
                                    break;
                                }
                            }
                        }, _reconnectLoopCts.Token);
                    }
                    else if (!IsAuthorized && (IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode || IsUsingCloudWanMode))
                    {
                        if (IsWifiTelemetryDead && !IsBluetoothConnected)
                        {
                            continue;
                        }

                        await Task.Delay(1500);
                        await VerifyPasswordAgainstHardwareAsync();
                    }
                }
                catch (Exception ex)
                {
                    await App.Log($"Supervisor error: {ex.Message}");
                }
            }
        }, token);
    }

    public async Task VerifyPasswordAgainstHardwareAsync()
    {
        if (WaitingForAuthorization || IsPromptingForMasterPassword || IsAuthorized) return;

        if (!WaitingForAuthorization && _passwordVerificationCts != null)
            _passwordVerificationCts.Cancel();

        WaitingForAuthorization = true;
        OnConnectionStateChanged?.Invoke(IsBluetoothConnected);

        string savedPass = Preferences.Default.Get(InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        if (IsUsingCloudWanMode)
        {
            CloudflareHost = Preferences.Default.Get("CloudflareHostKey", string.Empty);
            CloudflareClientId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
            CloudflareClientSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

            if (string.IsNullOrEmpty(CloudflareHost) && !string.IsNullOrEmpty(CloudflareClientId) && !string.IsNullOrEmpty(CloudflareClientSecret))

            {
                IsWifiTelemetryDead = true;
                return;
            }

            await App.Log("--> [ROUTING]: Launching Cloudflare Zero-Trust WAN auth packet...");
            var wanTargetUrl = $"https://{CloudflareHost}/api/auth";
            using var wanRequestMessage = new HttpRequestMessage(HttpMethod.Post, wanTargetUrl);

            wanRequestMessage.Headers.Add("CF-Access-Client-Id", CloudflareClientId);
            wanRequestMessage.Headers.Add("CF-Access-Client-Secret", CloudflareClientSecret);

            var pwHash = GenerateFletcher16Hash(savedPass).ToString();
            wanRequestMessage.Content = new StringContent(pwHash, Encoding.UTF8, "text/plain");

            WaitingForAuthorization = true;
            OnConnectionStateChanged?.Invoke(false);
            HttpResponseMessage wanResponse = await _httpClient.SendAsync(wanRequestMessage);

            if (wanResponse.IsSuccessStatusCode)
            {
                IsAuthorized = true;
                await App.Log("--> [HANDSHAKE SECURED]: Auth validation state cleared successfully!");
            }
            else
            {
                await App.Log("--> [HANDSHAKE REJECTED]: Auth failed token caught. Displaying single alert prompt...");
                IsAuthorized = false;
            }

            OnAuthorizationRequestComplete?.Invoke(!IsAuthorized);
            WaitingForAuthorization = false;
            OnConnectionStateChanged?.Invoke(false);

            return;
        }

        if (IsBluetoothConnected)
        {
            OnTelemetryReceived -= PasswordVerificationTelemetryHandler;
            OnTelemetryReceived += PasswordVerificationTelemetryHandler;

            _passwordVerificationCts ??= new();
            var token = _passwordVerificationCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                }

                _passwordVerificationCts = null;
                WaitingForAuthorization = false;
                OnConnectionStateChanged?.Invoke(true);
                OnTelemetryReceived -= PasswordVerificationTelemetryHandler;
            });
        }

        bool cmdResult = false;

        try
        {
            cmdResult = await SendSecureCommandAsync(savedPass, "VERIFYPASS");
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("--> [ADMIN]: Unable to send command.") && (IsUsingWifiTransportMode || IsUsingLocalApMode))
            {
                await App.Log("--> [ADMIN]: Failure verify master password over Wifi channels. Default to alternate communication routes.");
                WaitingForAuthorization = false;
                IsAuthorized = false;
                _passwordVerificationCts?.Cancel();
                OnAuthorizationRequestComplete?.Invoke(false);
                return;
            }

            throw;
        }

        if (cmdResult)
        {
            if (IsUsingWifiTransportMode || IsUsingLocalApMode)
            {
                IsAuthorized = true;
                WaitingForAuthorization = false;
                _passwordVerificationCts?.Cancel();

                OnConnectionStateChanged?.Invoke(false);

                await App.Log("--> [HANDSHAKE SECURED]: Auth validation state cleared successfully!");
                OnAuthorizationRequestComplete?.Invoke(false);
            }            
        }
        else if (IsUsingWifiTransportMode || IsUsingLocalApMode)
        {
            IsAuthorized = false;
            WaitingForAuthorization = false;
            _passwordVerificationCts?.Cancel();
            
            OnConnectionStateChanged?.Invoke(false);

            await App.Log("--> [HANDSHAKE REJECTED]: Auth failed token caught. Displaying single alert prompt...");
            OnAuthorizationRequestComplete?.Invoke(true);
        }
        else
        {
            _passwordVerificationCts?.Cancel();
        }
    }

    public async void PasswordVerificationTelemetryHandler(string fullTelemetryMessage)
    {
        if (string.IsNullOrEmpty(fullTelemetryMessage)) return;
        await App.Log($"--> [SINGLE-STREAM AUTH INTERCEPTOR]: {fullTelemetryMessage}");

        if (fullTelemetryMessage.Contains("AUTH_SUCCESS"))
        {
            IsAuthorized = true;

            await App.Log("--> [HANDSHAKE SECURED]: Auth validation state cleared successfully!");
            OnAuthorizationRequestComplete?.Invoke(false);

            if (_passwordVerificationCts != null)
            {
                try
                {
                    if (!_passwordVerificationCts.IsCancellationRequested)
                    {
                        _passwordVerificationCts.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    _passwordVerificationCts?.Dispose();
                    _passwordVerificationCts = null;
                }
            }
        }
        else if (fullTelemetryMessage.Contains("AUTH_FAILED") || fullTelemetryMessage.Contains("ROUTER_ERROR") || fullTelemetryMessage.Contains("401") || fullTelemetryMessage.Contains("Unauthorized"))
        {
            IsAuthorized = false;

            await App.Log("--> [HANDSHAKE REJECTED]: Auth failed token caught. Displaying single alert prompt...");
            OnAuthorizationRequestComplete?.Invoke(true);

            if (_passwordVerificationCts != null)
            {
                try
                {
                    if (!_passwordVerificationCts.IsCancellationRequested)
                    {
                        _passwordVerificationCts.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    _passwordVerificationCts?.Dispose();
                    _passwordVerificationCts = null;
                }
            }
        }
        else
        {
            string savedPass = Preferences.Default.Get(InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            await SendSecureCommandAsync(savedPass, "VERIFYPASS");
        }
    }

    public async Task StartDiscoveryScanAsync()
    {
        if (!(_ble?.IsOn ?? false) || _adapter.IsScanning) return;

        await MainThread.InvokeOnMainThreadAsync(() => DiscoveredDevices.Clear());
        await App.Log("--> [BLE FREQUENCY SCAN]: Running a fresh 6-second visual search track...");

        _adapter.ScanTimeout = 6000;
        await _adapter.StartScanningForDevicesAsync();
    }

    public async Task<bool> PairAndConnectDeviceAsync(IDevice selectedDevice)
    {
        try
        {
            if (_adapter.IsScanning) await _adapter.StopScanningForDevicesAsync();

            _targetDevice = selectedDevice;
            await App.Log($"--> [USER SELECTION PAIRING]: Connecting straight to node: {_targetDevice.Name}");

            await _adapter.ConnectToDeviceAsync(_targetDevice);

            Preferences.Default.Set(DeviceCacheKey, _targetDevice.Id.ToString());

            return true;
        }
        catch (Exception ex)
        {
            await App.Log($"--> [PAIRING CONNECTION REJECTED]: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendSecureCommandAsync(string passcode, string action)
    {
        await App.Log("Sending command: " + action);
        string formattedCommandBody = $"{passcode}:{action}";
        string encryptedBase64CommandString = await EncryptLocalPayloadAES128CBC(formattedCommandBody);

        if (IsBluetoothConnected && _rxCharacteristic != null && !(IsUsingWifiTransportMode || IsUsingLocalApMode))
        {
            try
            {
                await App.Log($"--> [ROUTING]: Commencing Bluetooth command action '{action}'...");
                byte[] txPayloadBytes = Encoding.UTF8.GetBytes(encryptedBase64CommandString);
                bool bleSuccess = !Convert.ToBoolean(await _rxCharacteristic.WriteAsync(txPayloadBytes));

                if (bleSuccess) return true;
                await App.Log("--> [FAILOVER]: BLE transmission failed. Falling over to network paths...");
            }
            catch (Exception bleEx)
            {
                await App.Log($"--> [BLE COMMAND FAULT]: {bleEx.Message}. Cascading smoothly to network layers...");
            }
        }

        if (action == "GETCFKEYS" && (IsUsingWifiTransportMode || IsUsingLocalApMode || IsUsingCloudWanMode))
            throw new Exception("--> [ADMIN]: Assuming transport switched during the 'GETCFKEYS' request which is only required by BLE connectivity."); ;

        if ((IsUsingWifiTransportMode || IsUsingLocalApMode) && !IsWifiTelemetryDead)
        {
            int postRetries = 0;
            int maxPostRetries = 5;

            string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

            if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP == "0.0.0.0" || cachedVehicleIP == "STA_HOTSPOT")
            {
                if (IsUsingLocalApMode)
                {
                    cachedVehicleIP = "192.168.4.1";
                }
                else
                {
                    await App.Log("--> Local AP mode node configured and no previous vehicle IP found.");
                    throw new Exception("--> [ADMIN]: Unable to send command.");
                }
            }

            while (postRetries < maxPostRetries && !IsWifiTelemetryDead)
            {
                try
                {
                    if (!string.IsNullOrEmpty(cachedVehicleIP) && !cachedVehicleIP.Contains("0.0.0.0"))
                    {
                        await App.Log($"--> [ROUTING]: Offloading command '{action}' over Local Wi-Fi API server...");
                        string targetUrl = $"http://{cachedVehicleIP}/api/command";
                        var stringContent = new StringContent(encryptedBase64CommandString, Encoding.UTF8, "text/plain");

                        HttpResponseMessage response = await _httpClient.PostAsync(targetUrl, stringContent);
                        if (response.IsSuccessStatusCode)
                        {
                            await App.Log($"--> [HYBRID LINK ROUTER]: Wi-Fi Command delivered successfully: {action}");
                            return true;
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            await App.Log($"--> [HYBRID LINK ROUTER]: Wi-Fi Command rejected due to authorization failure: {action}");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    postRetries++;
                    
                    if (postRetries == maxPostRetries)
                    {
                        await App.Log($"--> [HYBRID WARNING]: Wi-Fi transport lane faulted....");
                        throw new Exception($"--> [ADMIN]: Unable to send command. Message: {ex.Message}");
                    }

                    await Task.Delay(1000);
                }
            }
        }

        if (IsUsingCloudWanMode)
        {
            try
            {
                CloudflareHost = Preferences.Default.Get("CloudflareHostKey", string.Empty);
                CloudflareClientId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
                CloudflareClientSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

                if (string.IsNullOrEmpty(CloudflareHost) && !string.IsNullOrEmpty(CloudflareClientId) && !string.IsNullOrEmpty(CloudflareClientSecret))
                    return false;

                await App.Log("--> [ROUTING]: Launching Cloudflare Zero-Trust WAN packet...");
                var wanTargetUrl = $"https://{CloudflareHost}/api/command";
                using var wanRequestMessage = new HttpRequestMessage(HttpMethod.Post, wanTargetUrl);

                wanRequestMessage.Headers.Add("CF-Access-Client-Id", CloudflareClientId);
                wanRequestMessage.Headers.Add("CF-Access-Client-Secret", CloudflareClientSecret);
                wanRequestMessage.Content = new StringContent(encryptedBase64CommandString, Encoding.UTF8, "text/plain");

                HttpResponseMessage wanResponse = await _httpClient.SendAsync(wanRequestMessage);
                if (wanResponse.IsSuccessStatusCode)
                {
                    await App.Log($"--> [WAN SUCCESS]: Remote command executed cleanly via Cloudflare edge: {action}");
                    return true;
                }
                else
                {
                    await App.Log($"--> [WAN FAILURE]: {await wanResponse.Content.ReadAsStringAsync()}");
                }
            }
            catch (Exception wanEx)
            {
                await App.Log($"--> [WAN COMMAND FAULT]: Cloud pipeline unreachable: {wanEx.Message}");
            }
        }
        else
        {
            await App.Log($"--> [ADMIN]: Cloud WAN not available.");
        }

        throw new Exception("--> [ADMIN]: Unable to send command.");
    }

    public async Task DisconnectCurrentDeviceAsync()
    {
        try
        {
            if (_txCharacteristic != null)
            {
                try { await _txCharacteristic.StopUpdatesAsync(); } catch { }
            }

            if (_targetDevice != null && _adapter != null)
            {
                await App.Log($"--> [BLE HARDWARE TEARDOWN]: Breaking link to device: {_targetDevice.Name}");
                await _adapter.DisconnectDeviceAsync(_targetDevice);
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [BLE HW DISCONNECT FAULT]: {ex.Message}");
        }
        finally
        {
            OnConnectionStateChanged?.Invoke(false);
        }
    }

    public async Task ForceProactiveRebootRecoveryAsync()
    {
        IsRebootingWatchdogActive = true;

        int maxReconnectionAttempts = 10;
        int currentAttempt = 0;

        if (IsUsingWifiTransportMode || IsUsingLocalApMode)
        {
            string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

            if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP == "0.0.0.0")
            {
                if (IsUsingLocalApMode)
                {
                    cachedVehicleIP = "192.168.4.1";
                }
            }

            if (!string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP != "0.0.0.0")
            {
                await App.Log($"--> [WIFI WATCHDOG START]: Initializing rapid REST sweeps to http://{cachedVehicleIP}...");

                while (currentAttempt < maxReconnectionAttempts && IsRebootingWatchdogActive)
                {
                    currentAttempt++;
                    await App.Log($"--> [WIFI WATCHDOG]: Subnet inquiry pass #{currentAttempt} of {maxReconnectionAttempts}...");

                    try
                    {
                        if (!string.IsNullOrEmpty(cachedVehicleIP))
                        {
                            string targetUrl = $"http://{cachedVehicleIP}/api/status";

                            var watchdogTimer = Stopwatch.StartNew();
                            string jsonResultString = await _httpClient.GetStringAsync(targetUrl);
                            watchdogTimer.Stop();

                            if (watchdogTimer.ElapsedMilliseconds > 150)
                            {
                                await App.Log($"--> [WIFI WATCHDOG EJECT]: Stale network response ({watchdogTimer.ElapsedMilliseconds}ms). Aborting Wi-Fi recovery.");
                                break;
                            }

                            using JsonDocument jsonDoc = JsonDocument.Parse(jsonResultString);
                            if (jsonDoc.RootElement.TryGetProperty("status", out JsonElement statusProp) && statusProp.GetString() == "Ready")
                            {
                                await App.Log("--> [WIFI WATCHDOG SUCCESS]: Vehicle module network server verified stable.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await App.Log($"--> [WIFI WATCHDOG RETRY PASS]: Network endpoint still booting: {ex.Message}");
                        await Task.Delay(1000);
                    }
                }
            }

            await App.Log("--> [WIFI WATCHDOG FAULT]: Wi-Fi recovery degraded or exhausted. Dropping link to base BLE...");
        }

        currentAttempt = 0;
        string targetedMacAddress = Preferences.Default.Get("LastConnectedDeviceMac", string.Empty);
        await App.Log($"--> [BLE WATCHDOG START]: Initializing fallback radio link to address: {targetedMacAddress}...");

        await Task.Delay(1000);

        while (IsRebootingWatchdogActive)
        {
            await Task.Delay(1000);
        }

        //while (currentAttempt < maxReconnectionAttempts && IsRebootingWatchdogActive)
        //{
        //    currentAttempt++;
        //    await App.Log($"--> [BLE WATCHDOG]: Attempting hardware re-link #{currentAttempt} of {maxReconnectionAttempts} to: {targetedMacAddress}");
        //    try
        //    {
        //        if (await AutoConnectAsync(false))
        //        {
        //            IsRebootingWatchdogActive = false;
        //            await App.Log("--> [BLE WATCHDOG SUCCESS]: Radio pipeline synchronized cleanly!");
        //            return;
        //        }
        //        else throw new Exception("Connection attempt failed. Device still booting or unreachable.");
        //    }
        //    catch (Exception ex)
        //    {
        //        await App.Log($"--> [BLE WATCHDOG RETRY PASS]: Module still power-cycling: {ex.Message}");
        //        await Task.Delay(800);
        //    }
        //}

        IsRebootingWatchdogActive = false;
        await App.Log("--> [WATCHDOG CRITICAL FAILURE]: Both communication channels are exhausted.");
    }

    public static async Task<(string wifiAp, string wifiApPw, string bleName, string routerSsid, string cfHost, string cfId, string cfSecret, bool isOk)> FetchWifiAdminParametersAsync()
    {
        int maxRetries = 3;
        int currentTry = 0;

        while (currentTry < maxRetries)
        {
            currentTry++;
            try
            {
                string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

                if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP == "0.0.0.0" || cachedVehicleIP == "STA_HOTSPOT")
                {
                    if (App.NetworkService.IsUsingLocalApMode)
                    {
                        cachedVehicleIP = "192.168.4.1";
                    }
                }

                if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP == "0.0.0.0") return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                using var localWebClient = new HttpClient();
                localWebClient.Timeout = TimeSpan.FromMilliseconds(3000);

                var apiResponse = await localWebClient.GetAsync($"http://{cachedVehicleIP}/api/admin");

                if (apiResponse.IsSuccessStatusCode)
                {
                    string encryptedBase64Payload = await apiResponse.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(encryptedBase64Payload))
                        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                    string rawJsonProfileText = await DecryptLocalPayloadAES128CBC(encryptedBase64Payload);

                    if (string.IsNullOrEmpty(rawJsonProfileText))
                        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                    using JsonDocument jsonDoc = JsonDocument.Parse(rawJsonProfileText);
                    var root = jsonDoc.RootElement;

                    string wifiAp = root.TryGetProperty("wifi_ap", out JsonElement apNode) ? apNode.GetString() ?? "Error" : "Loading...";
                    string wifiApPw = root.TryGetProperty("wifi_ap_pw", out JsonElement apPwNode) ? apPwNode.GetString() ?? "Error" : "NONE";
                    string bleName = root.TryGetProperty("ble_name", out JsonElement bleNode) ? bleNode.GetString() ?? "Error" : "Loading...";
                    string routerSsid = root.TryGetProperty("router_ssid", out JsonElement ssidNode) ? ssidNode.GetString() ?? "NONE" : "NONE";
                    string cfHost = root.TryGetProperty("cf_host", out JsonElement hProp) ? hProp.GetString() ?? "Error" : "Loading...";
                    string cfId = root.TryGetProperty("cf_id", out JsonElement idProp) ? idProp.GetString() ?? "Error" : "Loading...";
                    string cfSecret = root.TryGetProperty("cf_secret", out JsonElement secretProp) ? secretProp.GetString() ?? "Error" : "NONE";

                    return (wifiAp, wifiApPw, bleName, routerSsid, cfHost, cfId, cfSecret, true);
                }
                else
                {
                    if (currentTry == maxRetries)
                    {
                        await App.Log($"--> [WIFI ADMIN FETCH ERROR]: Failed to retrieve administrative parameters from the vehicle node: {apiResponse.ReasonPhrase}");

                        MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await App.Current.MainPage.DisplayAlertAsync("Wi-Fi Admin Fetch Error", $"Failed to retrieve administrative parameters from the vehicle node: {apiResponse.ReasonPhrase}", "OK");
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (currentTry == maxRetries)
                {
                    await App.Log($"--> [WIFI ADMIN FETCH ERROR]: Failed to retrieve administrative parameters from the vehicle node: {ex.Message}");
                    MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await App.Current.MainPage.DisplayAlertAsync("Wi-Fi Admin Fetch Error", $"Failed to retrieve administrative parameters from the vehicle node: {ex.Message}", "OK");
                    });
                }
            }
        }

        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
    }

    public static async Task<(string wifiAp, string wifiApPw, string bleName, string routerSsid, string cfHost, string cfId, string cfSecret, bool isOk)> FetchCloudAdminParametersAsync()
    {
        int maxRetries = 3;
        int currentTry = 0;

        while (currentTry < maxRetries)
        {
            currentTry++;
            try
            {
                string cfHost = Preferences.Default.Get("CloudflareHostKey", "versahub.taigon1984.workers.dev");
                string cfId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
                string cfSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

                if (string.IsNullOrEmpty(cfHost) || string.IsNullOrEmpty(cfId) || string.IsNullOrEmpty(cfSecret))
                {
                    await App.Log("--> [WAN PROFILE ERROR]: Missing local Zero-Trust configuration passport keys.");
                    return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
                }

                using var cloudWebClient = new HttpClient();
                cloudWebClient.Timeout = TimeSpan.FromMilliseconds(4500);

                var targetRequestUrl = $"https://{cfHost}/api/admin";
                using var adminRequestMessage = new HttpRequestMessage(HttpMethod.Get, targetRequestUrl);

                adminRequestMessage.Headers.Add("cf-access-client-id", cfId);
                adminRequestMessage.Headers.Add("cf-access-client-secret", cfSecret);

                var apiResponse = await cloudWebClient.SendAsync(adminRequestMessage);

                if (apiResponse.IsSuccessStatusCode)
                {
                    string rawJsonProfileText = await apiResponse.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(rawJsonProfileText))
                        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                    using JsonDocument jsonDoc = JsonDocument.Parse(rawJsonProfileText);
                    var root = jsonDoc.RootElement;

                    string wifiAp = root.TryGetProperty("wifi_ap", out JsonElement apNode) ? apNode.GetString() ?? "Error" : "Loading...";
                    string wifiApPw = root.TryGetProperty("wifi_ap_pw", out JsonElement apPwNode) ? apPwNode.GetString() ?? "Error" : "NONE";
                    string bleName = root.TryGetProperty("ble_name", out JsonElement bleNode) ? bleNode.GetString() ?? "Error" : "Loading...";
                    string routerSsid = root.TryGetProperty("router_ssid", out JsonElement ssidNode) ? ssidNode.GetString() ?? "NONE" : "NONE";
                    string responseCfHost = root.TryGetProperty("cf_host", out JsonElement hProp) ? hProp.GetString() ?? "Error" : "Loading...";
                    string responseCfId = root.TryGetProperty("cf_id", out JsonElement idProp) ? idProp.GetString() ?? "Error" : "Loading...";

                    await App.Log("--> [WAN PROFILE SUCCESS]: Administrative parameter vectors synchronized over Cellular lanes!");
                    return (wifiAp, wifiApPw, bleName, routerSsid, responseCfHost, responseCfId, string.Empty, true);
                }
                else
                {
                    if (currentTry == maxRetries)
                    {
                        await App.Log($"--> [WAN ADMIN FETCH ERROR]: Failed to retrieve administrative parameters from the WAN node: {apiResponse.ReasonPhrase}");

                        MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await App.Current.MainPage.DisplayAlertAsync("WAN Admin Fetch Error", $"Failed to retrieve administrative parameters from the WAN node: {apiResponse.ReasonPhrase}", "OK");
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (currentTry == maxRetries)
                {
                    await App.Log($"--> [WAN ADMIN FETCH ERROR]: Failed to retrieve administrative parameters from the WAN node: {ex.Message}");
                    MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await App.Current.MainPage.DisplayAlertAsync("WAN Admin Fetch Error", $"Failed to retrieve administrative parameters from the WAN node: {ex.Message}", "OK");
                    });
                }
            }
        }

        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
    }

    public async void UpdateLifecycleState(bool isForeground)
    {
        _isAppInForeground = isForeground;
        await App.Log($"--> [WAN WATCHDOG]: Foreground layout state changed: {_isAppInForeground}");
    }

    public async Task ManageCloudFlareTelemetryPollingLifecycle()
    {
        try
        {
            if (_isTelemetryActive)
                return;

            _isTelemetryActive = true;
            CloudflareHost = Preferences.Default.Get("CloudflareHostKey", string.Empty);
            CloudflareClientId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
            CloudflareClientSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

            if (string.IsNullOrEmpty(CloudflareHost) || string.IsNullOrEmpty(CloudflareClientId) || string.IsNullOrEmpty(CloudflareClientSecret))
            {
                _isTelemetryActive = false;
                IsWANReportedOnline = false;
                IsUsingCloudWanMode = false;
                return;
            }

            string activeKey = Preferences.Default.Get("VersaPasscodeKey", "VersaPasscode99");
            string encryptedBase64PasswordString = "";

            using (var aesEngine = System.Security.Cryptography.Aes.Create())
            {
                aesEngine.Key = App.SecretSharedKeyBytes;
                aesEngine.IV = App.InitializationVectorBytes;
                aesEngine.Mode = System.Security.Cryptography.CipherMode.CBC;
                aesEngine.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using var memoryStream = new MemoryStream();
                using (var cryptoStream = new System.Security.Cryptography.CryptoStream(memoryStream, aesEngine.CreateEncryptor(), System.Security.Cryptography.CryptoStreamMode.Write))
                {
                    byte[] plainTextBytes = Encoding.UTF8.GetBytes(activeKey);
                    cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                    cryptoStream.FlushFinalBlock();
                }
                encryptedBase64PasswordString = Convert.ToBase64String(memoryStream.ToArray());
            }

            var requestUrl = $"https://{CloudflareHost}/api/telemetry";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl);

            requestMessage.Headers.Add("cf-access-client-id", CloudflareClientId);
            requestMessage.Headers.Add("cf-access-client-secret", CloudflareClientSecret);

            requestMessage.Content = new StringContent(encryptedBase64PasswordString, Encoding.UTF8, "text/plain");

            var apiResponse = await _httpClient.SendAsync(requestMessage);
            if (apiResponse.IsSuccessStatusCode)
            {
                string rawJson = await apiResponse.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(rawJson))
                {
                    OnTelemetryReceived?.Invoke(rawJson);
                }
            }
            else _WANFailureCount++;

            if (_WANFailureCount > 3)
            {
                _WANFailureCount = 0;
                IsWifiTelemetryDead = true;
            }

            if (IsUsingCloudWanMode && !(IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode) && !IsWifiTelemetryDead)
            {
                await Task.Delay(_isAppInForeground ? 10000 : 30000);
            }
            else if (IsBluetoothConnected || IsUsingWifiTransportMode || IsUsingLocalApMode)
            {
                IsUsingCloudWanMode = false;
            }
            else if (IsWifiTelemetryDead)
            {
                await App.Log("--> WAN telemetry reported dead. Disabling WAN connection.");
                IsUsingCloudWanMode = false;
                IsWANReportedOnline = false;
                IsConnecting = false;
            }
        }
        catch (Exception ex)
        {
            if (!ex.Message.StartsWith("The request was canceled due to the configured HttpClient.Timeout"))
                IsUsingCloudWanMode = false;

            await App.Log($"--> [WAN ERROR]: Cloud telemetry sync dropped: {ex.Message}");
        }

        _isTelemetryActive = false;
    }

    public async Task ManageWifiTelemetryPollingLifecycle(bool startWorker)
    {
        if (_isTelemetryActive && startWorker)
            return;

        _wifiTelemetryCts?.Cancel();
        _wifiTelemetryCts = null;

        if (!startWorker)
        {
            _isTelemetryActive = false;
            await App.Log("--> [UI NETWORK ENGINE]: Stopping Wifi telemetry polling.");
            return;
        }

        _isTelemetryActive = true;
        _wifiTelemetryCts = new CancellationTokenSource();
        var executionPassToken = _wifiTelemetryCts.Token;
        var maxFailures = 5;
        var currentFailureCount = 0;

        await App.Log("--> [UI NETWORK ENGINE]: Wi-Fi/Cloud link active. Spawning localized high-speed background HTTP polling thread...");

        await Task.Run(async () =>
        {
            var localSocketHandler = new SocketsHttpHandler()
            {
                AllowAutoRedirect = true,
                UseCookies = false
            };

            using (var telemetryClient = new HttpClient(localSocketHandler))
            {
                telemetryClient.Timeout = TimeSpan.FromMilliseconds(2500);

                string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
                string encryptedBase64PayloadString = await EncryptLocalPayloadAES128CBC(activeKey);

                while (!executionPassToken.IsCancellationRequested)
                {
                    await App.Log("--> [WIFI POLLING] - Initiating telemetry poll");
                    string targetIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

                    if (string.IsNullOrEmpty(targetIP) || targetIP == "0.0.0.0" || targetIP == "STA_HOTSPOT")
                    {
                        if (IsUsingLocalApMode)
                        {
                            targetIP = "192.168.4.1";
                        }
                        else
                        {
                            await App.Log("--> [WIFI POLLING]: Local AP mode node configured and no previous vehicle IP found.");
                            await ManageWifiTelemetryPollingLifecycle(false);
                        }
                    }

                    try
                    {
                        var httpPasscodeContent = new StringContent(encryptedBase64PayloadString, Encoding.UTF8, "text/plain");

                        var networkResponse = await telemetryClient.PostAsync($"http://{targetIP}/api/telemetry", httpPasscodeContent, executionPassToken);

                        if (networkResponse.IsSuccessStatusCode)
                        {
                            string inboundNetworkString = await networkResponse.Content.ReadAsStringAsync(executionPassToken);

                            if (!string.IsNullOrEmpty(inboundNetworkString))
                            {
                                string cleanJsonDataPayload = inboundNetworkString.Trim();

                                if (!cleanJsonDataPayload.StartsWith('{'))
                                {
                                    cleanJsonDataPayload = await DecryptLocalPayloadAES128CBC(cleanJsonDataPayload);
                                }

                                if (!string.IsNullOrWhiteSpace(cleanJsonDataPayload))
                                {
                                    try
                                    {
                                        OnTelemetryReceived?.Invoke(cleanJsonDataPayload);
                                        await App.Log("Successfully parsed telemetry JSON frame from Wi-Fi transport.");
                                    }
                                    catch (Exception parseEx)
                                    {
                                        await App.Log($"--> [UI PARSER CHOKE]: String exception handled safely: {parseEx.Message}");
                                    }
                                }
                            }
                        }
                        else if (networkResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            await App.Log("--> [WIFI POLLING]: Telemetry poll rejected due to authorization failure.");
                            await ManageWifiTelemetryPollingLifecycle(false);
                        }
                    }
                    catch (TaskCanceledException cancelEx)
                    {
                        if (IsWifiTelemetryDead || !cancelEx.Message.Contains("The request was canceled due to the configured HttpClient.Timeout"))
                        {
                            await ManageWifiTelemetryPollingLifecycle(false);
                        }

                        if (!cancelEx.Message.Contains("The request was canceled due to the configured HttpClient.Timeout"))
                        {
                            currentFailureCount++;
                        }

                        _wifiTelemetryCts = new CancellationTokenSource();
                        executionPassToken = _wifiTelemetryCts.Token;

                        if (currentFailureCount > maxFailures)
                        {
                            await App.Log("--> [WIFI POLLING]: Max consecutive failures occurred on Wifi telemetry transport.");
                            IsWifiTelemetryDead = true;
                            await ManageWifiTelemetryPollingLifecycle(false);
                        }
                    }
                    catch (Exception loopEx)
                    {
                        await App.Log($"--> [UI POLLING ENGINE DROPOUT]: Sockets handled connection lag safely: {loopEx.Message}");
                    }

                    try { await Task.Delay(10000, executionPassToken); } catch (TaskCanceledException) { break; }
                }
            }

            await App.Log("--> [UI NETWORK ENGINE]: Background HTTP data polling task thread closed down cleanly.");

        }, executionPassToken);
    }

    public static async Task<string> DecryptLocalPayloadAES128CBC(string base64CipherText)
    {
        if (string.IsNullOrWhiteSpace(base64CipherText)) return string.Empty;

        try
        {
            string sanitizedBase64 = base64CipherText.Trim()
                                                      .Replace("\r", "")
                                                      .Replace("\n", "")
                                                      .Replace(" ", "");

            if (sanitizedBase64.Length % 4 != 0)
            {
                await App.Log($"--> [AES ERROR]: Bad Base64 length string caught: {sanitizedBase64.Length}");
                return string.Empty;
            }

            byte[] cipherTextBytes = Convert.FromBase64String(sanitizedBase64);
            string plaintextOutputResult = string.Empty;

            using (var aesEngine = System.Security.Cryptography.Aes.Create())
            {
                aesEngine.Key = App.SecretSharedKeyBytes;
                aesEngine.IV = App.InitializationVectorBytes;
                aesEngine.Mode = System.Security.Cryptography.CipherMode.CBC;
                aesEngine.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using var memoryStream = new MemoryStream(cipherTextBytes);
                using var cryptoDecryptor = aesEngine.CreateDecryptor();
                using var cryptoStream = new System.Security.Cryptography.CryptoStream(memoryStream, cryptoDecryptor, System.Security.Cryptography.CryptoStreamMode.Read);
                using var streamReader = new StreamReader(cryptoStream, Encoding.UTF8);
                plaintextOutputResult = streamReader.ReadToEnd();
            }

            await App.Log($"--> [AES DECRYPTION SUCCESS]: Decoded clean JSON frame.");
            return plaintextOutputResult;
        }
        catch (Exception cryptoEx)
        {
            await App.Log($"--> [🚨 AES CRITICAL CRASH]: Exception aborted the decryption tracking: {cryptoEx.Message}");
            return string.Empty;
        }
    }

    private static async Task<string> EncryptLocalPayloadAES128CBC(string plainInput)
    {
        if (string.IsNullOrEmpty(plainInput)) return string.Empty;

        try
        {
            using var aesEngine = System.Security.Cryptography.Aes.Create();
            aesEngine.Key = App.SecretSharedKeyBytes;
            aesEngine.IV = App.InitializationVectorBytes;
            aesEngine.Mode = System.Security.Cryptography.CipherMode.CBC;
            aesEngine.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

            using var memoryStream = new MemoryStream();
            using (var cryptoStream = new System.Security.Cryptography.CryptoStream(memoryStream, aesEngine.CreateEncryptor(), System.Security.Cryptography.CryptoStreamMode.Write))
            {
                byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainInput);
                cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                cryptoStream.FlushFinalBlock();
            }

            return Convert.ToBase64String(memoryStream.ToArray());
        }
        catch (Exception ex)
        {
            await App.Log($"--> [CRYPTO ENCRYPT ERROR]: Serialization failure: {ex.Message}");
            return string.Empty;
        }
    }

    public static uint GenerateFletcher16Hash(string data)
    {
        if (string.IsNullOrEmpty(data)) return 0;

        byte[] bytes = Encoding.ASCII.GetBytes(data);

        uint sum1 = 0;
        uint sum2 = 0;

        for (int i = 0; i < bytes.Length; i++)
        {
            sum1 = (sum1 + bytes[i]) % 255;
            sum2 = (sum2 + sum1) % 255;
        }

        return (sum2 << 8) | sum1;
    }

    private async Task<bool> VerifyTrueInternetRouteToHostAsync()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            await App.Log("--> [WAN RADAR]: OS reports zero underlying data interfaces active.");
            return false;
        }

        try
        {
            string cfHost = Preferences.Default.Get("CloudflareHostKey", CloudflareHost);
            string cfId = Preferences.Default.Get("CloudflareClientIdKey", CloudflareClientId);
            string cfSecret = Preferences.Default.Get("CloudflareClientSecretKey", CloudflareClientSecret);

            if (string.IsNullOrEmpty(cfHost))
            {
                Debug.WriteLine("--> [WAN RADAR]: Missing Cloudflare client configuration. No route to WAN host.");
                return false;
            }

            using var routeTimeoutToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));

            using var pingRequest = new HttpRequestMessage(HttpMethod.Head, $"https://{cfHost}/api/status");

            pingRequest.Headers.Add("cf-access-client-id", cfId);
            pingRequest.Headers.Add("cf-access-client-secret", cfSecret);

            var response = await _httpClient.SendAsync(pingRequest, routeTimeoutToken.Token);
            
            if (response.IsSuccessStatusCode)
                await App.Log($"--> [WAN RADAR]: Route check to {cfHost} returned status: {response.StatusCode} | Verified Live: {response.IsSuccessStatusCode}");

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            await App.Log($"--> [WAN RADAR CRITICAL]: Host route dead or blocked by firewall: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> VerifyWifiHealthWithDebounceAsync(string lastKnownIp)
    {
        var activeProfiles = Connectivity.Current.ConnectionProfiles;
        bool hasPhysicalWifiInterface = activeProfiles.Contains(ConnectionProfile.WiFi);

        if (!hasPhysicalWifiInterface || Connectivity.Current.NetworkAccess != NetworkAccess.Internet || Connectivity.Current.NetworkAccess != NetworkAccess.Local)
            return false;

        if (!await _wifiRadarLockoutMutedGate.WaitAsync(0)) return false;

        try
        {
            var timespanSinceLastCheck = DateTime.UtcNow - _lastWifiHandshakeTimestamp;
            if (timespanSinceLastCheck.TotalMilliseconds < DEBOUNCE_COOLDOWN_MILLISECONDS)
            {
                return true;
            }

            _lastWifiHandshakeTimestamp = DateTime.UtcNow;

            using var pingRadarClient = new HttpClient();
            pingRadarClient.Timeout = TimeSpan.FromMilliseconds(1500);

            var apiResponse = await pingRadarClient.GetAsync($"http://{lastKnownIp}/api/status");

            if (apiResponse.IsSuccessStatusCode)
            {
                await App.Log($"--> [LAN RADAR SUCCESS]: True Wi-Fi Route verified active to: {lastKnownIp}");
                IsUsingCloudWanMode = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [LAN RADAR FAIL]: Wi-Fi route dropping out or unreachable: {ex.Message}");
        }
        finally
        {
            _wifiRadarLockoutMutedGate.Release();
        }

        return false;
    }

    private async Task ProvisionBLECommunication()
    {
        if (_targetDevice == null) return;

        try
        {
            await App.Log("--> [TRANSPORT]: Verifying over-the-air channel bandwidth frames...");

            using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int negotiatedMtuSize = await _targetDevice.RequestMtuAsync(256).WaitAsync(timeoutSource.Token);
            await App.Log($"--> [TRANSPORT SUCCESS]: Channel MTU optimized cleanly to: {negotiatedMtuSize} bytes.");
        }
        catch (OperationCanceledException)
        {
            await App.Log("--> [TRANSPORT WARN]: MTU handshake response timed out. Falling back to OS auto-negotiated limits.");
            return;
        }
        catch (Exception ex)
        {
            await App.Log($"--> [TRANSPORT ERROR]: Optional MTU negotiation bypassed: {ex.Message}");
            return;
        }
        IService? targetService = null;

        try
        {
            await App.Log("--> [TRANSPORT]: Getting bluetooth service...");

            using (var serviceTimeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                targetService = await _targetDevice.GetServiceAsync(ServiceUuid).WaitAsync(serviceTimeoutSource.Token);
            }

            if (targetService == null)
            {
                await App.Log("--> [GATT EXCEPTION]: Service link failure:Target GATT service interface returned null reference profile.");
                await RecycleBluetoothAdapterStateAsync();
                OnConnectionStateChanged?.Invoke(false);
                return;
            }
            else
            {
                await App.Log("--> [TRANSPORT]: Service obtained!");
            }

            _rxCharacteristic = await targetService.GetCharacteristicAsync(RxCharUuid);
            _txCharacteristic = await targetService.GetCharacteristicAsync(TxCharUuid);

            await Task.Delay(1000);

            if (_txCharacteristic != null && !(IsUsingWifiTransportMode || IsUsingLocalApMode))
            {
                _txCharacteristic.ValueUpdated -= NativeCharacteristic_ValueUpdated;
                _txCharacteristic.ValueUpdated += NativeCharacteristic_ValueUpdated;

                try
                {
                    using (var serviceTimeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        await _txCharacteristic.StartUpdatesAsync().WaitAsync(serviceTimeoutSource.Token);
                    }

                    await App.Log("--> [BLE SUCCESS]: Live telemetry channels fully open and sanitized.");
                }
                catch (Exception ex)
                {
                    await App.Log($"--> [BLE FAILURE]: Unable to start transmission updates. Error: {ex.Message}");
                    await RecycleBluetoothAdapterStateAsync();
                    OnConnectionStateChanged?.Invoke(false);
                    return;
                }

                _bLECommunicationProvisioned = true;
                await StartRssiTracking();

                while (ActiveRssi == -100 && IsBluetoothConnected)
                {
                    await Task.Delay(1000);
                }
                
                await App.Log("--> [GATT SUCCESS]: Primary service bridge successfully discovered!");
                await App.Log($"--> [TRANSPORT SUCCESS]: Obtained target service with UUID {ServiceUuid}.");

                OnConnectionStateChanged?.Invoke(true);
            }
            return;
        }
        catch (OperationCanceledException)
        {
            await App.Log("--> [GATT CRITICAL TIMEOUT]: Service discovery hung and was aborted. recycling adapter state...");
        }
        catch (Exception ex)
        {
            await App.Log($"--> [GATT EXCEPTION]: Service link failure: {ex.Message}");
        }

        await RecycleBluetoothAdapterStateAsync();
        OnConnectionStateChanged?.Invoke(false);
    }

    public async Task StartRssiTracking()
    {
        StopRssiTracking();

        _rssiLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => PollRssiAsync(_rssiLoopCts.Token));
    }

    public void StopRssiTracking()
    {
        lock (_rssiLock)
        {
            if (_rssiLoopCts != null)
            {
                try
                {
                    if (!_rssiLoopCts.IsCancellationRequested)
                    {
                        _rssiLoopCts.Cancel();
                    }
                }
                finally
                {
                    _rssiLoopCts.Dispose();
                    _rssiLoopCts = null;
                }
            }
        }

        ActiveRssi = -100;
    }

    private static string GetCurrentWifiSubnetBase()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var connectivityManager = (Android.Net.ConnectivityManager)context.GetSystemService(Android.Content.Context.ConnectivityService);
            if (connectivityManager == null) return string.Empty;

            var activeNetwork = connectivityManager.ActiveNetwork;
            var linkProperties = connectivityManager.GetLinkProperties(activeNetwork);
            if (linkProperties == null) return string.Empty;

            foreach (var linkAddress in linkProperties.LinkAddresses)
            {
                if (linkAddress?.Address is Java.Net.Inet4Address inet4Address)
                {
                    string ipAddressString = inet4Address.HostAddress;
                    int prefixLength = linkAddress.PrefixLength;

                    return CalculateSubnetBase(ipAddressString, prefixLength);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [SUBNET DETECTOR ERROR]: {ex.Message}");
        }
#endif

        return string.Empty;
    }

    private static string CalculateSubnetBase(string ipAddress, int prefixLength)
    {
        try
        {
            var ipBytes = System.Net.IPAddress.Parse(ipAddress).GetAddressBytes();
            uint mask = ~(0xFFFFFFFF >> prefixLength);
            byte[] maskBytes = BitConverter.GetBytes(mask);

            if (BitConverter.IsLittleEndian)
                Array.Reverse(maskBytes);

            byte[] subnetBytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                subnetBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            }

            return $"{subnetBytes[0]}.{subnetBytes[1]}.{subnetBytes[2]}.";
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task PollRssiAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_targetDevice == null || !IsBluetoothConnected)
                {
                    continue;
                }

                try
                {
                    bool? success = false;
                    if (_targetDevice != null)
                    {
                        success = await _targetDevice.UpdateRssiAsync(cancellationToken);

                        if (success == true)
                        {
                            ActiveRssi = _targetDevice.Rssi;
                        }
                    }

                    if (success == false)
                    {
                        ActiveRssi = -100;
                    }
                }
                catch (Exception ex)
                {
                    await App.Log($"RSSI Update failed: {ex.Message}");
                    ActiveRssi = -100;
                }

                OnRssiUpdated(ActiveRssi);

                if (ActiveRssi == -100)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnSystemWirelessHardwareStateChanged(object sender, ConnectivityChangedEventArgs e)
    {
        await App.Log($"--> [HARDWARE RADAR]: Phone network state shift detected. Access: {e.NetworkAccess}");
        bool hasPhysicalWifiInterface = e.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

        if (hasPhysicalWifiInterface)
            IsWifiTelemetryDead = false;

        LastTransportSwitchTimestamp = DateTime.MinValue;

        OnConnectionStateChanged?.Invoke(IsBluetoothConnected);
    }

    private async void NativeCharacteristic_ValueUpdated(object? sender, Plugin.BLE.Abstractions.EventArgs.CharacteristicUpdatedEventArgs args)
    {
        try
        {
            if (args.Characteristic?.Value != null)
            {
                string rawString = Encoding.UTF8.GetString(args.Characteristic.Value);
                OnTelemetryReceived?.Invoke(rawString);
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [BLE VALUE READING CHOKE]: {ex.Message}");
        }
    }

    public async Task RecycleBluetoothAdapterStateAsync()
    {
        try
        {
            await App.Log("--> [BLE SUPERVISOR]: Critical timeout detected. Initiating adapter recycling sequence...");

            _txCharacteristic?.ValueUpdated -= NativeCharacteristic_ValueUpdated;

            if (_targetDevice != null && (_targetDevice.State == DeviceState.Connected || _targetDevice.State == DeviceState.Connecting))
            {
                await App.Log("--> [BLE SUPERVISOR]: Terminating active GATT socket handles...");

                using var disconnectToken = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await CrossBluetoothLE.Current.Adapter.DisconnectDeviceAsync(_targetDevice, disconnectToken.Token);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Graceful disconnect bypassed: {ex.Message}");
        }
        finally
        {
            _txCharacteristic = null;
            _rxCharacteristic = null;
            _targetDevice = null;

            await App.Log("--> [BLE SUPERVISOR]: Native GATT caches flushed. Radio rails settling...");
            await Task.Delay(1000);

            await App.Log("--> [BLE SUPERVISOR]: Adapter state recycled successfully. Ready for cold-start reconnect.");
        }
    }
}
