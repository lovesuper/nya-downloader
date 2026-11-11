using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoDownloader;

// What Windows gets from the shell, the taskbar and the tray, done the macOS way.
static class MacSystem
{
    public const string HomePage = "https://github.com/lovesuper/nya-downloader";

    static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UsageLog.Write($"Couldn't run {Path.GetFileName(file)}: {e.Message}");
        }
    }

    public static void OpenUrl(string url) => Run("/usr/bin/open", url);

    public static void Reveal(string file) => Run("/usr/bin/open", "-R", file);

    public static void OpenTextFile(string file) => Run("/usr/bin/open", "-t", file);

    // The text goes to osascript as arguments, never as part of the script, so quotes in a file name can't break it.
    public static void Notify(string title, string text) =>
        Run("/usr/bin/osascript", "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
            title, text);

    public static void Alert(string title, string text, bool wait)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            foreach (var a in new[] { "-e", "on run argv", "-e", "display alert (item 1 of argv) message (item 2 of argv) as critical", "-e", "end run", title, text })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (wait) p?.WaitForExit();
        }
        catch { }
    }
}

// The Dock icon: a badge with the download progress, like the taskbar button on Windows, and a bounce when a
// download finishes in the background. Called on the main thread only.
static class DockTile
{
    const string ObjC = "/usr/lib/libobjc.dylib";
    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern long Send(IntPtr receiver, IntPtr selector, long arg);

    static string? _badge;
    static bool _failed;

    static IntPtr App() => Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));

    public static void SetBadge(string? text)
    {
        if (_failed || text == _badge) return;
        _badge = text;
        Try(() =>
        {
            var tile = Send(App(), sel_registerName("dockTile"));
            var label = IntPtr.Zero;
            if (text != null)
            {
                var utf8 = Marshal.StringToCoTaskMemUTF8(text);
                try { label = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8); }
                finally { Marshal.FreeCoTaskMem(utf8); }
            }
            Send(tile, sel_registerName("setBadgeLabel:"), label);
        });
    }

    // NSInformationalRequest: one bounce.
    public static void Bounce() => Try(() => Send(App(), sel_registerName("requestUserAttention:"), 10L));

    static void Try(Action action)
    {
        if (_failed) return;
        try { action(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _failed = true;
            UsageLog.Write("Dock badge is unavailable: " + e.Message);
        }
    }
}

// Updating the app replaces the whole NyaDownloader.app with the one from the release's zip.
static class MacUpdate
{
    // The NyaDownloader.app the app runs from; null when it runs outside a bundle (dotnet run).
    public static string? Bundle { get; } = FindBundle();

    static string? FindBundle()
    {
        var macOS = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        var contents = Path.GetDirectoryName(macOS ?? "");
        var app = Path.GetDirectoryName(contents ?? "");
        return Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents" &&
               app != null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }

    static string Beside(string app, string suffix) => Path.Combine(Path.GetDirectoryName(app)!, "." + Path.GetFileName(app) + suffix);

    public static async Task InstallAsync(AppRelease release, Action<double> onProgress, CancellationToken ct)
    {
        var app = Bundle ?? throw new InvalidOperationException("The app isn't running from NyaDownloader.app.");
        var staging = Beside(app, ".new");
        DeleteDir(staging);
        try { Directory.CreateDirectory(staging); }
        catch (IOException e) { throw new UnauthorizedAccessException(e.Message, e); }
        try
        {
            var zip = Path.Combine(staging, "update.zip");
            await AppUpdate.DownloadAsync(release, zip, onProgress, ct);
            var ditto = new YtDlp.Engine("/usr/bin/ditto", [], "ditto");
            var (code, output) = await YtDlp.RunAsync(["-x", "-k", AppUpdate.NewPath(zip), staging], null, ct, ditto);
            if (code != 0) throw new InvalidDataException("Couldn't unpack the update: " + output.Trim());
            var fresh = Directory.GetDirectories(staging, "*.app").FirstOrDefault();
            if (fresh == null || !File.Exists(Path.Combine(fresh, "Contents", "MacOS", AppInfo.Name)))
                throw new InvalidDataException("The update doesn't contain the app.");
            var old = Beside(app, ".old");
            DeleteDir(old);
            Directory.Move(app, old);
            try { Directory.Move(fresh, app); }
            catch
            {
                Directory.Move(old, app);
                throw;
            }
        }
        finally
        {
            DeleteDir(staging);
        }
    }

    // Opens the new version once this process has exited.
    public static void Relaunch()
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var a in new[] { "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; exec /usr/bin/open \"$2\"", "sh",
                                  Environment.ProcessId.ToString(), Bundle! })
            psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    public static void CleanUp()
    {
        if (Bundle is not { } app) return;
        DeleteDir(Beside(app, ".old"));
        DeleteDir(Beside(app, ".new"));
    }

    static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
