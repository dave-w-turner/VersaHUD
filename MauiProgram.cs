using Microsoft.Extensions.Logging;

namespace VersaHUD
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var exceptionObject = args.ExceptionObject as Exception;
                if (exceptionObject != null)
                {
                    string crashDumpPath = Path.Combine(FileSystem.Current.AppDataDirectory, "versa_crash_dump.txt");
                    string crashText = $"========================================\n" +
                                       $"CRASH TIMESTAMP: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                       $"EXCEPTION TYPE: {exceptionObject.GetType().FullName}\n" +
                                       $"MESSAGE: {exceptionObject.Message}\n" +
                                       $"STACK TRACE:\n{exceptionObject.StackTrace}\n" +
                                       $"========================================\n";

                    File.WriteAllText(crashDumpPath, crashText);
                }
            };

            return builder.Build();
        }
    }
}
