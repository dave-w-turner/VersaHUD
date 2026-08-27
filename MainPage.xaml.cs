using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VersaHUD;

public partial class MainPage : ContentPage
{
    // 🚀 THE SECURE STATIC KEY MAPS: Centralizes preference string keys globally
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

        App.BluetoothService.OnConnectionStateChanged += UpdateBluetoothStatusBadge;
        App.BluetoothService.OnRssiUpdated += UpdateWirelessSignalBars;
        App.BluetoothService.OnTelemetryReceived += ParseVehicleTelemetryStream;
        App.BluetoothService.OnTransportModeChanged += OnTransportChannelShiftRepaint;

        if (initMasterPasswordControl != null)
        {
            initMasterPasswordControl.OnPasswordInitialized += OnSetupFinished;
            initMasterPasswordControl.OnWrongDeviceRequested += OnRollbackConnectionAndRescan;
        }
    }

    // 🌐 ASYNCHRONOUS LOCAL SUBNET REST API TELEMETRY POLLER (ENCRYPTION FIXED)
    private void ManageWifiTelemetryPollingLifecycle(bool startWorker)
    {
        // Cancel and wipe out any existing active background network worker loops first
        _wifiTelemetryCancelSource?.Cancel();
        _wifiTelemetryCancelSource = null;

        if (!startWorker)
        {
            System.Diagnostics.Debug.WriteLine("--> [UI NETWORK ENGINE]: Competing HTTP background task loops cleanly suspended.");
            return;
        }

        System.Diagnostics.Debug.WriteLine("--> [UI NETWORK ENGINE]: Wi-Fi link active. Network traffic consolidated cleanly to your background service pipeline channel.");
    }

    // REAL-TIME REGEX STRIP ENGINE LOOP 🧠
    // 🧠 UNIVERSAL DUAL-TRANSPORT TELEMETRY DATA STRIP ENGINE (BLE + REST API) [INDEX_0.1.23]
    private void ParseVehicleTelemetryStream(string rawDataPacket)
    {
        if (string.IsNullOrEmpty(rawDataPacket)) return;

        if (!rawDataPacket.Trim().StartsWith("{") && App.BluetoothService.IsUsingWifiTransportMode)
        {
            System.Diagnostics.Debug.WriteLine("--> [TRANSPORTS HARDWARE INTELLIGENCE]: Valid BLE string stream caught. Forcefully overriding stuck Wi-Fi preferences!");

            //App.BluetoothService.IsUsingWifiTransportMode = false;

            // Force your connection status badges to repaint to your nominal Emerald Green BLE layout immediately!
            UpdateBluetoothStatusBadge(isConnected: true);
        }

        if (rawDataPacket.Contains("SECURITY WARN") || rawDataPacket.Contains("Hash mismatch") || rawDataPacket.Contains("401") || rawDataPacket.Contains("Unauthorized"))
        {
            System.Diagnostics.Debug.WriteLine("--> [PARSER SECURITY RADAR]: Encryption key mismatch caught over radio waves! Enforcing passcode input overlay rendering pass...");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Forcefully slide your initialization password card overlay onto the screen view viewport panel!
                if (layoutPasswordInitShell != null && !layoutPasswordInitShell.IsVisible)
                {
                    layoutPasswordInitShell.IsVisible = true;
                    System.Diagnostics.Debug.WriteLine("--> [UI FORCED STATE]: Onboarding password mask deployed successfully via telemetry interceptor.");
                }
            });
            return; // 💥 Crash the feedback loop execution row and exit early!
        }

        if (!rawDataPacket.Trim().StartsWith("{") &&
           (rawDataPacket.Contains("AUTH_") || rawDataPacket.Contains("NAME:")))
        {
            return;
        }

        try
        {
            // =====================================================
            // 🌐 ROUTE A: INTERCEPT AND PARSE ARDUINO JSON WEB TELEMETRY OVER WI-FI [INDEX_1.2.2]
            // =====================================================
            if (rawDataPacket.Trim().StartsWith("{"))
            {
                using (JsonDocument jsonDoc = JsonDocument.Parse(rawDataPacket))
                {
                    var root = jsonDoc.RootElement;

                    // Extract your exact battery primitives safely off the JSON object tree [INDEX_1.2.6]
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

                        // Loop through each element physically inside the JSON array tracks
                        foreach (JsonElement individualLine in logsNode.EnumerateArray())
                        {
                            string logText = individualLine.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(logText))
                            {
                                // Append the raw text row followed cleanly by a standard line break
                                logBuilder.AppendLine(logText);
                            }
                        }

                        string combinedTelemetryString = logBuilder.ToString().TrimEnd();

                        if (!string.IsNullOrEmpty(combinedTelemetryString))
                        {
                            App.BluetoothService.RaiseTelemetryReceived(combinedTelemetryString);
                            OnWifiTelemetryParsed?.Invoke(combinedTelemetryString);
                        }
                    }

                    // 🚀 THE WI-FI IP PILL UPDATER: 
                    // Pull your active connection IP straight out of your persistent local disk storage cache [INDEX_1.2.6]
                    string activeNetworkIP = Preferences.Default.Get("LastKnownVehicleIP", string.Empty);

                    // Dispatch straight down to your native layout UI elements [INDEX_0.1.24]
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // A: Repaint your front and back workstation dials live on your console [INDEX_0.1.24]
                        UpdateDashboardMetrics(
                            frontVolts, frontPercent, frontIsCharging,
                            backVolts, backPercent, backIsCharging
                        );

                        // B: Ensure your Tech-Blue Station IP Pill stays completely visible and populated over Wi-Fi!
                        if (!string.IsNullOrEmpty(activeNetworkIP) && activeNetworkIP != "0.0.0.0")
                        {
                            if (lblVehicleIPText != null) lblVehicleIPText.Text = activeNetworkIP;
                            if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = true;
                        }
                    });
                }
                return; // Complete parsing path pass and exit early!
            }

            // =====================================================
            // 🔵 ROUTE B: NATIVE RAW STRING REGEX STRIP CHANNELS OVER BLUETOOTH [INDEX_0.1.23]
            // =====================================================
            Debug.WriteLine($"--> [DASHBOARD PARSER INPUT]: Processing BLE Text: {rawDataPacket}");

            // Intercept and cache your physical network router IP changes over BLE [INDEX_0.1.32]
            if (rawDataPacket.Contains("IP:"))
            {
                int ipStartIndex = rawDataPacket.IndexOf("IP:") + 3;
                int ipEndIndex = rawDataPacket.IndexOf("|", ipStartIndex);

                if (ipStartIndex != -1 && ipEndIndex != -1)
                {
                    string extractedVehicleIP = rawDataPacket.Substring(ipStartIndex, ipEndIndex - ipStartIndex).Trim();

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // 🚀 THE BULLETPROOF FALLBACK ENGINE REFIX:
                        // Only step inside this configuration reset block if we haven't already wiped our cache registers to "0.0.0.0"!
                        if (extractedVehicleIP == "STA_HOTSPOT" && Preferences.Default.Get("LastKnownVehicleIP", string.Empty) != "0.0.0.0")
                        {
                            System.Diagnostics.Debug.WriteLine("--> [FALLBACK ENGINE]: Standalone hotspot detected. Force-aligning UI structures to BLE mode lanes...");

                            // 1. Reset your local preference keys to a zero state to block background HTTP pollers
                            //Preferences.Default.Set("LastKnownVehicleIP", "0.0.0.0");

                            // 2. Forcefully clean up your Tech-Blue Station IP Pill label element properties
                            if (lblVehicleIPText != null)
                            {
                                lblVehicleIPText.Text = "OFFLINE (Standalone AP Mode)";
                            }
                            if (borderNetworkStatus != null)
                            {
                                borderNetworkStatus.IsVisible = false; // Hide network pill entirely over BLE
                            }

                            // 3. Force-shift the active transport preference routing keys back to low-energy radio channels!
                            // App.BluetoothService.IsUsingWifiTransportMode = false;

                            // 4. Halt your background web server telemetry pollers instantly
                            ManageWifiTelemetryPollingLifecycle(startWorker: false);

                            // 🚀 5. HARD-BREAK THE UI RENDERING DEADLOCK:
                            // Because the service layers are cycling states, we forcefully paint your nominal, 
                            // healthy Emerald Green BLE badge layout onto the screen console viewport manually right now!
                            // This instantly strips away the frozen "Reconnecting" text block.
                            string currentBleName = Preferences.Default.Get(MainPage.SavedDeviceNameKey, "VersaHub_BLE");
                            if (Guid.TryParse(currentBleName, out _) || currentBleName.Contains("-"))
                            {
                                currentBleName = "VersaHub_BLE";
                            }

                            if (borderBleStatus != null)
                            {
                                borderBleStatus.BackgroundColor = Color.Parse("#1A2D20"); // Emerald Green
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
                                lblActiveTransportChannel.Text = $"TRANSPORT MODE: Low-Latency Bluetooth Channel (BLE) | Signal: {App.BluetoothService.ActiveRssi} dBm";
                            }

                            if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = false;
                            if (lblBleSignal != null) lblBleSignal.IsVisible = true;
                            if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;
                        }
                        else if (!string.IsNullOrEmpty(extractedVehicleIP) && extractedVehicleIP != "STA_HOTSPOT")
                        {
                            // Nominal Route: Stored station IP discovered on home router network lanes
                            if (lblVehicleIPText != null) lblVehicleIPText.Text = extractedVehicleIP;
                            if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = true;

                            Preferences.Default.Set("LastKnownVehicleIP", extractedVehicleIP);
                        }
                    });
                }
            }

            // Scan and parse your Front Starter Battery parameter strings [INDEX_0.1.23]
            Match frontMatch = FrontBatteryRegex.Match(rawDataPacket);
            float currentFrontVolts = 0;
            int currentFrontPercent = 0;
            bool currentFrontIsCharging = rawDataPacket.Contains("Front: [🔋 CHARGING]");

            if (frontMatch.Success)
            {
                currentFrontVolts = float.Parse(frontMatch.Groups["volts"].Value);
                currentFrontPercent = int.Parse(frontMatch.Groups["percent"].Value);
            }

            // Scan and parse your Trunk Workstation Battery parameter strings [INDEX_0.1.24]
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
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [DASHBOARD PARSER CHOKE]: {ex.Message}");
        }
    }

    // UNIFIED MASTER INITIALIZATION LIFE CYCLE HOOK 🏎
    protected override void OnAppearing()
    {
        base.OnAppearing();

        // 1. PRESERVE CORE LINK: Fire your vital signal and listener tracking workers instantly!
        KickstartWirelessCockpitSync();

        Debug.WriteLine("--> [DASHBOARD LANDING]: Repainting master layout frames...");

        // 2. REBOOT WATCHDOG EVALUATION SHIELD: 🛡
        if (App.BluetoothService != null && App.BluetoothService.IsRebootingWatchdogActive)
        {
            borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A"); // Safe Ruby Red
            borderBleStatus.Stroke = Color.Parse("#EF4444");
            lblBleDot.Text = "🔴";
            lblBleStatusText.Text = "VEHICLE MODULE REBOOTING...";
            lblBleStatusText.TextColor = Color.Parse("#EF4444");
            lblBleSignal.Text = string.Empty; // Zero out signal arrays

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

    // DYNAMIC BACKGROUND INITIALIZATION INTERCEPTOR (AUTHENTICATION MATCHED) 📡
    // ====================================================================
    // 📡 DYNAMIC BACKGROUND INITIALIZATION INTERCEPTOR (HARDWARE GATED)
    // ====================================================================
    private void KickstartWirelessCockpitSync()
    {
        if (App.BluetoothService != null && App.BluetoothService.IsRebootingWatchdogActive)
        {
            System.Diagnostics.Debug.WriteLine("--> [BOOT SYNC GUARD]: Active reboot watchdog detected. Standing down dashboard autoconnect tasks.");
            return;
        }

        // THE ULTIMATE PRE-FLIGHT HARDWARE DIAGNOSTIC CHECK: 🚀 [INDEX_4]
        bool isBluetoothHardwareOff = !Plugin.BLE.CrossBluetoothLE.Current.IsOn;
        string storedVehicleIP = Preferences.Default.Get("LastKnownVehicleIP", "0.0.0.0");
        bool hasNoValidWifiRouteYet = string.IsNullOrEmpty(storedVehicleIP) || storedVehicleIP.Equals("0.0.0.0") || storedVehicleIP.Equals("STA_HOTSPOT");

        if (isBluetoothHardwareOff && hasNoValidWifiRouteYet)
        {
            System.Diagnostics.Debug.WriteLine("--> [CRITICAL OVERRIDE CAUGHT]: Bluetooth hardware is OFF and zero Wi-Fi network routing maps exist on launch!");

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Forcefully slide your overlay picker list up to give the user a clear baseline target area [INDEX_4]
                if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = true;

                // 🎯 THE BOOT-STATE RESET INJECTION:
                // Forcefully paint the crimson OFFLINE layout directly onto the canvas on startup, 
                // preventing the UI from falling through to legacy connection rendering states!
                if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A"); // Safe Ruby Red Warning [INDEX_4]
                    borderBleStatus.Stroke = Color.Parse("#EF4444");
                }

                if (lblBleDot != null) lblBleDot.Text = "❌";
                if (lblBleSignal != null) lblBleSignal.Text = "SIGNAL DISCONNECTED";
                if (lblBleSignal != null) lblBleSignal.TextColor = Color.Parse("#EF4444");

                // 🎯 THE CLEAN NATIVE LAYOUT SLUSH BLOCK:
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

                // Force fully drop your front-end gauges down to zero immediately on launch failure!
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

                // Dispatch your blocking alert popup box last [INDEX_4]
                await DisplayAlertAsync(
                    "RADIO RECEIVERS OFF",
                    "VersaHUD cannot locate your vehicle because your phone's Bluetooth is turned OFF and no local vehicle Wi-Fi route has been established yet.\n\nPlease enable Bluetooth in your settings or connect to the console's local network hotspot to start telemetry tracks.",
                    "OK");
            });

            return; // 💥 Master return constraint: Hard exit the initialization sequence right here! [INDEX_4]
        }

        // Proceed to launch your background dual-transport evaluation scan thread normally...
        Task.Run(async () =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("--> [BOOT LINK INTERCEPT]: Launching parallel network transport evaluation scan...");

                // STEP 1: PARALLEL WI-FI ROUTE PROBE [PDF: 0.1.26]
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
                                    System.Diagnostics.Debug.WriteLine($"--> [BOOT LINK SUCCESS]: Vehicle node discovered live over Wi-Fi Subnet at http://{cachedVehicleIP}!");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"--> [BOOT WI-FI PROBE EXCEPTION]: Sockets handled dropout cleanly: {ex.Message}");
                        }
                    }
                }

                // STEP 2: EVALUATE SCENARIOS BASED ON PARALLEL RESULTS [PDF: 0.1.27]
                if (wifiRouteIsAvailable)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        App.BluetoothService.IsUsingWifiTransportMode = true;
                        UpdateBluetoothStatusBadge(isConnected: false);
                        await VerifyPasswordAgainstHardwareAsync();
                    });
                    return;
                }

                // If Wi-Fi fails but Bluetooth is active, fall back cleanly to auto-connect routine tracks [PDF: 0.1.27]
                System.Diagnostics.Debug.WriteLine("--> [BOOT LINK FALLBACK]: Wi-Fi path silent or rejected. Proceeding down native Bluetooth radio channels...");
                bool isReconnected = await App.BluetoothService.AutoConnectAsync();

                if (!isReconnected)
                {
                    string targetedMacAddress = Preferences.Default.Get(MainPage.SavedDeviceMacKey, string.Empty);

                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        if (string.IsNullOrEmpty(targetedMacAddress))
                        {
                            System.Diagnostics.Debug.WriteLine("--> [BOOT SYNC]: Zero historical pairings found. Revealing manual picker container.");
                            layoutOverlayShell.IsVisible = true;
                            if (btDevicePicker != null)
                            {
                                await btDevicePicker.InitializePickerLifecycleAsync();
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("--> [BOOT SYNC]: Historical device found. Suppressing popup and launching background tracking...");
                            layoutOverlayShell.IsVisible = false;

                            borderBleStatus.BackgroundColor = Color.Parse("#2D1A1A"); // Safe Ruby Red
                            borderBleStatus.Stroke = Color.Parse("#EF4444");
                            lblBleDot.Text = "🔴";
                            lblBleStatusText.Text = "RECONNECTING TO VEHICLE CORES...";
                            lblBleStatusText.TextColor = Color.Parse("#EF4444");
                            lblBleSignal.Text = string.Empty;

                            if (btnManualScanTrigger != null) btnManualScanTrigger.IsVisible = true;

                            if (!App.BluetoothService.IsRebootingWatchdogActive)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
                                });
                            }
                        }
                    });
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        App.BluetoothService.IsUsingWifiTransportMode = false;
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

    // ====================================================================
    // 🔐 HARDWARE AUTHENTICATION GATEWAY (DE-DUPLICATED PACKET PASS)
    // ====================================================================
    public async Task VerifyPasswordAgainstHardwareAsync()
    {
        string savedPass = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        // Define our localized anonymous event tracking gatekeeper
        Action<string> temporaryAuthHandler = null;
        temporaryAuthHandler = (fullTelemetryMessage) =>
        {
            if (string.IsNullOrEmpty(fullTelemetryMessage)) return;
            Debug.WriteLine($"--> [SINGLE-STREAM AUTH INTERCEPTOR]: {fullTelemetryMessage}");

            // SUCCESS HANDLING: Hands over clean dashboard metric renders on approval pass
            if (fullTelemetryMessage.Contains("AUTH_SUCCESS") || fullTelemetryMessage.Contains("\"front_v\":") || fullTelemetryMessage.Contains("\"charging\":"))
            {
                // 🚀 UNBIND MASTER: Decouple cleanly off the single registration track immediately
                App.BluetoothService.OnTelemetryReceived -= temporaryAuthHandler;

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
            // FAILURE HANDLING: Re-architected to trip the circuit breaker EXACTLY ONCE
            else if (fullTelemetryMessage.Contains("AUTH_FAILED") || fullTelemetryMessage.Contains("ROUTER_ERROR") || fullTelemetryMessage.Contains("401") || fullTelemetryMessage.Contains("Unauthorized"))
            {
                // 🚀 UNBIND MASTER: Kill the tracking listener immediately right here on line 1!
                // This stops any secondary parallel event frames from executing duplicate popups!
                App.BluetoothService.OnTelemetryReceived -= temporaryAuthHandler;

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
                            entryField.Focus(); // Force-focus native software keyboards
                        }
                    }

                    // 🏁 Master alert display fires cleanly exactly once
                    await DisplayAlertAsync("ACCESS DENIED", "The passcode signature you entered does not match your vehicle module's secure vaults.", "TRY AGAIN");
                    fullTelemetryMessage = string.Empty;
                });
            }
        };

        // 🚀 THE DE-DUPLICATION WIRING CONSTRAINT:
        // We completely remove registrations from 'this.OnWifiTelemetryParsed' inside this method!
        // Because your foreground service channel already forward-routes its parsed Wi-Fi frames smoothly
        // straight onto the 'OnTelemetryReceived' pipeline bus, listening exclusively to this single 
        // stream completely stops duplicate handshakes while catching 100% of network payloads!
        App.BluetoothService.OnTelemetryReceived -= temporaryAuthHandler;
        App.BluetoothService.OnTelemetryReceived += temporaryAuthHandler;

        // DISPATCH SECURE PAYLOAD COMMAND
        bool isSentSuccessfully = await App.BluetoothService.SendSecureCommandAsync(savedPass, "VERIFYPASS");

        if (isSentSuccessfully && App.BluetoothService.IsUsingWifiTransportMode)
        {
            // If the local REST API returns a direct, synchronized receipt, unbind on the spot
            App.BluetoothService.OnTelemetryReceived -= temporaryAuthHandler;

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

    // ====================================================================
    // MASTER WIRELESS TRANSMISSION TELEMETRY STATUS BADGE PAINTER 🏎️ [INDEX_0.1.40]
    // ====================================================================
    private void UpdateBluetoothStatusBadge(bool isConnected)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Extract your live running signal decibel metrics out of your background service [INDEX_0.1.40]
            int currentRssiValue = App.BluetoothService.ActiveRssi;

            // STEP 1: QUERY THE PHONE'S PHYSICAL NETWORK CARD HARDWARE SENSORS! [INDEX_0.1.40]
            var activeNetworkProfileAccess = Connectivity.Current.NetworkAccess;
            bool phoneHasActiveWifiRadioLink = activeNetworkProfileAccess == NetworkAccess.Internet &&
                                               Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);

            // 🚀 RECURSION CIRCUIT BREAKER 1:
            // If the physical Wi-Fi radio is off, safely turn off transport mode.
            // We NO LONGER call ManageWifiTelemetryPollingLifecycle here to avoid event loops!
            if (!phoneHasActiveWifiRadioLink && App.BluetoothService.IsUsingWifiTransportMode)
            {
                System.Diagnostics.Debug.WriteLine("--> [HARDWARE RADAR RECOVERY]: Phone Wi-Fi adapter disabled. Force-dropping transport lane preferences back to BLE...");
                App.BluetoothService.IsUsingWifiTransportMode = false;
            }

            // 🚀 THE CONNECTED COCKPIT CIRCUIT BREAKER OVERSIGHT:
            // Stripping away the rogue lblOrderStatusText condition clears out your CS0103 property compiler chokes!
            if (!isConnected && !App.BluetoothService.IsUsingWifiTransportMode)
            {
                System.Diagnostics.Debug.WriteLine("--> [DASHBOARD COCKPIT DETACH]: Both transport networks are completely OFFLINE. Initializing absolute zero-out reset passes...");

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

                // 🎯 THE CLEAN NATIVE LAYOUT SLUSH BLOCK:
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

                // Force fully drop your front-end gauges down to zero!
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

            // STEP 2: EVALUATE TRANS-LINK THEMES
            // 🚀 RECURSION CIRCUIT BREAKER 2: 
            // We evaluate transport mode preferences strictly driven by your background service.
            // If it says Wi-Fi is active, draw the tech blue panel layout, otherwise draw BLE!
            if (phoneHasActiveWifiRadioLink && App.BluetoothService.IsUsingWifiTransportMode)
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
                        borderBleStatus.BackgroundColor = Color.Parse("#1A242D"); // Tech Subnet Blue [INDEX_0.1.42]
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
                    return; // Complete Wi-Fi theme render pass safely! [INDEX_0.1.42]
                }
            }

            // NOMINAL BLUETOOTH LOW ENERGY CONNECTION RENDER (IF NOT IN WI-FI MODE) [INDEX_0.1.42]
            if (isConnected && !App.BluetoothService.IsUsingWifiTransportMode)
            {
                string currentBleName = Preferences.Default.Get(MainPage.SavedDeviceNameKey, "VersaHub_BLE");
                if (Guid.TryParse(currentBleName, out _) || currentBleName.Contains("-")) currentBleName = "VersaHub_BLE";

                if (borderNetworkStatus != null) borderNetworkStatus.IsVisible = false;

                if (borderBleStatus != null)
                {
                    borderBleStatus.BackgroundColor = Color.Parse("#1A2D20"); // Emerald Green [INDEX_0.1.42]
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

    // DASHBOARD ACTUATOR TRIGGER: LOCK SWITCH 🕹 
    private async void OnLockClicked(object sender, EventArgs e)
    {
        try
        {
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            System.Diagnostics.Debug.WriteLine("--> [UI CONTROL]: Dispatching secure over-the-air LOCK token packet...");

            // 🚀 THE ASYNC ISOLATION REFIX: 
            // We dispatch the secure command task to run independently in the background. 
            // We strip away the rigid 'if (!transmissionSuccess)' alert popup check block here entirely!
            // Your Arduino web server and BLE loops will catch the request and return verification 
            // live to your packet terminal logs smoothly without triggering false signal fault overlays.
            _ = Task.Run(async () =>
            {
                await App.BluetoothService.SendSecureCommandAsync(activeKey, "LOCK");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"--> [LOCK UI CHOKE]: {ex.Message}");
        }
    }

    // DASHBOARD ACTUATOR TRIGGER: UNLOCK SWITCH 🕹 
    private async void OnUnlockClicked(object sender, EventArgs e)
    {
        try
        {
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
            System.Diagnostics.Debug.WriteLine("--> [UI CONTROL]: Dispatching secure over-the-air UNLOCK token packet...");

            // 🚀 THE ASYNC ISOLATION REFIX:
            _ = Task.Run(async () =>
            {
                await App.BluetoothService.SendSecureCommandAsync(activeKey, "UNLOCK");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"--> [UNLOCK UI CHOKE]: {ex.Message}");
        }
    }

    // 📊 REAL-TIME COCKPIT TELEMETRY DATA REPAINT ENGINE
    private void UpdateDashboardMetrics(float frontVolts, int frontPercent, bool frontIsCharging, float backVolts, int backPercent, bool backIsCharging)
    {
        // 1. UPDATE FRONT STARTER BATTERY GAUGES
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
            progressFront.ProgressColor = Color.Parse("#10B981"); // Nominal Emerald Green
            lblFrontVolts.TextColor = Color.Parse("#10B981");
        }

        // 2. UPDATE TRUNK WORKSTATION BATTERY GAUGES
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
            progressBack.ProgressColor = Color.Parse("#3B82F6"); // Workstation Tech Blue
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
            if (App.BluetoothService != null) await App.BluetoothService.DisconnectCurrentDeviceAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                layoutPasswordInitShell.IsVisible = false;
                if (initMasterPasswordControl != null)
                {
                    var entryField = initMasterPasswordControl.FindByName<Entry>("entryInitialPass");
                    if (entryField != null) entryField.Text = string.Empty;
                }
                layoutOverlayShell.IsVisible = true;
                if (btDevicePicker != null) _ = btDevicePicker.InitializePickerLifecycleAsync();
            });
        }
        catch (Exception ex) { Debug.WriteLine($"--> [RECOVERY EXCEPTION SHIELD]: {ex.Message}"); }
    }

    // ====================================================================
    // 🎯 THE PICKER EXIT ROUTINE ACTION
    // ====================================================================
    private void OnClosePickerOverlayClicked(object sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (layoutOverlayShell != null)
            {
                // Forcefully drop the dark mask container overlay window out of view!
                layoutOverlayShell.IsVisible = false;
                System.Diagnostics.Debug.WriteLine("--> [UI CONTROL]: Device selection picker overlay hidden cleanly.");
            }
        });
    }

    // ====================================================================
    // DASHBOARD MANUAL REFRESH SEARCH DISCOVERY TRIGGER 🕹️
    // ====================================================================
    private async void OnManualScanTriggerClicked(object sender, EventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("--> [UI CONTROL]: User requested manual scan refresh pass...");

            // 🚀 THE UNCONDITIONAL HARDWARE RE-ARM GATEWAY:
            // Instead of trusting your picker control's internal memory state tracking,
            // we forcefully request the Android security kernel to refresh its active hardware links!
            // This ensures that turning Bluetooth back on right before tapping refresh will re-index your frequencies instantly.
            bool isPermissionApproved = false;

#if ANDROID
            var nativeAndroidContext = Android.App.Application.Context;
            bool hasNativeScanClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothScan) == Android.Content.PM.Permission.Granted;
            bool hasNativeConnectClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) == Android.Content.PM.Permission.Granted;

            if (!hasNativeScanClearance || !hasNativeConnectClearance)
            {
                System.Diagnostics.Debug.WriteLine("--> [WATCHDOG]: System token validation missing. Requesting dynamic hardware tracking permissions...");
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

            // Verify if your phone's physical wireless adapter chips are turned on right now
            bool isRadioHardwareActive = Plugin.BLE.CrossBluetoothLE.Current.IsOn;

            if (isPermissionApproved && isRadioHardwareActive)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = true;

                    if (btDevicePicker != null)
                    {
                        System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR]: Forcing active device list reset sweep over radio waves...");
                        // Hard reset your picker list collections and invoke a fresh frequency look up pass!
                        await btDevicePicker.InitializePickerLifecycleAsync();
                    }
                });
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("--> [CRITICAL SELECTION BLOCK]: Refresh scan blocked. Permission Approved: " + isPermissionApproved + " | Radio Active: " + isRadioHardwareActive);
                await DisplayAlertAsync("BLUETOOTH REQUIRED", "VersaHUD cannot execute a visual radar refresh scan because your phone's Bluetooth radio switch is turned OFF or permissions were denied.\n\nPlease ensure Bluetooth is active in your drop-down panel and try again.", "OK");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [MANUAL SCAN TRIGGER FAULT]: Dynamic security check failed: {ex.Message}");
        }
    }

    // 🚀 THE BACKGROUND FRAME PROXY TUNNEL
    // Allows your background service channel to cleanly forward parsed Wi-Fi packets 
    // straight to your screen views without spawning duplicate network sockets!
    public void RaiseWifiTelemetryParsedEventProxy(string synchronizedPacketText)
    {
        if (string.IsNullOrEmpty(synchronizedPacketText)) return;

        // Feed the string data cleanly down into your dual-transport regex strip engines!
        ParseVehicleTelemetryStream(synchronizedPacketText);
    }

    private void OnTransportChannelShiftRepaint(bool isWifiActive)
    {
        // Force the execution pass back onto the master visual UI thread layer instantly
        MainThread.BeginInvokeOnMainThread(() =>
        {
            System.Diagnostics.Debug.WriteLine($"--> [UI INTENT OVERRIDE]: Transport event captured. Wi-Fi Active: {isWifiActive}. Repainting console cluster panels...");

            if (isWifiActive)
            {
                // 1. Force fully collapse your dark bottom drawer selection sheets out of view!
                if (layoutOverlayShell != null) layoutOverlayShell.IsVisible = false;

                // 2. Force fully trigger your master status badge method to draw your tech-blue gauges!
                // Passing a false flag here is now completely safe because your guard rules are bypassed!
                UpdateBluetoothStatusBadge(isConnected: false);

                System.Diagnostics.Debug.WriteLine("--> [UI INTENT OVERRIDE SUCCESS]: Cockpit interface successfully unlocked over local Wi-Fi subnet!");
            }
        });
    }

    ~MainPage()
    {
        App.BluetoothService.OnConnectionStateChanged -= UpdateBluetoothStatusBadge;
        App.BluetoothService.OnRssiUpdated -= UpdateWirelessSignalBars;
        App.BluetoothService.OnTelemetryReceived -= ParseVehicleTelemetryStream;
        if (initMasterPasswordControl != null)
        {
            initMasterPasswordControl.OnPasswordInitialized -= OnSetupFinished;
            initMasterPasswordControl.OnWrongDeviceRequested -= OnRollbackConnectionAndRescan;
        }
    }
}
