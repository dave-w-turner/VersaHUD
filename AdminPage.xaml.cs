using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VersaHUD;

public partial class AdminPage : ContentPage, INotifyPropertyChanged
{
    private string _wifiApName = "Loading...";
    private string _bleBroadcastName = "Loading...";
    private string _routerBridgeSSID = "Loading...";
    private string _cloudflareHost = "Loading...";
    private string _cloudflareClientId = "Loading...";
    private string _cloudflareClientSecret = "Loading...";
    private bool _isRouterConfigured = false;

    private CancellationTokenSource? _adminWifiWatchdogCancelSource;

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

    public string CloudflareHost
    {
        get => _cloudflareHost;
        set { _cloudflareHost = value; OnPropertyChanged(); }
    }

    public string CloudflareClientId
    {
        get => _cloudflareClientId;
        set { _cloudflareClientId = value; OnPropertyChanged(); }
    }

    public string CloudflareClientSecret
    {
        get => _cloudflareClientSecret;
        set { _cloudflareClientSecret = value; OnPropertyChanged(); }
    }

    public new event PropertyChangedEventHandler PropertyChanged;
    protected new void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public AdminPage()
    {
        InitializeComponent();

        App.NetworkService.OnTelemetryReceived += LogIncomingStreamToTerminal;
        App.NetworkService.OnConnectionStateChanged += OnVehicleLinkStateChanged;
        this.BindingContext = this;

        string runningHost = Preferences.Default.Get("CloudflareHostKey", "silent-bird-d9c0.taigon1984.workers.dev");
        string runningClientId = Preferences.Default.Get("CloudflareClientIdKey", "PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE");
        string runningSecret = Preferences.Default.Get("CloudflareClientSecretKey", "PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE");

        entryCfHost?.Text = runningHost.Equals("silent-bird-d9c0.taigon1984.workers.dev") ? "" : runningHost;
        entryCfClientId?.Text = runningClientId.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE") ? "" : runningClientId;
        entryCfClientSecret?.Text = runningSecret.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE") ? "" : runningSecret;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = true;
            return;
        }
        else
        {
            if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = false;
        }

        await Task.Delay(300);

        if (App.NetworkService != null && !App.NetworkService.IsRebootingWatchdogActive)
        {
            if (App.NetworkService.IsUsingWifiTransportMode)
            {
                Debug.WriteLine("--> [ADMIN CONTROL HUB]: Fetching clean configuration matrices straight from API...");

                var (wifiAp, bleName, routerSsid, cfHost, cfId, isOk) = await App.NetworkService.FetchWifiAdminParametersAsync();

                if (isOk)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        this.WifiApName = wifiAp;
                        this.BleBroadcastName = bleName;
                        this.CloudflareHost = cfHost.Equals("silent-bird-d9c0.taigon1984.workers.dev") ? string.Empty : cfHost;
                        this.CloudflareClientId = cfId.Equals("NONE") ? string.Empty : cfId;

                        if (entryCfHost != null) entryCfHost.Text = CloudflareHost;
                        if (entryCfClientId != null) entryCfClientId.Text = CloudflareClientId;

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
                }
                return;
            }

            Debug.WriteLine("--> [ADMIN CONTROL HUB]: Fetching parameters over-the-air via serial text scraping...");
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

            await App.NetworkService.SendSecureCommandAsync(activeKey, "GETWIFINAME");
            await Task.Delay(150);
            await App.NetworkService.SendSecureCommandAsync(activeKey, "GETBLENAME");
            await Task.Delay(150);
            await App.NetworkService.SendSecureCommandAsync(activeKey, "GETROUTER");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _adminWifiWatchdogCancelSource?.Cancel();
        _adminWifiWatchdogCancelSource = null;
    }

    private void OnMainPageWifiTelemetryParsed(string unifiedWifiDataPacket)
    {
        LogIncomingStreamToTerminal(unifiedWifiDataPacket);
    }

    private void OnVehicleLinkStateChanged(bool isConnected)
    {
        if (isConnected)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (layoutRebootLockoutShell != null && layoutRebootLockoutShell.IsVisible)
                {
                    layoutRebootLockoutShell.IsVisible = false;
                    Debug.WriteLine("--> [LOCKOUT SHIELD]: Native BLE connection restored. Dismissing blocker.");
                }

                await Task.Delay(400);

                if (App.NetworkService != null && !App.NetworkService.IsRebootingWatchdogActive)
                {
                    string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
                    Debug.WriteLine("--> [ADMIN CONTROL HUB]: Executing post-reboot automated BLE serial command sync pass...");

                    await App.NetworkService.SendSecureCommandAsync(activeKey, "GETROUTER");
                    await Task.Delay(150);
                    await App.NetworkService.SendSecureCommandAsync(activeKey, "GETWIFINAME");
                    await Task.Delay(150);
                    await App.NetworkService.SendSecureCommandAsync(activeKey, "GETCFHOST");
                    await Task.Delay(150);
                    await App.NetworkService.SendSecureCommandAsync(activeKey, "GETCFID");
                }
            });
        }
    }

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
            Debug.WriteLine($"--> [ADMIN ROTATION INSPECTOR]: {incomingStreamMessage}");
            if (incomingStreamMessage.Contains("Master Cryptographic Token Rotated"))
            {
                App.NetworkService.OnTelemetryReceived -= telemetryVerificationHandler;
                rotationCompletedSource.TrySetResult(true);
            }
        };

        App.NetworkService.OnTelemetryReceived += telemetryVerificationHandler;

        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
        string payloadCommand = $"UPDATEMASTERPASS={newPassInput}";

        bool commandTransmitted = await App.NetworkService.SendSecureCommandAsync(currentActiveKey, payloadCommand);

        if (!commandTransmitted)
        {
            App.NetworkService.OnTelemetryReceived -= telemetryVerificationHandler;
            await DisplayAlertAsync("TRANSMISSION FAULT", "Could not establish a physical wireless channel link. Verification aborted.", "OK");
            return;
        }

        try
        {
            Task timeoutTrackerTask = Task.Delay(3000);
            Task completedGateTask = await Task.WhenAny(rotationCompletedSource.Task, timeoutTrackerTask);

            if (completedGateTask == rotationCompletedSource.Task && await rotationCompletedSource.Task)
            {
#if ANDROID
                var nativeContext = Android.App.Application.Context;
                string preferencesFileName = $"{nativeContext.PackageName}.preferences";
                var nativePreferences = nativeContext.GetSharedPreferences(preferencesFileName, Android.Content.FileCreationMode.Private);

                using (var storageEditor = nativePreferences.Edit())
                {
                    // We write using your exact, clean string literal key matching your firmware firmware variables!
                    storageEditor.PutString("MasterPasswordKey", newPassInput);
                    storageEditor.Apply(); // Flash the update securely down to the physical silicon chip
                }
#else
                Preferences.Default.Set("MasterPasswordKey", newPassInput);
#endif
                entryNewMasterPass.Text = string.Empty;
                await DisplayAlertAsync("ROTATION SUCCESSFUL", "The vehicle module registers and your mobile app preferences have been successfully synchronized under your new master key!", "OK");
            }
            else
            {
                App.NetworkService.OnTelemetryReceived -= telemetryVerificationHandler;
                await DisplayAlertAsync("VAULT LOCKOUT SHIELD", "The rotation command was transmitted, but the app missed the cryptographic receipt confirmation from the car module. Local settings rolled back to maintain alignment. Verify connections and try again.", "OK");
            }
        }
        catch (Exception ex)
        {
            App.NetworkService.OnTelemetryReceived -= telemetryVerificationHandler;
            Debug.WriteLine($"--> [ADMIN SYNC CRASH SHIELD]: {ex.Message}");
        }
    }

    private async void OnUpdateWifiAPClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(entryWifiAP.Text)) return;

        string targetNewAPId = entryWifiAP.Text.Trim();
        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        Debug.WriteLine($"--> [ADMIN CONTROL HUB]: Dispatching secure over-the-air Wifi AP ID swap to '{targetNewAPId}'...");

        bool commandWasDelivered = await App.NetworkService.SendSecureCommandAsync(currentActiveKey, $"SETWIFINAME={targetNewAPId}");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.NetworkService.ForceProactiveRebootRecoveryAsync();
                layoutRebootLockoutShell?.IsVisible = false;
            });

            await DisplayAlertAsync("IDENTITY ROTATED", "The parameter update was delivered successfully. System reboot initiated.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
        }
    }

    private async void OnUpdateBleNameClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(entryBleName.Text)) return;

        string targetNewBleId = entryBleName.Text.Trim();
        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

        Debug.WriteLine($"--> [ADMIN CONTROL HUB]: Dispatching secure over-the-air BLE ID swap to '{targetNewBleId}'...");

        bool commandWasDelivered = await App.NetworkService.SendSecureCommandAsync(currentActiveKey, $"SETBLENAME={targetNewBleId}");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.NetworkService.ForceProactiveRebootRecoveryAsync();
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

        bool commandWasDelivered = await App.NetworkService.SendSecureCommandAsync(currentActiveKey, payload);

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.NetworkService.ForceProactiveRebootRecoveryAsync();
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

    private async void OnScanWifiNetworksClicked(object sender, EventArgs e)
    {
        try
        {
            Debug.WriteLine("--> [WIFI RADAR]: Initializing live vehicle airwave scan pass...");
            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

            bool transmitted = await App.NetworkService.SendSecureCommandAsync(activeKey, "SCANWIFI");

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
                    App.NetworkService.OnTelemetryReceived -= scanResultInterceptor;
                    string rawSsidList = incomingStreamMessage.Substring(incomingStreamMessage.IndexOf("WIFI_LIST:") + 10).Trim();
                    scanCompletedSource.TrySetResult(rawSsidList);
                }
            };

            App.NetworkService.OnTelemetryReceived += scanResultInterceptor;
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
                App.NetworkService.OnTelemetryReceived -= scanResultInterceptor;
                await DisplayAlertAsync("RADAR TIMEOUT", "The vehicle module failed to return its network inventory within 4 seconds.", "OK");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [WIFI SCAN CHOKE]: {ex.Message}");
        }
    }

    private async void OnForgetRouterClicked(object sender, EventArgs e)
    {
        bool doubleCheck = await DisplayAlertAsync("PURGE ROUTER PROFILE",
            "This will completely erase your stored Wi-Fi station credentials inside the vehicle module's memory cells, force a hard reboot, and lock the microchip back into standalone Access Point mode. Proceed?",
            "FORGET NETWORK", "CANCEL");

        if (!doubleCheck) return;

        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
        bool commandWasDelivered = await App.NetworkService.SendSecureCommandAsync(currentActiveKey, "SAVEROUTER=CLEAR,CLEAR");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.NetworkService.ForceProactiveRebootRecoveryAsync();
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
                await App.NetworkService.ForceProactiveRebootRecoveryAsync();
            });

            RouterBridgeSSID = string.Empty;
            IsRouterConfigured = false;
            layoutUnconfiguredRouter.IsVisible = true;
            layoutConfiguredRouter.IsVisible = false;

            await DisplayAlertAsync("WIPE COMMAND FIRED", "The vehicle module is erasing credentials and performing a clean reboot now.", "OK");
        }
    }

    private static void LogIncomingStreamToTerminal(string rawPacket)
    {
        if (string.IsNullOrEmpty(rawPacket)) return;

        AdminPage? activePageInstance = null;

        var rootWindow = Application.Current.MainPage as AppShell;
        activePageInstance = rootWindow.CurrentPage as AdminPage;

        if (activePageInstance == null) return;

        if (rawPacket.Contains("Rebooting controller..."))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (activePageInstance.layoutRebootLockoutShell != null)
                {
                    activePageInstance.layoutRebootLockoutShell.IsVisible = true;
                    if (!App.NetworkService.IsRebootingWatchdogActive)
                    {
                        await Task.Delay(1200);
                        await App.NetworkService.ForceProactiveRebootRecoveryAsync();
                    }
                }
            });
        }

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
                App.NetworkService.IsRebootingWatchdogActive = false;
                await Application.Current.MainPage.DisplayAlertAsync("ROUTER LINK SUCCESSFUL", "The vehicle module has successfully established a secure wireless handshake with your home station.", "OK");
            });
        }

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

        if (App.NetworkService.IsUsingWifiTransportMode) return;

        if (activePageInstance.WifiApName == "Loading..." || activePageInstance.BleBroadcastName == "Loading..." || activePageInstance.RouterBridgeSSID == "Loading...")
        {
            int cfHostIndex = rawPacket.IndexOf("CF_HOST:");
            int cfIdIndex = rawPacket.IndexOf("CF_ID:");
            int apIndex = rawPacket.IndexOf("AP_NAME:");
            int bleIndex = rawPacket.IndexOf("BLE_NAME:");
            int routerIndex = rawPacket.IndexOf("ROUTER_SSID:");

            if (apIndex != -1 || bleIndex != -1 || routerIndex != -1)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (cfHostIndex != -1)
                    {
                        string hostResult = rawPacket.Substring(cfHostIndex + 8).Trim();
                        activePageInstance.CloudflareHost = hostResult.Equals("silent-bird-d9c0.taigon1984.workers.dev") ? string.Empty : hostResult;
                        if (activePageInstance.entryCfHost != null) activePageInstance.entryCfHost.Text = activePageInstance.CloudflareHost;
                    }

                    if (cfIdIndex != -1)
                    {
                        string idResult = rawPacket.Substring(cfIdIndex + 6).Trim();
                        activePageInstance.CloudflareClientId = idResult.Contains("PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE") ? string.Empty : idResult;
                        if (activePageInstance.entryCfClientId != null) activePageInstance.entryCfClientId.Text = activePageInstance.CloudflareClientId;
                    }

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

    private async void OnUpdateCloudflareCredentialsClicked(object sender, EventArgs e)
    {
        try
        {
            string targetHost = entryCfHost?.Text?.Trim() ?? string.Empty;
            string targetClientId = entryCfClientId?.Text?.Trim() ?? string.Empty;
            string targetClientSecret = entryCfClientSecret?.Text?.Trim() ?? string.Empty;
            
            if (string.IsNullOrWhiteSpace(targetHost) || targetHost.Length < 5 ||
                string.IsNullOrWhiteSpace(targetClientId) || targetClientId.Length < 10 ||
                string.IsNullOrWhiteSpace(targetClientSecret) || targetClientSecret.Length < 10)
            {
                // Circuit Breaker: Halt right here. No local cache changes, no over-the-air packets!
                await DisplayAlertAsync("INPUT CRITERIA FAULT",
                    "All three configuration fields are strictly required and cannot be left empty or blank.\n\nPlease completely fill out the Hostname, Client ID, and Client Secret fields and try again.",
                    "OK");
                return;
            }

            Preferences.Default.Set("CloudflareHostKey", targetHost);
            Preferences.Default.Set("CloudflareClientIdKey", targetClientId);
            Preferences.Default.Set("CloudflareClientSecretKey", targetClientSecret);

            Debug.WriteLine("--> [PHONE DISK SUCCESS]: All three verified Cloudflare keys locked into application preference arrays.");

            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

            if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = true;

            await Task.Run(async () =>
            {
                await App.NetworkService.SendSecureCommandAsync(activeKey, $"SAVECFHOST={targetHost}");
                await Task.Delay(1200);

                await App.NetworkService.SendSecureCommandAsync(activeKey, $"SAVECFID={targetClientId}");
                await Task.Delay(1200);

                await App.NetworkService.SendSecureCommandAsync(activeKey, $"SAVECFSECRET={targetClientSecret}");
                await Task.Delay(1500);

                await App.NetworkService.ForceProactiveRebootRecoveryAsync();

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = false;

                    await DisplayAlertAsync("VAULT FLASH RECPT", "Your Cloudflare Zero-Trust machine passport credentials have been successfully committed to your vehicle module's non-volatile EEPROM scales!", "DONE");

                    await Navigation.PopAsync();
                });
            });
        }
        catch (Exception ex)
        {
            if (layoutRebootLockoutShell != null) layoutRebootLockoutShell.IsVisible = false;
            Debug.WriteLine($"--> [CLOUDFLARE WRITE CHOKE]: {ex.Message}");
            await DisplayAlertAsync("LINK FAULT", $"The transmission stream encountered an exception: {ex.Message}", "OK");
        }
    }

    ~AdminPage()
    {
        App.NetworkService.OnConnectionStateChanged -= OnVehicleLinkStateChanged;
        App.NetworkService.OnTelemetryReceived -= LogIncomingStreamToTerminal;

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

            App.NetworkService.IsRebootingWatchdogActive = false;
        }
    }
}