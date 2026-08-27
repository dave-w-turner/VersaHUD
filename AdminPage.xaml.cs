using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VersaHUD;

public partial class AdminPage : ContentPage, INotifyPropertyChanged
{
    private string _wifiApName = "Loading...";
    private string _bleBroadcastName = "Loading...";
    private string _routerBridgeSSID = "Loading...";
    private bool _isRouterConfigured = false;

    private CancellationTokenSource? _adminWifiWatchdogCancelSource;

    // PROPERTY BLOCK REPOSITORIES BOUND NATIVELY TO XAML TILES
    public string WifiApName
    {
        get => _wifiApName;
        set { _wifiApName = value; OnPropertyChanged(); }
    }

    public string BleBroadcastName
    {
        get => _bleBroadcastName;
        set { _bleBroadcastName = value; OnPropertyChanged(); }
    }

    public string RouterBridgeSSID
    {
        get => _routerBridgeSSID;
        set { _routerBridgeSSID = value; OnPropertyChanged(); }
    }

    public bool IsRouterConfigured
    {
        get => _isRouterConfigured;
        set { _isRouterConfigured = value; OnPropertyChanged(); }
    }

    public new event PropertyChangedEventHandler PropertyChanged;
    protected new void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // =====================================================
    // 🏗️ INITIALIZATION CONSTRUCTOR
    // =====================================================
    public AdminPage()
    {
        InitializeComponent();

        // 🚀 DUAL-CHANNEL BINDING START: Connect terminal logging to native Bluetooth immediately
        App.BluetoothService.OnTelemetryReceived += LogIncomingStreamToTerminal;
        App.BluetoothService.OnConnectionStateChanged += OnVehicleLinkStateChanged;
        this.BindingContext = this;
    }

    // =====================================================
    // 🌐 SEAMLESS ENTRY HANDSHAKE Llifecycle PIPELINES
    // =====================================================
    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (App.BluetoothService != null && App.BluetoothService.IsRebootingWatchdogActive)
        {
            if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = true;
            return;
        }
        else
        {
            if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = false;
        }

        await Task.Delay(300); // GATT / Socket stabilization pad

        if (App.BluetoothService != null && !App.BluetoothService.IsRebootingWatchdogActive)
        {
            // 🌐 TRANSPORT ROUTE A: HIGH-BANDWIDTH CLEAN WI-FI REST API PULL LANES
            if (App.BluetoothService.IsUsingWifiTransportMode)
            {
                System.Diagnostics.Debug.WriteLine("--> [ADMIN CONTROL HUB]: Fetching clean configuration matrices straight from API...");

                var (wifiAp, bleName, routerSsid, isOk) = await App.BluetoothService.FetchWifiAdminParametersAsync();

                if (isOk)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // Safely bind primitives straight onto your UI tile tracking variables with ZERO regex string scraping noise!
                        this.WifiApName = wifiAp;
                        this.BleBroadcastName = bleName;

                        if (routerSsid == "NONE" || string.IsNullOrEmpty(routerSsid))
                        {
                            this.RouterBridgeSSID = string.Empty;
                            this.IsRouterConfigured = false;
                            layoutUnconfiguredRouter.IsVisible = true;
                            layoutConfiguredRouter.IsVisible = false;
                        }
                        else
                        {
                            this.RouterBridgeSSID = routerSsid;
                            this.IsRouterConfigured = true;
                            layoutUnconfiguredRouter.IsVisible = false;
                            layoutConfiguredRouter.IsVisible = true;
                        }
                    });
                    return; // 🚀 Complete Wi-Fi extraction cleanly and bypass old legacy BLE text parser commands entirely!
                }
            }

            // 🔵 TRANSPORT ROUTE B: FALLBACK NATIVE BLUETOOTH LOW ENERGY STRING COMMAND HOOKS
            System.Diagnostics.Debug.WriteLine("--> [ADMIN CONTROL HUB]: Fetching parameters over-the-air via serial text scraping...");
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

            await App.BluetoothService.SendSecureCommandAsync(activeKey, "GETWIFINAME");
            await Task.Delay(150);
            await App.BluetoothService.SendSecureCommandAsync(activeKey, "GETBLENAME");
            await Task.Delay(150);
            await App.BluetoothService.SendSecureCommandAsync(activeKey, "GETROUTER");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // 🚀 THE LIFECYCLE CLEANUP SAFETY ENVELOPE:
        // Force-kill and cancel your local background Wi-Fi watchdogs instantly if the user 
        // backs out of this console screen view, preventing thread memory leaks or duplicate pops!
        _adminWifiWatchdogCancelSource?.Cancel();
        _adminWifiWatchdogCancelSource = null;
    }

    // Proxy pass helper converts the MainPage's Wi-Fi network updates straight to your shared terminal logging processor
    private void OnMainPageWifiTelemetryParsed(string unifiedWifiDataPacket)
    {
        LogIncomingStreamToTerminal(unifiedWifiDataPacket);
    }

    // AUTOMATED BLUETOOTH OVERLAY DISMISSAL & HARDWARE STATE REFRESH 🔵 [INDEX_0.1.44]
    private void OnVehicleLinkStateChanged(bool isConnected)
    {
        if (isConnected)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (layoutRebootLockoutShell != null && layoutRebootLockoutShell.IsVisible)
                {
                    layoutRebootLockoutShell.IsVisible = false;
                    System.Diagnostics.Debug.WriteLine("--> [LOCKOUT SHIELD]: Native BLE connection restored. Dismissing blocker.");
                }

                await Task.Delay(400);

                if (App.BluetoothService != null && !App.BluetoothService.IsRebootingWatchdogActive)
                {
                    string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
                    System.Diagnostics.Debug.WriteLine("--> [ADMIN CONTROL HUB]: Executing post-reboot automated BLE serial command sync pass...");

                    await App.BluetoothService.SendSecureCommandAsync(activeKey, "GETROUTER");
                    await Task.Delay(150);
                    await App.BluetoothService.SendSecureCommandAsync(activeKey, "GETWIFINAME");
                }
            });
        }
    }

    // =====================================================
    // 🔄 USER CLICK ACTUATORS: INTERFACE COMMAND ACTIONS
    // =====================================================
    private async void OnRotateMasterPassClicked(object sender, EventArgs e)
    {
        string newPassInput = entryNewMasterPass.Text;

        if (string.IsNullOrWhiteSpace(newPassInput) || newPassInput.Trim().Length < 3)
        {
            await DisplayAlertAsync("INVALID KEY LENGTH", "The new master password must be at least 3 characters long.", "OK");
            return;
        }

        newPassInput = newPassInput.Trim();

        bool doubleCheck = await DisplayAlertAsync("OVER-THE-AIR WRITE",
            "This will permanently re-flash the internal EEPROM authorization registers inside your vehicle module. Proceed?",
            "ROTATE TOKENS", "CANCEL");

        if (!doubleCheck) return;

        var rotationCompletedSource = new TaskCompletionSource<bool>();
        Action<string> telemetryVerificationHandler = null;

        telemetryVerificationHandler = (incomingStreamMessage) =>
        {
            System.Diagnostics.Debug.WriteLine($"--> [ADMIN ROTATION INSPECTOR]: {incomingStreamMessage}");
            if (incomingStreamMessage.Contains("Master Cryptographic Token Rotated"))
            {
                App.BluetoothService.OnTelemetryReceived -= telemetryVerificationHandler;
                rotationCompletedSource.TrySetResult(true);
            }
        };

        App.BluetoothService.OnTelemetryReceived += telemetryVerificationHandler;

        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
        string payloadCommand = $"UPDATEMASTERPASS={newPassInput}";

        bool commandTransmitted = await App.BluetoothService.SendSecureCommandAsync(currentActiveKey, payloadCommand);

        if (!commandTransmitted)
        {
            App.BluetoothService.OnTelemetryReceived -= telemetryVerificationHandler;
            await DisplayAlertAsync("TRANSMISSION FAULT", "Could not establish a physical wireless channel link. Verification aborted.", "OK");
            return;
        }

        try
        {
            Task timeoutTrackerTask = Task.Delay(3000);
            Task completedGateTask = await Task.WhenAny(rotationCompletedSource.Task, timeoutTrackerTask);

            if (completedGateTask == rotationCompletedSource.Task && await rotationCompletedSource.Task)
            {
                Preferences.Default.Set(Controls.InitMasterPassword.MasterPasswordKey, newPassInput);
                entryNewMasterPass.Text = string.Empty;
                await DisplayAlertAsync("ROTATION SUCCESSFUL", "The vehicle module registers and your mobile app preferences have been successfully synchronized under your new master key!", "OK");
            }
            else
            {
                App.BluetoothService.OnTelemetryReceived -= telemetryVerificationHandler;
                await DisplayAlertAsync("VAULT LOCKOUT SHIELD", "The rotation command was transmitted, but the app missed the cryptographic receipt confirmation from the car module. Local settings rolled back to maintain alignment. Verify connections and try again.", "OK");
            }
        }
        catch (Exception ex)
        {
            App.BluetoothService.OnTelemetryReceived -= telemetryVerificationHandler;
            System.Diagnostics.Debug.WriteLine($"--> [ADMIN SYNC CRASH SHIELD]: {ex.Message}");
        }
    }

    private async void OnUpdateWifiAPClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(entryWifiAP.Text)) return;

        string targetNewAPId = entryWifiAP.Text.Trim();
        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        System.Diagnostics.Debug.WriteLine($"--> [ADMIN CONTROL HUB]: Dispatching secure over-the-air Wifi AP ID swap to '{targetNewAPId}'...");

        // Fire your payload-encrypted instruction down your current active transport path safely
        bool commandWasDelivered = await App.BluetoothService.SendSecureCommandAsync(currentActiveKey, $"SETWIFINAME={targetNewAPId}");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
                layoutRebootLockoutShell?.IsVisible = false;
            });

            await DisplayAlertAsync("IDENTITY ROTATED", "The parameter update was delivered successfully. System reboot initiated.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
        }
    }

    // TWO-WAY WIRELESS CHANNEL BROADCAST IDENTITY ROTATOR
    private async void OnUpdateBleNameClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(entryBleName.Text)) return;

        string targetNewBleId = entryBleName.Text.Trim();
        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        System.Diagnostics.Debug.WriteLine($"--> [ADMIN CONTROL HUB]: Dispatching secure over-the-air BLE ID swap to '{targetNewBleId}'...");

        // Fire your payload-encrypted instruction down your current active transport path safely
        bool commandWasDelivered = await App.BluetoothService.SendSecureCommandAsync(currentActiveKey, $"SETBLENAME={targetNewBleId}");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
            });

            await DisplayAlertAsync("IDENTITY ROTATED", "The parameter update was delivered successfully. System reboot initiated.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
        }
    }

    private async void OnSaveRouterClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(entryRouterSSID.Text) || string.IsNullOrWhiteSpace(entryRouterPass.Text)) return;
        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
        string payload = $"SAVEROUTER={entryRouterSSID.Text.Trim()},{entryRouterPass.Text.Trim()}";

        bool commandWasDelivered = await App.BluetoothService.SendSecureCommandAsync(currentActiveKey, payload);

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
            });

            await DisplayAlertAsync("Wi-Fi SETTINGS SAVED", "The parameter update was delivered successfully. System reboot initiated.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
        }

        entryRouterPass.IsEnabled = false;
        entryRouterSSID.IsEnabled = false;
        btnLinkToRouter.IsEnabled = false;
    }

    // REAL-TIME OVER-THE-AIR WI-FI ENVIRONMENT LOOKUP RADAR 🔍 
    private async void OnScanWifiNetworksClicked(object sender, EventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("--> [WIFI RADAR]: Initializing live vehicle airwave scan pass...");
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

            bool transmitted = await App.BluetoothService.SendSecureCommandAsync(activeKey, "SCANWIFI");

            if (!transmitted)
            {
                await DisplayAlertAsync("LINK FAULT", "Could not talk to the vehicle module. Verify your Bluetooth badge is green.", "OK");
                return;
            }

            var scanCompletedSource = new TaskCompletionSource<string>();
            Action<string> scanResultInterceptor = null;

            scanResultInterceptor = (incomingStreamMessage) =>
            {
                if (incomingStreamMessage.Contains("WIFI_LIST:"))
                {
                    App.BluetoothService.OnTelemetryReceived -= scanResultInterceptor;
                    string rawSsidList = incomingStreamMessage.Substring(incomingStreamMessage.IndexOf("WIFI_LIST:") + 10).Trim();
                    scanCompletedSource.TrySetResult(rawSsidList);
                }
            };

            App.BluetoothService.OnTelemetryReceived += scanResultInterceptor;
            lblDebugTerminal.Text += $"\n[{DateTime.Now:HH:mm:ss}] info: Arduino scanning Wi-Fi channels... please wait.";

            Task timeoutTask = Task.Delay(4000);
            Task finishedTask = await Task.WhenAny(scanCompletedSource.Task, timeoutTask);

            if (finishedTask == scanCompletedSource.Task)
            {
                string cleanSsidList = await scanCompletedSource.Task;
                string[] discoveredNetworks = cleanSsidList.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                if (discoveredNetworks.Length == 0)
                {
                    await DisplayAlertAsync("RADAR EMPTY", "The vehicle module completed its scan but detected 0 nearby networks.", "OK");
                    return;
                }

                string selectedSSID = await DisplayActionSheetAsync("AVAILABLE WI-FI NETWORKS", "CANCEL", null, discoveredNetworks);
                if (!string.IsNullOrEmpty(selectedSSID) && selectedSSID != "CANCEL")
                {
                    RouterBridgeSSID = selectedSSID;
                }
            }
            else
            {
                App.BluetoothService.OnTelemetryReceived -= scanResultInterceptor;
                await DisplayAlertAsync("RADAR TIMEOUT", "The vehicle module failed to return its network inventory within 4 seconds.", "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"--> [WIFI SCAN CHOKE]: {ex.Message}");
        }
    }

    // FORGET ROUTER AND RESTORE FACTORY ACCESS POINT (DUAL-TRANSPORT SECURED)
    private async void OnForgetRouterClicked(object sender, EventArgs e)
    {
        bool doubleCheck = await DisplayAlertAsync("PURGE ROUTER PROFILE",
            "This will completely erase your stored Wi-Fi station credentials inside the vehicle module's memory cells, force a hard reboot, and lock the microchip back into standalone Access Point mode. Proceed?",
            "FORGET NETWORK", "CANCEL");

        if (!doubleCheck) return;

        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
        bool commandWasDelivered = await App.BluetoothService.SendSecureCommandAsync(currentActiveKey, "SAVEROUTER=CLEAR,CLEAR");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
            });

            await DisplayAlertAsync("Wi-Fi SETTINGS SAVED", "The parameter update was delivered successfully. System reboot initiated.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
        }

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
            });

            RouterBridgeSSID = string.Empty;
            IsRouterConfigured = false;
            layoutUnconfiguredRouter.IsVisible = true;
            layoutConfiguredRouter.IsVisible = false;

            await DisplayAlertAsync("WIPE COMMAND FIRED", "The vehicle module is erasing credentials and performing a clean reboot now.", "OK");
        }
    }

    // =====================================================
    // 📊 MASTER STREAM PACKET INTERCEPTOR TERMINAL PARSER
    // =====================================================
    private static void LogIncomingStreamToTerminal(string rawPacket)
    {
        if (string.IsNullOrEmpty(rawPacket)) return;

        AdminPage? activePageInstance = null;

        var rootWindow = Application.Current.MainPage as AppShell;
        activePageInstance = rootWindow.CurrentPage as AdminPage;

        if (activePageInstance == null) return;

        // TRIGGER 1: REBOOT CONTROLLER INTERCEPT 🛑
        if (rawPacket.Contains("Rebooting controller..."))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (activePageInstance.layoutRebootLockoutShell != null)
                {
                    activePageInstance.layoutRebootLockoutShell.IsVisible = true;
                    if (!App.BluetoothService.IsRebootingWatchdogActive)
                    {
                        await Task.Delay(1200);
                        await App.BluetoothService.ForceProactiveRebootRecoveryAsync();
                    }
                }
            });
        }

        // TRIGGER 2: ROUTER HANDSHAKE VERIFICATION ERROR INTERCEPT 🚀
        if (rawPacket.Contains("ROUTER_ERROR"))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (activePageInstance.layoutRebootLockoutShell != null)
                {
                    activePageInstance.layoutRebootLockoutShell.IsVisible = false;
                }

                if (activePageInstance.entryRouterPass != null)
                {
                    activePageInstance.entryRouterPass.Text = string.Empty;
                    activePageInstance.entryRouterPass.Focus();
                    activePageInstance.entryRouterPass.IsEnabled = true;
                    activePageInstance.entryRouterSSID.IsEnabled = true;
                    activePageInstance.btnLinkToRouter.IsEnabled = true;
                }
                await Application.Current.MainPage.DisplayAlertAsync("ROUTER LINK FAILED", "The vehicle module could not establish an active wireless handshake with your home station. Verify your network credentials and try again.", "OK");
            });
            return;
        }

        // TRIGGER 3: ROUTER STORAGE SUCCESS CAPTURE 🟢
        if (rawPacket.Contains("[ADMIN_SUCCESS]: Router credentials stored."))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (activePageInstance.entryRouterPass != null)
                {
                    activePageInstance.entryRouterPass.Text = string.Empty;
                    activePageInstance.entryRouterPass.IsEnabled = true;
                    activePageInstance.entryRouterSSID.IsEnabled = true;
                    activePageInstance.btnLinkToRouter.IsEnabled = true;
                }
                App.BluetoothService.IsRebootingWatchdogActive = false;
                await Application.Current.MainPage.DisplayAlertAsync("ROUTER LINK SUCCESSFUL", "The vehicle module has successfully established a secure wireless handshake with your home station.", "OK");
            });
        }

        // TRIGGER 4: REBOOT LOCKOUT SHEET AUTO-DISMISSAL OVERRIDES 📡
        if (activePageInstance.layoutRebootLockoutShell != null && activePageInstance.layoutRebootLockoutShell.IsVisible)
        {
            if (rawPacket.Contains("[SYS]") || rawPacket.Contains("AP_NAME:") || rawPacket.Contains("BLE_NAME:") || rawPacket.Contains("ROUTER_SSID:"))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    activePageInstance.layoutRebootLockoutShell.IsVisible = false;
                });
            }
        }

        // TRIGGER 5: LIVE RAW DEBUG TERMINAL LOG BUFFER PRINTING 📊
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (activePageInstance.lblDebugTerminal != null)
            {
                string timeStampStr = DateTime.Now.ToString("HH:mm:ss");
                activePageInstance.lblDebugTerminal.Text += $"\n[{timeStampStr}] rx: {rawPacket.Trim()}";

                if (activePageInstance.lblDebugTerminal.Text.Length > 2000)
                {
                    activePageInstance.lblDebugTerminal.Text = "[SYS] Buffer optimized.\n" + activePageInstance.lblDebugTerminal.Text.Substring(activePageInstance.lblDebugTerminal.Text.Length - 1000);
                }
            }

            if (activePageInstance.switchAutoscroll != null && activePageInstance.switchAutoscroll.IsToggled && activePageInstance.scrollTerminal != null)
            {
                await activePageInstance.scrollTerminal.ScrollToAsync(0, activePageInstance.lblDebugTerminal.Height, true);
            }
        });

        if (App.BluetoothService.IsUsingWifiTransportMode) return;

        if (activePageInstance.WifiApName == "Loading..." || activePageInstance.BleBroadcastName == "Loading..." || activePageInstance.RouterBridgeSSID == "Loading...")
        {
            // Fallback text scraping handles natively for BLE mode channels safely below [INDEX_0.1.43]
            int apIndex = rawPacket.IndexOf("AP_NAME:");
            int bleIndex = rawPacket.IndexOf("BLE_NAME:");
            int routerIndex = rawPacket.IndexOf("ROUTER_SSID:");

            if (apIndex != -1 || bleIndex != -1 || routerIndex != -1)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (apIndex != -1)
                    {
                        activePageInstance.WifiApName = rawPacket.Substring(apIndex + 8).Trim();
                    }

                    if (bleIndex != -1)
                    {
                        activePageInstance.BleBroadcastName = rawPacket.Substring(bleIndex + 9).Trim();
                    }

                    if (routerIndex != -1)
                    {
                        string ssidResult = rawPacket.Substring(routerIndex + 12).Trim();

                        if (ssidResult.Contains("[❌ NONE SAVED]") || ssidResult.Contains("[X NONE SAVED]") || string.IsNullOrEmpty(ssidResult) || ssidResult.Contains("NONE"))
                        {
                            activePageInstance.RouterBridgeSSID = string.Empty;
                            activePageInstance.IsRouterConfigured = false;
                            activePageInstance.layoutUnconfiguredRouter.IsVisible = true;
                            activePageInstance.layoutConfiguredRouter.IsVisible = false;
                        }
                        else
                        {
                            activePageInstance.RouterBridgeSSID = ssidResult;
                            activePageInstance.IsRouterConfigured = true;
                            activePageInstance.layoutUnconfiguredRouter.IsVisible = false;
                            activePageInstance.layoutConfiguredRouter.IsVisible = true;
                        }
                    }
                });
            }
        }
    }

    // =====================================================
    // 💀 DESTRUCTOR LIFECYCLE CLEANUP
    // =====================================================
    ~AdminPage()
    {
        App.BluetoothService.OnConnectionStateChanged -= OnVehicleLinkStateChanged;
        App.BluetoothService.OnTelemetryReceived -= LogIncomingStreamToTerminal;

        MainPage mainPageInstance = null;
        if (Shell.Current != null)
        {
            foreach (var item in Shell.Current.Items)
                foreach (var section in item.Items)
                {
                    foreach (var content in section.Items)
                    {
                        if (content.Content is MainPage resolvedPage)
                        {
                            mainPageInstance = resolvedPage;
                        }
                    }
                }

            if (mainPageInstance != null)
            {
                mainPageInstance.OnWifiTelemetryParsed -= OnMainPageWifiTelemetryParsed;
            }

            App.BluetoothService.IsRebootingWatchdogActive = false;
        }
    }
}