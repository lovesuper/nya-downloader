namespace VideoDownloader;

static class Program
{
    [STAThread]
    static void Main() => CrashReporter.Run(() =>
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    });
}
