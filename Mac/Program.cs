using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace VideoDownloader;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        YtDlp.AddHomebrewToPath();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReport.Report(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("ERROR (background task)", e.Exception);
            e.SetObserved();
        };
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashReport.Report(ex, fatal: true);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}

sealed class App : Application
{
    public override void Initialize()
    {
        Name = AppInfo.Name;
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme { DensityStyle = DensityStyle.Compact });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            CrashReport.Report(e.Exception, fatal: false);
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var about = new NativeMenuItem($"About {AppInfo.Name}");
            about.Click += (_, _) => MacSystem.OpenUrl(MacSystem.HomePage);
            var look = new NativeMenuItem("Background...") { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
            look.Click += (_, _) => window.ShowLook();
            NativeMenu.SetMenu(this, new NativeMenu { about, new NativeMenuItemSeparator(), look });
        }
        base.OnFrameworkInitializationCompleted();
    }
}

static class CrashReport
{
    public static void Report(Exception? ex, bool fatal)
    {
        ex ??= new Exception("unknown error");
        ErrorLog.Write(fatal ? "CRASH" : "ERROR", ex, fatal ? "the app was closed" : null);
        MacSystem.Alert(AppInfo.Name,
            $"Something went wrong{(fatal ? " and the app has to close" : "")}:\n\n{ex.Message}\n\nDetails were saved to error.log:\n{ErrorLog.FilePath}",
            wait: fatal);
    }
}
