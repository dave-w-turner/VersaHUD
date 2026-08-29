using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VersaHUD;

public partial class MainPage : ContentPage
{
    public const string SavedDeviceMacKey = "LastConnectedDeviceMac";
    public const string SavedDeviceNameKey = "LastConnectedBleId";
        
    private static readonly Regex FrontBatteryRegex = new
    Regex(@"Front:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)",
    RegexOptions.Compiled);
    private static readonly Regex BackBatteryRegex = new
    Regex(@"Back:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)",
    RegexOptions.Compiled);

    private CancellationTokenSource? _wifiTelemetryCancelSource;
    public event Action<string>? OnWifiTelemetryParsed;

    public MainPage()
    {
        InitializeComponent();

        App.NetworkService.OnConnectionStateChanged += UpdateBluetoothStatusBadge;
        App.NetworkService.OnRssiUpdated += UpdateWirelessSignalBars;
        App.NetworkService.OnTelemetryReceived += ParseVehicleTelemetryStream;
        App.NetworkService.OnTransportModeChanged += OnTransportChannelShiftRepaint;

        if (initMasterPasswordControl != null)
        {
            initMasterPasswordControl.OnPasswordInitialized += OnSetupFinished;
            initMasterPasswordControl.OnWrongDeviceRequested += OnRollbackConnectionAndRescan;
        }
    }

    private void ManageWifiTelemetryPollingLifecycle(bool startWorker)
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

        Debug.WriteLine("--> [UI NETWORK ENGINE]: Wi-Fi link active. Spawning localized high-speed background HTTP polling thread...");

        Task.Run(async () =>
        {
            var localSocketHandler = new SocketsHttpHandler()
            {
                AllowAutoRedirect = true,
                UseCookies = false
            };

            using (var telemetryClient = new System.Net.Http.HttpClient(localSocketHandler))
            {
                telemetryClient.Timeout = TimeSpan.FromMilliseconds(2000);

                string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
                byte[] secretSharedKeyBytes = new byte[] { 0x5A, 0xA5, 0x1F, 0x2C, 0x7E, 0x9D, 0x8B, 0x34, 0x61, 0xF0, 0xE3, 0xD2, 0xC1, 0xB0, 0x09, 0x48 };
                byte[] initializationVectorBytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
                string encryptedBase64PayloadString = "";

                try
                {
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
                                byte[] plainTextBytes = Encoding.UTF8.GetBytes(activeKey);
                                cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                                cryptoStream.FlushFinalBlock();
                            }
                            encryptedBase64PayloadString = Convert.ToBase64String(memoryStream.ToArray());
                        }
                    }
                }
                catch (Exception cipherEx)
                {
                    Debug.WriteLine($"--> [UI POLLING ENGINE ERROR]: Encryption compilation failed: {cipherEx.Message}");
                    return;
                }

                while (!executionPassToken.IsCancellationRequested)
                {
                    string targetIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

                    if (string.IsNullOrEmpty(targetIP) || targetIP == "0.0.0.0" || targetIP == "STA_HOTSPOT")
                    {
                        await Task.Delay(3000, executionPassToken);
                        continue;
                    }

                    try
                    {
                        var httpPasscodeContent = new StringContent(encryptedBase64PayloadString, System.Text.Encoding.UTF8, "text/plain");

                        var networkResponse = await telemetryClient.PostAsync($"http://{targetIP}/api/telemetry", httpPasscodeContent, executionPassToken);

                        if (networkResponse.IsSuccessStatusCode)
                        {
                            string rawJsonOutputString = await networkResponse.Content.ReadAsStringAsync(executionPassToken);

                            if (!string.IsNullOrEmpty(rawJsonOutputString))
                            {
                                ParseVehicleTelemetryStream(rawJsonOutputString);
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception loopEx)
                    {
                        Debug.WriteLine($"--> [UI POLLING ENGINE DROPOUT]: Sockets handled connection lag safely: {loopEx.Message}");
                    }

                    try { await Task.Delay(3000, executionPassToken); } catch (TaskCanceledException) { break; }
                }
            }

            Debug.WriteLine("--> [UI NETWORK ENGINE]: Background HTTP data polling task thread closed down cleanly.");

        }, executionPassToken);
    }

    private void ParseVehicleTelemetryStream(string rawDataPacket)
    {
        if (string.IsNullOrEmpty(rawDataPacket)) return;

        if (rawDataPacket.Contains("[CF_ERR]"))
        {
            string currentSavedHost = Preferences.Default.Get("CloudflareHostKey", "silent-bird-d9c0.taigon1984.workers.dev");
            string currentSavedClientId = Preferences.Default.Get("CloudflareClientIdKey", "PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE");
            string currentSavedSecret = Preferences.Default.Get("CloudflareClientSecretKey", "PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE");

            if (string.IsNullOrEmpty(currentSavedHost) || currentSavedHost.Equals("silent-bird-d9c0.taigon1984.workers.dev") ||
                string.IsNullOrEmpty(currentSavedClientId) || currentSavedClientId.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE") ||
                string.IsNullOrEmpty(currentSavedSecret) || currentSavedSecret.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE"))
            {
                Debug.WriteLine("--> [UI FILTER]: Cloudflare exception caught, but credentials match factory defaults. Suppressing alert.");
                return;
            }

            string cleanErrorMessage = "Unknown Network Exception Caught over Airwaves.";
            if (rawDataPacket.Contains("AUTH_REJECTED"))
                cleanErrorMessage = "Cloudflare Zero-Trust Access denied the handshake.\n\nPlease verify that your 'Client ID' and 'Client Secret' match your Service Credentials exactly.";
            else if (rawDataPacket.Contains("WORKER_NOT_FOUND"))
                cleanErrorMessage = "The custom DNS Hostname could not be resolved.\n\nPlease verify your worker name endpoint URL string (e.g. silent-bird-...).";
            else if (rawDataPacket.Contains("DNS_UNREACHABLE"))
                cleanErrorMessage = "The microcontroller cannot connect to the server.\n\nVerify that the vehicle module has an active data/hotspot network connection.";

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                bool userClickedFix = await DisplayAlertAsync("🚨 CLOUDFLARE CONFIG ERROR",
                    $"Your vehicle module reported a WAN tunnel connection failure:\n\n{cleanErrorMessage}",
                    "FIX CONFIG", "CANCEL");

                if (userClickedFix)
                {
                    Debug.WriteLine("--> [UI INTENT ROUTER]: Driver requested configuration fix. Pushing AdminPage view...");
                    await Navigation.PushAsync(new AdminPage());
                }
            });
            return;
        }

        if (!rawDataPacket.Trim().StartsWith("{") && App.NetworkService.IsUsingWifiTransportMode)
        {
            Debug.WriteLine("--> [TRANSPORTS HARDWARE INTELLIGENCE]: Valid BLE string stream caught. Forcefully overriding stuck Wi-Fi preferences!");

            UpdateBluetoothStatusBadge(isConnected: true);
            return;
        }

        if (rawDataPacket.Contains("SECURITY WARN") || rawDataPacket.Contains("Hash mismatch") || rawDataPacket.Contains("401") || rawDataPacket.Contains("Unauthorized"))
        {
            Debug.WriteLine("--> [PARSER SECURITY RADAR]: Encryption key mismatch caught over radio waves! Enforcing passcode input overlay rendering pass...");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (layoutPasswordInitShell != null && !layoutPasswordInitShell.IsVisible)
                {
                    layoutPasswordInitShell.IsVisible = true;
                    Debug.WriteLine("--> [UI FORCED STATE]: Onboarding password mask deployed successfully via telemetry interceptor.");
                }
            });
            return;
        }

        if (!rawDataPacket.Trim().StartsWith("{") &&
           (rawDataPacket.Contains("AUTH_") || rawDataPacket.Contains("NAME:")))
        {
            return;
        }

        try
        {
            if (rawDataPacket.Trim().StartsWith("{"))
            {
                using (JsonDocument jsonDoc = JsonDocument.Parse(rawDataPacket))
                {
                    var root = jsonDoc.RootElement;

                    float frontVolts = root.TryGetProperty("front_v", out JsonElement fv) ? (float)fv.GetDouble() : 0f;
                    int frontPercent = root.TryGetProperty("front_p", out JsonElement fp) ? fp.GetInt32() : 0;

                    float backVolts = root.TryGetProperty("background_v", out JsonElement bv) ? (float)bv.GetDouble() : 0f;
                    int backPercent = root.TryGetProperty("back_p", out JsonElement bp) ? bp.GetInt32() : 0;

                    bool frontIsCharging = root.TryGetProperty("charging_f", out JsonElement c) && c.GetBoolean();
                    bool backIsCharging = root.TryGetProperty("charging_b", out JsonElement b) && b.GetBoolean();

                    root.TryGetProperty("system_logs", out JsonElement logsNode);

                    if (logsNode.ValueKind == JsonValueKind.Array)
                    {
                        var logBuilder = new System.Text.StringBuilder();

                        foreach (JsonElement individualLine in logsNode.EnumerateArray())
                        {
                            string logText = individualLine.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(logText))
                            {
                                logBuilder.AppendLine(logText);
                            }
                        }

                        string combinedTelemetryString = logBuilder.ToString().TrimEnd();

                        if (!string.IsNullOrEmpty(combinedTelemetryString))
                        {
                            App.NetworkService.RaiseTelemetryReceived(combinedTelemetryString);
                            OnWifiTelemetryParsed?.Invoke(combinedTelemetryString);
                        }
                    }

                    string activeNetworkIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        UpdateDashboardMetrics(
                            frontVolts, frontPercent, frontIsCharging,
                            backVolts, backPercent, backIsCharging
                        );

                        if (!string.IsNullOrEmpty(activeNetworkIP) && activeNetworkIP != "0.0.0.0")
                        {
                            if (lblVehicleIPText != null) lblVehicleIPText.Text = activeNetworkIP;
                            if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = true;
                        }
                    });
                }
                return;
            }

            Debug.WriteLine($"--> [DASHBOARD PARSER INPUT]: Processing BLE Text: {rawDataPacket}");

            if (rawDataPacket.Contains("IP:") && !App.NetworkService.IsUsingWifiTransportMode)
            {
                int ipStartIndex = rawDataPacket.IndexOf("IP:") + 3;
                int ipEndIndex = rawDataPacket.IndexOf("|", ipStartIndex);

                if (ipStartIndex != -1 && ipEndIndex != -1)
                {
                    string extractedVehicleIP = rawDataPacket.Substring(ipStartIndex, ipEndIndex - ipStartIndex).Trim();

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (extractedVehicleIP == "STA_HOTSPOT" && Preferences.Default.Get("LastKnownVehicleIP", string.Empty) != "0.0.0.0")
                        {
                            Debug.WriteLine("--> [FALLBACK ENGINE]: Standalone hotspot detected. Force-aligning UI structures to BLE mode lanes...");

                            if (lblVehicleIPText != null)
                            {
                                lblVehicleIPText.Text = "OFFLINE (Standalone AP Mode)";
                            }
                            if (borderNetworkStatus != null)
                            {
                                borderNetworkStatus.IsVisible = false;
                            }

                            ManageWifiTelemetryPollingLifecycle(startWorker: false);

                            string currentBleName = Preferences.Default.Get(MainPage.SavedDeviceNameKey, "VersaHub_BLE");
                            if (Guid.TryParse(currentBleName, out _) || currentBleName.Contains("-"))
                            {
                                currentBleName = "VersaHub_BLE";
                            }

                            if (borderBleStatus != null)
                            {
                                borderBleStatus.BackgroundColor = Color.Parse("#1A2D20");
                                borderBleStatus.Stroke = Color.Parse("#10B981");
                            }

                            if (lblBleDot != null) lblBleDot.Text = "🟢";

                            if (lblBleStatusText != null)
                            {
                                lblBleStatusText.Text = $"CONNECTED: {currentBleName.ToUpper()}";
                                lblBleStatusText.TextColor = Color.Parse("#10B981");
                            }

                            if (lblActiveTransportChannel != null)
                            {
                                lblActiveTransportChannel.Text = $"TRANSPORT MODE: Low-Latency Bluetooth Channel (BLE) | Signal: {App.NetworkService.ActiveRssi} dBm";
                            }

                            if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = false;
                            if (lblBleSignal != null) lblBleSignal.IsVisible = true;
                            if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;
                        }
                        else if (!string.IsNullOrEmpty(extractedVehicleIP) && extractedVehicleIP != "STA_HOTSPOT")
                        {
                            if (lblVehicleIPText != null) lblVehicleIPText.Text = extractedVehicleIP;
                            if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = true;

                            Preferences.Default.Set("LastKnownVehicleIP", extractedVehicleIP);
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

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateDashboardMetrics(
                        currentFrontVolts, currentFrontPercent, currentFrontIsCharging,
                        currentBackVolts, currentBackPercent, currentBackIsCharging
                    );
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [DASHBOARD PARSER CHOKE]: {ex.Message}");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        KickstartWirelessCockpitSync();

        Debug.WriteLine("--> [DASHBOARD LANDING]: Repainting master layout frames...");

        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
            borderBleStatus.Stroke = Color.Parse("#EF4444");
            lblBleDot.Text = "🔴";
            lblBleStatusText.Text = "VEHICLE MODULE REBOOTING...";
            lblBleStatusText.TextColor = Color.Parse("#EF4444");
            lblBleSignal.Text = string.Empty;

            Debug.WriteLine("--> [UI STATE ALIGNMENT]: Dashboard badge force-shifted to REBOOTING tracking state.");
        }
    }

    private void OnSetupFinished(object sender, EventArgs e)
    {
        initMasterPasswordControl.OnPasswordInitialized -= OnSetupFinished;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            layoutPasswordInitShell.IsVisible = false;
            KickstartWirelessCockpitSync();
        });
    }

    private void KickstartWirelessCockpitSync()
    {
        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            Debug.WriteLine("--> [BOOT SYNC GUARD]: Active reboot watchdog detected. Standing down dashboard autoconnect tasks.");
            return;
        }

        bool isBluetoothHardwareOff = !Plugin.BLE.CrossBluetoothLE.Current.IsOn;
        string storedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
        bool hasNoValidWifiRouteYet = string.IsNullOrEmpty(storedVehicleIP) || storedVehicleIP.Equals("0.0.0.0") || storedVehicleIP.Equals("STA_HOTSPOT");

        if (isBluetoothHardwareOff && hasNoValidWifiRouteYet)
        {
            Debug.WriteLine("--> [CRITICAL OVERRIDE CAUGHT]: Bluetooth hardware is OFF and zero Wi-Fi network routing maps exist on launch!");

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = true;

                if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
                    borderBleStatus.Stroke = Color.Parse("#EF4444");
                }

                if (lblBleDot != null) lblBleDot.Text = "❌";
                if (lblBleSignal != null) lblBleSignal.Text = "SIGNAL DISCONNECTED";
                if (lblBleSignal != null) lblBleSignal.TextColor = Color.Parse("#EF4444");

                if (lblBleStatusText != null)
                {
                    lblBleStatusText.Text = "OFFLINE — RADIO LINK OFF";
                    lblBleStatusText.TextColor = Color.Parse("#EF4444");
                }

                if (lblActiveTransportChannel != null)
                {
                    lblActiveTransportChannel.Text = "TRANSPORT MODE: Halted. Enable Bluetooth or connect to vehicle Wi-Fi.";
                }

                if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = true;

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

                await DisplayAlertAsync(
                    "RADIO RECEIVERS OFF",
                    "VersaHUD cannot locate your vehicle because your phone's Bluetooth is turned OFF and no local vehicle Wi-Fi route has been established yet.\n\nPlease enable Bluetooth in your settings or connect to the console's local network hotspot to start telemetry tracks.",
                    "OK");
            });

            return;
        }

        Task.Run(async () =>
        {
            try
            {
                Debug.WriteLine("--> [BOOT LINK INTERCEPT]: Launching parallel network transport evaluation scan...");

                string cachedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);
                bool wifiRouteIsAvailable = false;

                if (!string.IsNullOrEmpty(cachedVehicleIP) && cachedVehicleIP != "0.0.0.0" && cachedVehicleIP != "STA_HOTSPOT")
                {
                    using (var timeoutTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000))) // Optimized down to 3 seconds
                    {
                        try
                        {
                            var localSocketHandler = new SocketsHttpHandler()
                            {
                                AllowAutoRedirect = true,
                                UseCookies = false
                            };

                            using (var bootWebClient = new System.Net.Http.HttpClient(localSocketHandler))
                            {
                                bootWebClient.Timeout = TimeSpan.FromMilliseconds(3000);
                                string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

                                byte[] secretSharedKeyBytes = new byte[] { 0x5A, 0xA5, 0x1F, 0x2C, 0x7E, 0x9D, 0x8B, 0x34, 0x61, 0xF0, 0xE3, 0xD2, 0xC1, 0xB0, 0x09, 0x48 };
                                byte[] initializationVectorBytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
                                string encryptedBase64PayloadString = "";

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
                                            byte[] plainTextBytes = Encoding.UTF8.GetBytes(activeKey);
                                            cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                                            cryptoStream.FlushFinalBlock();
                                        }
                                        encryptedBase64PayloadString = Convert.ToBase64String(memoryStream.ToArray());
                                    }
                                }

                                var httpPasscodeContent = new StringContent(encryptedBase64PayloadString, System.Text.Encoding.UTF8, "text/plain");
                                var networkResponse = await bootWebClient.PostAsync($"http://{cachedVehicleIP}/api/telemetry", httpPasscodeContent, timeoutTokenSource.Token);

                                if (networkResponse.IsSuccessStatusCode)
                                {
                                    wifiRouteIsAvailable = true;
                                    Debug.WriteLine($"--> [BOOT LINK SUCCESS]: Vehicle node discovered live over Wi-Fi Subnet at http://{cachedVehicleIP}!");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"--> [BOOT WI-FI PROBE EXCEPTION]: Sockets handled dropout cleanly: {ex.Message}");
                        }
                    }
                }

                if (wifiRouteIsAvailable)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        App.NetworkService.IsUsingWifiTransportMode = true;
                        UpdateBluetoothStatusBadge(isConnected: false);
                        await VerifyPasswordAgainstHardwareAsync();
                    });
                    return;
                }

                Debug.WriteLine("--> [BOOT LINK FALLBACK]: Wi-Fi path silent or rejected. Proceeding down native Bluetooth radio channels...");
                bool isReconnected = await App.NetworkService.AutoConnectAsync();

                if (!isReconnected)
                {
                    string targetedMacAddress = Preferences.Default.Get(MainPage.SavedDeviceMacKey, string.Empty);

                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        if (string.IsNullOrEmpty(targetedMacAddress))
                        {
                            Debug.WriteLine("--> [BOOT SYNC]: Zero historical pairings found. Revealing manual picker container.");
                            layoutOverlayShell.IsVisible = true;
                            if (btDevicePicker != null)
                            {
                                if (btnLock != null) btnLock.IsEnabled = false;
                                if (btnUnlock != null) btnUnlock.IsEnabled = false;
                                await btDevicePicker.InitializePickerLifecycleAsync();
                            }
                        }
                        else
                        {
                            Debug.WriteLine("--> [BOOT SYNC]: Historical device found. Suppressing popup and launching background tracking...");
                            layoutOverlayShell.IsVisible = false;

                            borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
                            borderBleStatus.Stroke = Color.Parse("#EF4444");
                            lblBleDot.Text = "🔴";
                            lblBleStatusText.Text = "RECONNECTING TO VEHICLE CORES...";
                            lblBleStatusText.TextColor = Color.Parse("#EF4444");
                            lblBleSignal.Text = string.Empty;

                            if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = true;

                            if (!App.NetworkService.IsRebootingWatchdogActive)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await App.NetworkService.ForceProactiveRebootRecoveryAsync();
                                });
                            }
                        }
                    });
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        App.NetworkService.IsUsingWifiTransportMode = false;
                        await VerifyPasswordAgainstHardwareAsync();
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"--> [BOOT WORKFLOW SHIELD]: {ex.Message}");
            }
        });
    }

    public async Task VerifyPasswordAgainstHardwareAsync()
    {
        string savedPass = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        Action<string> temporaryAuthHandler = null;
        temporaryAuthHandler = (fullTelemetryMessage) =>
        {
            if (string.IsNullOrEmpty(fullTelemetryMessage)) return;
            Debug.WriteLine($"--> [SINGLE-STREAM AUTH INTERCEPTOR]: {fullTelemetryMessage}");

            if (fullTelemetryMessage.Contains("AUTH_SUCCESS") || fullTelemetryMessage.Contains("\"front_v\":") || fullTelemetryMessage.Contains("\"charging\":"))
            {
                App.NetworkService.OnTelemetryReceived -= temporaryAuthHandler;

                Debug.WriteLine("--> [HANDSHAKE SECURED]: Auth validation state cleared successfully!");

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (layoutPasswordInitShell != null && layoutPasswordInitShell.IsVisible)
                    {
                        layoutPasswordInitShell.IsVisible = false;
                        await DisplayAlertAsync("VAULT SYNCED", "Your master passcode has been verified against your vehicle's registers. Security clearance accepted.", "ENTER COCKPIT");
                    }
                    fullTelemetryMessage = string.Empty;
                });
            }
            else if (fullTelemetryMessage.Contains("AUTH_FAILED") || fullTelemetryMessage.Contains("ROUTER_ERROR") || fullTelemetryMessage.Contains("401") || fullTelemetryMessage.Contains("Unauthorized"))
            {
                App.NetworkService.OnTelemetryReceived -= temporaryAuthHandler;

                Debug.WriteLine("--> [HANDSHAKE REJECTED]: Auth failed token caught. Displaying single alert prompt...");

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (layoutPasswordInitShell != null)
                    {
                        layoutPasswordInitShell.IsVisible = true;
                    }

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
                    fullTelemetryMessage = string.Empty;
                });
            }
        };

        App.NetworkService.OnTelemetryReceived -= temporaryAuthHandler;
        App.NetworkService.OnTelemetryReceived += temporaryAuthHandler;

        bool isSentSuccessfully = await App.NetworkService.SendSecureCommandAsync(savedPass, "VERIFYPASS");

        if (isSentSuccessfully && App.NetworkService.IsUsingWifiTransportMode)
        {
            App.NetworkService.OnTelemetryReceived -= temporaryAuthHandler;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (layoutPasswordInitShell != null && layoutPasswordInitShell.IsVisible)
                {
                    layoutPasswordInitShell.IsVisible = false;
                    await DisplayAlertAsync("VAULT SYNCED", "Your master passcode has been verified against your vehicle's registers via local Wi-Fi. Security clearance accepted.", "ENTER COCKPIT");
                }
            });
        }
    }

    private void UpdateBluetoothStatusBadge(bool isConnected)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            int currentRssiValue = App.NetworkService.ActiveRssi;

            var activeNetworkProfileAccess = Connectivity.Current.NetworkAccess;
            bool phoneHasActiveWifiRadioLink = activeNetworkProfileAccess == NetworkAccess.Internet &&
                                               Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

            if (App.NetworkService != null && App.NetworkService.IsUsingWifiTransportMode)
            {
                Debug.WriteLine("--> [UI GATEWAY]: Active Wi-Fi transport lane is confirmed running. Suppressing accidental zero-out sweeps.");

                ExecuteWifiThemeRedrawPass();
                return;
            }

            if (!isConnected)
            {
                Debug.WriteLine("--> [DASHBOARD COCKPIT DETACH]: All channels severed. Safe shutdown zero-out activated...");

                ManageWifiTelemetryPollingLifecycle(startWorker: false);

                if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
                    borderBleStatus.Stroke = Color.Parse("#EF4444");
                }

                if (lblBleDot != null) lblBleDot.Text = "❌";
                if (lblBleSignal != null) { lblBleSignal.Text = "SIGNAL DISCONNECTED"; lblBleSignal.TextColor = Color.Parse("#EF4444"); }

                if (lblBleStatusText != null) { lblBleStatusText.Text = "OFFLINE — COCKPIT LINK LOST"; lblBleStatusText.TextColor = Color.Parse("#EF4444"); }
                if (lblActiveTransportChannel != null) lblActiveTransportChannel.Text = "TRANSPORT MODE: Disconnected Fallback State Channels Active.";
                if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = true;

                if (btnLock != null) btnLock.IsEnabled = false;
                if (btnUnlock != null) btnUnlock.IsEnabled = false;

                // 🎯 Clean, safe, zero-out pass execution (Guaranteed flicker-proof!)
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

                return;
            }

            if (!phoneHasActiveWifiRadioLink && App.NetworkService.IsUsingWifiTransportMode)
            {
                Debug.WriteLine("--> [HARDWARE RADAR RECOVERY]: Phone Wi-Fi adapter disabled. Force-dropping transport lane preferences back to BLE...");
                App.NetworkService.IsUsingWifiTransportMode = false;
            }

            if (!isConnected && !App.NetworkService.IsUsingWifiTransportMode)
            {
                Debug.WriteLine("--> [DASHBOARD COCKPIT DETACH]: Both transport networks are completely OFFLINE. Initializing absolute zero-out reset passes...");

                ManageWifiTelemetryPollingLifecycle(startWorker: false);

                if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A");
                    borderBleStatus.Stroke = Color.Parse("#EF4444");
                }

                if (lblBleDot != null) lblBleDot.Text = "❌";
                if (lblBleSignal != null) lblBleSignal.Text = "SIGNAL DISCONNECTED";
                if (lblBleSignal != null) lblBleSignal.TextColor = Color.Parse("#EF4444");

                if (lblBleStatusText != null)
                {
                    lblBleStatusText.Text = "OFFLINE - LINK LOST";
                    lblBleStatusText.TextColor = Color.Parse("#EF4444");
                }

                if (lblActiveTransportChannel != null)
                {
                    lblActiveTransportChannel.Text = "TRANSPORT MODE: Disconnected Fallback State Channels Active.";
                }

                if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = true;

                if (btnLock != null) btnLock.IsEnabled = false;
                if (btnUnlock != null) btnUnlock.IsEnabled = false;

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

                return;
            }

            if (phoneHasActiveWifiRadioLink && App.NetworkService.IsUsingWifiTransportMode)
            {
                string cachedIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");

                if (!string.IsNullOrEmpty(cachedIP) && cachedIP != "0.0.0.0")
                {
                    if (lblBleSignal != null) lblBleSignal.IsVisible = false;
                    if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = false;
                    if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;

                    if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = true;
                    if (lblVehicleIPText != null) lblVehicleIPText.Text = cachedIP;

                    if (borderBleStatus != null)
                    {
                        borderBleStatus.BackgroundColor = Color.Parse("#1A242D");
                        borderBleStatus.Stroke = Color.Parse("#3B82F6");
                    }

                    if (lblBleDot != null) lblBleDot.Text = "🌐";

                    if (lblBleStatusText != null)
                    {
                        lblBleStatusText.Text = "LOCAL WI-FI SUBNET ONLINE";
                        lblBleStatusText.TextColor = Color.Parse("#3B82F6");
                    }

                    if (lblActiveTransportChannel != null)
                    {
                        lblActiveTransportChannel.Text = $"TRANSPORT MODE: REST API LINK ({cachedIP})";
                    }

                    if (btnLock != null) btnLock.IsEnabled = true;
                    if (btnUnlock != null) btnUnlock.IsEnabled = true;

                    return;
                }
            }

            if (isConnected && !App.NetworkService.IsUsingWifiTransportMode)
            {
                string currentBleName = Preferences.Default.Get(MainPage.SavedDeviceNameKey, "VersaHub_BLE");
                if (Guid.TryParse(currentBleName, out _) || currentBleName.Contains("-")) currentBleName = "VersaHub_BLE";

                if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#1A2D20");
                    borderBleStatus.Stroke = Color.Parse("#10B981");
                }

                if (lblBleDot != null) lblBleDot.Text = "🟢";

                if (lblBleStatusText != null)
                {
                    lblBleStatusText.Text = $"CONNECTED: {currentBleName.ToUpper()}";
                    lblBleStatusText.TextColor = Color.Parse("#10B981");
                }

                if (lblActiveTransportChannel != null)
                {
                    lblActiveTransportChannel.Text = $"TRANSPORT MODE: Low-Latency Bluetooth Channel (BLE) | Signal: {currentRssiValue} dBm";
                }

                if (btnLock != null) btnLock.IsEnabled = true;
                if (btnUnlock != null) btnUnlock.IsEnabled = true;

                if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = false;
                if (lblBleSignal != null) lblBleSignal.IsVisible = true;
                if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;
            }
        });
    }

    private void UpdateWirelessSignalBars(int rssi)
    {
        MainThread.BeginInvokeOnMainThread(() =>
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

    private async void OnLockClicked(object sender, EventArgs e)
    {
        try
        {
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            Debug.WriteLine("--> [UI CONTROL]: Dispatching secure over-the-air LOCK token packet...");

            _ = Task.Run(async () =>
            {
                await App.NetworkService.SendSecureCommandAsync(activeKey, "LOCK");
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [LOCK UI CHOKE]: {ex.Message}");
        }
    }

    private async void OnUnlockClicked(object sender, EventArgs e)
    {
        try
        {
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            Debug.WriteLine("--> [UI CONTROL]: Dispatching secure over-the-air UNLOCK token packet...");

            _ = Task.Run(async () =>
            {
                await App.NetworkService.SendSecureCommandAsync(activeKey, "UNLOCK");
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [UNLOCK UI CHOKE]: {ex.Message}");
        }
    }

    private void UpdateDashboardMetrics(float frontVolts, int frontPercent, bool frontIsCharging, float backVolts, int backPercent, bool backIsCharging)
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
    }

    private async void OnAdminNavigationClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new AdminPage());
    }

    private async void OnRollbackConnectionAndRescan(object sender, EventArgs e)
    {
        try
        {
            Debug.WriteLine("--> [RECOVERY HUB]: Wrong device selected. Executing wireless reset line...");
            if (App.NetworkService != null) await App.NetworkService.DisconnectCurrentDeviceAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                layoutPasswordInitShell.IsVisible = false;
                if (initMasterPasswordControl != null)
                {
                    var entryField = initMasterPasswordControl.FindByName<Entry>("entryInitialPass");
                    if (entryField != null) entryField.Text = string.Empty;
                }
                layoutOverlayShell.IsVisible = true;
                if (btnLock != null) btnLock.IsEnabled = false;
                if (btnUnlock != null) btnUnlock.IsEnabled = false;
                if (btDevicePicker != null) _ = btDevicePicker.InitializePickerLifecycleAsync();
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [RECOVERY EXCEPTION SHIELD]: {ex.Message}");
        }
    }

    private void OnClosePickerOverlayClicked(object sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (layoutOverlayShell != null)
            {
                layoutOverlayShell.IsVisible = false;
                Debug.WriteLine("--> [UI CONTROL]: Device selection picker overlay hidden cleanly.");
            }
        });
    }

    private async void OnManualScanTriggerClicked(object sender, EventArgs e)
    {
        try
        {
            Debug.WriteLine("--> [UI CONTROL]: User requested manual scan refresh pass...");

            bool isPermissionApproved = false;

#if ANDROID
            var nativeAndroidContext = Android.App.Application.Context;
            bool hasNativeScanClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothScan) == Android.Content.PM.Permission.Granted;
            bool hasNativeConnectClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) == Android.Content.PM.Permission.Granted;

            if (!hasNativeScanClearance || !hasNativeConnectClearance)
            {
                Debug.WriteLine("--> [WATCHDOG]: System token validation missing. Requesting dynamic hardware tracking permissions...");
                var forcedStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();
                isPermissionApproved = (forcedStatus == PermissionStatus.Granted);
            }
            else
            {
                isPermissionApproved = true;
            }
#else
            var fallbackStatus = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();
            isPermissionApproved = (fallbackStatus == PermissionStatus.Granted);
#endif

            bool isRadioHardwareActive = Plugin.BLE.CrossBluetoothLE.Current.IsOn;

            if (isPermissionApproved && isRadioHardwareActive)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = true;

                    if (btDevicePicker != null)
                    {
                        Debug.WriteLine("--> [HARDWARE MONITOR]: Forcing active device list reset sweep over radio waves...");
                        if (btnLock != null) btnLock.IsEnabled = false;
                        if (btnUnlock != null) btnUnlock.IsEnabled = false;
                        await btDevicePicker.InitializePickerLifecycleAsync();
                    }
                });
            }
            else
            {
                Debug.WriteLine("--> [CRITICAL SELECTION BLOCK]: Refresh scan blocked. Permission Approved: " + isPermissionApproved + " | Radio Active: " + isRadioHardwareActive);
                await DisplayAlertAsync("BLUETOOTH REQUIRED", "VersaHUD cannot execute a visual radar refresh scan because your phone's Bluetooth radio switch is turned OFF or permissions were denied.\n\nPlease ensure Bluetooth is active in your drop-down panel and try again.", "OK");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [MANUAL SCAN TRIGGER FAULT]: Dynamic security check failed: {ex.Message}");
        }
    }

    public void RaiseWifiTelemetryParsedEventProxy(string synchronizedPacketText)
    {
        if (string.IsNullOrEmpty(synchronizedPacketText)) return;

        ParseVehicleTelemetryStream(synchronizedPacketText);
    }

    private void OnTransportChannelShiftRepaint(bool isWifiActive)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Debug.WriteLine($"--> [UI INTENT OVERRIDE]: Transport event captured. Wi-Fi Active: {isWifiActive}. Repainting console cluster panels...");

            if (isWifiActive)
            {
                if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;

                UpdateBluetoothStatusBadge(isConnected: false);

                Debug.WriteLine("--> [UI INTENT OVERRIDE SUCCESS]: Cockpit interface successfully unlocked over local Wi-Fi subnet!");
            }
        });
    }

    private void OnRefreshScanClicked(object sender, EventArgs e)
    {
        btDevicePicker.TriggerRefreshScan();
    }

    private void ExecuteWifiThemeRedrawPass()
    {
        string cachedIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
        if (string.IsNullOrEmpty(cachedIP) || cachedIP == "0.0.0.0") return;

        if (lblBleSignal != null) lblBleSignal.IsVisible = false;
        if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = false;
        if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;

        if (btnLock != null) btnLock.IsEnabled = true;
        if (btnUnlock != null) btnUnlock.IsEnabled = true;

        if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = true;
        if (lblVehicleIPText != null) lblVehicleIPText.Text = cachedIP;

        if (borderBleStatus != null)
        {
            borderBleStatus.BackgroundColor = Color.Parse("#1A242D");
            borderBleStatus.Stroke = Color.Parse("#3B82F6");
        }

        if (lblBleDot != null) lblBleDot.Text = "🌐";
        if (lblBleStatusText != null) { lblBleStatusText.Text = "LOCAL WI-FI SUBNET ONLINE"; lblBleStatusText.TextColor = Color.Parse("#3B82F6"); }
        if (lblActiveTransportChannel != null) lblActiveTransportChannel.Text = $"TRANSPORT MODE: REST API LINK ({cachedIP})";

        ManageWifiTelemetryPollingLifecycle(startWorker: true);
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
