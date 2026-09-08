using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.OS;

namespace VersaHUD;

[BroadcastReceiver(Name = "com.raddevelopment.versahub.BootReceiver", Enabled = true, Exported = true, DirectBootAware = true)]
[IntentFilter(new[] {
    Intent.ActionBootCompleted,
    Intent.ActionLockedBootCompleted,
    "android.intent.action.QUICKBOOT_POWERON",
    BluetoothAdapter.ActionStateChanged
})]
public class BootReceiver : BroadcastReceiver
{
    public override async void OnReceive(Context context, Intent intent)
    {
        await App.Log($"--> [HARDWARE MONITOR]: Intercepted native phone radio event: {intent.Action}");

        if (intent.Action == BluetoothAdapter.ActionStateChanged)
        {
            int stateCode = intent.GetIntExtra(BluetoothAdapter.ExtraState, BluetoothAdapter.Error);

            App.NetworkService.LastTransportSwitchTimestamp = DateTime.MinValue;

            if (stateCode == (int)State.On)
            {
                await App.Log("--> [HARDWARE MONITOR]: Bluetooth hardware initialized. Triggering rapid background reconnection pipeline...");

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);
                        await App.Log("--> [HARDWARE MONITOR]: Invoking AutoConnectAsync dynamically over active radio waves...");
                        
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            App.NetworkService.AutoConnectAsync(btAdapterOnOverride: true);
                        });
                    }
                    catch (Exception ex)
                    {
                        await App.Log($"--> [HARDWARE MONITOR RECOVERY CHOKE]: {ex.Message}");
                    }
                });
            }
            else if (stateCode == (int)State.Off || stateCode == (int)State.TurningOff)
            {
                await App.Log("--> [HARDWARE MONITOR]: Physical Bluetooth radio switch toggled OFF. Verifying active transport channels...");

                if (App.NetworkService != null)
                {
                    if (App.NetworkService.IsUsingWifiTransportMode || App.NetworkService.IsUsingCloudWanMode || App.NetworkService.IsUsingLocalApMode)
                    {
                        await App.Log("--> [HARDWARE MONITOR RADAR]: Bluetooth radio severed, but active Wi-Fi transport link is live! Suppressing disconnect alert.");
                        return;
                    }

                    await App.Log("--> [HARDWARE MONITOR CRITICAL]: Both networks dead. Purging residual wireless cache properties...");

                    App.NetworkService.StopRssiTracking();
                }
            }
            
            return;
        }

        if (intent.Action == Intent.ActionBootCompleted ||
            intent.Action == Intent.ActionLockedBootCompleted ||
            intent.Action == "android.intent.action.QUICKBOOT_POWERON")
        {
            Intent serviceIntent = new Intent(context, typeof(TelemetryForegroundService));

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                context.StartForegroundService(serviceIntent);
            }
            else
            {
                context.StartService(serviceIntent);
            }

            await App.Log("--> [HARDWARE MONITOR]: TelemetryForegroundService successfully launched on device boot.");
        }
    }
}
