using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VersaHUD;

public partial class AdminPage : ContentPage, INotifyPropertyChanged
{
    private new event PropertyChangedEventHandler PropertyChanged;
    private bool _isRouterConfigured = false;

    private CancellationTokenSource? _adminWifiWatchdogCancelSource;

    public AdminPage()
    {
        InitializeComponent();

        App.NetworkService.OnConnectionStateChanged -= OnVehicleLinkStateChanged;
        App.NetworkService.OnConnectionStateChanged += OnVehicleLinkStateChanged;

        MainPage.CurrentInstance?.OnTelemetryParsed -= LogIncomingStreamToTerminal;
        MainPage.CurrentInstance?.OnTelemetryParsed += LogIncomingStreamToTerminal;

        BindingContext = this;

        string runningHost = Preferences.Default.Get("CloudflareHostKey", "silent-bird-d9c0.taigon1984.workers.dev");
        string runningClientId = Preferences.Default.Get("CloudflareClientIdKey", "PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE");
        string runningSecret = Preferences.Default.Get("CloudflareClientSecretKey", "PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE");

        entryCfHost?.Text = runningHost.Equals("silent-bird-d9c0.taigon1984.workers.dev") ? "" : runningHost;
        entryCfClientId?.Text = runningClientId.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_ID_HERE") ? "" : runningClientId;
        entryCfClientSecret?.Text = runningSecret.Equals("PASTE_YOUR_CF_ACCESS_CLIENT_SECRET_HERE") ? "" : runningSecret;
    }

    private void LogIncomingStreamToTerminal(string rawPacket)
    {
        if (string.IsNullOrEmpty(rawPacket)) return;

        if ((App.NetworkService.IsUsingCloudWanMode || App.NetworkService.IsUsingWifiTransportMode) && rawPacket.StartsWith('{'))
            return;

        if (rawPacket.Contains("Rebooting controller..."))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (layoutRebootLockoutShell != null)
                {
                    layoutRebootLockoutShell.IsVisible = true;
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
                layoutRebootLockoutShell?.IsVisible = false;

                if (entryRouterPass != null)
                {
                    entryRouterPass.Text = string.Empty;
                    entryRouterPass.Focus();
                    entryRouterPass.IsEnabled = true;
                    entryRouterSSID.IsEnabled = true;
                    btnLinkToRouter.IsEnabled = true;
                }
                await Application.Current.MainPage.DisplayAlertAsync("ROUTER LINK FAILED", "The vehicle module could not establish an active wireless handshake with your home station. Verify your network credentials and try again.", "OK");
            });
            return;
        }

        if (rawPacket.Contains("[ADMIN_SUCCESS]: Router credentials stored."))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (entryRouterPass != null)
                {
                    entryRouterPass.Text = string.Empty;
                    entryRouterPass.IsEnabled = true;
                    entryRouterSSID.IsEnabled = true;
                    btnLinkToRouter.IsEnabled = true;
                }
                App.NetworkService.IsRebootingWatchdogActive = false;
                await Application.Current.MainPage.DisplayAlertAsync("ROUTER LINK SUCCESSFUL", "The vehicle module has successfully established a secure wireless handshake with your home station.", "OK");
            });
        }

        if (layoutRebootLockoutShell != null && layoutRebootLockoutShell.IsVisible)
        {
            if (rawPacket.Contains("[SYS]") || rawPacket.Contains("AP_NAME:") || rawPacket.Contains("BLE_NAME:") || rawPacket.Contains("ROUTER_SSID:"))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    layoutRebootLockoutShell.IsVisible = false;
                });
            }
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (lblDebugTerminal != null)
            {
                lblDebugTerminal.Text += $"\nrx: {rawPacket.Trim()}";

                if (lblDebugTerminal.Text.Length > 10000)
                {
                    lblDebugTerminal.Text = string.Concat("[SYS] Buffer optimized.\n", lblDebugTerminal.Text.AsSpan(lblDebugTerminal.Text.Length - 5000));
                }
            }

            if (switchAutoscroll != null && switchAutoscroll.IsToggled && scrollTerminal != null)
            {
                await scrollTerminal.ScrollToAsync(0, lblDebugTerminal.Height, true);
            }
        });

        if (App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingCloudWanMode) return;

        if (entryWifiAP.Text == "Loading..." || entryBleName.Text == "Loading..." || entryRouterSSID.Text == "Loading..." || rawPacket.Contains("CF_KEYS:"))
        {
            if (rawPacket.Contains("CF_KEYS:") && !rawPacket.Contains("ERR_EMPTY_VAULTS"))
            {
                try
                {
                    int payloadHeaderIndex = rawPacket.IndexOf("CF_KEYS:") + 8;
                    string base64CipherString = rawPacket[payloadHeaderIndex..].Trim();

                    string decryptedPlaintextBlock = Services.NetworkHubService.DecryptLocalPayloadAES128CBC(base64CipherString);

                    if (!string.IsNullOrWhiteSpace(decryptedPlaintextBlock) && decryptedPlaintextBlock.Contains(","))
                    {
                        string[] parameterSegments = decryptedPlaintextBlock.Split(',');

                        if (parameterSegments.Length == 3)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                entryCfHost?.Text = parameterSegments[0].Trim();
                                entryCfClientId?.Text = parameterSegments[1].Trim();
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"--> [ADMIN CRYPTO EXCEPTION]: Failure unpacking over-the-air parameters: {ex.Message}");
                }
            }

            int apIndex = rawPacket.IndexOf("AP_NAME:");
            int bleIndex = rawPacket.IndexOf("BLE_NAME:");
            int routerIndex = rawPacket.IndexOf("ROUTER_SSID:");

            if (apIndex != -1 || bleIndex != -1 || routerIndex != -1)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (apIndex != -1)
                    {
                        entryWifiAP.Text = rawPacket.Substring(apIndex + 8).Trim();
                    }

                    if (bleIndex != -1)
                    {
                        entryBleName.Text = rawPacket.Substring(bleIndex + 9).Trim();
                    }

                    if (routerIndex != -1)
                    {
                        string ssidResult = rawPacket.Substring(routerIndex + 12).Trim();

                        if (ssidResult.Contains("[❌ NONE SAVED]") || ssidResult.Contains("[X NONE SAVED]") || string.IsNullOrEmpty(ssidResult) || ssidResult.Contains("NONE"))
                        {
                            entryRouterSSID.Text = string.Empty;
                            _isRouterConfigured = false;
                            layoutUnconfiguredRouter.IsVisible = true;
                            layoutConfiguredRouter.IsVisible = false;
                        }
                        else
                        {
                            lblRouterSSID.Text = ssidResult;
                            _isRouterConfigured = true;
                            layoutUnconfiguredRouter.IsVisible = false;
                            layoutConfiguredRouter.IsVisible = true;
                        }
                    }
                });
            }
        }
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
                }

                await Task.Delay(400);

                if (App.NetworkService != null && !App.NetworkService.IsRebootingWatchdogActive)
                {
                    if (!(App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingCloudWanMode))
                    {
                        string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
                        Debug.WriteLine("--> [ADMIN CONTROL HUB]: Executing post-reboot automated BLE serial command sync pass...");

                        await App.NetworkService.SendSecureCommandAsync(activeKey, "GETROUTER");
                        await Task.Delay(150);
                        await App.NetworkService.SendSecureCommandAsync(activeKey, "GETWIFINAME");
                        await Task.Delay(150);
                        await App.NetworkService.SendSecureCommandAsync(activeKey, "GETCFKEYS");
                    }
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
                    storageEditor.PutString("MasterPasswordKey", newPassInput);
                    storageEditor.Apply();
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
                    entryRouterSSID.Text = selectedSSID;
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

            entryRouterSSID.Text = string.Empty;
            _isRouterConfigured = false;
            layoutUnconfiguredRouter.IsVisible = true;
            layoutConfiguredRouter.IsVisible = false;

            await DisplayAlertAsync("WIPE COMMAND FIRED", "The vehicle module is erasing credentials and performing a clean reboot now.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
        }
    }

    private async void OnRebootControllerClicked(object sender, EventArgs e)
    {
        bool doubleCheck = await DisplayAlertAsync("REBOOT CONTROLLER",
            "This will force a hard reboot of the vehicle module. Proceed?",
            "REBOOT", "CANCEL");

        if (!doubleCheck) return;

        string currentActiveKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");
        bool commandWasDelivered = await App.NetworkService.SendSecureCommandAsync(currentActiveKey, "REBOOT");

        if (commandWasDelivered)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                await App.NetworkService.ForceProactiveRebootRecoveryAsync();
            });

            entryRouterSSID.Text = string.Empty;
            _isRouterConfigured = false;
            layoutUnconfiguredRouter.IsVisible = true;
            layoutConfiguredRouter.IsVisible = false;

            await DisplayAlertAsync("WIPE COMMAND FIRED", "The vehicle module is erasing credentials and performing a clean reboot now.", "OK");
        }
        else
        {
            await DisplayAlertAsync("LINK FAULT", "Could not deliver the parameters update packet. Verify your active communication transport channels are clear and try again.", "OK");
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
                await DisplayAlertAsync("INPUT CRITERIA FAULT", "All three configuration fields are strictly required and cannot be left blank.", "OK");
                return;
            }

            Preferences.Default.Set("CloudflareHostKey", targetHost);
            Preferences.Default.Set("CloudflareClientIdKey", targetClientId);
            Preferences.Default.Set("CloudflareClientSecretKey", targetClientSecret);

            string activeKey = Preferences.Default.Get(Controls.InitMasterPassword.MasterPasswordKey, "VersaPasscode99");

            await Task.Run(async () =>
            {
                string unifiedCloudflarePayload = $"SAVECFKEYS={targetHost},{targetClientId},{targetClientSecret}";

                layoutRebootLockoutShell?.IsVisible = true;

                await Task.Run(async () =>
                {
                    await App.NetworkService.SendSecureCommandAsync(activeKey, unifiedCloudflarePayload);
                    await Task.Delay(5000);
                    await App.NetworkService.ForceProactiveRebootRecoveryAsync();

                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        layoutRebootLockoutShell?.IsVisible = false;
                        await DisplayAlertAsync("VAULT FLASH SUCCESS", "Your complete Cloudflare Zero-Trust machine passport credentials have been successfully flashed into your vehicle module's persistent memory vaults!", "DONE");
                        await Navigation.PopAsync();
                    });
                });
            });
        }
        catch (Exception ex)
        {
            layoutRebootLockoutShell?.IsVisible = false;
            Debug.WriteLine($"--> [CLOUDFLARE WRITE CHOKE]: {ex.Message}");
            await DisplayAlertAsync("LINK FAULT", $"The transmission stream encountered an exception: {ex.Message}", "OK");
        }
    }
    
    protected new void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (App.NetworkService != null && App.NetworkService.IsRebootingWatchdogActive)
        {
            layoutRebootLockoutShell?.IsVisible = true;
            return;
        }
        else
            layoutRebootLockoutShell?.IsVisible = false;

        await Task.Delay(300);

        if (App.NetworkService != null && !App.NetworkService.IsRebootingWatchdogActive)
        {
            if (App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingCloudWanMode)
            {
                Debug.WriteLine("--> [ADMIN CONTROL HUB]: Fetching clean configuration matrices straight from API...");

                var (wifiAp, bleName, routerSsid, cfHost, cfId, isOk) = App.NetworkService.IsUsingWifiTransportMode ?
                    await Services.NetworkHubService.FetchWifiAdminParametersAsync() : await Services.NetworkHubService.FetchCloudAdminParametersAsync();

                if (isOk)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        entryWifiAP.Text = wifiAp;
                        entryBleName.Text = bleName;
                        entryCfHost.Text = cfHost.Equals("silent-bird-d9c0.taigon1984.workers.dev") ? string.Empty : cfHost;
                        entryCfClientId.Text = cfId.Equals("NONE") ? string.Empty : cfId;


                        if (routerSsid == "NONE" || string.IsNullOrEmpty(routerSsid))
                        {
                            entryRouterSSID.Text = string.Empty;
                            _isRouterConfigured = false;
                            layoutUnconfiguredRouter.IsVisible = true;
                            layoutConfiguredRouter.IsVisible = false;
                        }
                        else
                        {
                            entryRouterSSID.Text = routerSsid;
                            _isRouterConfigured = true;
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
            await Task.Delay(500);
            await App.NetworkService.SendSecureCommandAsync(activeKey, "GETBLENAME");
            await Task.Delay(500);
            await App.NetworkService.SendSecureCommandAsync(activeKey, "GETROUTER");
            await Task.Delay(500);
            await App.NetworkService.SendSecureCommandAsync(activeKey, "GETCFKEYS");
            await Task.Delay(500);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _adminWifiWatchdogCancelSource?.Cancel();
        _adminWifiWatchdogCancelSource = null;
    }

    ~AdminPage()
    {
        App.NetworkService.OnConnectionStateChanged -= OnVehicleLinkStateChanged;
        App.NetworkService.OnTelemetryReceived -= LogIncomingStreamToTerminal;
    }
}