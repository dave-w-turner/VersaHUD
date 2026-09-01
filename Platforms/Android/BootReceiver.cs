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
    public override void OnReceive(Context context, Intent intent)
    {
        System.Diagnostics.Debug.WriteLine($"--> [HARDWARE MONITOR]: Intercepted native phone radio event: {intent.Action}");

        if (intent.Action == BluetoothAdapter.ActionStateChanged)
        {
            int stateCode = intent.GetIntExtra(BluetoothAdapter.ExtraState, BluetoothAdapter.Error);

            if (stateCode == (int)State.On)
            {
                System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR]: Bluetooth hardware initialized. Triggering rapid background reconnection pipeline...");

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);

                        if (App.NetworkService != null && !App.NetworkService.IsUsingWifiTransportMode)
                        {
                            System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR]: Invoking AutoConnectAsync dynamically over active radio waves...");
                            if (await App.NetworkService.AutoConnectAsync())
                                App.NetworkService.RaiseConnectionStateChangedProxy(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"--> [HARDWARE MONITOR RECOVERY CHOKE]: {ex.Message}");
                    }
                });
            }
            else if (stateCode == (int)State.Off || stateCode == (int)State.TurningOff)
            {
                System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR]: Physical Bluetooth radio switch toggled OFF. Verifying active transport channels...");

                if (App.NetworkService != null)
                {
                    if (App.NetworkService.IsUsingWifiTransportMode)
                    {
                        System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR RADAR]: Bluetooth radio severed, but active Wi-Fi transport link is live! Suppressing disconnect alert.");
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR CRITICAL]: Both networks dead. Purging residual wireless cache properties...");

                    App.NetworkService.ActiveRssi = 0;
                    App.NetworkService.RaiseConnectionStateChangedProxy(false);
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

            System.Diagnostics.Debug.WriteLine("--> [HARDWARE MONITOR]: TelemetryForegroundService successfully launched on device boot.");
        }
    }
}
