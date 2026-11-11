using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
public class ProgressTrackerTests
{
    static double Pct(string line) =>
        double.Parse(line[DownloadPlan.ProgressPrefix.Length..].Split('|')[0].Trim().TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture);

    static string P(double pct, string format) =>
        FormattableString.Invariant($"{DownloadPlan.ProgressPrefix}{pct,5:0.0}%|1.0MiB/s|00:10|{format}");

    [Fact]
    public void Other_lines_pass_through_and_single_formats_drop_the_format_id()
    {
        var t = new ProgressTracker(@"C:\nowhere\sizes.txt");
        Assert.Equal("[youtube] Extracting URL: x", t.Rewrite("[youtube] Extracting URL: x"));
        Assert.Equal("[info] abc: Downloading 1 format(s): 18", t.Rewrite("[info] abc: Downloading 1 format(s): 18"));
        Assert.Equal(DownloadPlan.ProgressPrefix + " 42.0%|1.0MiB/s|00:10", t.Rewrite(P(42, "18")));
        Assert.Equal(DownloadPlan.ProgressPrefix + "50%|x", t.Rewrite(DownloadPlan.ProgressPrefix + "50%|x"));
    }

    [Fact]
    public void Video_and_audio_streams_fill_one_bar_by_their_sizes()
    {
        using var dir = new TempDir();
        var sizes = dir.File("sizes.txt", "abc|160|8000000|140|24000000\n");
        var t = new ProgressTracker(sizes);
        t.Rewrite("[info] abc: Downloading 1 format(s): 160+140");
        Assert.Equal(0, Pct(t.Rewrite(P(0, "160"))));
        Assert.Equal(12.5, Pct(t.Rewrite(P(50, "160"))));
        Assert.Equal(25, Pct(t.Rewrite(P(100, "160"))));
        Assert.Equal(25, Pct(t.Rewrite(P(0, "140"))));
        Assert.Equal(62.5, Pct(t.Rewrite(P(50, "140"))));
        Assert.Equal(100, Pct(t.Rewrite(P(100, "140"))));
        Assert.EndsWith("|1.0MiB/s|00:10", t.Rewrite(P(100, "140")));
    }

    [Fact]
    public void Without_sizes_each_stream_gets_an_equal_share_until_they_show_up()
    {
        using var dir = new TempDir();
        var sizes = Path.Combine(dir.Path, "sizes.txt");
        var t = new ProgressTracker(sizes);
        t.Rewrite("[info] abc: Downloading 1 format(s): 160+140-3");
        Assert.Equal(25, Pct(t.Rewrite(P(50, "160"))));
        File.WriteAllText(sizes, "abc|160|1000|140-3|3000\n");
        Assert.Equal(12.5, Pct(t.Rewrite(P(50, "160"))));
        Assert.Equal(62.5, Pct(t.Rewrite(P(50, "140-3"))));
    }

    [Fact]
    public void Each_video_of_a_list_uses_its_own_sizes()
    {
        using var dir = new TempDir();
        var sizes = dir.File("sizes.txt", "aaa|1|100|2|300\nbbb|1|300|2|100\n");
        var t = new ProgressTracker(sizes);
        t.Rewrite("[info] aaa: Downloading 1 format(s): 1+2");
        Assert.Equal(25, Pct(t.Rewrite(P(100, "1"))));
        t.Rewrite("[info] bbb: Downloading 1 format(s): 1+2");
        Assert.Equal(75, Pct(t.Rewrite(P(100, "1"))));
    }

    [Fact]
    public void Unknown_sizes_mean_equal_shares()
    {
        using var dir = new TempDir();
        var sizes = dir.File("sizes.txt", "abc|160|NA|140|5000\n");
        var t = new ProgressTracker(sizes);
        t.Rewrite("[info] abc: Downloading 1 format(s): 160+140");
        Assert.Equal(50, Pct(t.Rewrite(P(100, "160"))));
    }
}

[Trait("Category", "Unit")]
public class AppUpdateTests
{
    const string Json = """
        {"tag_name": "v1.9.0", "html_url": "https://github.com/lovesuper/nya-downloader/releases/tag/v1.9.0",
         "assets": [
           {"name": "notes.txt", "browser_download_url": "https://example.com/notes.txt", "size": 10, "digest": "sha256:00"},
           {"name": "NyaDownloader.exe", "browser_download_url": "https://example.com/NyaDownloader.exe", "size": 62624272,
            "digest": "sha256:5E835A397DA007A1399FFB584DA7B716BC6A33D0099158256B0E177311D159A1"}]}
        """;

    [Fact]
    public void Reads_the_release_and_its_exe()
    {
        var r = AppUpdate.Parse(Json)!;
        Assert.Equal(new Version(1, 9, 0), r.Version);
        Assert.Equal("https://github.com/lovesuper/nya-downloader/releases/tag/v1.9.0", r.PageUrl);
        Assert.Equal("https://example.com/NyaDownloader.exe", r.ExeUrl);
        Assert.Equal("5e835a397da007a1399ffb584da7b716bc6a33d0099158256b0e177311d159a1", r.Sha256);
        Assert.Equal(62624272, r.Size);
    }

    [Theory]
    [InlineData("{\"tag_name\": \"1.10.0\", \"html_url\": \"x\"}", "1.10.0", null)]
    [InlineData("{\"tag_name\": \"v2.0\", \"html_url\": \"x\", \"assets\": []}", "2.0", null)]
    public void Tags_with_or_without_v_and_no_exe(string json, string version, string? exe)
    {
        var r = AppUpdate.Parse(json)!;
        Assert.Equal(Version.Parse(version), r.Version);
        Assert.Equal(exe, r.ExeUrl);
    }

    [Theory]
    [InlineData("{\"tag_name\": \"latest\", \"html_url\": \"x\"}")]
    [InlineData("{\"message\": \"API rate limit exceeded\"}")]
    [InlineData("<html>")]
    public void Garbage_is_no_release(string json) => Assert.Null(AppUpdate.Parse(json));

    [Theory]
    [InlineData("1.9.0", "1.8.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.9.0", "1.9.0", false)]
    [InlineData("1.8.5", "1.9.0", false)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.9.0", "?", false)]
    public void Newer(string release, string current, bool newer) =>
        Assert.Equal(newer, AppUpdate.IsNewer(new AppRelease(Version.Parse(release), "", null, null, 0), current));

    static byte[] FakeExe(int size, byte first = (byte)'M')
    {
        var b = new byte[size];
        b[0] = first;
        b[1] = (byte)'Z';
        return b;
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Downloads_and_checks_the_new_exe_next_to_the_old_one()
    {
        var exe = FakeExe(3 * 1024 * 1024);
        using var http = new LocalHttp(new() { ["NyaDownloader.exe"] = exe });
        using var dir = new TempDir();
        var path = dir.File("NyaDownloader.exe", "old");
        var progress = new List<double>();
        await AppUpdate.DownloadAsync(new(new Version(9, 9, 9), "", http.Url("NyaDownloader.exe"), Sha(exe), exe.Length), path, progress.Add, CancellationToken.None);
        Assert.Equal(exe, File.ReadAllBytes(path + ".new"));
        Assert.Equal(1.0, progress[^1]);
        Assert.Equal("old", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("small")]
    [InlineData("not exe")]
    [InlineData("missing")]
    [InlineData("no asset")]
    public async Task A_bad_download_leaves_nothing_behind(string problem)
    {
        var good = FakeExe(2 * 1024 * 1024);
        var body = problem switch { "small" => FakeExe(1000), "not exe" => FakeExe(2 * 1024 * 1024, (byte)'A'), _ => good };
        using var http = new LocalHttp(problem == "missing" ? new() : new() { ["NyaDownloader.exe"] = body });
        using var dir = new TempDir();
        var path = dir.File("NyaDownloader.exe", "old");
        var release = new AppRelease(new Version(9, 9, 9), "", problem == "no asset" ? null : http.Url("NyaDownloader.exe"),
                                     problem == "digest" ? Sha(new byte[] { 1 }) : null, 0);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AppUpdate.DownloadAsync(release, path, _ => { }, CancellationToken.None));
        Assert.True(error is InvalidDataException or HttpRequestException, error.ToString());
        Assert.Equal(["NyaDownloader.exe"], dir.Files());
        Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact]
    public void Swap_keeps_the_old_exe_and_clean_up_removes_it()
    {
        using var dir = new TempDir();
        var exe = dir.File("NyaDownloader.exe", "running");
        dir.File("NyaDownloader.exe.new", "fresh");
        dir.File("NyaDownloader.old.exe", "older");
        AppUpdate.Swap(exe);
        Assert.Equal("fresh", File.ReadAllText(exe));
        Assert.Equal("running", File.ReadAllText(dir.File("NyaDownloader.old.exe")));
        Assert.Equal(["NyaDownloader.exe", "NyaDownloader.old.exe"], dir.Files().Order());

        dir.File("NyaDownloader.exe.new", "half");
        AppUpdate.CleanUp(exe);
        Assert.Equal(["NyaDownloader.exe"], dir.Files());
        Assert.Equal("fresh", File.ReadAllText(exe));
    }

    [Fact]
    public void Swap_without_a_new_exe_puts_the_old_one_back()
    {
        using var dir = new TempDir();
        var exe = dir.File("NyaDownloader.exe", "running");
        Assert.ThrowsAny<IOException>(() => AppUpdate.Swap(exe));
        Assert.Equal("running", File.ReadAllText(exe));
        Assert.Equal(["NyaDownloader.exe"], dir.Files());
    }
}

[Trait("Category", "Unit")]
public class SubtitlePickTests
{
    static readonly SubtitleLanguage En = SubtitleLanguage.From("en");

    [Theory]
    [InlineData("en,en-US", "en-US", "en-US")]
    [InlineData("en,en-US", "", "en")]
    [InlineData("en-GB,en-US", "en-GB,en-US", "en-GB")]
    [InlineData("en,en-GB", "en,en-GB", "en")]
    [InlineData("eng-US", "", "eng-US")]
    [InlineData("", "", null)]
    public void One_file_per_language_prefers_real_subtitles(string found, string manual, string? picked) =>
        Assert.Equal(picked, En.Pick(found.Split(',', StringSplitOptions.RemoveEmptyEntries),
                                     manual.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()));

    [Fact]
    public void Manual_subtitles_list_is_read_per_video()
    {
        using var dir = new TempDir();
        var file = dir.File("manual.txt",
            "abc|{\"en-US\": [{\"ext\": \"vtt\", \"url\": \"https://x/?a=1|2\"}], \"de\": []}\n" +
            "def|{}\n" +
            "ghi|NA\n" +
            "broken line\n");
        var m = Downloader.ManualSubtitles(file);
        Assert.Equal(["de", "en-US"], m["abc"].Order());
        Assert.Empty(m["def"]);
        Assert.Empty(m["ghi"]);
        Assert.Equal(3, m.Count);
        Assert.Empty(Downloader.ManualSubtitles(Path.Combine(dir.Path, "missing.txt")));
    }
}

[Trait("Category", "Unit")]
[Collection(nameof(SettingsEnvironment))]
public class TaskbarAndStatusTests
{
    [Fact]
    public void Windows_gives_us_the_real_taskbar_progress_interface()
    {
        var error = MainFormLayoutTests.OnSta(() =>
        {
            try { TaskbarProgress.CheckAvailable(); return null; }
            catch (Exception e) { return e.Message; }
        });
        Assert.Null(error);
    }

    [Fact]
    public void Taskbar_state_follows_what_it_is_told()
    {
        var t = new TaskbarProgress();
        using var c = new Control();
        t.Set(c, TaskbarProgress.State.Normal, 420);
        Assert.Equal((TaskbarProgress.State.Normal, 420), (t.Current, t.Value));
        t.Set(c, TaskbarProgress.State.Indeterminate, 500);
        Assert.Equal((TaskbarProgress.State.Indeterminate, 0), (t.Current, t.Value));
        t.Set(c, TaskbarProgress.State.Error, 5000);
        Assert.Equal((TaskbarProgress.State.Error, 1000), (t.Current, t.Value));
    }

    [Fact]
    public void A_stuck_taskbar_never_blocks_the_window()
    {
        var (elapsed, applied) = MainFormLayoutTests.OnSta(() =>
        {
            using var stuck = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var applied = new List<(TaskbarProgress.State, int)>();
            int first = 1;
            var t = new TaskbarProgress((_, s, v) =>
            {
                if (Interlocked.Exchange(ref first, 0) == 1) { entered.Set(); stuck.Wait(3000); }
                lock (applied) applied.Add((s, v));
            });
            using var c = new Control();
            _ = c.Handle;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            t.Set(c, TaskbarProgress.State.Normal, 1);
            var elapsed = watch.ElapsedMilliseconds;
            if (!entered.Wait(5000)) throw new TimeoutException("the taskbar thread never ran");
            watch.Restart();
            for (int i = 2; i <= 200; i++) t.Set(c, TaskbarProgress.State.Normal, i);
            elapsed += watch.ElapsedMilliseconds;
            stuck.Set();
            for (int i = 0; i < 100; i++)
            {
                lock (applied) if (applied.Contains((TaskbarProgress.State.Normal, 200))) break;
                Thread.Sleep(50);
            }
            lock (applied) return (elapsed, applied.ToList());
        });
        Assert.True(elapsed < 500, $"setting the taskbar took {elapsed} ms while the shell was stuck");
        Assert.Equal([(TaskbarProgress.State.Normal, 1), (TaskbarProgress.State.Normal, 200)], applied);
    }

    [Fact]
    public void Window_progress_shows_on_the_taskbar_button()
    {
        var states = MainFormLayoutTests.OnSta(() =>
        {
            using var dir = new TempDir();
            Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", dir.Path);
            try
            {
                using var form = new MainForm();
                _ = form.Handle;
                var taskbar = (TaskbarProgress)typeof(MainForm).GetField("_taskbar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                void Call(string name, params object[] args) =>
                    typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
                var seen = new List<(TaskbarProgress.State, int)>();
                Call("StartHeartbeat", "Preparing download", 0);
                seen.Add((taskbar.Current, taskbar.Value));
                Call("OnOutput", DownloadPlan.ProgressPrefix + " 42.0%|1.0MiB/s|00:10");
                seen.Add((taskbar.Current, taskbar.Value));
                Call("ShowStatus", "Failed.", Color.Firebrick);
                Call("SetBusy", false);
                seen.Add((taskbar.Current, taskbar.Value));
                Call("ShowStatus", "Saved 1 of 2 videos", Color.FromArgb(0xB3, 0x5C, 0x00));
                Call("SetBusy", false);
                seen.Add((taskbar.Current, taskbar.Value));
                Call("ShowStatus", "Done.", Color.FromArgb(0x1E, 0x7B, 0x34));
                Call("SetBusy", false);
                seen.Add((taskbar.Current, taskbar.Value));
                return seen;
            }
            finally { Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", null); }
        });
        Assert.Equal(
            [(TaskbarProgress.State.Indeterminate, 0), (TaskbarProgress.State.Normal, 420), (TaskbarProgress.State.Error, 1000),
             (TaskbarProgress.State.Paused, 1000), (TaskbarProgress.State.None, 0)],
            states);
    }

    [Theory]
    [InlineData(2, 3, 1, "The video was removed or is not available.", "Saved 2 of 3 videos, 1 failed. The video was removed or is not available.")]
    [InlineData(0, 1, 1, null, "Saved 0 of 1 video, 1 failed. See the log below.")]
    public void Partial_download_status(int saved, int total, int failed, string? hint, string text) =>
        Assert.Equal(text, MainForm.PartialStatus(saved, total, failed, hint));
}
