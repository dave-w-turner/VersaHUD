using Plugin.BLE;
using Plugin.BLE.Abstractions;
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

    private CancellationTokenSource? _wifiTelemetryCancelSource;
    
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

    private bool _isAppInForeground = true;
    private static readonly SemaphoreSlim _wifiRadarLockoutMutedGate = new(1, 1);
    private static DateTime _lastWifiHandshakeTimestamp = DateTime.MinValue;
    private bool _bLECommunicationProvisioned = false;
    private bool _isConnecting = false;
    private DateTime _lastTransportSwitchTimestamp = DateTime.MinValue;    
    private const int TRANSPORT_FLAPPING_COOLDOWN_SECONDS = 30;
    private const int DEBOUNCE_COOLDOWN_MILLISECONDS = 3500;
    private const int MIN_PASS_RSSI_VALUE = -80;

    public event Action<int> OnRssiUpdated;    
    public event Action<string>? OnTelemetryReceived;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<bool>? OnTransportModeChanged;

    public System.Collections.ObjectModel.ObservableCollection<IDevice> DiscoveredDevices { get; } = [];
    public int ActiveRssi { get; set; } = -100;
    public bool IsUsingWifiTransportMode { get; set; } = false;
    public bool IsRebootingWatchdogActive { get; set; } = false;
    public string CloudflareHost { get; set; } = Preferences.Default.Get("CloudflareHostKey", string.Empty);
    public string ClientId { get; set; } = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
    public string ClientSecret { get; set; } = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);
    public bool IsUsingCloudWanMode { get; set; } = false;

    public bool IsBluetoothConnected => CrossBluetoothLE.Current.IsOn && _targetDevice != null &&
                               _targetDevice.State == DeviceState.Connected;

    public bool? IsWANReportedOnline {  get; set; } = null;
    public DateTime LastReportedWANLinkState { get; set; } = DateTime.MinValue;

    public NetworkHubService()
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;

        _adapter.DeviceDisconnected += async (s, e) =>
        {
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

    public async Task<bool> AutoConnectAsync(bool wifiAdapterOff = false)
    {
        if (_isConnecting) return false;

        _isConnecting = true;

        var activeProfiles = Connectivity.Current.ConnectionProfiles;
        bool hasPhysicalWifiInterface = !wifiAdapterOff && activeProfiles.Contains(ConnectionProfile.WiFi) && (Connectivity.Current.NetworkAccess == NetworkAccess.Internet || Connectivity.Current.NetworkAccess == NetworkAccess.Local);
        bool phoneHasInternetAccess = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

        if (!(CrossBluetoothLE.Current.IsOn || hasPhysicalWifiInterface || phoneHasInternetAccess))
        {
            MainThread.BeginInvokeOnMainThread(() => { 
                MainPage.CurrentInstance.DisplayAlertAsync("Network Error", "No active network interfaces detected. Please enable Bluetooth, Wi-Fi, or Cellular data to continue.", "OK");
            });

            _isConnecting = false;
            return false;
        }

        try
        {
            if (!IsBluetoothConnected && CrossBluetoothLE.Current.IsOn)
            {
                string cachedId = Preferences.Default.Get(DeviceCacheKey, string.Empty);

                if (!string.IsNullOrEmpty(cachedId))
                {
                    Debug.WriteLine($"--> [CACHE HIT]: Reconnecting straight to historical device: {cachedId}");
                    Guid deviceGuid = Guid.Parse(cachedId);

                    _targetDevice = await _adapter.ConnectToKnownDeviceAsync(deviceGuid);

                    if (IsBluetoothConnected)
                    {
                        await ProvisionBLECommunication(true);

                        IsUsingCloudWanMode = false;
                        IsUsingWifiTransportMode = false;
                    }

                    _lastTransportSwitchTimestamp = DateTime.MinValue;
                }
            }
            else if (_targetDevice != null)
            {
                if (IsBluetoothConnected && !_bLECommunicationProvisioned)
                    await ProvisionBLECommunication(!IsBluetoothConnected);

                if ((_targetDevice != null && ActiveRssi >= MIN_PASS_RSSI_VALUE && IsUsingWifiTransportMode) || !hasPhysicalWifiInterface || IsUsingCloudWanMode)
                {
                    Debug.WriteLine("--> [AUTO-CONNECT]: BLE signal strength is acceptable. Using BLE transport.");
                    IsUsingWifiTransportMode = false;
                    IsUsingCloudWanMode = false;
                }
            }

            if (!IsUsingWifiTransportMode && hasPhysicalWifiInterface)
            {
                string lastKnownIp = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
                if (!string.IsNullOrEmpty(lastKnownIp) && !lastKnownIp.Equals("0.0.0.0") && !lastKnownIp.Equals("STA_HOTSPOT"))
                {
                    var secondsSinceLastTransportSwitch = (DateTime.UtcNow - _lastTransportSwitchTimestamp).TotalSeconds;

                    if (secondsSinceLastTransportSwitch >= TRANSPORT_FLAPPING_COOLDOWN_SECONDS)
                    {
                        Debug.WriteLine("--> [AUTO-CONNECT]: Evaluating network transport route preference to Wifi route...");

                        var debounceResult = await VerifyWifiHealthWithDebounceAsync(lastKnownIp);
                        bool isWifiServerActive = ActiveRssi < MIN_PASS_RSSI_VALUE && debounceResult || !(IsBluetoothConnected && debounceResult);

                        if (isWifiServerActive)
                        {
                            IsUsingWifiTransportMode = true;
                            IsUsingCloudWanMode = false;
                            _txCharacteristic?.ValueUpdated -= NativeCharacteristic_ValueUpdated;
                            _bLECommunicationProvisioned = false;
                            _lastTransportSwitchTimestamp = DateTime.UtcNow;

                            Debug.WriteLine("--> [FAILOVER SUCCESS]: Vehicle node discovered live over Wi-Fi Subnet. Engaging Wi-Fi transport channels!");

                            _ = ManageWifiTelemetryPollingLifecycle(true);

                            OnConnectionStateChanged?.Invoke(false);
                            _isConnecting = false;
                            return true;
                        }
                    }
                }
            }

            if (IsBluetoothConnected)
            {               
                _isConnecting = false;

                if (!hasPhysicalWifiInterface)
                    OnConnectionStateChanged?.Invoke(true);

                return true;
            }

            if (!hasPhysicalWifiInterface)
                IsUsingWifiTransportMode = false;

            Debug.WriteLine("--> [BLE HW WARNING]: Bluetooth hardware radio is completely powered OFF. Attempting rapid failover check to WAN fallback routes...");

            var minutesSinceWANStatusReported = (DateTime.UtcNow - LastReportedWANLinkState).TotalMinutes;

            if (!(IsBluetoothConnected || IsUsingWifiTransportMode) && ((IsWANReportedOnline ?? true) || minutesSinceWANStatusReported >= 20) && await VerifyTrueInternetRouteToHostAsync())
            {
                Debug.WriteLine("--> [AUTO-CONNECT SUCCESS]: Bluetooth off, but Internet path to Cloudflare verified live. Activating Cloud WAN fallback...");

                if (!IsUsingCloudWanMode)
                {
                    IsUsingWifiTransportMode = false;
                    IsUsingCloudWanMode = true;

                    _txCharacteristic?.ValueUpdated -= NativeCharacteristic_ValueUpdated;
                    _bLECommunicationProvisioned = false;

                    _ = ManageCloudFlareTelemetryPollingLifecycle();

                    OnConnectionStateChanged?.Invoke(false);
                }

                await Task.Delay(5000);
                _ = AutoConnectAsync();

                _isConnecting = false;
                return true;
            }
            else
            {
                if (!IsWANReportedOnline ?? false)
                    Debug.WriteLine("--> [AUTO-CONNECT FAULT]: WAN previously reported offline. Connection attempt skipped.");
                else
                    Debug.WriteLine("--> [AUTO-CONNECT FAULT]: Local radios dead and internet routing unverified. All WAN fallback loops suspended.");

                IsUsingWifiTransportMode = false;
                IsUsingCloudWanMode = false;
                _bLECommunicationProvisioned = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [BLE RECOVERY TRACK CHOKE]: {ex.Message}");
        }

        OnConnectionStateChanged?.Invoke(false);
        _isConnecting = false;
        await Task.Delay(5000);
        _ = AutoConnectAsync();
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

            await ProvisionBLECommunication(true);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [PAIRING CONNECTION REJECTED]: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendSecureCommandAsync(string passcode, string action)
    {
        Debug.WriteLine("Sending command: " + action);
        string formattedCommandBody = $"{passcode}:{action}";
        string encryptedBase64CommandString = EncryptLocalPayloadAES128CBC(formattedCommandBody);

        if (IsBluetoothConnected && _rxCharacteristic != null)
        {
            try
            {
                Debug.WriteLine("--> [ROUTING]: Commencing Bluetooth byte dispatch...");
                byte[] txPayloadBytes = Encoding.UTF8.GetBytes(formattedCommandBody);
                bool bleSuccess = !Convert.ToBoolean(await _rxCharacteristic.WriteAsync(txPayloadBytes));

                if (bleSuccess) return true;
                Debug.WriteLine("--> [FAILOVER]: BLE transmission failed. Falling over to network paths...");
            }
            catch (Exception bleEx)
            {
                Debug.WriteLine($"--> [BLE COMMAND FAULT]: {bleEx.Message}. Cascading smoothly to network layers...");
            }
        }
        
        if (IsUsingWifiTransportMode)
        {
            try
            {
                string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
                if (!string.IsNullOrEmpty(cachedVehicleIP) && !cachedVehicleIP.Contains("0.0.0.0"))
                {
                    Debug.WriteLine("--> [ROUTING]: Offloading command over Local Wi-Fi API server...");
                    string targetUrl = $"http://{cachedVehicleIP}/api/command";
                    var stringContent = new StringContent(encryptedBase64CommandString, Encoding.UTF8, "text/plain");

                    HttpResponseMessage response = await _httpClient.PostAsync(targetUrl, stringContent);
                    if (response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"--> [HYBRID LINK ROUTER]: Wi-Fi Command delivered successfully: {action}");
                        return true;
                    }
                }
            }
            catch (Exception wifiEx)
            {
                Debug.WriteLine($"--> [HYBRID WARNING]: Wi-Fi transport lane faulted: {wifiEx.Message}. Cascading to WAN if available...");
            }
        }

        if (App.NetworkService.IsUsingCloudWanMode)
        {
            try
            {
                CloudflareHost = Preferences.Default.Get("CloudflareHostKey", string.Empty);
                ClientId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
                ClientSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

                if (string.IsNullOrEmpty(CloudflareHost) && !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret))
                    return false;

                Debug.WriteLine("--> [ROUTING]: Launching Cloudflare Zero-Trust WAN packet...");
                var wanTargetUrl = $"https://{CloudflareHost}/api/command";
                using var wanRequestMessage = new HttpRequestMessage(HttpMethod.Post, wanTargetUrl);

                wanRequestMessage.Headers.Add("CF-Access-Client-Id", ClientId);
                wanRequestMessage.Headers.Add("CF-Access-Client-Secret", ClientSecret);
                wanRequestMessage.Content = new StringContent(encryptedBase64CommandString, Encoding.UTF8, "text/plain");

                HttpResponseMessage wanResponse = await _httpClient.SendAsync(wanRequestMessage);
                if (wanResponse.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"--> [WAN SUCCESS]: Remote command executed cleanly via Cloudflare edge: {action}");
                    return true;
                }
            }
            catch (Exception wanEx)
            {
                Debug.WriteLine($"--> [WAN COMMAND FAULT]: Cloud pipeline unreachable: {wanEx.Message}");
            }
        }
        else
        {

            Debug.WriteLine($"--> [ADMIN]: Cloud WAN not available.");
        }

        return false;
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
                if (await AutoConnectAsync())
                {
                    IsRebootingWatchdogActive = false;
                    Debug.WriteLine("--> [BLE WATCHDOG SUCCESS]: Radio pipeline synchronized cleanly!");
                    return;
                }
                else throw new Exception("Connection attempt failed. Device still booting or unreachable.");
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

    public static async Task<(string wifiAp, string bleName, string routerSsid, string cfHost, string cfId, bool isOk)> FetchWifiAdminParametersAsync()
    {
        try
        {
            string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
            if (string.IsNullOrEmpty(cachedVehicleIP) || cachedVehicleIP == "0.0.0.0") return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

            using var localWebClient = new HttpClient();
            localWebClient.Timeout = TimeSpan.FromMilliseconds(3000);

            var apiResponse = await localWebClient.GetAsync($"http://{cachedVehicleIP}/api/admin");

            if (apiResponse.IsSuccessStatusCode)
            {
                string encryptedBase64Payload = await apiResponse.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(encryptedBase64Payload))
                    return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                string rawJsonProfileText = DecryptLocalPayloadAES128CBC(encryptedBase64Payload);

                if (string.IsNullOrEmpty(rawJsonProfileText))
                    return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                using JsonDocument jsonDoc = JsonDocument.Parse(rawJsonProfileText);
                var root = jsonDoc.RootElement;

                string wifiAp = root.TryGetProperty("wifi_ap", out JsonElement apNode) ? apNode.GetString() ?? "Error" : "Loading...";
                string bleName = root.TryGetProperty("ble_name", out JsonElement bleNode) ? bleNode.GetString() ?? "Error" : "Loading...";
                string routerSsid = root.TryGetProperty("router_ssid", out JsonElement ssidNode) ? ssidNode.GetString() ?? "NONE" : "NONE";
                string cfHost = root.TryGetProperty("cf_host", out JsonElement hProp) ? hProp.GetString() ?? "Error" : "Loading...";
                string cfId = root.TryGetProperty("cf_id", out JsonElement idProp) ? idProp.GetString() ?? "Error" : "Loading...";

                return (wifiAp, bleName, routerSsid, cfHost, cfId, true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [API PROFILE EXCEPTION]: Fallback to scraping: {ex.Message}");
        }

        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
    }

    public static async Task<(string wifiAp, string bleName, string routerSsid, string cfHost, string cfId, bool isOk)> FetchCloudAdminParametersAsync()
    {
        try
        {
            string cfHost = Preferences.Default.Get("CloudflareHostKey", "versahub.taigon1984.workers.dev");
            string cfId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
            string cfSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

            if (string.IsNullOrEmpty(cfHost) || string.IsNullOrEmpty(cfId) || string.IsNullOrEmpty(cfSecret))
            {
                Debug.WriteLine("--> [WAN PROFILE ERROR]: Missing local Zero-Trust configuration passport keys.");
                return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
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
                    return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);

                using JsonDocument jsonDoc = JsonDocument.Parse(rawJsonProfileText);
                var root = jsonDoc.RootElement;

                string wifiAp = root.TryGetProperty("wifi_ap", out JsonElement apNode) ? apNode.GetString() ?? "Error" : "Loading...";
                string bleName = root.TryGetProperty("ble_name", out JsonElement bleNode) ? bleNode.GetString() ?? "Error" : "Loading...";
                string routerSsid = root.TryGetProperty("router_ssid", out JsonElement ssidNode) ? ssidNode.GetString() ?? "NONE" : "NONE";
                string responseCfHost = root.TryGetProperty("cf_host", out JsonElement hProp) ? hProp.GetString() ?? "Error" : "Loading...";
                string responseCfId = root.TryGetProperty("cf_id", out JsonElement idProp) ? idProp.GetString() ?? "Error" : "Loading...";

                Debug.WriteLine("--> [WAN PROFILE SUCCESS]: Administrative parameter vectors synchronized over Cellular lanes!");
                return (wifiAp, bleName, routerSsid, responseCfHost, responseCfId, true);
            }
            else
            {
                Debug.WriteLine($"--> [WAN PROFILE FAULT]: Edge proxy rejected request with status code: {apiResponse.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [WAN PROFILE CRITICAL EXCEPTION]: Sockets handled dropout safely: {ex.Message}");
        }

        return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false);
    }

    public void UpdateLifecycleState(bool isForeground)
    {
        _isAppInForeground = isForeground;
        Debug.WriteLine($"--> [WAN WATCHDOG]: Foreground layout state changed: {_isAppInForeground}");
    }

    public async Task ManageCloudFlareTelemetryPollingLifecycle()
    {
        try
        {
            CloudflareHost = Preferences.Default.Get("CloudflareHostKey", string.Empty);
            ClientId = Preferences.Default.Get("CloudflareClientIdKey", string.Empty);
            ClientSecret = Preferences.Default.Get("CloudflareClientSecretKey", string.Empty);

            if (string.IsNullOrEmpty(CloudflareHost) || string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
                return;

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

            requestMessage.Headers.Add("cf-access-client-id", ClientId);
            requestMessage.Headers.Add("cf-access-client-secret", ClientSecret);

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

            if (IsUsingCloudWanMode && !(IsBluetoothConnected || IsUsingWifiTransportMode))
            {
                await Task.Delay(_isAppInForeground ? 10000 : 30000);
                _ = ManageCloudFlareTelemetryPollingLifecycle();
            }
            else if (IsBluetoothConnected || IsUsingWifiTransportMode)
            {
                IsUsingCloudWanMode = false;
                OnConnectionStateChanged?.Invoke(IsBluetoothConnected);
            }
        }
        catch (Exception ex)
        {
            IsUsingCloudWanMode = false;
            OnConnectionStateChanged?.Invoke(IsBluetoothConnected);
            Debug.WriteLine($"--> [WAN ERROR]: Cloud telemetry sync dropped: {ex.Message}");
        }
    }

    public async Task ManageWifiTelemetryPollingLifecycle(bool startWorker)
    {
        _wifiTelemetryCancelSource?.Cancel();
        _wifiTelemetryCancelSource = null;

        if (!startWorker)
        {
            Debug.WriteLine("--> [UI NETWORK ENGINE]: Competing HTTP background task loops cleanly suspended.");
            return;
        }

        _wifiTelemetryCancelSource = new CancellationTokenSource();
        var executionPassToken = _wifiTelemetryCancelSource.Token;

        Debug.WriteLine("--> [UI NETWORK ENGINE]: Wi-Fi/Cloud link active. Spawning localized high-speed background HTTP polling thread...");

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
                string encryptedBase64PayloadString = EncryptLocalPayloadAES128CBC(activeKey);

                while (!executionPassToken.IsCancellationRequested)
                {
                    Debug.WriteLine("--> [WIFI POLLING] - Initiating telemetry poll");
                    string targetIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

                    if (string.IsNullOrEmpty(targetIP) || targetIP == "0.0.0.0" || targetIP == "STA_HOTSPOT")
                    {
                        try { await Task.Delay(2000, executionPassToken); } catch (TaskCanceledException) { break; }
                        continue;
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
                                    cleanJsonDataPayload = DecryptLocalPayloadAES128CBC(cleanJsonDataPayload);
                                }

                                if (!string.IsNullOrWhiteSpace(cleanJsonDataPayload))
                                {
                                    try
                                    {
                                        OnTelemetryReceived?.Invoke(cleanJsonDataPayload);
                                    }
                                    catch (Exception parseEx)
                                    {
                                        Debug.WriteLine($"--> [UI PARSER CHOKE]: String exception handled safely: {parseEx.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (TaskCanceledException cancelEx)
                    {
                        if (!cancelEx.Message.Contains("The request was canceled due to the configured HttpClient.Timeout"))
                            break;

                        _wifiTelemetryCancelSource = new CancellationTokenSource();
                        executionPassToken = _wifiTelemetryCancelSource.Token;
                    }
                    catch (Exception loopEx)
                    {
                        Debug.WriteLine($"--> [UI POLLING ENGINE DROPOUT]: Sockets handled connection lag safely: {loopEx.Message}");
                    }

                    if (_targetDevice != null && IsBluetoothConnected)
                    {
                        await _targetDevice.UpdateRssiAsync();

                        ActiveRssi = _targetDevice?.Rssi ?? -100;

                        if (ActiveRssi >= MIN_PASS_RSSI_VALUE)
                        {
                            Debug.WriteLine("--> [WIFI POLLING] - BLE signal strength restored. Suspending Wi-Fi polling and reverting to BLE transport.");
                            _ = ManageWifiTelemetryPollingLifecycle(false);
                            _ = AutoConnectAsync();
                        }
                    }

                    if (!IsUsingWifiTransportMode)
                    {
                        Debug.WriteLine("--> [WIFI POLLING] - Wifi transport mode not in use.");
                        _ = ManageWifiTelemetryPollingLifecycle(false);
                        _ = AutoConnectAsync();
                    }

                    try { await Task.Delay(1000, executionPassToken); } catch (TaskCanceledException) { break; }
                }
            }

            Debug.WriteLine("--> [UI NETWORK ENGINE]: Background HTTP data polling task thread closed down cleanly.");

        }, executionPassToken);
    }    

    public static string DecryptLocalPayloadAES128CBC(string base64CipherText)
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
                Debug.WriteLine($"--> [AES ERROR]: Bad Base64 length string caught: {sanitizedBase64.Length}");
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

            Debug.WriteLine($"--> [AES DECRYPTION SUCCESS]: Decoded clean JSON frame: {plaintextOutputResult}");
            return plaintextOutputResult;
        }
        catch (Exception cryptoEx)
        {
            Debug.WriteLine($"--> [🚨 AES CRITICAL CRASH]: Exception aborted the decryption tracking: {cryptoEx.Message}");
            return string.Empty;
        }
    }

    private static string EncryptLocalPayloadAES128CBC(string plainInput)
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
            Debug.WriteLine($"--> [CRYPTO ENCRYPT ERROR]: Serialization failure: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task<bool> VerifyTrueInternetRouteToHostAsync()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            Debug.WriteLine("--> [WAN RADAR]: OS reports zero underlying data interfaces active.");
            return false;
        }

        try
        {
            string cfHost = Preferences.Default.Get("CloudflareHostKey", CloudflareHost);
            string cfId = Preferences.Default.Get("CloudflareClientIdKey", "9b28e96698ee489c6a80c96c4e211317.access");
            string cfSecret = Preferences.Default.Get("CloudflareClientSecretKey", "cfast_UZxeMyGQK0vwF6H62qE9V0dot9DNG2qDGvmNAkTQ850111d6");

            if (string.IsNullOrEmpty(cfHost)) return false;

            using var routeTimeoutToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));

            using var pingRequest = new HttpRequestMessage(HttpMethod.Head, $"https://{cfHost}/api/status");

            pingRequest.Headers.Add("cf-access-client-id", cfId);
            pingRequest.Headers.Add("cf-access-client-secret", cfSecret);

            var response = await _httpClient.SendAsync(pingRequest, routeTimeoutToken.Token);

            bool isRouteLiveAndValid = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            Debug.WriteLine($"--> [WAN RADAR]: Route check to {cfHost} returned status: {response.StatusCode} | Verified Live: {isRouteLiveAndValid}");
            return isRouteLiveAndValid;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [WAN RADAR CRITICAL]: Host route dead or blocked by firewall: {ex.Message}");
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
                Debug.WriteLine($"--> [LAN RADAR SUCCESS]: True Wi-Fi Route verified active to: {lastKnownIp}");
                IsUsingCloudWanMode = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [LAN RADAR FAIL]: Wi-Fi route dropping out or unreachable: {ex.Message}");
        }
        finally
        {
            _wifiRadarLockoutMutedGate.Release();
        }

        return false;
    }

    private async Task ProvisionBLECommunication(bool newConnection)
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

        await _targetDevice.UpdateRssiAsync();
        ActiveRssi = _targetDevice.Rssi;
        OnRssiUpdated(ActiveRssi);

        if (_txCharacteristic != null && !IsUsingWifiTransportMode)
        {
            _txCharacteristic.ValueUpdated -= NativeCharacteristic_ValueUpdated;
            _txCharacteristic.ValueUpdated += NativeCharacteristic_ValueUpdated;
            
            await _txCharacteristic.StartUpdatesAsync();

            if (newConnection)
                OnConnectionStateChanged?.Invoke(true);
            Debug.WriteLine("--> [BLE SUCCESS]: Live telemetry channels fully open and sanitized.");
        }

        _bLECommunicationProvisioned = true;
    }

    private async void OnSystemWirelessHardwareStateChanged(object sender, ConnectivityChangedEventArgs e)
    {
        Debug.WriteLine($"--> [HARDWARE RADAR]: Phone network state shift detected. Access: {e.NetworkAccess}");
        bool hasPhysicalWifiInterface = e.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

        if (hasPhysicalWifiInterface)
            _lastTransportSwitchTimestamp = DateTime.MinValue;

        _ = AutoConnectAsync(!hasPhysicalWifiInterface);
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

            Task.Run(() =>
            {
                var activeProfiles = Connectivity.Current.ConnectionProfiles;
                bool hasPhysicalWifiInterface = activeProfiles.Contains(ConnectionProfile.WiFi);
                var elapsedNotificationSwitchSeconds = (DateTime.UtcNow - _lastTransportSwitchTimestamp).TotalSeconds;

                if (ActiveRssi < MIN_PASS_RSSI_VALUE && !IsUsingWifiTransportMode && hasPhysicalWifiInterface)
                {
                    if (elapsedNotificationSwitchSeconds >= TRANSPORT_FLAPPING_COOLDOWN_SECONDS)
                    {
                        _lastTransportSwitchTimestamp = DateTime.UtcNow;
                        Debug.WriteLine("--> [BLE SIGNAL WEAK]: RSSI below threshold. Attempting Wi-Fi transport mode failover...");
                        _ = AutoConnectAsync();
                    }
                }

                _targetDevice?.UpdateRssiAsync();
                ActiveRssi = _targetDevice?.Rssi ?? -100;
                OnRssiUpdated(ActiveRssi);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [BLE VALUE READING CHOKE]: {ex.Message}");
        }
    }
}
