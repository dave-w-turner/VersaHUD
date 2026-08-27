namespace VersaHUD;

public partial class BluetoothPermissions : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new (string androidPermission, bool isRuntime)[]
        {
            (Android.Manifest.Permission.BluetoothScan, true),   // Radar scan arrays
            (Android.Manifest.Permission.BluetoothConnect, true) // Connection pipelines
        };
}

public class ModernBluetooth : VersaHUD.BluetoothPermissions { }
