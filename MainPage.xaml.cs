using Plugin.BLE;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VersaHUD.Services;

namespace VersaHUD;

public partial class MainPage : ContentPage
{
    private static readonly Regex FrontBatteryRegex = new(@"Front:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)", RegexOptions.Compiled);
    private static readonly Regex BackBatteryRegex = new(@"Back:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)", RegexOptions.Compiled);
    private static string _lastTelemetryValue = string.Empty;
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

        if (initMasterPasswordControl != null)
        {
            initMasterPasswordControl.OnPasswordInitialized += OnSetupFinished;
            initMasterPasswordControl.OnWrongDeviceRequested += OnRollbackConnectionAndRescan;
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
                if (layoutPasswordInitShell != null && !layoutPasswordInitShell.IsVisible)
                {
                    layoutPasswordInitShell.IsVisible = true;
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

                        _lastTelemetryValue = freshTelemetryChangesOnly;
                        OnTelemetryParsed?.Invoke(freshTelemetryChangesOnly);
                        App.NetworkService.IsWifiTelemetryDead = false;
                    }
                }

                await UpdateDashboardMetrics(frontVolts, frontPercent, frontIsCharging, backVolts, backPercent, backIsCharging, isCrossCharging);

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (lblCloudWanTelemetryStatus != null && !App.NetworkService.IsUsingCloudWanMode)
                    {
                        if (isArduinoCloudTunnelConnected)
                        {
                            App.NetworkService.IsWANReportedOnline = true;
                            lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: ONLINE";
                            lblCloudWanTelemetryStatus.TextColor = Color.Parse("#10B981");
                        }
                        else
                        {

                            App.NetworkService.IsWANReportedOnline = false;
                            lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: OFFLINE";
                            lblCloudWanTelemetryStatus.TextColor = Color.Parse("#EF4444");
                        }

                        App.NetworkService.LastReportedWANLinkState = DateTime.UtcNow;
                    }

                    if (!App.NetworkService.IsUsingCloudWanMode)
                    {
                        string activeNetworkIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
                        if (!string.IsNullOrEmpty(activeNetworkIP) && activeNetworkIP != "0.0.0.0")
                        {
                            lblVehicleIPText?.Text = activeNetworkIP;
                            borderNetworkStatus?.IsVisible = true;
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
                int ipEndIndex = rawDataPacket.IndexOf("|", ipStartIndex);

                if (ipStartIndex != -1 && ipEndIndex != -1)
                {
                    string extractedVehicleIP = rawDataPacket.Substring(ipStartIndex, ipEndIndex - ipStartIndex).Trim();
                    bool carReportsWanIsLive = rawDataPacket.Contains("WAN_ONLINE");

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        if (lblCloudWanTelemetryStatus != null)
                        {
                            if (carReportsWanIsLive)
                            {
                                App.NetworkService.IsWANReportedOnline = true;
                                lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: ONLINE";
                                lblCloudWanTelemetryStatus.TextColor = Color.Parse("#10B981");
                            }
                            else
                            {
                                App.NetworkService.IsWANReportedOnline = false;
                                lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: OFFLINE";
                                lblCloudWanTelemetryStatus.TextColor = Color.Parse("#EF4444");
                            }

                            App.NetworkService.LastReportedWANLinkState = DateTime.UtcNow;
                        }

                        if (!string.IsNullOrEmpty(extractedVehicleIP))
                        {
                            if (extractedVehicleIP == "STA_HOTSPOT" || extractedVehicleIP == "0.0.0.0")
                            {
                                lblVehicleIPText?.Text = "ONLINE (Standalone AP Mode)";
                                App.NetworkService.IsWifiTelemetryDead = false;
                            }
                            else
                            {
                                lblVehicleIPText?.Text = extractedVehicleIP;
                            }

                            Preferences.Default.Set("LastKnownVehicleIP", extractedVehicleIP);
                            borderNetworkStatus?.IsVisible = true;
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
                borderBleStatus.BackgroundColor = Color.Parse("#2D221A");
                borderBleStatus.Stroke = Color.Parse("#FFBF00");

                lblBleDot.Text = "🔐";
                lblBleStatusText.Text = "VERIFYING SECURITY VAULTS...";
                lblBleStatusText.TextColor = Color.Parse("#FFBF00");
            });

            return;
        }

        if (App.NetworkService.IsConnecting && !(isConnected || App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingWifiTransportMode))
        {
            await App.Log("--> [BOOT SYNC]: Historical device found. Suppressing popup and launching background tracking...");

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                borderBleStatus.BackgroundColor = Color.Parse("#1A2D20");
                borderBleStatus.Stroke = Color.Parse("#FFBF00");
                lblBleDot.Text = "🔴";
                lblBleStatusText.Text = "RECONNECTING TO VEHICLE CORES...";
                lblBleStatusText.TextColor = Color.Parse("#FFBF00");
                lblBleSignal.Text = string.Empty;

                btnManualScanTrigger?.IsVisible = true;
                btnAdminNavigation?.IsEnabled = false;
                btnUnlock?.IsEnabled = false;
                btnLock?.IsEnabled = false;
            });
        }
        else if (!(isConnected || App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingCloudWanMode))
        {
            await App.Log("--> [DASHBOARD COCKPIT DETACH]: All transport networks are completely OFFLINE. Initializing absolute zero-out reset passes...");

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                borderNetworkStatus?.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
                    borderBleStatus.Stroke = Color.Parse("#EF4444");
                }

                lblBleDot?.Text = "❌";
                lblBleSignal?.Text = "SIGNAL DISCONNECTED";
                lblBleSignal?.TextColor = Color.Parse("#EF4444");

                if (lblBleStatusText != null)
                {
                    lblBleStatusText.Text = "OFFLINE - LINK LOST";
                    lblBleStatusText.TextColor = Color.Parse("#EF4444");
                }

                if (App.NetworkService.IsWANReportedOnline ?? false && !(Connectivity.Current.NetworkAccess == NetworkAccess.Internet) && !NetworkHubService.HasPhysicalWifiConnection && !CrossBluetoothLE.Current.IsOn && !App.NetworkService.IsWifiTelemetryDead)
                {
                    lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: ONLINE";
                    lblCloudWanTelemetryStatus.TextColor = Color.Parse("#10B981");
                    lblActiveTransportChannel?.Text = "TRANSPORT MODE: Disconnected (Enable Bluetooth, Wifi, or Mobile Data to connect).";
                }
                else if (App.NetworkService.IsWANReportedOnline ?? false && !(Connectivity.Current.NetworkAccess == NetworkAccess.Internet) && !NetworkHubService.HasPhysicalWifiConnection && !CrossBluetoothLE.Current.IsOn)
                {
                    lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: ONLINE";
                    lblCloudWanTelemetryStatus.TextColor = Color.Parse("#10B981");
                    lblActiveTransportChannel?.Text = "TRANSPORT MODE: Disconnected (Enable Bluetooth or Mobile Data to connect).";
                }
                else if (!NetworkHubService.HasPhysicalWifiConnection && !CrossBluetoothLE.Current.IsOn && !App.NetworkService.IsWifiTelemetryDead)
                {
                    lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: OFFLINE";
                    lblCloudWanTelemetryStatus.TextColor = Color.Parse("#EF4444");
                    lblActiveTransportChannel?.Text = "TRANSPORT MODE: Disconnected (Enable Bluetooth or Wifi to connect).";
                }
                else
                {
                    lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: OFFLINE";
                    lblCloudWanTelemetryStatus.TextColor = Color.Parse("#EF4444");
                    lblActiveTransportChannel?.Text = "TRANSPORT MODE: Disconnected (Enable Bluetooth to connect).";
                }

                btnManualScanTrigger?.IsVisible = true;
                btnAdminNavigation?.IsEnabled = false;

                btnLock?.IsEnabled = false;
                btnUnlock?.IsEnabled = false;

                lblFrontVolts.Text = "0.00 V";
                lblFrontPercent.Text = "0%";
                progressFront.Progress = 0.0f;
                progressFront.ProgressColor = Colors.DarkSlateGray;
                lblFrontIcon.Text = "❌";

                lblBackVolts.Text = "0.00 V";
                lblBackPercent.Text = "0%";
                progressBack.Progress = 0.0f;
                progressBack.ProgressColor = Colors.DarkSlateGray;
                lblBackIcon.Text = "❌";
            });

            _ = Task.Run(async () => {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    layoutReconnecting.IsVisible = true;
                    while (App.NetworkService.ReconnectCountdown > 0)
                    {
                        lblReconnecting?.Text = $"Reconnecting in {App.NetworkService.ReconnectCountdown} seconds.";
                        await Task.Delay(500);
                    }
                    layoutReconnecting.IsVisible = false;
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
                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#1A2D20");
                    borderBleStatus.Stroke = Color.Parse("#10B981");
                }

                lblBleDot?.Text = "🟢";

                if (lblBleStatusText != null)
                {
                    lblBleStatusText.Text = $"CONNECTED: {currentBleName.ToUpper()}";
                    lblBleStatusText.TextColor = Color.Parse("#10B981");
                }

                lblActiveTransportChannel?.Text = $"TRANSPORT MODE: Low-Latency Bluetooth Channel (BLE)";

                btnManualScanTrigger?.IsVisible = false;

                if (lblBleSignal != null && !lblBleSignal.IsVisible)
                {
                    lblBleSignal?.IsVisible = true;
                    lblBleSignal?.TextColor = Color.Parse("#EF4444");

                    if (App.NetworkService.ActiveRssi == -100)
                    {
                        lblBleSignal?.Text = "Waiting For RSSI Update";
                    }
                }

                if (App.NetworkService.IsAuthorized)
                {
                    btnLock?.IsEnabled = true;
                    btnUnlock?.IsEnabled = true;
                    btnAdminNavigation?.IsEnabled = true;
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
                lblBleSignal.Text = string.Empty;
                return;
            }

            if (rssi >= -60)
            {
                lblBleSignal.Text = $"📶 EXCELLENT ({rssi} dBm)";
                lblBleSignal.TextColor = Color.Parse("#10B981");
            }
            else if (rssi >= -75)
            {
                lblBleSignal.Text = $"📊 GOOD ({rssi} dBm)";
                lblBleSignal.TextColor = Color.Parse("#3B82F6");
            }
            else if (rssi >= -90)
            {
                lblBleSignal.Text = $"📉 WEAK ({rssi} dBm)";
                lblBleSignal.TextColor = Color.Parse("#F59E0B");
            }
            else
            {
                lblBleSignal.Text = $"⚠ CRITICAL ({rssi} dBm)";
                lblBleSignal.TextColor = Color.Parse("#EF4444");
            }
        });
    }

    private async Task UpdateDashboardMetrics(float frontVolts, int frontPercent, bool frontIsCharging, float backVolts, int backPercent, bool backIsCharging, bool isCrossCharging)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            lblFrontVolts.Text = $"{frontVolts:F2} V";
            lblFrontPercent.Text = $"{frontPercent}%";
            progressFront.Progress = frontPercent / 100.0f;

            if (frontIsCharging)
            {
                lblFrontIcon.Text = "⚡";
                progressFront.ProgressColor = Colors.Yellow;
                lblFrontVolts.TextColor = Colors.Yellow;
            }
            else if (frontPercent < 15)
            {
                lblFrontIcon.Text = "❌";
                progressFront.ProgressColor = Colors.Red;
                lblFrontVolts.TextColor = Colors.Red;
            }
            else
            {
                lblFrontIcon.Text = "🔋";
                progressFront.ProgressColor = Color.Parse("#10B981");
                lblFrontVolts.TextColor = Color.Parse("#10B981");
            }

            lblBackVolts.Text = $"{backVolts:F2} V";
            lblBackPercent.Text = $"{backPercent}%";
            progressBack.Progress = backPercent / 100.0f;

            if (backIsCharging)
            {
                lblBackIcon.Text = "⚡";
                progressBack.ProgressColor = Colors.Yellow;
                lblBackVolts.TextColor = Colors.Yellow;
            }
            else if (backPercent < 15)
            {
                lblBackIcon.Text = "❌";
                progressBack.ProgressColor = Colors.Red;
                lblBackVolts.TextColor = Colors.Red;
            }
            else
            {
                lblBackIcon.Text = "🔋";
                progressBack.ProgressColor = Color.Parse("#3B82F6");
                lblBackVolts.TextColor = Color.Parse("#3B82F6");
            }

            if (isCrossCharging)
            {
                lblCrossChargeStatus.Text = "⚡ CROSS-CHARGING ACTIVE";
                lblCrossChargeStatus.TextColor = Colors.Yellow;
                layoutCrossCharging.IsVisible = true;
            }
            else
            {
                layoutCrossCharging.IsVisible = false;
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
                lblBleSignal?.IsVisible = true;
            }
            else
            {
                lblBleSignal?.IsVisible = false;
            }

            btnManualScanTrigger?.IsVisible = false;

            if (App.NetworkService.IsAuthorized)
            {
                btnLock?.IsEnabled = true;
                btnUnlock?.IsEnabled = true;
                btnAdminNavigation?.IsEnabled = true;
            }
            else
            {
                btnLock?.IsEnabled = false;
                btnUnlock?.IsEnabled = false;
                btnAdminNavigation?.IsEnabled = false;
            }

            borderNetworkStatus?.IsVisible = true;
            lblVehicleIPText?.Text = cachedIP;

            if (borderBleStatus != null)
            {
                borderBleStatus.BackgroundColor = Color.Parse("#1A242D");
                borderBleStatus.Stroke = Color.Parse("#3B82F6");
            }

            lblBleDot?.Text = "🌐";
            if (lblBleStatusText != null)
            {
                lblBleStatusText.Text = "LOCAL WI-FI SUBNET ONLINE";
                lblBleStatusText.TextColor = Color.Parse("#3B82F6");
            }

            lblActiveTransportChannel?.Text = $"TRANSPORT MODE: REST API LINK ({cachedIP})";
        });
    }

    private async void ExecuteCloudWanThemeRedrawPass()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (lblBleSignal != null)
            {
                lblBleSignal.Text = " 📶 WAN LIVE";
                lblBleSignal.TextColor = Color.Parse("#F59E0B");
                lblBleSignal.IsVisible = true;
            }

            btnManualScanTrigger?.IsVisible = false;

            if (App.NetworkService.IsAuthorized)
            {
                btnLock?.IsEnabled = true;
                btnUnlock?.IsEnabled = true;
                btnAdminNavigation?.IsEnabled = true;
            }
            else
            {
                btnLock?.IsEnabled = false;
                btnUnlock?.IsEnabled = false;
                btnAdminNavigation?.IsEnabled = false;
            }

            borderNetworkStatus?.IsVisible = true;
            lblVehicleIPText?.Text = "Cloudflare Proxy";

            if (borderBleStatus != null)
            {
                borderBleStatus.BackgroundColor = Color.Parse("#2D221A");
                borderBleStatus.Stroke = Color.Parse("#F59E0B");
            }

            lblBleDot?.Text = "☁️";
            if (lblBleStatusText != null)
            {
                lblBleStatusText.Text = "WAN CONNECTED";
                lblBleStatusText.TextColor = Color.Parse("#F59E0B");
            }

            lblActiveTransportChannel?.Text = "TRANSPORT MODE: Encrypted WAN Link Active";

            if (lblCloudWanTelemetryStatus != null)
            {
                lblCloudWanTelemetryStatus.Text = "☁️ CLOUD LINK: ONLINE";
                lblCloudWanTelemetryStatus.TextColor = Color.Parse("#10B981");
            }
        });
    }

    private async void OnSetupFinished(object sender, EventArgs e)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            layoutPasswordInitShell.IsVisible = false;
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
                layoutPasswordInitShell?.IsVisible = true;

                if (initMasterPasswordControl != null)
                {
                    var entryField = initMasterPasswordControl.FindByName<Entry>("entryInitialPass");
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
                if (layoutPasswordInitShell != null && layoutPasswordInitShell.IsVisible)
                {
                    layoutPasswordInitShell.IsVisible = false;
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
                layoutPasswordInitShell.IsVisible = false;
                if (initMasterPasswordControl != null)
                {
                    var entryField = initMasterPasswordControl.FindByName<Entry>("entryInitialPass");
                    entryField?.Text = string.Empty;
                }
                btDevicePicker.IsVisible = true;
                btnLock?.IsEnabled = false;
                btnUnlock?.IsEnabled = false;
                btnAdminNavigation?.IsEnabled = false;

                if (btDevicePicker != null)
                {
                    await btDevicePicker.InitializePickerLifecycleAsync();
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
                if (btDevicePicker != null)
                {
                    await App.Log("--> [HARDWARE MONITOR]: Forcing active device list reset sweep over radio waves...");
                    btnLock?.IsEnabled = false;
                    btnUnlock?.IsEnabled = false;
                    btnAdminNavigation?.IsEnabled = false;
                    await btDevicePicker.InitializePickerLifecycleAsync();
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
        await btDevicePicker.TriggerRefreshScan();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!(App.NetworkService.IsBluetoothConnected || App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingLocalApMode || App.NetworkService.IsUsingCloudWanMode))
            await MainThread.InvokeOnMainThreadAsync(async () => await KickstartWirelessCockpitSync());

        await App.Log("--> [DASHBOARD LANDING]: Repainting master layout frames...");

        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
            borderBleStatus.Stroke = Color.Parse("#EF4444");
            lblBleDot.Text = "🔴";
            lblBleStatusText.Text = "VEHICLE MODULE REBOOTING...";
            lblBleStatusText.TextColor = Color.Parse("#EF4444");
            lblBleSignal.Text = string.Empty;

            await App.Log("--> [UI STATE ALIGNMENT]: Dashboard badge force-shifted to REBOOTING tracking state.");
        }

        _lastTelemetryValue = string.Empty;
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

                    if (btDevicePicker != null)
                    {
                        await App.Log("--> [HARDWARE MONITOR]: Forcing active device list reset sweep over radio waves...");
                        btnLock?.IsEnabled = false;
                        btnUnlock?.IsEnabled = false;
                        btnAdminNavigation?.IsEnabled = false;
                        await btDevicePicker.InitializePickerLifecycleAsync();
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
        if (initMasterPasswordControl != null)
        {
            initMasterPasswordControl.OnPasswordInitialized -= OnSetupFinished;
            initMasterPasswordControl.OnWrongDeviceRequested -= OnRollbackConnectionAndRescan;
        }
    }
}
