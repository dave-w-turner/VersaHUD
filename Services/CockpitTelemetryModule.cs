using System.Diagnostics;
namespace VersaHUD.Services;

public class CockpitTelemetryModule
{
    private bool _isModuleRunning = false;

    public void Initialize()
    {
        Debug.WriteLine("--> [TELEMETRY MODULE]: Ground systems initialized. Registering event pipelines...");
        App.BluetoothService.OnTelemetryReceived += ProcessIncomingAirwavesFrame;
    }

    public async Task StartAsync()
    {
        if (_isModuleRunning) return;

#if ANDROID
        Debug.WriteLine("--> [TELEMETRY MODULE]: Executing pre-flight Target SDK 36 validation gates...");

        var notificationStatus = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (notificationStatus != PermissionStatus.Granted)
        {
            notificationStatus = await Permissions.RequestAsync<Permissions.PostNotifications>();
        }   

        if (notificationStatus == PermissionStatus.Granted)
        {
            var androidContext = Android.App.Application.Context;

            var serviceIntent = new Android.Content.Intent(androidContext, typeof(VersaHUD.TelemetryForegroundService));

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            {
                androidContext.StartForegroundService(serviceIntent);
            }
            else
            {
                androidContext.StartService(serviceIntent);
            }

            _isModuleRunning = true;
            Debug.WriteLine("--> [TELEMETRY MODULE]: Persistent background lock screen panel successfully deployed.");
        }
        else
        {
            Debug.WriteLine("--> [TELEMETRY MODULE CRITICAL]: Deployment halted due to missing PostNotifications clearance.");
        }
#endif
    }

    private void ProcessIncomingAirwavesFrame(string rawPacket)
    {
        if (string.IsNullOrEmpty(rawPacket)) return;

        Debug.WriteLine($"--> [TELEMETRY MODULE RX]: Routed {rawPacket.Length} data bytes to status drawer.");
    }

    public void Shutdown()
    {
        App.BluetoothService.OnTelemetryReceived -= ProcessIncomingAirwavesFrame;
        _isModuleRunning = false;
        Debug.WriteLine("--> [TELEMETRY MODULE]: Channels safely de-provisioned.");
    }
}
