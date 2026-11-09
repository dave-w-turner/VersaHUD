namespace VersaHUD;

public partial class SplashPage : ContentPage
{
    public SplashPage() => InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Task.Delay(3000);

        App.Current?.Windows[0].Page = new AppShell();
    }
}