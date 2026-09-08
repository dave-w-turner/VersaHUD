using System.Diagnostics;
namespace VersaHUD.Services;

public class CockpitTelemetryModule
{
    private bool _isModuleRunning = false;

    public async void Initialize()
    {
        await App.Log("--> [TELEMETRY MODULE]: Ground systems initialized. Registering event pipelines...");
        App.NetworkService.OnTelemetryReceived += ProcessIncomingAirwavesFrame;
    }

    public async Task StartAsync()
    {
        if (_isModuleRunning) return;

#if ANDROID
        await App.Log("--> [TELEMETRY MODULE]: Executing pre-flight Target SDK 36 validation gates...");

        var notificationStatus = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (notificationStatus != PermissionStatus.Granted)
        {
            notificationStatus = await Permissions.RequestAsync<Permissions.PostNotifications>();
        }   

        if (notificationStatus == PermissionStatus.Granted)
        {
            var androidContext = Android.App.Application.Context;

            var serviceIntent = new Android.Content.Intent(androidContext, typeof(TelemetryForegroundService));

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            {
                androidContext.StartForegroundService(serviceIntent);
            }
            else
            {
                androidContext.StartService(serviceIntent);
            }

            _isModuleRunning = true;
            await App.Log("--> [TELEMETRY MODULE]: Persistent background lock screen panel successfully deployed.");
        }
        else
        {
            await App.Log("--> [TELEMETRY MODULE CRITICAL]: Deployment halted due to missing PostNotifications clearance.");
        }
#endif
    }

    private async void ProcessIncomingAirwavesFrame(string rawPacket)
    {
        if (string.IsNullOrEmpty(rawPacket)) return;

        await App.Log($"--> [TELEMETRY MODULE RX]: Routed {rawPacket.Length} data bytes to status drawer.");
    }

    public async void Shutdown()
    {
        App.NetworkService.OnTelemetryReceived -= ProcessIncomingAirwavesFrame;
        _isModuleRunning = false;
        await App.Log("--> [TELEMETRY MODULE]: Channels safely de-provisioned.");
    }
}
