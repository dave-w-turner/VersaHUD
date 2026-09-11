using Plugin.BLE;
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
        await MainThread.InvokeOnMainThreadAsync(() =>
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

        await MainThread.InvokeOnMainThreadAsync(() =>
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

        await App.Log($"--> [PICKER ACTION]: Staging persistent storage commit for ID: {selectedDevice.Id}");

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
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await Application.Current.MainPage.DisplayAlertAsync("CONNECTION FAULT", "Could not save device information.", "OK");
                        return;
                    });
                }
            }
            await App.Log("--> [PICKER SUCCESS]: Hard disk serialization finalized cleanly.");
        }
#endif

        await MainThread.InvokeOnMainThreadAsync(async () =>
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
                await App.Log("--> [PICKER WATCHDOG]: First clean-install handshake timed out. Initializing silent stabilization retry...");
                await Task.Delay(500);
                pairingSuccess = await Task.Run(async () => await App.NetworkService.PairAndConnectDeviceAsync(selectedDevice));
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [PICKER EXCEPTION]: Bluetooth stack fault in Release Profile: {ex.Message}");
            pairingSuccess = false;
        }

        if (!pairingSuccess)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                listBleDevices.SelectedItem = null;
                await App.Log("--> [PICKER CRITICAL FAULT]: Second connection pass failed. Restoring view radar states...");
                await Application.Current.MainPage.DisplayAlertAsync("CONNECTION FAULT", "Cockpit connection timed out. Tap your device node to re-link.", "OK");

                IsVisible = true;
                await Task.Delay(100);

                await ExecuteVisualRadarScanAsync();
            });
        }
        else
        {
            await App.Log("--> [PICKER SUCCESS]: Handshake established successfully over stabilized channel lanes!");

            await MainThread.InvokeOnMainThreadAsync(async () =>
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

    private async void OnClosePickerOverlayClicked(object sender, EventArgs e)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            IsVisible = false;
            await App.Log("--> [UI CONTROL]: Device selection picker overlay hidden cleanly.");
        });
    }

    private static async Task<bool> GetBTPermissions()
    {
        try
        {
            var currentStatus = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();

            if (currentStatus != PermissionStatus.Granted)
            {
                await App.Log("--> [WATCHDOG]: System token validation missing. Launching dynamic hardware prompt...");

                PermissionStatus requestedStatus = PermissionStatus.Unknown;
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    requestedStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();
                });

                return requestedStatus == PermissionStatus.Granted;
            }

            return true;
        }
        catch (Exception ex)
        {
            await App.Log($"--> [PERMISSION SYSTEM EXCEPTION]: {ex.Message}");
            return false;
        }
    }

    public async Task InitializePickerLifecycleAsync()
    {
        try
        {
            bool isRadioHardwareActive = CrossBluetoothLE.Current.IsOn;

            if (!isRadioHardwareActive)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await MainPage.CurrentInstance.DisplayAlertAsync("BLUETOOTH REQUIRED", "VersaHUD cannot execute a visual radar refresh scan because your phone's Bluetooth radio switch is turned OFF.\n\nPlease ensure Bluetooth is active in your drop-down panel and try again.", "OK");
                });
                return;
            }

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

            if (isPermissionApproved)
            {
                await ExecuteVisualRadarScanAsync();
                return;
            }
            else
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await MainPage.CurrentInstance.DisplayAlertAsync("BLUETOOTH REQUIRED", "VersaHUD cannot execute a visual radar refresh scan because permissions were denied.\n\nPlease ensure you approve the rerquest for Bluetooth permissions.", "OK");
                });
            }
        }
        catch (Exception ex)
        {
            await App.Log($"--> [PICKER RUNTIME CRASH SHIELD]: {ex.Message}");
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
