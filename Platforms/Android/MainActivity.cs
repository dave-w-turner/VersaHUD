using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace VersaHUD;

[Activity(Theme = "@style/Maui.SplashTheme",
          MainLauncher = true,
          LaunchMode = LaunchMode.SingleTop,
          ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.PostNotifications>();
        }

        if (status == PermissionStatus.Granted)
        {
            Intent serviceIntent = new Intent(this, typeof(TelemetryForegroundService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                this.StartForegroundService(serviceIntent);
            }
            else
            {
                this.StartService(serviceIntent);
            }
        }
    }
}
