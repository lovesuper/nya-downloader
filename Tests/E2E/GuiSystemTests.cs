using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace VideoDownloader.Tests.E2E;

[Trait("Category", "E2E")]
[Collection(nameof(Gui))]
public class GuiSystemTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    delegate bool EnumProc(IntPtr h, IntPtr data);

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static IntPtr NameBox(IntPtr dialog)
    {
        var combo = FindWindowEx(dialog, IntPtr.Zero, "ComboBoxEx32", null);
        if (combo != IntPtr.Zero && FindWindowEx(combo, IntPtr.Zero, "ComboBox", null) is var inner && inner != IntPtr.Zero
            && FindWindowEx(inner, IntPtr.Zero, "Edit", null) is var edit && edit != IntPtr.Zero)
            return edit;
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(dialog, (h, _) =>
        {
            if (ClassOf(h) != "Edit" || !NyaApp.IsWindowVisible(h)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    static void Answer(NyaApp app, string title, string text)
    {
        var dialog = app.WaitForWindow(title, 20);
        var box = IntPtr.Zero;
        app.WaitFor(() => (box = NameBox(dialog)) != IntPtr.Zero, 10, $"the name box in \"{title}\"");
        SendMessage(box, 0x000C, IntPtr.Zero, text);
        Thread.Sleep(200);
        for (int i = 0; i < 3 && NyaApp.FindWindow(null, title) != IntPtr.Zero; i++)
        {
            NyaApp.PostMessage(dialog, 0x0111, 1, 0);
            for (int t = 0; t < 15 && NyaApp.FindWindow(null, title) != IntPtr.Zero; t++) Thread.Sleep(100);
        }
        app.WaitFor(() => NyaApp.FindWindow(null, title) == IntPtr.Zero, 10, $"\"{title}\" to close");
    }

    [Fact]
    public void Browse_takes_the_folder_chosen_in_the_system_dialog()
    {
        using var app = new NyaApp();
        using var target = new TempDir();
        app.Press("outPick");
        Answer(app, "Where to save downloads", target.Path);
        app.WaitFor(() => app.Value("outDir") == target.Path, 5, "the chosen folder");
        app.WaitFor(() => File.ReadAllText(NyaApp.LogPath("using.log")).Contains($"Folder chosen: {target.Path}"), 5, "the log line");
    }

    [Fact]
    public void Choose_takes_a_cookies_file_from_the_system_dialog()
    {
        using var app = new NyaApp();
        using var dir = new TempDir();
        var cookies = Path.Combine(dir.Path, "cookies.txt");
        File.WriteAllText(cookies, CookieLines.YouTubeSignedIn);
        app.SetValue("url", TestUrls.YouTube);
        app.Press("cookiesPick");
        Answer(app, "Choose cookies.txt", cookies);
        app.WaitFor(() => app.Text("status") == "Cookies saved for: YouTube", 5, "the cookies to be saved");
        Assert.True(app.CloseAndWait());
        Assert.Equal(cookies, SavedCookies(app));
    }

    static string? SavedCookies(NyaApp app)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(app.Data.Path, "settings.json")))!;
        return settings.Cookies.GetValueOrDefault("YouTube");
    }

    [Fact]
    public void Text_selected_in_the_log_is_copied()
    {
        var saved = ClipboardState.Save();
        try
        {
            using var app = new NyaApp();
            app.SetValue("url", "https://example.com/no-video-here");
            app.Press("download");
            app.WaitFor(() => app.Enabled("download") && app.Value("log").Length > 0, 60, "the download to fail");
            var element = app.El("log");
            var log = NyaApp.Hwnd(element);
            int bottom = (int)element.Current.BoundingRectangle.Height - 8;
            ClipboardState.SetText("before");
            NyaApp.PostMessage(log, 0x0201, 1, 1 | (24 << 16));
            NyaApp.PostMessage(log, 0x0200, 1, 3000 | (bottom << 16));
            NyaApp.PostMessage(log, 0x0202, 0, 3000 | (bottom << 16));
            Thread.Sleep(300);
            SendMessage(log, 0x0301, IntPtr.Zero, IntPtr.Zero);
            var copied = ClipboardState.Text();
            Assert.NotEqual("before", copied);
            Assert.False(string.IsNullOrWhiteSpace(copied));
            Assert.Contains(copied!.TrimEnd(), app.Value("log"));
        }
        finally { ClipboardState.Restore(saved); }
    }

    static class ClipboardState
    {
        static T Sta<T>(Func<T> work) => Unit.MainFormLayoutTests.OnSta(work);

        public static System.Windows.Forms.DataObject? Save() => Sta(() =>
        {
            var copy = new System.Windows.Forms.DataObject();
            var current = System.Windows.Forms.Clipboard.GetDataObject();
            if (current == null) return null;
            foreach (var format in current.GetFormats(false))
                try { if (current.GetData(format, false) is { } data) copy.SetData(format, false, data); }
                catch (Exception e) when (e is ExternalException or InvalidOperationException or NotSupportedException or ArgumentException) { }
            return copy;
        });

        public static void Restore(System.Windows.Forms.DataObject? saved) => Sta(() =>
        {
            if (saved == null || saved.GetFormats().Length == 0) System.Windows.Forms.Clipboard.Clear();
            else System.Windows.Forms.Clipboard.SetDataObject(saved, true, 5, 100);
            return 0;
        });

        public static void SetText(string text) => Sta(() => { System.Windows.Forms.Clipboard.SetText(text); return 0; });

        public static string? Text() => Sta(() => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);
    }
}
