using Android.App;
using Android.Content;
using Android.OS;
using System.Text.RegularExpressions;

namespace VersaHUD;

[Service(Name = "com.raddevelopment.versahub.TelemetryForegroundService", Enabled = true, Exported = false)]
public class TelemetryForegroundService : Service
{
    private const string CHANNEL_ID = "versahud_cockpit_channel";
    private const int NOTIFICATION_ID = 90210;
    private NotificationManager _notificationManager;

    private static readonly Regex FrontRegex = new Regex(@"Front:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)", RegexOptions.Compiled);
    private static readonly Regex BackRegex = new Regex(@"Back:\s*(?:\[[^\]]+\]\s*)?(?<volts>[\d.]+)\s*V\s*\((?<percent>\d+)%\)", RegexOptions.Compiled);

    private float _frontVolts = 0f;
    private int _frontPercent = 0;
    private float _backVolts = 0f;
    private int _backPercent = 0;
    private bool _isFrontCharging = false;
    private bool _isTrunkCharging = false;

    private BootReceiver? _dynamicBluetoothStateReceiver;

    private Notification BuildTelemetryStatusNotification()
    {
        Notification.Builder builder = (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            ? new Notification.Builder(this, CHANNEL_ID)
            : new Notification.Builder(this);

        Intent launchAppIntent = new Intent(this, typeof(MainActivity));
        launchAppIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);

        PendingIntent pendingContentIntent = PendingIntent.GetActivity(
            this,
            0,
            launchAppIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        builder.SetContentIntent(pendingContentIntent);

        bool isBluetoothConnected = App.NetworkService != null && App.NetworkService.IsBluetoothConnected;
        bool isSystemTotallyOffline = !isBluetoothConnected && !App.NetworkService.IsUsingWifiTransportMode && !App.NetworkService.IsUsingCloudWanMode;

        var multiLineTextStyle = new Notification.BigTextStyle();

        Notification.Action lockActionRow;
        Notification.Action unlockActionRow;

        if (isSystemTotallyOffline)
        {
            _frontVolts = 0.0f;
            _frontPercent = 0;
            _backVolts = 0.0f;
            _backPercent = 0;
            _isFrontCharging = false;
            _isTrunkCharging = false;

            string emptyProgressIndicator = GenerateVisualProgressIndicatorMeter(0);

            multiLineTextStyle.BigText(
                $"FRONT BATTERY   ::   0.0V  ( 0% )   {emptyProgressIndicator}   🔋 [ OFFLINE ]\n" +
                $"TRUNK BATTERY   ::   0.0V  ( 0% )   {emptyProgressIndicator}   🔋 [ OFFLINE ]\n\n" +
                "⚠️ COCKPIT TELEMETRY DATA LINK SEVERED — LINK LOST\n" +
                "Enable phone Bluetooth or reconnect to vehicle Wi-Fi subnet.");

            builder.SetContentTitle("🚨 VERSA HUD — LINK LOST");

            lockActionRow = new Notification.Action.Builder(
                global::Android.Resource.Drawable.IcMenuInfoDetails,
                "🔑 LOCK (OFFLINE)",
                null).Build();

            unlockActionRow = new Notification.Action.Builder(
                global::Android.Resource.Drawable.IcMenuShare,
                "🔓 UNLOCK (OFFLINE)",
                null).Build();
        }
        else
        {
            string fProgressIndicator = GenerateVisualProgressIndicatorMeter(_frontPercent);
            string bProgressIndicator = GenerateVisualProgressIndicatorMeter(_backPercent);

            string fChargingFlag = _isFrontCharging ? "⚡ [ CHARGING ]" : "🔋 [ IDLE ] ";
            string bSystemFlag = _isTrunkCharging ? "⚡ [ CHARGING ]" : "🔋 [ IDLE ] ";

            float fDisplayVolts = _frontVolts;
            float bDisplayVolts = _backVolts;
            int fDisplayPercent = _frontPercent;
            int bDisplayPercent = _backPercent;

            multiLineTextStyle.BigText(
                $"FRONT BATTERY   ::   {fDisplayVolts:F1}V  ({fDisplayPercent,3}%)   {fProgressIndicator}   {fChargingFlag}\n" +
                $"TRUNK BATTERY   ::   {bDisplayVolts:F1}V  ({bDisplayPercent,3}%)   {bProgressIndicator}   {bSystemFlag}");

            builder.SetContentTitle("📡 VERSA HUD STATUS");

            Intent lockIntent = new Intent(this, typeof(NotificationActionReceiver)).SetAction("VERSAHUD_ACTION_LOCK");
            PendingIntent pLock = PendingIntent.GetBroadcast(this, 1, lockIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            Intent unlockIntent = new Intent(this, typeof(NotificationActionReceiver)).SetAction("VERSAHUD_ACTION_UNLOCK");
            PendingIntent pUnlock = PendingIntent.GetBroadcast(this, 2, unlockIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            lockActionRow = new Notification.Action.Builder(
                global::Android.Resource.Drawable.IcMenuInfoDetails,
                "🔑 LOCK DOORS",
                pLock).Build();

            unlockActionRow = new Notification.Action.Builder(
                global::Android.Resource.Drawable.IcMenuShare,
                "🔓 UNLOCK DOORS",
                pUnlock).Build();
        }

        int customIconId = ApplicationContext.Resources.GetIdentifier("versahud_cockpit_badge", "drawable", PackageName);
        if (customIconId == 0) customIconId = global::Android.Resource.Drawable.IcMenuManage;

        builder.SetSmallIcon(customIconId)
               .SetStyle(multiLineTextStyle)
               .AddAction(lockActionRow)
               .AddAction(unlockActionRow)
               .SetOngoing(true)
               .SetVisibility(NotificationVisibility.Public);

        Notification finalNotification = builder.Build();
        finalNotification.Flags |= NotificationFlags.ForegroundService | NotificationFlags.NoClear | NotificationFlags.OngoingEvent;

        return finalNotification;
    }

    private static string GenerateVisualProgressIndicatorMeter(int percentageValue)
    {
        int totalSegments = 15;
        int activeSegments = (int)Math.Round((percentageValue / 100.0) * totalSegments);
        activeSegments = Math.Clamp(activeSegments, 0, totalSegments);

        string filledTrack = new('❚', activeSegments);
        string emptyTrack = new('┄', totalSegments - activeSegments);

        return $"⦗{filledTrack}{emptyTrack}⦘";
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(CHANNEL_ID, "Cockpit Telemetry Monitor", NotificationImportance.Min)
            {
                Description = "Displays live vehicle battery metrics and locking control switches."
            };

            channel.SetSound(null, null);
            channel.EnableVibration(false);
            channel.LockscreenVisibility = NotificationVisibility.Public;

            var manager = (NotificationManager)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
    }

    private void OnTelemetryReceivedUpdateWidget(string rawPacket)
    {
        if (string.IsNullOrEmpty(rawPacket)) return;

        if (rawPacket.Trim().StartsWith('{'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rawPacket);
                var root = doc.RootElement;
                _frontVolts = root.TryGetProperty("front_v", out var fv) ? (float)fv.GetDouble() : 0f;
                _frontPercent = root.TryGetProperty("front_p", out var fp) ? fp.GetInt32() : 0;
                _backVolts = root.TryGetProperty("background_v", out var bv) ? (float)bv.GetDouble() : 0f;
                _backPercent = root.TryGetProperty("back_p", out var bp) ? bp.GetInt32() : 0;

                _isFrontCharging = root.TryGetProperty("charging_f", out var cf) && cf.GetBoolean();
                _isTrunkCharging = root.TryGetProperty("charging_b", out var cb) && cb.GetBoolean();
            }
            catch { return; }
        }
        else
        {
            Match fMatch = FrontRegex.Match(rawPacket);
            if (fMatch.Success)
            {
                _frontVolts = float.Parse(fMatch.Groups["volts"].Value);
                _frontPercent = Math.Clamp(int.Parse(fMatch.Groups["percent"].Value), 0, 100);
            }
            Match bMatch = BackRegex.Match(rawPacket);
            if (bMatch.Success)
            {
                _backVolts = float.Parse(bMatch.Groups["volts"].Value);
                _backPercent = Math.Clamp(int.Parse(bMatch.Groups["percent"].Value), 0, 100);
            }

            _isFrontCharging = rawPacket.Contains("Front: [🔋 CHARGING]") || rawPacket.Contains("charging_f\":true");
            _isTrunkCharging = rawPacket.Contains("Back: [🔋 CHARGING]") || rawPacket.Contains("Back: [🔋 CHARGING") || rawPacket.Contains("charging_b\":true");
        }

        _notificationManager.Notify(NOTIFICATION_ID, BuildTelemetryStatusNotification());
    }

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();
        _notificationManager = (NotificationManager)GetSystemService(NotificationService);

        Notification.Builder quickStartBuilder = (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            ? new Notification.Builder(this, CHANNEL_ID)
            : new Notification.Builder(this);

        quickStartBuilder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuManage)
                         .SetContentTitle("Versa HUD Cockpit")
                         .SetOngoing(true);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(NOTIFICATION_ID, quickStartBuilder.Build(), Android.Content.PM.ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NOTIFICATION_ID, quickStartBuilder.Build());
        }

        Notification customPanelNotification = BuildTelemetryStatusNotification();
        _notificationManager.Notify(NOTIFICATION_ID, customPanelNotification);

        try
        {
            if (_dynamicBluetoothStateReceiver == null)
            {
                _dynamicBluetoothStateReceiver = new BootReceiver();

                var bluetoothToggleFilter = new IntentFilter(Android.Bluetooth.BluetoothAdapter.ActionStateChanged);

                RegisterReceiver(_dynamicBluetoothStateReceiver, bluetoothToggleFilter);
                System.Diagnostics.Debug.WriteLine("--> [SERVICE LAUNCH]: Programmatic Runtime Bluetooth State Receiver successfully injected into Android kernel.");
            }
        }
        catch (Exception rxEx)
        {
            System.Diagnostics.Debug.WriteLine($"--> [SERVICE LAUNCH WARNING]: Runtime receiver registry bypassed: {rxEx.Message}");
        }

        App.NetworkService.OnConnectionStateChanged += (isConnected) =>
        {
            _notificationManager.Notify(NOTIFICATION_ID, BuildTelemetryStatusNotification());
        };

        App.NetworkService.OnTransportModeChanged += (isWifiActive) =>
        {
            _notificationManager.Notify(NOTIFICATION_ID, BuildTelemetryStatusNotification());
        };

        App.NetworkService.OnTelemetryReceived += OnTelemetryReceivedUpdateWidget;

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        try
        {
            if (_dynamicBluetoothStateReceiver != null)
            {
                UnregisterReceiver(_dynamicBluetoothStateReceiver);
                _dynamicBluetoothStateReceiver = null;
                System.Diagnostics.Debug.WriteLine("--> [SERVICE SHUTDOWN]: Runtime Bluetooth receiver safely de-provisioned.");
            }
        }
        catch { }

        App.NetworkService.OnTelemetryReceived -= OnTelemetryReceivedUpdateWidget;
        StopForeground(true);
        base.OnDestroy();
    }
}