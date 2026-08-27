namespace VersaHUD;

public partial class SplashPage : ContentPage
{
    public SplashPage() => InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Task.Delay(3000);

        Application.Current.MainPage = new AppShell();
    }
}