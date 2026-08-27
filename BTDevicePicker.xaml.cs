namespace VersaHUD.Controls;

public partial class BTDevicePicker : ContentView
{
	public BTDevicePicker()
	{
		InitializeComponent();

        listBleDevices.ItemsSource = App.BluetoothService?.DiscoveredDevices;
    }

    public async Task InitializePickerLifecycleAsync()
    {
        try
        {
#if ANDROID
            System.Diagnostics.Debug.WriteLine("--> [PICKER WATCHDOG]: Resolving custom ModernBluetooth runtime permissions matrix...");

            var scanStatus = await Permissions.CheckStatusAsync<ModernBluetooth>();

            if (scanStatus != PermissionStatus.Granted)
            {
                scanStatus = await Permissions.RequestAsync<ModernBluetooth>();
            }

            if (scanStatus != PermissionStatus.Granted)
            {
                await Application.Current.MainPage.DisplayAlertAsync("PERMISSION REQUIRED",
                    "Android requires Nearby Devices authorization to link with your Nissan console.", "OK");
                return;
            }

            await Task.Delay(300);
#endif

            bool isReconnected = await App.BluetoothService.AutoConnectAsync(); 
            
            if (!isReconnected)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    this.IsVisible = true;
                    this.InvalidateMeasure();
                    await ExecuteVisualRadarScanAsync();
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"--> [PICKER RUNTIME CRASH SHIELD]: {ex.Message}");
        }
    }

    private async void OnBleDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not Plugin.BLE.Abstractions.Contracts.IDevice selectedDevice) return;

        indicatorScanning.IsRunning = false;
        indicatorScanning.IsVisible = false;

        System.Diagnostics.Debug.WriteLine($"--> [PICKER ACTION]: Staging persistent storage commit for ID: {selectedDevice.Id}");

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
                else throw new Exception("Failed to acquire native shared preferences editor for disk commit.");
            }
            System.Diagnostics.Debug.WriteLine("--> [PICKER SUCCESS]: Hard disk serialization finalized cleanly.");
        }
#endif
        if (this.Parent is Grid parentGrid) parentGrid.IsVisible = false;

        bool pairingSuccess = await App.BluetoothService.PairAndConnectDeviceAsync(selectedDevice);
        
        if (!pairingSuccess)
        {
            System.Diagnostics.Debug.WriteLine("--> [PICKER WATCHDOG]: First clean-install handshake timed out. Initializing silent stabilization retry...");

            await Task.Delay(500);

            pairingSuccess = await App.BluetoothService.PairAndConnectDeviceAsync(selectedDevice);
        }

        if (!pairingSuccess)
        {
            listBleDevices.SelectedItem = null;

            System.Diagnostics.Debug.WriteLine("--> [PICKER CRITICAL FAULT]: Second connection pass failed. Restoring view radar states...");

            if (this.Parent is Grid pGrid) pGrid.IsVisible = true;
            await ExecuteVisualRadarScanAsync();
            
            await Application.Current.MainPage.DisplayAlertAsync("CONNECTION FAULT", "Cockpit connection timed out. Tap your device node to re-link.", "OK");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("--> [PICKER SUCCESS]: Handshake established successfully over stabilized channel lanes!");

            if (this.Parent is Grid pGrid) pGrid.IsVisible = false;

            if (Shell.Current?.CurrentPage is MainPage mainPage)
            {
                await mainPage.VerifyPasswordAgainstHardwareAsync();
            }

            listBleDevices.SelectedItem = null;
        }
    }

    private async Task ExecuteVisualRadarScanAsync()
    {
        bool isPermissionApproved = false;

#if ANDROID
        var nativeAndroidContext = Android.App.Application.Context;

        bool hasNativeScanClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothScan) == Android.Content.PM.Permission.Granted;
        bool hasNativeConnectClearance = nativeAndroidContext.CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) == Android.Content.PM.Permission.Granted;

        if (!hasNativeScanClearance || !hasNativeConnectClearance)
        {
            System.Diagnostics.Debug.WriteLine("--> [RADAR SECURITY INTERCEPT]: Hardware tokens flushed out via radio cycle. Forcing native re-request...");

            var forcedStatus = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                return await Permissions.RequestAsync<Permissions.Bluetooth>();
            });
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

        if (!isPermissionApproved)
        {
            System.Diagnostics.Debug.WriteLine("--> [RADAR HALTED]: Missing Bluetooth security permissions to communicate with physical antennas.");

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (indicatorScanning != null)
                {
                    indicatorScanning.IsRunning = false;
                    indicatorScanning.IsVisible = false;
                }

                var structuralShellPage = Application.Current?.MainPage;
                if (structuralShellPage != null)
                {
                    await structuralShellPage.DisplayAlert("PERMISSIONS REQUIRED", "VersaHUD cannot execute its scanning radar because the application lacks active hardware Bluetooth permissions.", "OK");
                }
            });
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (indicatorScanning != null)
            {
                indicatorScanning.IsVisible = true;
                indicatorScanning.IsRunning = true;
            }
        });

        await App.BluetoothService.StartDiscoveryScanAsync();

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
        await ExecuteVisualRadarScanAsync();
    }
}

#if ANDROID
public class ModernBluetooth : Microsoft.Maui.ApplicationModel.Permissions.BasePlatformPermission
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
    public class ModernBluetooth : Microsoft.Maui.ApplicationModel.Permissions.BasePlatformPermission { }
#endif
