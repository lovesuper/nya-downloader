using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Tests.Unit;
using Xunit;

namespace VideoDownloader.Tests.E2E;

[Trait("Category", "E2E")]
[Collection(nameof(Gui))]
public class GuiUpdateTests
{
    static string AppVersion => Version.Parse(FileVersionInfo.GetVersionInfo(NyaApp.Exe).FileVersion!).ToString(3);

    static string ReleaseJson(string tag, string exeUrl, byte[]? exe = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        html_url = "https://github.com/lovesuper/nya-downloader/releases/tag/" + tag,
        assets = new object[]
        {
            new
            {
                name = "NyaDownloader.exe", browser_download_url = exeUrl, size = exe?.Length ?? 0,
                digest = exe == null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant(),
            },
        },
    });

    static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void Audio_m4a_is_remembered_and_downloaded()
    {
        using var app = new NyaApp(s => { s.AudioOnly = true; s.AudioFormat = "m4a"; });
        Assert.True(app.IsSelected("m4a"));
        Assert.False(app.Enabled("quality"));
        Assert.StartsWith("m4a keeps the site's original sound", app.Text("qualityHint"));
        Assert.Equal($"Done: {TestUrls.TikTokFile}_audio.m4a", app.Download(TestUrls.TikTok));
        Assert.Equal([TestUrls.TikTokFile + "_audio.m4a"], app.Out.Files());

        app.Press("mp3");
        app.WaitFor(() => app.Text("qualityHint").StartsWith("mp3 plays everywhere"), 5, "the mp3 hint");
        Assert.True(app.CloseAndWait());
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(app.Data.Path, "settings.json")))!;
        Assert.Equal((true, "mp3"), (saved.AudioOnly, saved.AudioFormat));
    }

    [Fact]
    public void A_newer_release_shows_a_banner_and_an_older_one_does_not()
    {
        using var http = new LocalHttp(new()
        {
            ["new"] = Encoding.UTF8.GetBytes(ReleaseJson("v9.9.9", "https://example.com/NyaDownloader.exe")),
            ["old"] = Encoding.UTF8.GetBytes(ReleaseJson("v1.0.0", "https://example.com/NyaDownloader.exe")),
        }) { Delay = TimeSpan.FromSeconds(2) };
        using (var app = new NyaApp(env: [("NYADOWNLOADER_UPDATES_URL", http.Url("new"))]))
        {
            app.WaitFor(() => app.Shown("appBanner"), 20, "the update banner");
            Assert.Equal($"NyaDownloader 9.9.9 is out (you have {AppVersion}).", app.Text("appUpdateText"));
            Assert.True(app.Shown("appUpdateNow"));
            Assert.True(app.Shown("appNotes"));
            Thread.Sleep(800);
            using var shot = app.Screenshot();
            var (row, run) = LongestBlackRun(shot);
            Assert.True(run < shot.Width / 4, $"a black strip of {run}px at y={row}: the background wasn't redrawn after the banner moved things");
        }
        var usingLog = NyaApp.LogPath("using.log");
        var before = ReadShared(usingLog).Length;
        using (var app = new NyaApp(env: [("NYADOWNLOADER_UPDATES_URL", http.Url("old"))]))
        {
            app.WaitFor(() => ReadShared(usingLog) is var text && text[Math.Min(before, text.Length)..].Contains($"App update check: {AppVersion} is the latest"), 20, "the update check");
            Thread.Sleep(500);
            Assert.False(app.Shown("appBanner"));
        }
    }

    [Fact]
    public void Update_downloads_the_new_exe_swaps_it_in_and_restarts()
    {
        using var home = new TempDir();
        var exe = Path.Combine(home.Path, "NyaDownloader.exe");
        File.Copy(NyaApp.Exe, exe);
        var bytes = File.ReadAllBytes(exe);
        using var http = new LocalHttp(new() { ["NyaDownloader.exe"] = bytes });
        http.Add("release", Encoding.UTF8.GetBytes(ReleaseJson("v9.9.9", http.Url("NyaDownloader.exe"), bytes)));
        Process? restarted = null;
        try
        {
            using (var app = new NyaApp(exe: exe, env: [("NYADOWNLOADER_UPDATES_URL", http.Url("release"))]))
            {
                app.WaitFor(() => app.Shown("appUpdateNow"), 20, "the Update button");
                var pressed = DateTime.Now;
                app.Press("appUpdateNow");
                Assert.True(app.Process.WaitForExit(TimeSpan.FromSeconds(90)), "the old app didn't close for the restart");
                app.WaitFor(() => (restarted = Process.GetProcessesByName("NyaDownloader").FirstOrDefault(p => PathOf(p) == exe)) != null, 20, "the restarted app");
                Assert.True(File.GetLastWriteTime(exe) >= pressed.AddSeconds(-2), "the exe wasn't replaced");
                restarted!.Refresh();
                app.WaitFor(() => { restarted.Refresh(); return restarted.MainWindowHandle != IntPtr.Zero; }, 20, "the restarted window");
                for (int i = 0; i < 20; i++)
                {
                    NyaApp.GetWindowThreadProcessId(NyaApp.GetForegroundWindow(), out var pid);
                    Assert.NotEqual(restarted.Id, pid);
                    Thread.Sleep(100);
                }
                Assert.True(NyaApp.IsIconic(restarted.MainWindowHandle), "the restarted app should come up the way the old one was started");
            }
            Assert.Equal(bytes, File.ReadAllBytes(exe));
            var log = ReadShared(Path.Combine(home.Path, "using.log"));
            Assert.Contains("Update app: installed 9.9.9, restarting", log);
            Assert.Equal(2, log.Split("App started").Length - 1);
        }
        finally
        {
            if (restarted is { HasExited: false })
            {
                NyaApp.PostMessage(restarted.MainWindowHandle, 0x0010, 0, 0);
                if (!restarted.WaitForExit(10000)) restarted.Kill(entireProcessTree: true);
            }
        }
    }

    static (int Row, int Run) LongestBlackRun(System.Drawing.Bitmap bmp)
    {
        (int Row, int Run) best = (0, 0);
        for (int y = 0; y < bmp.Height; y += 2)
        {
            int run = 0;
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                run = c.R < 12 && c.G < 12 && c.B < 12 ? run + 1 : 0;
                if (run > best.Run) best = (y, run);
            }
        }
        return best;
    }

    static string? PathOf(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }
}
