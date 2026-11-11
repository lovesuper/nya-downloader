using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using VideoDownloader.Tests.Integration;
using Xunit;

namespace VideoDownloader.Tests.E2E;

[CollectionDefinition(nameof(Gui), DisableParallelization = true)]
public class Gui;

[Trait("Category", "E2E")]
[Collection(nameof(Gui))]
public class GuiTests
{
    static long LogLength(string name) => File.Exists(NyaApp.LogPath(name)) ? new FileInfo(NyaApp.LogPath(name)).Length : 0;

    static string LogSince(string name, long offset)
    {
        var path = NyaApp.LogPath(name);
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        s.Seek(Math.Min(offset, s.Length), SeekOrigin.Begin);
        return new StreamReader(s).ReadToEnd();
    }

    static Settings SavedSettings(NyaApp app) =>
        JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(app.Data.Path, "settings.json")))!;

    [Fact]
    public void Startup_title_engine_and_update_notice()
    {
        var usingLog = LogLength("using.log");
        using var app = new NyaApp();
        var v = Version.Parse(FileVersionInfo.GetVersionInfo(NyaApp.Exe).FileVersion!).ToString(3);
        Assert.NotNull(app.ByName($"NyaDownloader {v}"));
        Assert.StartsWith("yt-dlp 20", app.Text("engine"));
        Assert.False(app.Shown("setup"), "Install components only when something is missing");
        Assert.Equal("", app.Value("url"));
        Assert.False(app.Shown("platform"), "no site badge without a link");

        app.WaitFor(() => LogSince("using.log", usingLog).Contains("Update check:"), 120, "the update check");
        bool available = LogSince("using.log", usingLog).Contains("Update check: available");
        Thread.Sleep(500);
        Assert.Equal(available, app.Shown("update"));
        Assert.Equal(available, app.Shown("updateBanner"));
    }

    [Fact]
    public void Starts_minimized_and_inactive_when_asked_like_a_Run_Minimized_shortcut()
    {
        using var app = new NyaApp();
        Assert.True(app.StartedMinimized, "STARTUPINFO SW_SHOWMINNOACTIVE was ignored - the window came up normal (and could take the focus)");
        NyaApp.GetWindowRect(app.Handle, out var r);
        Assert.True(r.Right <= System.Windows.Forms.SystemInformation.VirtualScreen.Left, $"test window is on screen at x={r.Left}..{r.Right}");
        Assert.False(NyaApp.IsIconic(app.Handle));
    }

    [Fact]
    public void On_a_fresh_PC_it_runs_and_offers_to_install_what_is_missing()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var app = new NyaApp(env: [("PATH", $@"{windows}\System32;{windows}"), ("DOTNET_ROOT", @"C:\no-dotnet-here")]);
        Assert.Equal("Missing: yt-dlp, ffmpeg, Deno (for YouTube). Click Install components.", app.Text("engine"));
        Assert.True(app.Shown("setup"));
        Assert.Equal("Install components (~148 MB)", app.Text("setup"));
        Assert.False(app.Shown("update"), "nothing to update without yt-dlp");
        Assert.True(app.Enabled("download"));
    }

    [Fact]
    public void Closing_right_after_start_is_clean()
    {
        var errorLog = LogLength("error.log");
        using var app = new NyaApp(waitForStartup: false);
        Assert.True(app.CloseAndWait(), "the app didn't close");
        Thread.Sleep(5000);
        var errors = LogSince("error.log", errorLog);
        Assert.DoesNotContain("ERROR", errors);
        Assert.DoesNotContain("CRASH", errors);
    }

    [Fact]
    public void Borderless_resizable_never_maximized()
    {
        using var app = new NyaApp();
        int style = NyaApp.GetWindowLong(app.Handle, -16);
        Assert.NotEqual(0x00C00000, style & 0x00C00000);
        Assert.Equal(0, style & 0x00010000);
        Assert.NotEqual(0, style & 0x00020000);

        NyaApp.GetWindowRect(app.Handle, out var r);
        int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
        int Hit(int x, int y) => (int)NyaApp.SendMessage(app.Handle, 0x0084, 0, (y << 16) | (x & 0xFFFF));
        Assert.Equal(13, Hit(r.Left + 1, r.Top + 1));
        Assert.Equal(10, Hit(r.Left + 2, midY));
        Assert.Equal(11, Hit(r.Right - 2, midY));
        Assert.Equal(15, Hit(midX, r.Bottom - 2));
        Assert.Equal(17, Hit(r.Right - 2, r.Bottom - 2));

        NyaApp.SendMessage(app.Handle, 0x0112, 0xF030, 0);
        NyaApp.SendMessage(app.Handle, 0x00A3, 2, 0);
        Thread.Sleep(400);
        Assert.False(NyaApp.IsZoomed(app.Handle));

        var minimize = NyaApp.Hwnd(app.ByName("Minimize"));
        var close = NyaApp.Hwnd(app.ByName("Close"));
        NyaApp.Click(minimize);
        app.WaitFor(() => NyaApp.IsIconic(app.Handle), 5, "the minimize button");
        NyaApp.ShowWindow(app.Handle, 4);
        app.WaitFor(() => !NyaApp.IsIconic(app.Handle), 5, "the window to come back");
        NyaApp.Click(close);
        Assert.True(app.Process.WaitForExit(5000), "close button");
    }

    [Fact]
    public void Hovering_the_fields_changes_nothing()
    {
        using var app = new NyaApp();
        foreach (var id in new[] { "url", "quality", "outDir" })
        {
            var el = app.El(id);
            var area = app.RectInWindow(el);
            area.Inflate(4, 4);
            using var before = app.Screenshot();
            for (int i = 0; i < 5; i++) { NyaApp.PostMessage(NyaApp.Hwnd(el), 0x0200, 0, 10 | (8 << 16)); Thread.Sleep(100); }
            Thread.Sleep(500);
            using var after = app.Screenshot();
            Assert.True(ChangedPixels(before, after, area) == 0, $"{id} reacts to the mouse");
            NyaApp.PostMessage(NyaApp.Hwnd(el), 0x02A3, 0, 0);
        }
    }

    [Fact]
    public void Background_is_a_half_visible_soft_blur()
    {
        using var app = new NyaApp(s => s.Look = new BackgroundLook { Visibility = 50, Blur = 40, Frost = 80, Ice = 0 });
        Thread.Sleep(1500);
        using var shot = app.Screenshot();
        var download = app.RectInWindow(app.El("download"));
        var showFile = app.RectInWindow(app.El("showFile"));
        int y0 = download.Top + download.Height / 2 - 4, x0 = showFile.Right + 40, x1 = shot.Width - 90;
        Assert.True(x1 - x0 > 200, $"no free space next to the buttons ({x0}..{x1})");
        int darkest = 255, steepest = 0;
        for (int y = y0; y < y0 + 8; y++)
            for (int x = x0; x < x1; x++)
            {
                Color c = shot.GetPixel(x, y), next = shot.GetPixel(x + 1, y);
                darkest = Math.Min(darkest, Math.Min(c.R, Math.Min(c.G, c.B)));
                steepest = Math.Max(steepest, Math.Abs(c.R - next.R) + Math.Abs(c.G - next.G) + Math.Abs(c.B - next.B));
            }
        Assert.True(darkest >= 124, $"the background is stronger than half: a channel goes down to {darkest}");
        Assert.True(steepest <= 18, $"the background is not blurred: neighbouring pixels differ by {steepest}");
    }

    [Fact]
    public void Liquid_glass_comes_from_the_saved_settings()
    {
        (Bitmap Shot, Rectangle Log, Settings Saved) Run(BackgroundLook look)
        {
            using var app = new NyaApp(s => s.Look = look);
            Thread.Sleep(1500);
            var shot = app.Screenshot();
            var log = app.RectInWindow(app.El("logBox"));
            Assert.True(app.CloseAndWait());
            return (shot, log, SavedSettings(app));
        }
        var (plain, log, saved) = Run(new BackgroundLook { Gif = 1, Visibility = 43, Glass = 0, Highlight = 0, Frost = 0, Ice = 0 });
        var (glass, _, savedGlass) = Run(new BackgroundLook { Gif = 1, Visibility = 43, Glass = 100, Highlight = 100, Frost = 0, Ice = 0 });
        using (plain)
        using (glass)
        {
            Assert.Equal("gif 1, visibility 43%, blur 0, scale 100%, glass 0, highlight 0%, frost 0%, ice 0, text backing 60%", saved.Look.ToString());
            Assert.Equal("gif 1, visibility 43%, blur 0, scale 100%, glass 100, highlight 100%, frost 0%, ice 0, text backing 60%", savedGlass.Look.ToString());
            int changed = 0, brightest = 0, brightestPlain = 0;
            for (int y = log.Top + 20; y < log.Bottom - 20; y += 2)
                for (int x = 0; x < log.Left - 2; x++)
                {
                    Color p = plain.GetPixel(x, y), g = glass.GetPixel(x, y);
                    if (Math.Abs(p.R - g.R) + Math.Abs(p.G - g.G) + Math.Abs(p.B - g.B) > 12) changed++;
                    brightest = Math.Max(brightest, g.R + g.G + g.B);
                    brightestPlain = Math.Max(brightestPlain, p.R + p.G + p.B);
                }
            Assert.True(changed > 50, $"the glass rim next to the log changed only {changed} pixels");
            Assert.True(brightest > brightestPlain + 30, $"no highlight on the rim: {brightest} vs {brightestPlain}");
        }
        Assert.Contains("background: bg1 (gif 1, visibility 43%, blur 0, scale 100%, glass 100", LogSince("using.log", 0));
    }

    [Fact]
    public void Text_gets_a_light_backing_and_the_log_is_frosted_glass()
    {
        (Bitmap Shot, Rectangle Log, Rectangle Engine) Run(int backing)
        {
            using var app = new NyaApp(s => s.Look = new BackgroundLook { Gif = 4, Visibility = 100, Glass = 0, Frost = 0, Ice = 0, Backing = backing });
            app.WaitFor(() => app.Text("engine").StartsWith("yt-dlp "), 30, "the engine line");
            Thread.Sleep(1000);
            var shot = app.Screenshot();
            var log = app.RectInWindow(app.El("logBox"));
            var engine = app.RectInWindow(app.El("engine"));
            Assert.True(app.CloseAndWait());
            Assert.Equal(backing, SavedSettings(app).Look.Backing);
            return (shot, log, engine);
        }
        static (int Lightest, int Darkest, int Steepest) Measure(Bitmap b, Rectangle r)
        {
            int lightest = 0, darkest = 255, steepest = 0;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    Color c = b.GetPixel(x, y), next = b.GetPixel(x + 1, y);
                    int low = Math.Min(c.R, Math.Min(c.G, c.B));
                    (lightest, darkest) = (Math.Max(lightest, low), Math.Min(darkest, low));
                    steepest = Math.Max(steepest, Math.Abs(c.R - next.R) + Math.Abs(c.G - next.G) + Math.Abs(c.B - next.B));
                }
            return (lightest, darkest, steepest);
        }
        var (bare, log, engine) = Run(0);
        var (backed, _, _) = Run(100);
        using (bare)
        using (backed)
        {
            var inside = Rectangle.Inflate(log, -24, -24);
            var (_, bareDark, bareSteep) = Measure(bare, inside);
            var (_, logDark, logSteep) = Measure(backed, inside);
            Assert.True(bareDark < 150, $"the GIF should show through a log without backing (darkest {bareDark})");
            Assert.True(logDark >= 170, $"the log backing is not light enough: darkest {logDark}");
            Assert.True(logSteep < 16 && logSteep < bareSteep / 2, $"the log is not frosted: {logSteep} vs {bareSteep}");
            var pad = new Rectangle(engine.Left - 7, engine.Top + 3, 4, engine.Height - 6);
            var (_, padBare, _) = Measure(bare, pad);
            var (_, padBacked, _) = Measure(backed, pad);
            Assert.True(padBare < 170, $"the GIF next to the engine line is already light ({padBare})");
            Assert.True(padBacked >= 170, $"the engine line has no light backing: darkest {padBacked}");
        }
    }

    [Fact]
    public void Ice_frosts_the_window_edges_from_the_saved_settings()
    {
        (Bitmap Shot, Rectangle Log) Run(int ice)
        {
            using var app = new NyaApp(s => s.Look = new BackgroundLook { Gif = 1, Visibility = 43, Frost = 80, Glass = 0, Ice = ice });
            Thread.Sleep(ice > 0 ? 6000 : 1500);
            var shot = app.Screenshot();
            var log = app.RectInWindow(app.El("logBox"));
            Assert.True(app.CloseAndWait());
            Assert.Equal(ice, SavedSettings(app).Look.Ice);
            return (shot, log);
        }
        var (clear, log) = Run(0);
        var (iced, _) = Run(70);
        using (clear)
        using (iced)
        {
            int changed = 0, total = 0;
            for (int y = log.Top; y < log.Bottom; y += 2)
                for (int x = 0; x < log.Left - 2; x++)
                {
                    Color a = clear.GetPixel(x, y), b = iced.GetPixel(x, y);
                    total++;
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 15) changed++;
                }
            Assert.True(changed > total / 5, $"the window edge did not frost over: {changed} of {total} pixels changed");
        }
    }

    [Fact]
    public void Gif_keeps_playing_while_the_window_is_not_focused()
    {
        using var app = new NyaApp(s => s.Look = new BackgroundLook { Gif = 1, Visibility = 100, Glass = 0, Ice = 0 }, env: [("NYADOWNLOADER_STILL", "0")]);
        Thread.Sleep(3000);
        Assert.NotEqual(app.Handle, NyaApp.GetForegroundWindow());
        using var first = app.Screenshot();
        Thread.Sleep(1200);
        using var later = app.Screenshot();
        int changed = ChangedPixels(first, later, new Rectangle(0, 0, first.Width, first.Height));
        Assert.True(changed > 2000, $"the background stood still without focus: {changed} pixels changed");
    }

    static int ChangedPixels(Bitmap a, Bitmap b, Rectangle area)
    {
        int n = 0;
        for (int y = Math.Max(0, area.Top); y < Math.Min(a.Height, area.Bottom); y++)
            for (int x = Math.Max(0, area.Left); x < Math.Min(a.Width, area.Right); x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 24) n++;
            }
        return n;
    }

    [Fact]
    public void Link_shows_the_site_and_colours_the_Download_button()
    {
        using var app = new NyaApp();
        var cases = new (string Url, string? Badge, Func<Color, bool> Looks, string Colour)[]
        {
            (TestUrls.YouTube, "YouTube", c => c.R > 180 && c.G < 60 && c.B < 90, "red"),
            (TestUrls.TikTok, "TikTok", c => c.R < 60 && c.G < 60 && c.B < 60, "black"),
            (TestUrls.InstagramReel, "Instagram", c => c.R > 200 && c.G is > 60 and < 120 && c.B < 50, "orange"),
            ("vimeo.com/123", "Other site", c => c.R > 180 && c.G > 180 && c.B > 180, "the normal grey"),
            ("", null, c => c.R > 180 && c.G > 180 && c.B > 180, "the normal grey"),
        };
        foreach (var (url, badge, looks, colour) in cases)
        {
            app.SetValue("url", url);
            Assert.Equal(badge != null, app.Shown("platform"));
            if (badge != null) Assert.Equal(badge, app.Text("platform"));
            using var shot = app.Screenshot();
            var button = app.RectInWindow(app.El("download"));
            var pixel = shot.GetPixel(button.Left + 4, button.Top + button.Height / 2);
            Assert.True(looks(pixel), $"Download button for '{url}' should be {colour}, is {pixel}");
        }
        app.SetValue("url", TestUrls.InstagramCarousel);
        Assert.StartsWith("Instagram: the app figures out whether the link is a reel, a photo or a carousel", app.Text("qualityHint"));
    }

    [Fact]
    public void Mode_quality_and_cover_come_back_and_are_saved()
    {
        using var app = new NyaApp(s => { s.AudioOnly = true; s.MaxHeight = 720; s.SaveCover = true; });
        Assert.True(app.IsSelected("mp3"));
        Assert.True(app.IsChecked("cover"));
        Assert.Equal("Up to 720p", app.Quality);
        Assert.False(app.Enabled("quality"), "quality means nothing for mp3");
        Assert.Contains("mp3", app.Text("qualityHint"));
        Assert.Equal(app.Out.Path, app.Value("outDir"));

        app.Press("video");
        app.WaitFor(() => app.Enabled("quality"), 5, "quality to unlock");
        Assert.StartsWith("Downloads the best quality up to 720p", app.Text("qualityHint"));

        Assert.True(app.CloseAndWait());
        var saved = SavedSettings(app);
        Assert.Equal((false, 720, true, app.Out.Path), (saved.AudioOnly, saved.MaxHeight, saved.SaveCover, saved.OutputDir));
        Assert.Contains("Mode: Video (mp4)", LogSince("using.log", 0));
    }

    [Fact]
    public void Cookies_status_per_site_and_Remove()
    {
        using var files = new TempDir();
        var tiktok = files.File("tiktok.txt", CookieLines.TikTok);
        var youtube = files.File("yt.txt", CookieLines.YouTubeSignedOut);
        using var app = new NyaApp(s => s.Cookies = new()
        {
            ["TikTok"] = tiktok, ["YouTube"] = youtube, ["Instagram"] = files.File("gone.txt"),
        });

        Assert.Equal("Saved cookies for: YouTube, TikTok, Instagram. They are used automatically.", app.Text("cookiesStatus"));
        Assert.False(app.Enabled("cookiesClear"));

        app.SetValue("url", TestUrls.YouTube);
        Assert.Equal("yt.txt: YouTube has signed this session out. Export the cookies again.", app.Text("cookiesStatus"));
        app.SetValue("url", TestUrls.InstagramReel);
        Assert.Equal("File gone.txt not found. Choose it again.", app.Text("cookiesStatus"));
        app.SetValue("url", TestUrls.TikTok);
        Assert.Equal("OK: tiktok.txt (TikTok cookies)", app.Text("cookiesStatus"));
        Assert.True(app.Enabled("cookiesClear"));

        app.Press("cookiesClear");
        app.WaitFor(() => app.Text("cookiesStatus").StartsWith("None set."), 5, "the cookies to be removed");
        Assert.False(app.Enabled("cookiesClear"));
        Assert.True(app.CloseAndWait());
        Assert.Equal(["Instagram", "YouTube"], SavedSettings(app).Cookies.Keys.Order());
    }

    [Fact]
    public void Cookie_file_next_to_the_exe_is_not_picked_up()
    {
        var file = Path.Combine(Path.GetDirectoryName(NyaApp.Exe)!, "cookies.txt");
        Assert.False(File.Exists(file), $"remove the leftover {file} first");
        File.WriteAllText(file, CookieLines.TikTok);
        try
        {
            using var app = new NyaApp();
            Assert.StartsWith("Not needed for most videos.", app.Text("cookiesStatus"));
            app.SetValue("url", TestUrls.TikTok);
            Assert.StartsWith("None set.", app.Text("cookiesStatus"));
            Assert.True(app.CloseAndWait());
            Assert.Empty(SavedSettings(app).Cookies);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Invalid_link_is_rejected()
    {
        using var app = new NyaApp();
        app.SetValue("url", "hello");
        app.Press("download");
        app.WaitFor(() => app.Text("status") == "Paste a video link first.", 5, "the error");
        Assert.True(app.Enabled("download"));
        Assert.Empty(app.Out.Files());
    }

    [Fact]
    public void YouTube_360p_with_cover()
    {
        using var app = new NyaApp(s => { s.MaxHeight = 360; s.SaveCover = true; });
        var status = app.Download(TestUrls.YouTube);
        Assert.StartsWith("Done: ", status);
        Assert.Contains($"[{TestUrls.YouTubeId}] 640x360.mp4", status);
        Assert.EndsWith(" + cover", status);
        Assert.Single(app.Out.Files("*.mp4"));
        Assert.Single(app.Out.Files("*.jpg"));
        Assert.True(app.Enabled("showFile"));
        var log = app.Value("log");
        Assert.Contains("[progress] 100.0%", log);
        Assert.Contains("Cover: ", log);
        Assert.DoesNotContain("[MetadataParser]", log);
    }

    [Fact]
    public void Instagram_carousel_is_detected_automatically()
    {
        using var app = new NyaApp(s => s.MaxHeight = 0);
        var status = app.Download(TestUrls.InstagramCarousel);
        Assert.StartsWith("Done: 5 photos and 2 videos (", status);
        Assert.Equal(5, app.Out.Files("*.jpg").Length);
        Assert.Equal(2, app.Out.Files("*.mp4").Length);
        var log = app.Value("log");
        Assert.Contains("Post: 5 photos and 2 videos", log);
        Assert.DoesNotContain("There is no video in this post", log);
    }

    [Fact]
    public void TikTok_audio()
    {
        var usingLog = LogLength("using.log");
        using var app = new NyaApp(s => s.AudioOnly = true);
        Assert.Equal($"Done: {TestUrls.TikTokFile}_audio.mp3", app.Download(TestUrls.TikTok));
        Assert.Equal([TestUrls.TikTokFile + "_audio.mp3"], app.Out.Files());
        app.WaitFor(() => LogSince("using.log", usingLog).Contains($"Notified: Download finished | Done: {TestUrls.TikTokFile}_audio.mp3 (quiet)"), 5, "the notification");
        Assert.DoesNotContain("Taskbar progress is unavailable", LogSince("using.log", usingLog));
    }

    [Fact]
    public void Cancel_removes_the_unfinished_files_and_does_not_notify()
    {
        var usingLog = LogLength("using.log");
        using var app = new NyaApp(s => s.MaxHeight = 0);
        app.SetValue("url", TestUrls.YouTubeLong);
        app.Press("download");
        app.WaitFor(() => app.Text("status").StartsWith("Downloading:"), 120, "the download to start");
        app.WaitFor(() => app.Out.Files().Any(f => Leftovers.IsTemporary(f)), 30, "an unfinished file to appear");
        app.Press("cancel");
        app.WaitFor(() => app.Text("status") == "Canceled.", 20, "the cancel");
        app.WaitFor(() => Directory.GetFileSystemEntries(app.Out.Path).Length == 0, 10, "the unfinished files to go");
        Assert.Contains("unfinished file(s)", app.Value("log"));
        Thread.Sleep(500);
        Assert.DoesNotContain("Notified:", LogSince("using.log", usingLog));
    }

    [Fact]
    public void Age_restricted_video_shows_the_cookies_dialog()
    {
        var errorLog = LogLength("error.log");
        var usingLog = LogLength("using.log");
        using var app = new NyaApp();
        var status = app.Download(TestUrls.YouTubeAgeRestricted);
        Assert.StartsWith("Failed. This video is age-restricted.", status);
        app.WaitFor(() => LogSince("using.log", usingLog).Contains("Notified: Download failed | Failed. This video is age-restricted."), 5, "the failure notification");
        var dialog = IntPtr.Zero;
        app.WaitFor(() => (dialog = NyaApp.FindWindow("#32770", "Age-restricted video")) != IntPtr.Zero, 10, "the dialog");
        Assert.NotEqual(dialog, NyaApp.GetForegroundWindow());
        NyaApp.PostMessage(dialog, 0x0010, 0, 0);
        app.WaitFor(() => NyaApp.FindWindow("#32770", "Age-restricted video") == IntPtr.Zero, 5, "the dialog to close");
        Assert.True(app.Shown("errorLogLink"));
        Assert.Contains($"DOWNLOAD ERROR: YouTube | video (mp4), up to 360p | {TestUrls.YouTubeAgeRestricted}", LogSince("error.log", errorLog));
    }

    [Fact]
    public void Closing_during_a_download_stops_it_cleanly()
    {
        var errorLog = LogLength("error.log");
        var usingLog = LogLength("using.log");
        using var app = new NyaApp(s => s.MaxHeight = 0);
        app.SetValue("url", TestUrls.YouTubeLong);
        app.Press("download");
        app.WaitFor(() => app.Text("status").StartsWith("Downloading:"), 120, "the download to start");
        Assert.NotEmpty(DownloadTests.ProcessesWorkingIn(app.Out.Path));

        Assert.True(app.CloseAndWait(15), "the app didn't close");
        Thread.Sleep(1500);
        Assert.Empty(DownloadTests.ProcessesWorkingIn(app.Out.Path));
        var errors = LogSince("error.log", errorLog);
        Assert.DoesNotContain("ERROR", errors);
        Assert.DoesNotContain("CRASH", errors);
        Assert.Contains("App closed while busy", LogSince("using.log", usingLog));
    }
}
