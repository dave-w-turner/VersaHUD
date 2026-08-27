namespace VersaHUD.Controls;

public partial class InitMasterPassword : ContentView
{
    // The key value used to save the password securely in your app's local memory registry [BUILD]
    public const string MasterPasswordKey = "CachedVehicleMasterPass";

    // 🚀 THE MESSENGER EVENT: Fires straight up to your MainPage once registration succeeds! [BUILD]
    public event EventHandler OnPasswordInitialized;
    public event EventHandler OnWrongDeviceRequested;

    public InitMasterPassword()
    {
        InitializeComponent();
    }

    // 🔓 INITIALIZATION REGISTER TRIGGER ACTION
    private async void OnInitializePasswordClicked(object sender, EventArgs e)
    {
        string enteredPasscode = entryInitialPass.Text;

        if (string.IsNullOrWhiteSpace(enteredPasscode) || enteredPasscode.Trim().Length < 3)
        {
            await App.Current.MainPage.DisplayAlertAsync("INVALID REGISTER KEY",
                "Your initialization master passcode must be at least 3 characters long.", "TRY AGAIN");
            return;
        }

        enteredPasscode = enteredPasscode.Trim();

        try
        {
            System.Diagnostics.Debug.WriteLine("--> [VAULT CORES]: Synchronizing master preference keys globally...");

            // Commit your correct configuration password straight to your persistent preferences storage cache
            Preferences.Default.Set(InitMasterPassword.MasterPasswordKey, enteredPasscode);

            entryInitialPass.Text = string.Empty; // Wipe out input text properties

            OnPasswordInitialized?.Invoke(this, EventArgs.Empty);

            var currentMainPage = Shell.Current?.CurrentPage as VersaHUD.MainPage;
            if (currentMainPage != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var layoutContainerShell = currentMainPage.FindByName<Grid>("layoutPasswordInitShell");
                    if (layoutContainerShell != null)
                    {
                        layoutContainerShell.IsVisible = false; // Hide the onboarding panel
                    }

                    // 🚀 LEAVE IT TO THE WATCHDOG: Fire the over-the-air verification pass 
                    // and let the true hardware receipt determine the next screen message! [INDEX_1.3.2]
                    await currentMainPage.VerifyPasswordAgainstHardwareAsync();
                });
            }

            // 🚀 FIXED: Completely stripped the premature "VAULT COMPLETED" prompt out of here!
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"--> [INITIALIZATION FAULT SHIELD]: {ex.Message}");
        }
    }

    private void OnWrongDeviceClicked(object sender, EventArgs e)
    {
        // Fire the event channel upward to tell MainPage to drop the password modal! [BUILD]
        OnWrongDeviceRequested?.Invoke(this, EventArgs.Empty);
    }
}