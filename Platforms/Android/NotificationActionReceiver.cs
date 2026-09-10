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
                try
                {
                    await App.Log("--> [NOTIFICATION RECEIVER]: Dispatching secure over-the-air LOCK token packet...");
                    await App.NetworkService.SendSecureCommandAsync(activeKey, "LOCK");
                }
                catch (Exception ex)
                {
                    await App.Log($"--> [NOTIFICATION RECEIVER CHOKE]: {ex.Message}");

                    if (App.Current?.MainPage != null)
                    {
                        await App.Current.MainPage.DisplayAlertAsync("COMMAND FAILURE", "Unable to deliver the LOCK command! Please check your connection.", "OK");
                    }
                }                
            });
        }
        else if (action == "VERSAHUD_ACTION_UNLOCK")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await App.Log("--> [NOTIFICATION RECEIVER]: Dispatching secure over-the-air UNLOCK token packet...");
                    await App.NetworkService.SendSecureCommandAsync(activeKey, "UNLOCK");
                }
                catch (Exception ex)
                {
                    await App.Log($"--> [NOTIFICATION RECEIVER CHOKE]: {ex.Message}");

                    if (App.Current?.MainPage != null)
                    {
                        await App.Current.MainPage.DisplayAlertAsync("COMMAND FAILURE", "Unable to deliver the UNLOCK command! Please check your connection.", "OK");
                    }
                }
            });
        }
    }
}
