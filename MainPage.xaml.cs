using Plugin.BLE;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VersaHUD.Controls;
using VersaHUD.Services;

namespace VersaHUD;

public partial class MainPage : ContentPage
{
    private static readonly Regex FrontBatteryRegex = new(@"Front:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)", RegexOptions.Compiled);
    private static readonly Regex BackBatteryRegex = new(@"Back:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)", RegexOptions.Compiled);
    private static readonly Regex AvailableRamBytes = new(@"💾\s*\[.+?\]\s*\d+%\s*\((\d+)\s*B\)", RegexOptions.Compiled);
    private HashSet<string> _processedVehicleLogLinesBucket = [];

    public event Action<string>? OnTelemetryParsed;
    public static MainPage CurrentInstance { get; private set; }

    public const string SavedDeviceNameKey = "LastConnectedBleId";
    public const string SavedDeviceMacKey = "LastConnectedDeviceMac";

    public MainPage()
    {
        InitializeComponent();

        CurrentInstance = this;

        App.NetworkService.OnConnectionStateChanged += UpdateBluetoothStatusBadge;
        App.NetworkService.OnRssiUpdated += UpdateWirelessSignalBars;
        App.NetworkService.OnTelemetryReceived += ParseVehicleTelemetryStream;
        App.NetworkService.OnAuthorizationRequestComplete += HandleAuthorizationRequest;

        if (InitMasterPassword.CurrentInstance != null)
        {
            InitMasterPassword.CurrentInstance.OnPasswordInitialized += OnSetupFinished;
            InitMasterPassword.CurrentInstance.OnWrongDeviceRequested += OnRollbackConnectionAndRescan;
        }
    }

    private async void ParseVehicleTelemetryStream(string rawDataPacket)
    {
        if (rawDataPacket.Contains("[CF_ERR]"))
        {
            string currentSavedHost = Preferences.Default.Get("CloudflareHostKey", "silent-bird-d9c0.taigon1984.workers.dev");
            string currentSavedClientId = Preferences.Default.Get("CloudflareClientIdKey", "PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE");
            string currentSavedSecret = Preferences.Default.Get("CloudflareClientSecretKey", "PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE");

            if (string.IsNullOrEmpty(currentSavedHost) || currentSavedHost.Equals("silent-bird-d9c0.taigon1984.workers.dev") ||
                string.IsNullOrEmpty(currentSavedClientId) || currentSavedClientId.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE") ||
                string.IsNullOrEmpty(currentSavedSecret) || currentSavedSecret.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE"))
            {
                await App.Log("--> [UI FILTER]: Cloudflare exception caught, but credentials match factory defaults. Suppressing alert.");
                return;
            }

            string cleanErrorMessage = "Unknown Network Exception Caught over Airwaves.";
            if (rawDataPacket.Contains("AUTH_REJECTED"))
                cleanErrorMessage = "Cloudflare Zero-Trust Access denied the handshake.\n\nPlease verify that your 'Client ID' and 'Client Secret' match your Service Credentials exactly.";
            else if (rawDataPacket.Contains("WORKER_NOT_FOUND"))
                cleanErrorMessage = "The custom DNS Hostname could not be resolved.\n\nPlease verify your worker name endpoint URL string (e.g. silent-bird-...).";
            else if (rawDataPacket.Contains("DNS_UNREACHABLE"))
                cleanErrorMessage = "The microcontroller cannot connect to the server.\n\nVerify that the vehicle module has an active data/hotspot network connection.";

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                bool userClickedFix = await DisplayAlertAsync("🚨 CLOUDFLARE CONFIG ERROR",
                    $"Your vehicle module reported a WAN tunnel connection failure:\n\n{cleanErrorMessage}",
                    "FIX CONFIG", "CANCEL");

                if (userClickedFix)
                {
                    await App.Log("--> [UI INTENT ROUTER]: Driver requested configuration fix. Pushing AdminPage view...");
                    await Navigation.PushAsync(new AdminPage());
                }
            });
            return;
        }

        if (rawDataPacket.Contains("SECURITY WARN") || rawDataPacket.Contains("Hash mismatch") || rawDataPacket.Contains("401") || rawDataPacket.Contains("Unauthorized"))
        {
            await App.Log("--> [PARSER SECURITY RADAR]: Encryption key mismatch caught over radio waves! Enforcing passcode input overlay rendering pass...");
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (LayoutPasswordInitVisible)
                {
                    LayoutPasswordInitVisible = true;
                }
            });
            return;
        }

        try
        {
            if (rawDataPacket.Trim().StartsWith('{') && (App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingCloudWanMode))
            {
                using JsonDocument jsonDoc = JsonDocument.Parse(rawDataPacket);
                var root = jsonDoc.RootElement;

                float frontVolts = root.TryGetProperty("front_v", out JsonElement fv) ? (float)fv.GetDouble() : 0f;
                int frontPercent = root.TryGetProperty("front_p", out JsonElement fp) ? fp.GetInt32() : 0;
                float backVolts = root.TryGetProperty("background_v", out JsonElement bv) ? (float)bv.GetDouble() : 0f;
                int backPercent = root.TryGetProperty("back_p", out JsonElement bp) ? bp.GetInt32() : 0;

                bool frontIsCharging = root.TryGetProperty("charging_f", out JsonElement c) && c.GetBoolean();
                bool backIsCharging = root.TryGetProperty("charging_b", out JsonElement b) && b.GetBoolean();
                bool isCrossCharging = root.TryGetProperty("cross_charging", out JsonElement cc) && cc.GetBoolean();

                bool isArduinoCloudTunnelConnected = root.TryGetProperty("wan_link", out JsonElement wanNode) && wanNode.ValueKind != JsonValueKind.Null && wanNode.GetBoolean();

                if (root.TryGetProperty("last_sync", out JsonElement ls))
                {
                    string timeString = ls.GetString();
                    if (TimeSpan.TryParse(timeString, out TimeSpan parsedTime))
                    {
                        double secondsDelta = (DateTime.UtcNow.TimeOfDay - parsedTime).TotalSeconds;

                        if (secondsDelta < 0) 
                            secondsDelta += 86400;

                        if (secondsDelta > 600)
                        {
                            await App.Log("--> [DASHBOARD PARSER]: No telemetry being returned from WAN endpoint. Setting flag to default to next transport type.");
                            App.NetworkService.IsWifiTelemetryDead = true;
                        }
                    }
                }

                root.TryGetProperty("system_logs", out JsonElement logsNode);

                if (logsNode.ValueKind == JsonValueKind.Array)
                {
                    var logBuilder = new StringBuilder();
                    var fullTelemetry = string.Empty;
                    bool hasNewUniqueLines = false;

                    foreach (JsonElement individualLine in logsNode.EnumerateArray().Reverse())
                    {
                        string logText = individualLine.GetString() ?? string.Empty;
                        logText = logText.Trim();

                        if (!string.IsNullOrEmpty(logText))
                        {
                            fullTelemetry += logText;
                            if (_processedVehicleLogLinesBucket.Add(logText))
                            {
                                logBuilder.AppendLine(logText);
                                hasNewUniqueLines = true;
                            }
                        }
                    }

                    if (hasNewUniqueLines)
                    {
                        string freshTelemetryChangesOnly = logBuilder.ToString().TrimEnd();

                        OnTelemetryParsed?.Invoke(freshTelemetryChangesOnly);
                        App.NetworkService.IsWifiTelemetryDead = false;
                    }
                }

                await UpdateDashboardMetrics(frontVolts, frontPercent, frontIsCharging, backVolts, backPercent, backIsCharging, isCrossCharging);

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (!App.NetworkService.IsUsingCloudWanMode)
                    {
                        if (isArduinoCloudTunnelConnected)
                        {
                            App.NetworkService.IsWANReportedOnline = true;
                            CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: ONLINE";
                            CloudWanTelemetryStatusTextColor = Color.Parse("#10B981");
                        }
                        else
                        {

                            App.NetworkService.IsWANReportedOnline = false;
                            CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: OFFLINE";
                            CloudWanTelemetryStatusTextColor = Color.Parse("#EF4444");
                        }

                        App.NetworkService.LastReportedWANLinkState = DateTime.UtcNow;
                    }

                    if (!App.NetworkService.IsUsingCloudWanMode)
                    {
                        string activeNetworkIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
                        if (!string.IsNullOrEmpty(activeNetworkIP) && activeNetworkIP != "0.0.0.0")
                        {
                            VehicleIPTextLabel = activeNetworkIP;
                            BorderNetworkStatusVisible = true;
                        }
                    }
                });

                if (App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode)
                    ExecuteWifiThemeRedrawPass();
                else if (App.NetworkService.IsUsingCloudWanMode)
                    ExecuteCloudWanThemeRedrawPass();

                return;
            }
            else
            {
                OnTelemetryParsed?.Invoke(rawDataPacket);
            }

            if (rawDataPacket.Contains("CF_KEYS:") && !rawDataPacket.Contains("ERR_EMPTY_VAULTS"))
            {
                try
                {
                    int keysHeaderIndex = rawDataPacket.IndexOf("CF_KEYS:") + 8;
                    string encryptedBase64Envelope = rawDataPacket.Substring(keysHeaderIndex).Trim();

                    string decryptedPlaintextKeys = await NetworkHubService.DecryptLocalPayloadAES128CBC(encryptedBase64Envelope);

                    if (!string.IsNullOrWhiteSpace(decryptedPlaintextKeys) && decryptedPlaintextKeys.Contains(","))
                    {
                        string[] splitTokens = decryptedPlaintextKeys.Split(',');

                        if (splitTokens.Length == 3)
                        {
                            string extractedHost = splitTokens[0].Trim();
                            string extractedId = splitTokens[1].Trim();
                            string extractedSecret = splitTokens[2].Trim();

                            App.NetworkService.CloudflareHost = extractedHost;
                            App.NetworkService.ClientId = extractedId;
                            App.NetworkService.ClientSecret = extractedSecret;

                            Preferences.Default.Set("CloudflareHostKey", extractedHost);
                            Preferences.Default.Set("CloudflareClientIdKey", extractedId);
                            Preferences.Default.Set("CloudflareClientSecretKey", extractedSecret);

                            await App.Log("--> [APP SYNC SUCCESS]: Secure Zero-Trust credentials pulled, decrypted, and saved to handset storage vaults!");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await App.Log($"--> [KEY DECRYPTION CHOKE]: Failed to unpack over-the-air parameters: {ex.Message}");
                }
                return;
            }


            if (rawDataPacket.Contains("IP:") && !App.NetworkService.IsUsingWifiTransportMode)
            {
                int ipStartIndex = rawDataPacket.IndexOf("IP:") + 3;
                int ipEndIndex = rawDataPacket.IndexOf('|', ipStartIndex);

                if (ipStartIndex != -1 && ipEndIndex != -1)
                {
                    string extractedVehicleIP = rawDataPacket.Substring(ipStartIndex, ipEndIndex - ipStartIndex).Trim();
                    bool carReportsWanIsLive = rawDataPacket.Contains("WAN_ONLINE");

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        if (carReportsWanIsLive)
                        {
                            App.NetworkService.IsWANReportedOnline = true;
                            CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: ONLINE";
                            CloudWanTelemetryStatusTextColor = Color.Parse("#10B981");
                        }
                        else
                        {
                            App.NetworkService.IsWANReportedOnline = false;
                            CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: OFFLINE";
                            CloudWanTelemetryStatusTextColor = Color.Parse("#EF4444");
                        }

                        App.NetworkService.LastReportedWANLinkState = DateTime.UtcNow;

                        if (!string.IsNullOrEmpty(extractedVehicleIP))
                        {
                            if (extractedVehicleIP == "STA_HOTSPOT" || extractedVehicleIP == "0.0.0.0")
                            {
                                VehicleIPTextLabel = "ONLINE (Standalone AP Mode)";
                                App.NetworkService.IsWifiTelemetryDead = false;
                            }
                            else
                            {
                                VehicleIPTextLabel = extractedVehicleIP;
                            }

                            Preferences.Default.Set("LastKnownVehicleIP", extractedVehicleIP);
                            BorderNetworkStatusVisible = true;
                        }
                    });
                }

                Match frontMatch = FrontBatteryRegex.Match(rawDataPacket);
                float currentFrontVolts = 0;
                int currentFrontPercent = 0;
                bool currentFrontIsCharging = rawDataPacket.Contains("Front: [🔋 CHARGING]");

                if (frontMatch.Success)
                {
                    currentFrontVolts = float.Parse(frontMatch.Groups["volts"].Value);
                    currentFrontPercent = int.Parse(frontMatch.Groups["percent"].Value);
                }

                Match backMatch = BackBatteryRegex.Match(rawDataPacket);
                float currentBackVolts = 0;
                int currentBackPercent = 0;
                bool currentBackIsCharging = rawDataPacket.Contains("Back: [🔋 CHARGING]");

                Match availableRam = AvailableRamBytes.Match(rawDataPacket);

                if (availableRam.Success && int.TryParse(availableRam.Groups[1].Value, out int freeBytes))
                {
                    MemoryIndicator.CurrentInstance.UpdateMemoryHardwareGauge(freeBytes);
                }
                else
                {
                    MemoryIndicator.CurrentInstance.Hide();
                }

                if (backMatch.Success)
                {
                    currentBackVolts = float.Parse(backMatch.Groups["volts"].Value);
                    currentBackPercent = int.Parse(backMatch.Groups["percent"].Value);
                }

                bool crossChargingActive = false;
                if (rawDataPacket.Contains("[⚡ CROSS_CHG ACTIVE]"))
                {
                    crossChargingActive = true;
                }

                await UpdateDashboardMetrics(currentFrontVolts, currentFrontPercent, currentFrontIsCharging, currentBackVolts, currentBackPercent, currentBackIsCharging, crossChargingActive);
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [DASHBOARD PARSER CHOKE]: {ex.Message}");
        }
    }

    private async void UpdateBluetoothStatusBadge(bool isConnected)
    {
        bool phoneHasActiveWifiRadioLink = Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

        if (App.NetworkService.WaitingForAuthorization)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                BorderBluetoothStatusBackgroundColor = Color.Parse("#2D221A");
                BorderBluetoothStatusStrokeColor = Color.Parse("#FFBF00");

                DotTextLabel = "🔐";
                BluetoothStatusTextLabel = "VERIFYING SECURITY VAULTS...";
                BluetoothStatusTextLabelColor = Color.Parse("#FFBF00");
            });

            return;
        }

        if (App.NetworkService.IsConnecting && !(isConnected || App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingWifiTransportMode))
        {
            await App.Log("--> [BOOT SYNC]: Historical device found. Suppressing popup and launching background tracking...");

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                BorderBluetoothStatusBackgroundColor = Color.Parse("#1A2D20");
                BorderBluetoothStatusStrokeColor = Color.Parse("#FFBF00");
                DotTextLabel = "🔴";
                BluetoothStatusTextLabel = "RECONNECTING TO VEHICLE CORES...";
                BluetoothStatusTextLabelColor = Color.Parse("#FFBF00");
                BluetoothSignalTextLabel = string.Empty;

                ManualScanButtonVisible = true;
                AdminNavigationButtonEnabled = false;
                UnLockButtonEnabled = false;
                LockButtonEnabled = false;
            });
        }
        else if (!(isConnected || App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingCloudWanMode))
        {
            await App.Log("--> [DASHBOARD COCKPIT DETACH]: All transport networks are completely OFFLINE. Initializing absolute zero-out reset passes...");

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                BorderNetworkStatusVisible = false;

                BorderBluetoothStatusBackgroundColor = Color.Parse("#2D1A1A");
                BorderBluetoothStatusStrokeColor = Color.Parse("#EF4444");

                DotTextLabel = "❌";
                BluetoothSignalTextLabel = "SIGNAL DISCONNECTED";
                BluetoothSignalTextLabelColor = Color.Parse("#EF4444");

                BluetoothStatusTextLabel = "OFFLINE - LINK LOST";
                BluetoothStatusTextLabelColor = Color.Parse("#EF4444");

                if (App.NetworkService.IsWANReportedOnline ?? false && !(Connectivity.Current.NetworkAccess == NetworkAccess.Internet) && !NetworkHubService.HasPhysicalWifiConnection && !CrossBluetoothLE.Current.IsOn && !App.NetworkService.IsWifiTelemetryDead)
                {
                    CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: ONLINE";
                    CloudWanTelemetryStatusTextColor = Color.Parse("#10B981");
                    ActiveTransportChannelTextLabel = "TRANSPORT MODE: Disconnected (Enable Bluetooth, Wifi, or Mobile Data to connect).";
                }
                else if (App.NetworkService.IsWANReportedOnline ?? false && !(Connectivity.Current.NetworkAccess == NetworkAccess.Internet) && !NetworkHubService.HasPhysicalWifiConnection && !CrossBluetoothLE.Current.IsOn)
                {
                    CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: ONLINE";
                    CloudWanTelemetryStatusTextColor = Color.Parse("#10B981");
                    ActiveTransportChannelTextLabel = "TRANSPORT MODE: Disconnected (Enable Bluetooth or Mobile Data to connect).";
                }
                else if (!NetworkHubService.HasPhysicalWifiConnection && !CrossBluetoothLE.Current.IsOn && !App.NetworkService.IsWifiTelemetryDead)
                {
                    CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: OFFLINE";
                    CloudWanTelemetryStatusTextColor = Color.Parse("#EF4444");
                    ActiveTransportChannelTextLabel = "TRANSPORT MODE: Disconnected (Enable Bluetooth or Wifi to connect).";
                }
                else
                {
                    CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: OFFLINE";
                    CloudWanTelemetryStatusTextColor = Color.Parse("#EF4444");
                    ActiveTransportChannelTextLabel = "TRANSPORT MODE: Disconnected (Enable Bluetooth to connect).";
                }

                ManualScanButtonVisible = true;
                AdminNavigationButtonEnabled = false;

                LockButtonEnabled = false;
                UnLockButtonEnabled = false;

                FrontVoltsTextLabel = "0.00 V";
                FrontPercentTextLabel = "0%";
                ProgressFrontValue = 0.0f;
                ProgressFrontColorValue = Colors.DarkSlateGray;
                FrontIconTextLabel = "❌";

                BackVoltsTextLabel = "0.00 V";
                BackPercentTextLabel = "0%";
                ProgressBackValue = 0.0f;
                ProgressBackColorValue = Colors.DarkSlateGray;
                BackIconTextLabel = "❌";
            });

            _ = Task.Run(async () => {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    LayoutReconnectingVisible = true;
                    while (App.NetworkService.ReconnectCountdown > 0)
                    {
                        ReconnectingTextLabel = $"Reconnecting in {App.NetworkService.ReconnectCountdown} seconds.";
                        await Task.Delay(500);
                    }
                    LayoutReconnectingVisible = false;
                });
            });
        }
        else
        {
            if (App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode)
            {
                ExecuteWifiThemeRedrawPass();
                return;
            }
            else if (App.NetworkService.IsUsingCloudWanMode)
            {
                ExecuteCloudWanThemeRedrawPass();
                return;
            }

            string currentBleName = Preferences.Default.Get(MainPage.SavedDeviceNameKey, "VersaHub_BLE");
            if (Guid.TryParse(currentBleName, out _) || currentBleName.Contains('-')) currentBleName = "VersaHub_BLE";

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                BorderBluetoothStatusBackgroundColor = Color.Parse("#1A2D20");
                BorderBluetoothStatusStrokeColor = Color.Parse("#10B981");

                DotTextLabel = "🟢";

                BluetoothStatusTextLabel = $"CONNECTED: {currentBleName.ToUpper()}";
                BluetoothStatusTextLabelColor = Color.Parse("#10B981");

                ActiveTransportChannelTextLabel = $"TRANSPORT MODE: Low-Latency Bluetooth Channel (BLE)";

                ManualScanButtonVisible = false;

                if (!BluetoothSignalVisible)
                {
                    BluetoothSignalVisible = true;
                    BluetoothSignalTextLabelColor = Color.Parse("#EF4444");

                    if (App.NetworkService.ActiveRssi == -100)
                    {
                        BluetoothSignalTextLabel = "Waiting For RSSI Update";
                    }
                }

                if (App.NetworkService.IsAuthorized)
                {
                    LockButtonEnabled = true;
                    UnLockButtonEnabled = true;
                    AdminNavigationButtonEnabled = true;
                }
            });
        }
    }

    private async void UpdateWirelessSignalBars(int rssi)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (rssi == 0)
            {
                BluetoothSignalTextLabel = string.Empty;
                return;
            }

            if (rssi >= -60)
            {
                BluetoothSignalTextLabel = $"📶 EXCELLENT ({rssi} dBm)";
                BluetoothSignalTextLabelColor = Color.Parse("#10B981");
            }
            else if (rssi >= -75)
            {
                BluetoothSignalTextLabel = $"📊 GOOD ({rssi} dBm)";
                BluetoothSignalTextLabelColor = Color.Parse("#3B82F6");
            }
            else if (rssi >= -90)
            {
                BluetoothSignalTextLabel = $"📉 WEAK ({rssi} dBm)";
                BluetoothSignalTextLabelColor = Color.Parse("#F59E0B");
            }
            else
            {
                BluetoothSignalTextLabel = $"⚠ CRITICAL ({rssi} dBm)";
                BluetoothSignalTextLabelColor = Color.Parse("#EF4444");
            }
        });
    }

    private async Task UpdateDashboardMetrics(float frontVolts, int frontPercent, bool frontIsCharging, float backVolts, int backPercent, bool backIsCharging, bool isCrossCharging)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            FrontVoltsTextLabel = $"{frontVolts:F2} V";
            FrontPercentTextLabel = $"{frontPercent}%";
            ProgressFrontValue = frontPercent / 100.0f;

            if (frontIsCharging)
            {
                FrontIconTextLabel = "⚡";
                ProgressFrontColorValue = Colors.Yellow;
                FrontVoltsTextLabelColorValue = Colors.Yellow;
            }
            else if (frontPercent < 15)
            {
                FrontIconTextLabel = "❌";
                ProgressFrontColorValue = Colors.Red;
                FrontVoltsTextLabelColorValue = Colors.Red;
            }
            else
            {
                FrontIconTextLabel = "🔋";
                ProgressFrontColorValue = Color.Parse("#10B981");
                FrontVoltsTextLabelColorValue = Color.Parse("#10B981");
            }

            BackVoltsTextLabel = $"{backVolts:F2} V";
            BackPercentTextLabel = $"{backPercent}%";
            ProgressBackValue = backPercent / 100.0f;

            if (backIsCharging)
            {
                BackIconTextLabel = "⚡";
                ProgressBackColorValue = Colors.Yellow;
                BackVoltsTextLabelColorValue = Colors.Yellow;
            }
            else if (backPercent < 15)
            {
                BackIconTextLabel = "❌";
                ProgressBackColorValue = Colors.Red;
                BackVoltsTextLabelColorValue = Colors.Red;
            }
            else
            {
                BackIconTextLabel = "🔋";
                ProgressBackColorValue = Color.Parse("#3B82F6");
                BackVoltsTextLabelColorValue = Color.Parse("#3B82F6");
            }

            if (isCrossCharging)
            {
                CrossChargeStatusTextLabel = "⚡ CROSS-CHARGING ACTIVE";
                CrossChargeStatusTextLabelColorValue = Colors.Yellow;
                CrossChargeStatusLayoutVisible = true;
            }
            else
            {
                CrossChargeStatusLayoutVisible = false;
            }
        });
    }

    private async void ExecuteWifiThemeRedrawPass()
    {
        string cachedIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
        if (string.IsNullOrEmpty(cachedIP) || cachedIP == "0.0.0.0") return;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (App.NetworkService.IsBluetoothConnected)
            {
                BluetoothSignalVisible = true;
            }
            else
            {
                BluetoothSignalVisible = false;
            }

            ManualScanButtonVisible = false;

            if (App.NetworkService.IsAuthorized)
            {
                LockButtonEnabled = true;
                UnLockButtonEnabled = true;
                AdminNavigationButtonEnabled = true;
            }
            else
            {
                LockButtonEnabled = false;
                UnLockButtonEnabled = false;
                AdminNavigationButtonEnabled = false;
            }

            BorderNetworkStatusVisible = true;
            VehicleIPTextLabel = cachedIP;

            BorderBluetoothStatusBackgroundColor = Color.Parse("#1A242D");
            BorderBluetoothStatusStrokeColor = Color.Parse("#3B82F6");

            DotTextLabel = "🌐";
            BluetoothStatusTextLabel = "LOCAL WI-FI SUBNET ONLINE";
            BluetoothStatusTextLabelColor = Color.Parse("#3B82F6");

            ActiveTransportChannelTextLabel = $"TRANSPORT MODE: REST API LINK ({cachedIP})";
        });
    }

    private async void ExecuteCloudWanThemeRedrawPass()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            BluetoothSignalTextLabel = " 📶 WAN LIVE";
            BluetoothSignalTextLabelColor = Color.Parse("#F59E0B");
            
            BluetoothSignalVisible = true;
            ManualScanButtonVisible = false;

            if (App.NetworkService.IsAuthorized)
            {
                LockButtonEnabled = true;
                UnLockButtonEnabled = true;
                AdminNavigationButtonEnabled = true;
            }
            else
            {
                LockButtonEnabled = false;
                UnLockButtonEnabled = false;
                AdminNavigationButtonEnabled = false;
            }

            BorderNetworkStatusVisible = true;
            VehicleIPTextLabel = "Cloudflare Proxy";

            BorderBluetoothStatusBackgroundColor = Color.Parse("#2D221A");
            BorderBluetoothStatusStrokeColor = Color.Parse("#F59E0B");

            DotTextLabel = "☁️";
            BluetoothStatusTextLabel = "WAN CONNECTED";
            BluetoothStatusTextLabelColor = Color.Parse("#F59E0B");

            ActiveTransportChannelTextLabel = "TRANSPORT MODE: Encrypted WAN Link Active";

            CloudWanTelemetryStatusTextLabel = "☁️ CLOUD LINK: ONLINE";
            CloudWanTelemetryStatusTextColor = Color.Parse("#10B981");
        });
    }

    private async void OnSetupFinished(object sender, EventArgs e)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            LayoutPasswordInitVisible = false;
            App.NetworkService.IsPromptingForMasterPassword = false;
            await App.NetworkService.VerifyPasswordAgainstHardwareAsync();
        });
    }

    private async void HandleAuthorizationRequest(bool promptForMasterPassword)
    {
        if (promptForMasterPassword)
        {
            await App.Log("--> [HANDSHAKE REJECTED]: Auth failed token caught. Displaying single alert prompt...");

            App.NetworkService.IsPromptingForMasterPassword = true;

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                LayoutPasswordInitVisible = true;

                if (InitMasterPassword.CurrentInstance != null)
                {
                    var entryField = InitMasterPassword.CurrentInstance.FindByName<Entry>("entryInitialPass");
                    if (entryField != null)
                    {
                        entryField.Text = string.Empty;
                        entryField.Focus();
                    }
                }

                await DisplayAlertAsync("ACCESS DENIED", "The passcode signature you entered does not match your vehicle module's secure vaults.", "TRY AGAIN");
            });
        }
        else
        {
            await App.Log("--> [HANDSHAKE SECURED]: Auth validation state cleared successfully!");

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (LayoutPasswordInitVisible)
                {
                    LayoutPasswordInitVisible = false;
                    await DisplayAlertAsync("VAULT SYNCED", "Your master passcode has been verified against your vehicle's registers. Security clearance accepted.", "ENTER COCKPIT");
                }

                UpdateBluetoothStatusBadge(App.NetworkService.IsBluetoothConnected);

                string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
                bool commandTransmitted = false;

                try
                {
                    commandTransmitted = await App.NetworkService.SendSecureCommandAsync(activeKey, "GETCFKEYS");
                }
                catch (Exception ex)
                {
                    if (ex.Message != "--> [ADMIN]: Unable to send command.")
                        throw;
                }

                if (commandTransmitted)
                {
                    await App.Log("--> [BOOT LINK SUCCESS]: Secure WIFI key-pull verification request offloaded natively on boot pass!");
                }
                else
                {
                    await App.Log("--> [BOOT LINK FAILURE]: Failed to process Secure WIFI key-pull verification request on boot pass! The command could not be transmitted.");
                }
            });
        }
    }

    private async void OnLockClicked(object sender, EventArgs e)
    {
        try
        {
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            await App.Log("--> [UI CONTROL]: Dispatching secure over-the-air LOCK token packet...");
            await App.NetworkService.SendSecureCommandAsync(activeKey, "LOCK");
        }
        catch (Exception ex)
        {
            await App.Log($"--> [LOCK UI CHOKE]: {ex.Message}");
            await DisplayAlertAsync("COMMAND FAILURE", "Unable to deliver the LOCK command! Please check your connection.", "OK");
        }
    }

    private async void OnUnlockClicked(object sender, EventArgs e)
    {
        try
        {
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            await App.Log("--> [UI CONTROL]: Dispatching secure over-the-air UNLOCK token packet...");
            await App.NetworkService.SendSecureCommandAsync(activeKey, "UNLOCK");
        }
        catch (Exception ex)
        {
            await App.Log($"--> [UNLOCK UI CHOKE]: {ex.Message}");
            await DisplayAlertAsync("COMMAND FAILURE", "Unable to deliver the UNLOCK command! Please check your connection.", "OK");
        }
    }

    private async void OnAdminNavigationClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new AdminPage());
    }

    private async void OnRollbackConnectionAndRescan(object sender, EventArgs e)
    {
        try
        {
            await App.Log("--> [RECOVERY HUB]: Wrong device selected. Executing wireless reset line...");
            if (App.NetworkService != null) await App.NetworkService.DisconnectCurrentDeviceAsync();

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                LayoutPasswordInitVisible = false;
                if (InitMasterPassword.CurrentInstance != null)
                {
                    var entryField = InitMasterPassword.CurrentInstance.FindByName<Entry>("entryInitialPass");
                    entryField?.Text = string.Empty;
                }
                BluetoothDevicePickerVisible = true;
                LockButtonEnabled = false;
                UnLockButtonEnabled = false;
                AdminNavigationButtonEnabled = false;

                if (BTDevicePicker.CurrentInstance != null)
                {
                    await BTDevicePicker.CurrentInstance.InitializePickerLifecycleAsync();
                }
            });
        }
        catch (Exception ex)
        {
            await App.Log($"--> [RECOVERY EXCEPTION SHIELD]: {ex.Message}");
        }
    }

    private async void OnManualScanTriggerClicked(object sender, EventArgs e)
    {
        try
        {
            await App.Log("--> [UI CONTROL]: User requested manual scan refresh pass...");

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (BTDevicePicker.CurrentInstance != null)
                {
                    await App.Log("--> [HARDWARE MONITOR]: Forcing active device list reset sweep over radio waves...");
                    LockButtonEnabled = false;
                    UnLockButtonEnabled = false;
                    AdminNavigationButtonEnabled = false;
                    await BTDevicePicker.CurrentInstance.InitializePickerLifecycleAsync();
                }
            });
        }
        catch (Exception ex)
        {
            await App.Log($"--> [MANUAL SCAN TRIGGER FAULT]: Dynamic security check failed: {ex.Message}");
        }
    }

    private async Task OnRefreshScanClicked(object sender, EventArgs e)
    {
        await BTDevicePicker.CurrentInstance.TriggerRefreshScan();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!(App.NetworkService.IsBluetoothConnected || App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingCloudWanMode))
            await MainThread.InvokeOnMainThreadAsync(async () => await KickstartWirelessCockpitSync());

        await App.Log("--> [DASHBOARD LANDING]: Repainting master layout frames...");

        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            BorderBluetoothStatusBackgroundColor = Color.Parse("#2D1A1A");
            BorderBluetoothStatusStrokeColor = Color.Parse("#EF4444");
            DotTextLabel = "🔴";
            BluetoothStatusTextLabel = "VEHICLE MODULE REBOOTING...";
            BluetoothStatusTextLabelColor = Color.Parse("#EF4444");
            BluetoothSignalTextLabel = string.Empty;

            await App.Log("--> [UI STATE ALIGNMENT]: Dashboard badge force-shifted to REBOOTING tracking state.");
        }

        _processedVehicleLogLinesBucket = [];

        App.NetworkService?.UpdateLifecycleState(true);
        UpdateBluetoothStatusBadge(App.NetworkService?.IsBluetoothConnected ?? false);
    }

    public async Task KickstartWirelessCockpitSync()
    {
        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            await App.Log("--> [BOOT SYNC GUARD]: Active reboot watchdog detected. Standing down dashboard autoconnect tasks.");
            return;
        }

        try
        {
            string targetedMacAddress = Preferences.Default.Get(SavedDeviceMacKey, string.Empty);
            if (string.IsNullOrEmpty(targetedMacAddress))
            {
                await App.Log("--> [BOOT SYNC]: Zero historical pairings found. Inflating UI elements before permissions...");

                    if (BTDevicePicker.CurrentInstance != null)
                    {
                        await App.Log("--> [HARDWARE MONITOR]: Forcing active device list reset sweep over radio waves...");
                        LockButtonEnabled = false;
                        UnLockButtonEnabled = false;
                        AdminNavigationButtonEnabled = false;
                        await BTDevicePicker.CurrentInstance.InitializePickerLifecycleAsync();
                    }
            }
            else
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    App.NetworkService.StartConnectionSupervisor();
                });
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [BOOT WORKFLOW SHIELD]: {ex.Message}");
        }
    }

    public async Task LogDebugToTerminal(string value)
    {
        OnTelemetryParsed?.Invoke(value.Replace("--> -->", "-->"));
    }

    ~MainPage()
    {
        App.NetworkService.OnConnectionStateChanged -= UpdateBluetoothStatusBadge;
        App.NetworkService.OnRssiUpdated -= UpdateWirelessSignalBars;
        App.NetworkService.OnTelemetryReceived -= ParseVehicleTelemetryStream;
        if (InitMasterPassword.CurrentInstance != null)
        {
            InitMasterPassword.CurrentInstance.OnPasswordInitialized -= OnSetupFinished;
            InitMasterPassword.CurrentInstance.OnWrongDeviceRequested -= OnRollbackConnectionAndRescan;
        }
    }
}
