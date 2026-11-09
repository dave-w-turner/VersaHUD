    namespace VersaHUD
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            Routing.RegisterRoute(nameof(AdminPage), typeof(AdminPage));
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            await GoToAsync($"//{nameof(MainPage)}");
        }
    }
}
