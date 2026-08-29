using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VersaHUD.Services;

public class NetworkHubService
{
    private readonly IBluetoothLE? _ble;
    private readonly IAdapter _adapter;
    private IDevice? _targetDevice;
    private ICharacteristic? _rxCharacteristic;
    private ICharacteristic? _txCharacteristic;

    private const string DeviceCacheKey = "LastConnectedBleId";

    private readonly Guid ServiceUuid = Guid.Parse("19B10000-E8F2-537E-4F1D-223A12345678");
    private readonly Guid RxCharUuid = Guid.Parse("19B10001-E8F2-537E-4F1D-223A12345678");
    private readonly Guid TxCharUuid = Guid.Parse("19B10002-E8F2-537E-4F1D-223A12345678");
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(800),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(1000)
    };

    public event Action<int> OnRssiUpdated;
    private CancellationTokenSource _rssiCancelSource;
    public event Action<string>? OnTelemetryReceived;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<bool>? OnTransportModeChanged;
    public System.Collections.ObjectModel.ObservableCollection<IDevice> DiscoveredDevices { get; } = [];
    public int ActiveRssi { get; set; } = -100;
    public bool IsUsingWifiTransportMode { get; set; } = false;
    public bool IsRebootingWatchdogActive { get; set; } = false;

    private int _successfulWifiPingsInARow = 0;
    private int _failedWifiPingsInARow = 0;
    private DateTime _lastWifiCheckTimestamp = DateTime.MinValue;
    private readonly SemaphoreSlim _networkLockGate = new(1, 1);

    public bool IsBluetoothConnected => _targetDevice != null &&
                                   _targetDevice.State == Plugin.BLE.Abstractions.DeviceState.Connected;

    public NetworkHubService()
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;

        _adapter.DeviceDisconnected += async (s, e) =>
        {
            _rssiCancelSource?.Cancel();
            OnConnectionStateChanged?.Invoke(false);
            Debug.WriteLine("--> [BLE SIGNAL LOST]: Vehicle out of range. Retrying setup...");
            await AutoConnectAsync();
        };

        _adapter.DeviceDiscovered += (s, args) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!string.IsNullOrEmpty(args.Device.Name) && !DiscoveredDevices.Any(d => d.Id == args.Device.Id))
                {
                    DiscoveredDevices.Add(args.Device);
                }
            });
        };

        Connectivity.Current.ConnectivityChanged += OnSystemWirelessHardwareStateChanged;
    }

    public async Task<bool> AutoConnectAsync()
    {
        if (IsRebootingWatchdogActive)
        {
            Debug.WriteLine("--> [AUTO-CONNECT]: Stuck reboot watchdog flag detected. Force-clearing recovery states to allow standard hardware handshakes!");
            IsRebootingWatchdogActive = false;
        }

        try
        {
            if (!(_ble?.IsOn ?? false))
            {
                Debug.WriteLine("--> [BLE HW WARNING]: Bluetooth hardware radio is completely powered OFF. Attempting rapid failover check to Wi-Fi fallback routes...");

                string lastKnownIp = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
                if (!string.IsNullOrEmpty(lastKnownIp) && !lastKnownIp.Equals("0.0.0.0") && !lastKnownIp.Equals("STA_HOTSPOT"))
                {
                    bool isWifiServerActive = await VerifyWifiHealthWithDebounceAsync(lastKnownIp);

                    if (isWifiServerActive)
                    {
                        IsUsingWifiTransportMode = true;
                        Debug.WriteLine("--> [FAILOVER SUCCESS]: Vehicle node discovered live over Wi-Fi Subnet while BLE radio is off. Engaging Wi-Fi transport channels!");

                        OnConnectionStateChanged?.Invoke(false);
                        return true;
                    }
                }

                OnConnectionStateChanged?.Invoke(false);
                return false;
            }

            string cachedId = Preferences.Default.Get(DeviceCacheKey, string.Empty);

            if (!string.IsNullOrEmpty(cachedId))
            {
                Debug.WriteLine($"--> [CACHE HIT]: Reconnecting straight to historical device: {cachedId}");
                Guid deviceGuid = Guid.Parse(cachedId);

                _targetDevice = await _adapter.ConnectToKnownDeviceAsync(deviceGuid);
                if (_targetDevice != null)
                {
                    await ProvisionCommunicationPipesAsync();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [BLE RECOVERY TRACK CHOKE]: {ex.Message}");
        }

        Debug.WriteLine("--> [CACHE MISS]: Device profile unknown. Handing off processing control back to user UI selector.");
        return false;
    }

    public async Task StartDiscoveryScanAsync()
    {
        if (!(_ble?.IsOn ?? false) || _adapter.IsScanning) return;

        MainThread.BeginInvokeOnMainThread(() => DiscoveredDevices.Clear());
        Debug.WriteLine("--> [BLE FREQUENCY SCAN]: Running a fresh 6-second visual search track...");

        _adapter.ScanTimeout = 6000;
        await _adapter.StartScanningForDevicesAsync();
    }

    public async Task<bool> PairAndConnectDeviceAsync(IDevice selectedDevice)
    {
        try
        {
            if (_adapter.IsScanning) await _adapter.StopScanningForDevicesAsync();

            _targetDevice = selectedDevice;
            Debug.WriteLine($"--> [USER SELECTION PAIRING]: Connecting straight to node: {_targetDevice.Name}");

            await _adapter.ConnectToDeviceAsync(_targetDevice);

            Preferences.Default.Set(DeviceCacheKey, _targetDevice.Id.ToString());

            await ProvisionCommunicationPipesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [PAIRING CONNECTION REJECTED]: {ex.Message}");
            return false;
        }
    }

    private async Task ProvisionCommunicationPipesAsync()
    {
        if (_targetDevice == null) return;

        try
        {
#if ANDROID
            int negotiatedMtuSize = await _targetDevice.RequestMtuAsync(256);
            Debug.WriteLine($"--> [BLE HARDWARE METRIC]: MTU buffer window optimized cleanly to: {negotiatedMtuSize} bytes.");
#endif
        }
        catch (Exception mtuEx)
        {
            Debug.WriteLine($"--> [BLE HW WARNING]: MTU request bypassed or unsupported by handset: {mtuEx.Message}");
        }

        var targetService = await _targetDevice.GetServiceAsync(ServiceUuid);
        if (targetService == null) return;

        _rxCharacteristic = await targetService.GetCharacteristicAsync(RxCharUuid);
        _txCharacteristic = await targetService.GetCharacteristicAsync(TxCharUuid);

        if (_txCharacteristic != null)
        {
            _txCharacteristic.ValueUpdated -= NativeCharacteristic_ValueUpdated;
            _txCharacteristic.ValueUpdated += NativeCharacteristic_ValueUpdated;

            await _txCharacteristic.StartUpdatesAsync();
            OnConnectionStateChanged?.Invoke(true);
            Debug.WriteLine("--> [BLE SUCCESS]: Live telemetry channels fully open and sanitized.");
        }

        _rssiCancelSource?.Cancel();
        _rssiCancelSource = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            int consecutiveFailureCount = 0;

            while (!_rssiCancelSource.Token.IsCancellationRequested)
            {
                // Verify our active hardware state contract cleanly on every iteration pass
                bool isDevicePhysicallyConnected = _targetDevice != null &&
                    _targetDevice.State == Plugin.BLE.Abstractions.DeviceState.Connected;

                if (!isDevicePhysicallyConnected)
                {
                    Debug.WriteLine("--> [WATCHDOG CRITICAL]: Device link disconnected natively. Tripping failover tracking circuits...");
                    break; // 💥 Break out of the loop cleanly to trigger the offline recovery sequence below!
                }

                try
                {
                    // Maintain your active signal strength metrics for your front-end signal bars
                    await _targetDevice.UpdateRssiAsync();
                    ActiveRssi = _targetDevice.Rssi;
                    OnRssiUpdated?.Invoke(ActiveRssi);
                    consecutiveFailureCount = 0; // Clear failure tracking history on success

                    string lastKnownIp = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
                    bool isWifiNetworkServerGenuinelyAlive = false;

                    // STEP 1: DYNAMIC ASYNC HEALTH INQUIRY
                    if (!lastKnownIp.Equals("0.0.0.0") && !lastKnownIp.Equals("STA_HOTSPOT"))
                    {
                        isWifiNetworkServerGenuinelyAlive = await VerifyWifiHealthWithDebounceAsync(lastKnownIp);
                    }

                    // STEP 2: THE PRIORITIZED AFFINITY ROUTING DECISION LAYER
                    if (isWifiNetworkServerGenuinelyAlive)
                    {
                        if (!IsUsingWifiTransportMode)
                        {
                            IsUsingWifiTransportMode = true;
                            Debug.WriteLine($"--> [NETWORK SELECTION]: Local Wi-Fi network server detected active. Switching transport pathways to Wi-Fi!");

                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                OnTransportModeChanged?.Invoke(true);
                                OnConnectionStateChanged?.Invoke(false);
                            });
                        }
                    }
                    else
                    {
                        if (IsUsingWifiTransportMode)
                        {
                            IsUsingWifiTransportMode = false;
                            Debug.WriteLine("--> [NETWORK SELECTION]: Wi-Fi link silent or unavailable. Hard-locking transport channels back to stable BLE!");

                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                OnConnectionStateChanged?.Invoke(true);
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;
                    Debug.WriteLine($"--> [WATCHDOG EXCEPTION]: Hardware signal query pass failed ({consecutiveFailureCount}/3): {ex.Message}");

                    if (consecutiveFailureCount >= 3)
                    {
                        Debug.WriteLine("--> [WATCHDOG CRITICAL]: Link drops verified constant. Terminating stale radio loop thread...");
                        break;
                    }
                }

                await Task.Delay(3000);
            }

            Debug.WriteLine("--> [WATCHDOG TURN-OVER]: Radio loop thread exited. Cleaning workspace state preferences and initiating automated background reconnects...");

            IsUsingWifiTransportMode = false;
            ActiveRssi = 0;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                OnConnectionStateChanged?.Invoke(false);
            });

            await AutoConnectAsync();
        }, _rssiCancelSource.Token);
    }

    public async Task<bool> SendSecureCommandAsync(string passcode, string action)
    {
        Debug.WriteLine("Sending command: " + action);
        IsUsingWifiTransportMode = await EvaluateNetworkTransportRoutePreferenceAsync();

        string formattedCommandBody = $"{passcode}:{action}";

        if (IsUsingWifiTransportMode)
        {
            try
            {
                string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
                if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP.Contains("0.0.0.0")) return false;

                byte[] secretSharedKeyBytes = new byte[] { 0x5A, 0xA5, 0x1F, 0x2C, 0x7E, 0x9D, 0x8B, 0x34, 0x61, 0xF0, 0xE3, 0xD2, 0xC1, 0xB0, 0x09, 0x48 };
                byte[] initializationVectorBytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
                string encryptedBase64CommandString = "";

                using (var aesEngine = System.Security.Cryptography.Aes.Create())
                {
                    aesEngine.Key = secretSharedKeyBytes;
                    aesEngine.IV = initializationVectorBytes;
                    aesEngine.Mode = System.Security.Cryptography.CipherMode.CBC;
                    aesEngine.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                    using (var memoryStream = new System.IO.MemoryStream())
                    {
                        using (var cryptoStream = new System.Security.Cryptography.CryptoStream(memoryStream, aesEngine.CreateEncryptor(), System.Security.Cryptography.CryptoStreamMode.Write))
                        {
                            byte[] plainTextBytes = Encoding.UTF8.GetBytes(formattedCommandBody); // Encrypts "Passcode:Action"
                            cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                            cryptoStream.FlushFinalBlock();
                        }
                        encryptedBase64CommandString = Convert.ToBase64String(memoryStream.ToArray());
                    }
                }

                string targetUrl = $"http://{cachedVehicleIP}/api/command";
                var stringContent = new StringContent(encryptedBase64CommandString, Encoding.UTF8, "text/plain");

                HttpResponseMessage response = await _httpClient.PostAsync(targetUrl, stringContent);

                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"--> [HYBRID LINK ROUTER]: Payload-Encrypted Wi-Fi Command delivered successfully: {action}");
                    return true;
                }
            }
            catch (Exception wifiEx)
            {
                Debug.WriteLine($"--> [HYBRID WARNING]: Secure Wi-Fi transport lane faulted: {wifiEx.Message}. Dropping down to BLE backup layers...");
            }
        }

        if (_rxCharacteristic == null) return false;
        try
        {
            if (_targetDevice != null && _targetDevice.State == Plugin.BLE.Abstractions.DeviceState.Connected)
            {
                // BLE operates natively over short close-range radio waves, passing raw bytes securely
                byte[] txPayloadBytes = Encoding.UTF8.GetBytes(formattedCommandBody);
                return !Convert.ToBoolean(await _rxCharacteristic.WriteAsync(txPayloadBytes));
            }
        }
        catch (Exception) { return false; }

        return false;
    }

    private void NativeCharacteristic_ValueUpdated(object? sender, Plugin.BLE.Abstractions.EventArgs.CharacteristicUpdatedEventArgs args)
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
            Debug.WriteLine($"--> [BLE VALUE READING CHOKE]: {ex.Message}");
        }
    }

    public async Task DisconnectCurrentDeviceAsync()
    {
        try
        {
            _rssiCancelSource?.Cancel();

            if (_txCharacteristic != null)
            {
                try { await _txCharacteristic.StopUpdatesAsync(); } catch { }
            }

            if (_targetDevice != null && _adapter != null)
            {
                Debug.WriteLine($"--> [BLE HARDWARE TEARDOWN]: Breaking link to device: {_targetDevice.Name}");
                await _adapter.DisconnectDeviceAsync(_targetDevice);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [BLE HW DISCONNECT FAULT]: {ex.Message}");
        }
        finally
        {
            OnConnectionStateChanged?.Invoke(false);
        }
    }
    
    private async Task<bool> VerifyWifiHealthWithDebounceAsync(string vehicleIP)
    {
        if ((DateTime.UtcNow - _lastWifiCheckTimestamp).TotalSeconds < 3)
        {
            return IsUsingWifiTransportMode;
        }

        await _networkLockGate.WaitAsync();
        try
        {
            if ((DateTime.UtcNow - _lastWifiCheckTimestamp).TotalSeconds < 3)
            {
                return IsUsingWifiTransportMode;
            }

            _lastWifiCheckTimestamp = DateTime.UtcNow;
            string targetUrl = $"http://{vehicleIP}/api/status";

            using var connectionTimeoutToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
            var stopwatchTimer = Stopwatch.StartNew();
            var responseMsg = await _httpClient.GetAsync(targetUrl, connectionTimeoutToken.Token);
            stopwatchTimer.Stop();

            long calculatedPingMs = stopwatchTimer.ElapsedMilliseconds;
            Debug.WriteLine($"--> [NETWORK PING]: Handshake packet returned in {calculatedPingMs}ms.");

            if (responseMsg.IsSuccessStatusCode && calculatedPingMs <= 1000)
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(await responseMsg.Content.ReadAsStringAsync(connectionTimeoutToken.Token));
                if (jsonDoc.RootElement.TryGetProperty("status", out JsonElement statusProp) && statusProp.GetString() == "Ready")
                {
                    _successfulWifiPingsInARow++;
                    _failedWifiPingsInARow = 0;

                    if (_successfulWifiPingsInARow >= 3 && !IsUsingWifiTransportMode)
                    {
                        Debug.WriteLine("--> [HYSTERESIS SMOOTHIER]: Wi-Fi link verified STABLE across 3 frames. Authorizing handoff!");
                        return true;
                    }

                    return IsUsingWifiTransportMode;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [HYSTERESIS WARNING]: Packet frame dropped or timed out: {ex.Message}");
        }
        finally
        {
            _networkLockGate.Release();
        }

        _failedWifiPingsInARow++;
        _successfulWifiPingsInARow = 0;

        if (_failedWifiPingsInARow >= 2 && IsUsingWifiTransportMode)
        {
            Debug.WriteLine("--> [HYSTERESIS BREAK]: Wi-Fi link dropped 2 frames consecutively. Forcing Bluetooth fallback!");
            return false;
        }

        return IsUsingWifiTransportMode;
    }

    private async Task<bool> EvaluateNetworkTransportRoutePreferenceAsync()
    {
        if (_targetDevice != null && _targetDevice.State == Plugin.BLE.Abstractions.DeviceState.Connected)
        {
            Debug.WriteLine("--> [TRANSMITTER SYSTEM]: Direct BLE link is alive. Routing secure payload over local radio waves.");
            return false;
        }

        string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
        if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP.Contains("0.0.0.0") || cachedVehicleIP.Contains("STA_HOTSPOT"))
        {
            return false;
        }

        return await VerifyWifiHealthWithDebounceAsync(cachedVehicleIP);
    }

    public async Task ForceProactiveRebootRecoveryAsync()
    {
        IsRebootingWatchdogActive = true;

        int maxReconnectionAttempts = 10;
        int currentAttempt = 0;

        if (IsUsingWifiTransportMode)
        {
            string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
            Debug.WriteLine($"--> [WIFI WATCHDOG START]: Initializing rapid REST sweeps to http://{cachedVehicleIP}...");

            while (currentAttempt < maxReconnectionAttempts && IsRebootingWatchdogActive)
            {
                currentAttempt++;
                Debug.WriteLine($"--> [WIFI WATCHDOG]: Subnet inquiry pass #{currentAttempt} of {maxReconnectionAttempts}...");

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
                            Debug.WriteLine($"--> [WIFI WATCHDOG EJECT]: Stale network response ({watchdogTimer.ElapsedMilliseconds}ms). Aborting Wi-Fi recovery.");
                            break;
                        }

                        using JsonDocument jsonDoc = JsonDocument.Parse(jsonResultString);
                        if (jsonDoc.RootElement.TryGetProperty("status", out JsonElement statusProp) && statusProp.GetString() == "Ready")
                        {
                            Debug.WriteLine("--> [WIFI WATCHDOG SUCCESS]: Vehicle module network server verified stable.");
                            IsRebootingWatchdogActive = false;
                            OnConnectionStateChanged?.Invoke(true);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"--> [WIFI WATCHDOG RETRY PASS]: Network endpoint still booting: {ex.Message}");
                    await Task.Delay(1000);
                }
            }

            Debug.WriteLine("--> [WIFI WATCHDOG FAULT]: Wi-Fi recovery degraded or exhausted. Dropping link to base BLE...");
            IsUsingWifiTransportMode = false;
        }

        currentAttempt = 0;
        string targetedMacAddress = Preferences.Default.Get("LastConnectedDeviceMac", string.Empty);
        Debug.WriteLine($"--> [BLE WATCHDOG START]: Initializing fallback radio link to address: {targetedMacAddress}...");

        await Task.Delay(1000);

        while (currentAttempt < maxReconnectionAttempts && IsRebootingWatchdogActive)
        {
            currentAttempt++;
            Debug.WriteLine($"--> [BLE WATCHDOG]: Attempting hardware re-link #{currentAttempt} of {maxReconnectionAttempts} to: {targetedMacAddress}");
            try
            {
                if (_adapter != null && !string.IsNullOrEmpty(targetedMacAddress))
                {
                    Guid deviceGuid = Guid.Parse(targetedMacAddress);
                    var reconnectedDevice = await _adapter.ConnectToKnownDeviceAsync(deviceGuid);

                    if (reconnectedDevice != null)
                    {
                        _targetDevice = reconnectedDevice;
                        await ProvisionCommunicationPipesAsync();

                        IsRebootingWatchdogActive = false;
                        Debug.WriteLine("--> [BLE WATCHDOG SUCCESS]: Radio pipeline synchronized cleanly!");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"--> [BLE WATCHDOG RETRY PASS]: Module still power-cycling: {ex.Message}");
                await Task.Delay(800);
            }
        }

        IsRebootingWatchdogActive = false;
        Debug.WriteLine("--> [WATCHDOG CRITICAL FAILURE]: Both communication channels are exhausted.");
    }

    public async Task<(string wifiAp, string bleName, string routerSsid, string cfHost, string cfId, bool isOk)> FetchWifiAdminParametersAsync()
    {
        try
        {
            string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
            if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP == "0.0.0.0") return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

            using (var localWebClient = new HttpClient())
            {
                localWebClient.Timeout = TimeSpan.FromMilliseconds(3000);

                var apiResponse = await localWebClient.GetAsync($"http://{cachedVehicleIP}/api/admin");

                if (apiResponse.IsSuccessStatusCode)
                {
                    string rawJsonProfileText = await apiResponse.Content.ReadAsStringAsync();

                    using (JsonDocument jsonDoc = JsonDocument.Parse(rawJsonProfileText))
                    {
                        var root = jsonDoc.RootElement;

                        string wifiAp = root.TryGetProperty("wifi_ap", out JsonElement apNode) ? apNode.GetString() ?? "Error" : "Loading...";
                        string bleName = root.TryGetProperty("ble_name", out JsonElement bleNode) ? bleNode.GetString() ?? "Error" : "Loading...";
                        string routerSsid = root.TryGetProperty("router_ssid", out JsonElement ssidNode) ? ssidNode.GetString() ?? "NONE" : "NONE";
                        string cfHost = root.TryGetProperty("cf_host", out JsonElement hProp) ? hProp.GetString() ?? "Error" : "Loading...";
                        string cfId = root.TryGetProperty("cf_id", out JsonElement idProp) ? idProp.GetString() ?? "Error" : "Loading...";

                        return (wifiAp, bleName, routerSsid, cfHost, cfId, true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [API PROFILE EXCEPTION]: Fallback to scraping: {ex.Message}");
        }

        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
    }

    public void RaiseTelemetryReceived(string simulatedRawPacketText)
    {
        if (!string.IsNullOrEmpty(simulatedRawPacketText))
        {
            OnTelemetryReceived?.Invoke(simulatedRawPacketText);
        }
    }

    private async void OnSystemWirelessHardwareStateChanged(object sender, ConnectivityChangedEventArgs e)
    {
        Debug.WriteLine($"--> [HARDWARE RADAR]: Phone network state shift detected. Access: {e.NetworkAccess}");

        var activeProfileAccess = e.NetworkAccess;
        bool phoneHasActiveWifiRadioLink = activeProfileAccess == NetworkAccess.Internet &&
                                           e.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

        bool isBluetoothDisconnected = _targetDevice == null || _targetDevice.State != Plugin.BLE.Abstractions.DeviceState.Connected;

        if (!phoneHasActiveWifiRadioLink && isBluetoothDisconnected)
        {
            Debug.WriteLine("--> [HARDWARE CIRCUIT BREAKER]: Both radio antennas are confirmed severed. Forcing immediate offline turnover passes...");

            IsUsingWifiTransportMode = false;
            OnConnectionStateChanged?.Invoke(false);
            return;
        }

        if (phoneHasActiveWifiRadioLink && !IsUsingWifiTransportMode)
        {
            Debug.WriteLine("--> [HARDWARE RADAR WAKEUP]: Wi-Fi antenna initialized from an offline stop. Launching rapid asynchronous subnet probe sweeps...");

            string lastKnownIp = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
            if (!string.IsNullOrEmpty(lastKnownIp) && !lastKnownIp.Equals("0.0.0.0") && !lastKnownIp.Equals("STA_HOTSPOT"))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);

                        bool isVehicleServerLiveAndStable = await VerifyWifiHealthWithDebounceAsync(lastKnownIp);

                        if (isVehicleServerLiveAndStable)
                        {
                            IsUsingWifiTransportMode = true;
                            Debug.WriteLine("--> [WAKEUP SUCCESS]: Vehicle module server verified active over local Wi-Fi paths. Bringing your dashboard online!");

                            OnTransportModeChanged?.Invoke(true);
                        }
                    }
                    catch (Exception wakeupEx)
                    {
                        Debug.WriteLine($"--> [HARDWARE RADAR WAKEUP ERROR]: Subnet probe failed safely: {wakeupEx.Message}");
                    }
                });
            }
        }
        else if (!phoneHasActiveWifiRadioLink && IsUsingWifiTransportMode)
        {
            Debug.WriteLine("--> [HARDWARE FAILOVER]: Wi-Fi link lost while in Wi-Fi transport mode. Re-routing tracks to base BLE...");
            IsUsingWifiTransportMode = false;
            await AutoConnectAsync();
        }
    }

    public void RaiseConnectionStateChangedProxy(bool isDeviceLinkActive)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnConnectionStateChanged?.Invoke(isDeviceLinkActive);
        });
    }
}