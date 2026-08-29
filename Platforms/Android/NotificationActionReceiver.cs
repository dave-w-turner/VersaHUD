using Android.Content;
using System.Diagnostics;

namespace VersaHUD;

[BroadcastReceiver(Name = "com.raddevelopment.versahub.NotificationActionReceiver", Enabled = true, Exported = false)]
public class NotificationActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context context, Intent intent)
    {
        string action = intent.Action;
        if (string.IsNullOrEmpty(action)) return;
        string preferencesFileName = $"{context.PackageName}.preferences";
        var nativePreferences = context.GetSharedPreferences(preferencesFileName, FileCreationMode.Private);

        string activeKey = nativePreferences?.GetString("MasterPasswordKey", "VersaPasscode99") ?? "VersaPasscode99";

        if (action == "VERSAHUD_ACTION_LOCK")
        {
            _ = Task.Run(async () =>
            {
                await App.NetworkService.SendSecureCommandAsync(activeKey, "LOCK");
            });
        }
        else if (action == "VERSAHUD_ACTION_UNLOCK")
        {
            _ = Task.Run(async () =>
            {
                await App.NetworkService.SendSecureCommandAsync(activeKey, "UNLOCK");
            });
        }
    }
}
