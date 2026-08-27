namespace VersaHUD.Controls;

public partial class InitMasterPassword : ContentView
{
    public const string MasterPasswordKey = "CachedVehicleMasterPass";

    public event EventHandler OnPasswordInitialized;
    public event EventHandler OnWrongDeviceRequested;

    public InitMasterPassword()
    {
        InitializeComponent();
    }

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

            Preferences.Default.Set(MasterPasswordKey, enteredPasscode);

#if ANDROID
            var nativeContext = Android.App.Application.Context;
            string preferencesFileName = $"{nativeContext.PackageName}.preferences";
            var nativePreferences = nativeContext.GetSharedPreferences(preferencesFileName, Android.Content.FileCreationMode.Private);

            using (var storageEditor = nativePreferences.Edit())
            {
                // We write using your exact, clean string literal key matching your firmware firmware variables!
                storageEditor.PutString("MasterPasswordKey", enteredPasscode);
                storageEditor.Apply(); // Flash the update securely down to the physical silicon chip
            }
            System.Diagnostics.Debug.WriteLine($"--> [NATIVE STORAGE LINK]: Master password token saved cleanly: {enteredPasscode}");
#else
            Preferences.Default.Set("MasterPasswordKey", enteredPasscode);
#endif

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

                    await currentMainPage.VerifyPasswordAgainstHardwareAsync();
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"--> [INITIALIZATION FAULT SHIELD]: {ex.Message}");
        }
    }

    private void OnWrongDeviceClicked(object sender, EventArgs e)
    {
        OnWrongDeviceRequested?.Invoke(this, EventArgs.Empty);
    }
}