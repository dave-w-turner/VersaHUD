using Microsoft.Extensions.DependencyInjection;
using VersaHUD.Services;

namespace VersaHUD
{
    public partial class App : Application
    {
        public static NetworkHubService? _bluetoothService { get; private set; }
        public static Services.CockpitTelemetryModule TelemetryModule { get; private set; } = new();

        public static NetworkHubService NetworkService =>
            _bluetoothService ??= new NetworkHubService();

        public App()
        {
            InitializeComponent();
            TelemetryModule.Initialize();
            MainPage = new SplashPage();
        }

        protected override async void OnStart()
        {
            base.OnStart();
        }
    }
}