namespace VideoDownloader;

public static class CrashReporter
{
    public static void Run(Action app)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("ERROR (background task)", e.Exception);
            e.SetObserved();
        };

        try
        {
            app();
        }
        catch (Exception ex)
        {
            Report(ex, fatal: true);
        }
    }

    static void Report(Exception? ex, bool fatal)
    {
        ex ??= new Exception("unknown error");
        ErrorLog.Write(fatal ? "CRASH" : "ERROR", ex, fatal ? "the app was closed" : null);
        try
        {
            MessageBox.Show(
                $"Something went wrong{(fatal ? " and the app has to close" : "")}:\n\n{ex.Message}\n\n" +
                $"Details were saved to error.log:\n{ErrorLog.FilePath}",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
