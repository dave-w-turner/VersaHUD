using System.Diagnostics;

namespace VersaHUD.Controls;

public partial class BTDevicePicker : ContentView
{
    public BTDevicePicker()
	{
		InitializeComponent();
        listBleDevices.ItemsSource = App.NetworkService?.DiscoveredDevices;
    }

    private async Task ExecuteVisualRadarScanAsync()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (indicatorScanning != null)
            {
                indicatorScanning.IsVisible = true;
                indicatorScanning.IsRunning = true;
            }
        });

        await App.NetworkService.StartDiscoveryScanAsync();

        int activeScanTimeoutMs = 6000;
        await Task.Delay(activeScanTimeoutMs);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (indicatorScanning != null)
            {
                indicatorScanning.IsRunning = false;
                indicatorScanning.IsVisible = false;
            }
        });
    }

    private async void OnRefreshScanClicked(object sender, EventArgs e)
    {
        if (!await GetBTPermissions())
            return;

        await Task.Delay(100);
        await ExecuteVisualRadarScanAsync();
    }

    private async void OnBleDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not Plugin.BLE.Abstractions.Contracts.IDevice selectedDevice) return;

        indicatorScanning.IsRunning = false;
        indicatorScanning.IsVisible = false;

        Debug.WriteLine($"--> [PICKER ACTION]: Staging persistent storage commit for ID: {selectedDevice.Id}");

        Preferences.Default.Set(MainPage.SavedDeviceMacKey, selectedDevice.Id.ToString());
        Preferences.Default.Set(MainPage.SavedDeviceNameKey, selectedDevice.Name ?? "VersaHub_BLE");

#if ANDROID
        var nativeSharedPrefs = Android.App.Application.Context.GetSharedPreferences("Microsoft.Maui.Essentials", Android.Content.FileCreationMode.Private);
        if (nativeSharedPrefs != null)
        {
            using (var preferenceDiskEditor = nativeSharedPrefs.Edit())
            {
                if (preferenceDiskEditor != null)
                {
                    preferenceDiskEditor.PutString(MainPage.SavedDeviceMacKey, selectedDevice.Id.ToString());
                    preferenceDiskEditor.PutString(MainPage.SavedDeviceNameKey, selectedDevice.Name ?? "VersaHub_BLE");
                    preferenceDiskEditor.Commit();
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await Application.Current.MainPage.DisplayAlertAsync("CONNECTION FAULT", "Could not save device information.", "OK");
                        return;
                    });
                }
            }
            Debug.WriteLine("--> [PICKER SUCCESS]: Hard disk serialization finalized cleanly.");
        }
#endif

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            IsVisible = false;
        });

        await Task.Delay(100);

        bool pairingSuccess = false;
        try
        {
            pairingSuccess = await Task.Run(async () => await App.NetworkService.PairAndConnectDeviceAsync(selectedDevice));

            if (!pairingSuccess)
            {
                Debug.WriteLine("--> [PICKER WATCHDOG]: First clean-install handshake timed out. Initializing silent stabilization retry...");
                await Task.Delay(500);
                pairingSuccess = await Task.Run(async () => await App.NetworkService.PairAndConnectDeviceAsync(selectedDevice));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [PICKER EXCEPTION]: Bluetooth stack fault in Release Profile: {ex.Message}");
            pairingSuccess = false;
        }

        if (!pairingSuccess)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                listBleDevices.SelectedItem = null;
                Debug.WriteLine("--> [PICKER CRITICAL FAULT]: Second connection pass failed. Restoring view radar states...");
                await Application.Current.MainPage.DisplayAlertAsync("CONNECTION FAULT", "Cockpit connection timed out. Tap your device node to re-link.", "OK");

                IsVisible = true;
                await Task.Delay(100);

                await ExecuteVisualRadarScanAsync();
            });
        }
        else
        {
            Debug.WriteLine("--> [PICKER SUCCESS]: Handshake established successfully over stabilized channel lanes!");

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (Shell.Current?.CurrentPage is MainPage mainPage)
                {
                    await Task.Delay(1000);
                    App.NetworkService.StartConnectionSupervisor();
                }

                listBleDevices.SelectedItem = null;
            });
        }
    }

    private void OnClosePickerOverlayClicked(object sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsVisible = false;
            Debug.WriteLine("--> [UI CONTROL]: Device selection picker overlay hidden cleanly.");
        });
    }

    private async Task<bool> GetBTPermissions()
    {
        await Task.Delay(250);
        bool isPermissionApproved = false;

#if ANDROID
        var nativeAndroidContext = Android.App.Application.Context;
        bool hasNativeScanClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothScan) == Android.Content.PM.Permission.Granted;
        bool hasNativeConnectClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) == Android.Content.PM.Permission.Granted;

        if (!hasNativeScanClearance || !hasNativeConnectClearance)
        {
            Debug.WriteLine("--> [WATCHDOG]: System token validation missing. Requesting dynamic hardware tracking permissions...");

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var forcedStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();
                isPermissionApproved = forcedStatus == PermissionStatus.Granted;
            });
        }
        else
        {
            isPermissionApproved = true;
        }
#else
            var fallbackStatus = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();
            isPermissionApproved = (fallbackStatus == PermissionStatus.Granted);
#endif

        return isPermissionApproved;
    }

    public async Task InitializePickerLifecycleAsync()
    {
        try
        {
            bool isRadioHardwareActive = Plugin.BLE.CrossBluetoothLE.Current.IsOn;
            bool isPermissionApproved = await GetBTPermissions();

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                IsVisible = true;
                InvalidateMeasure();

                if (indicatorScanning != null)
                {
                    indicatorScanning.IsRunning = false;
                    indicatorScanning.IsVisible = false;
                }
            });

            if (isPermissionApproved && isRadioHardwareActive)
            {
                await ExecuteVisualRadarScanAsync();
                return;
            }
            else
            {
                Debug.WriteLine("--> [CRITICAL SELECTION BLOCK]: Refresh scan blocked. Permission Approved: " + isPermissionApproved + " | Radio Active: " + isRadioHardwareActive);
                await MainPage.CurrentInstance.DisplayAlertAsync("BLUETOOTH REQUIRED", "VersaHUD cannot execute a visual radar refresh scan because your phone's Bluetooth radio switch is turned OFF or permissions were denied.\n\nPlease ensure Bluetooth is active in your drop-down panel and try again.", "OK");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> [PICKER RUNTIME CRASH SHIELD]: {ex.Message}");
        }
    }

    public async Task TriggerRefreshScan()
    {
        await InitializePickerLifecycleAsync();
    }
}

#if ANDROID
public class ModernBluetooth : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new (string, bool)[]
        {
            ("android.permission.BLUETOOTH_SCAN", true),
            ("android.permission.BLUETOOTH_CONNECT", true),
            ("android.permission.BLUETOOTH_ADVERTISE", true)
        };
}
#else
    public class ModernBluetooth : Permissions.BasePlatformPermission { }
#endif
