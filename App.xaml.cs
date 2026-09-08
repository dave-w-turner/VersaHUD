using System.Diagnostics;
using VersaHUD.Services;

namespace VersaHUD
{
    public partial class App : Application
    {
        public static CockpitTelemetryModule TelemetryModule { get; private set; } = new();
        public static NetworkHubService NetworkService { get; private set; } = new();

        public static readonly byte[] SecretSharedKeyBytes = [0x5A, 0xA5, 0x1F, 0x2C, 0x7E, 0x9D, 0x8B, 0x34, 0x61, 0xF0, 0xE3, 0xD2, 0xC1, 0xB0, 0x09, 0x48];
        public static readonly byte[] InitializationVectorBytes = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F];

        public static bool IsDebugOutputEnabled { get; set; } = false;

        public App()
        {
            InitializeComponent();
            TelemetryModule.Initialize();
            MainPage = new SplashPage();
        }

        public static async Task Log(string value)
        {
            if (IsDebugOutputEnabled)
                VersaHUD.MainPage.CurrentInstance?.LogDebugToTerminal($"[DEBUG] --> {value}");

            Debug.WriteLine(value);
        }

        protected override async void OnStart()
        {
            base.OnStart();
            NetworkService?.UpdateLifecycleState(true);
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            NetworkService?.UpdateLifecycleState(false);
        }

        protected override void OnResume()
        {
            base.OnResume();
            NetworkService?.UpdateLifecycleState(true);
        }
    }
}