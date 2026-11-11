using System.Diagnostics;
using Xunit;

namespace VideoDownloader.Tests.Integration;

[CollectionDefinition(nameof(Network), DisableParallelization = true)]
public class Network;

[Trait("Category", "Integration")]
[Collection(nameof(Network))]
public class DownloadTests
{
    static async Task<(Downloader.Result R, List<string> Lines)> Download(string url, string outDir, DownloadMode mode = DownloadMode.Video,
                                                                         int maxHeight = 0, bool cover = false, string? cookies = null)
    {
        var uri = PlatformDetector.ParseUrl(url)!;
        var lines = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var r = await Downloader.RunAsync(new(PlatformDetector.FromHost(uri.Host), uri, mode, maxHeight, outDir, cookies, cover),
                                          l => { lock (lines) lines.Add(l); }, timeout.Token);
        return (r, lines);
    }

    static void AssertOk(Downloader.Result r) => Assert.True(r.ExitCode == 0, "yt-dlp failed:\n" + ErrorLog.Tail(r.Output));

    [Fact]
    public async Task YouTube_capped_video_with_cover_then_already_downloaded()
    {
        using var dir = new TempDir();
        var (r, _) = await Download(TestUrls.YouTube, dir.Path, maxHeight: 360, cover: true);
        AssertOk(r);
        var video = Assert.Single(r.Saved);
        var name = Path.GetFileName(video);
        Assert.True(File.Exists(video));
        Assert.EndsWith(".mp4", name);
        Assert.Contains($"[{TestUrls.YouTubeId}]", name);
        Assert.Contains(" 640x360", name);
        Assert.DoesNotContain("!", name);
        Assert.Equal("640x360", r.Resolution);
        Assert.Equal((0, 1), (r.Photos, r.Videos));
        var cover = Assert.Single(r.Covers);
        Assert.Equal(Path.ChangeExtension(video, ".jpg"), cover);
        Assert.True(new FileInfo(cover).Length > 1000);

        var (again, lines) = await Download(TestUrls.YouTube, dir.Path, maxHeight: 360);
        AssertOk(again);
        Assert.Equal(video, Assert.Single(again.Saved));
        Assert.Contains(lines, l => l.Contains("has already been downloaded"));
    }

    [Fact]
    public async Task YouTube_audio_is_mp3()
    {
        using var dir = new TempDir();
        var (r, _) = await Download(TestUrls.YouTubeShort, dir.Path, DownloadMode.Mp3);
        AssertOk(r);
        var mp3 = Assert.Single(r.Saved);
        Assert.EndsWith($"[{TestUrls.YouTubeId}].mp3", mp3);
        Assert.Null(r.Resolution);
        Assert.True(new FileInfo(mp3).Length > 100_000);
        Assert.Equal([Path.GetFileName(mp3)], dir.Files());
    }

    [Fact]
    public async Task YouTube_age_restricted_video_asks_for_cookies()
    {
        using var dir = new TempDir();
        var (r, _) = await Download(TestUrls.YouTubeAgeRestricted, dir.Path, maxHeight: 360);
        Assert.NotEqual(0, r.ExitCode);
        Assert.Equal(Hints.LoginProblem.AgeRestricted, Hints.Diagnose(r.Output, Platform.YouTube).Login);
        Assert.Empty(dir.Files("*.mp4"));
    }

    [Fact]
    public async Task TikTok_video_and_audio_in_one_folder()
    {
        using var dir = new TempDir();
        var (video, _) = await Download(TestUrls.TikTok, dir.Path);
        AssertOk(video);
        Assert.Equal(Path.Combine(dir.Path, TestUrls.TikTokFile + ".mp4"), Assert.Single(video.Saved));

        var (audio, _) = await Download(TestUrls.TikTok, dir.Path, DownloadMode.Mp3);
        AssertOk(audio);
        Assert.Equal(Path.Combine(dir.Path, TestUrls.TikTokFile + "_audio.mp3"), Assert.Single(audio.Saved));
        Assert.True(File.Exists(video.Saved[0]), "extracting the audio must not delete the video");
    }

    [Fact]
    public async Task Instagram_carousel_saves_photos_and_videos()
    {
        using var dir = new TempDir();
        var (r, lines) = await Download(TestUrls.InstagramCarousel, dir.Path);
        AssertOk(r);
        Assert.Equal((5, 2), (r.Photos, r.Videos));
        Assert.Equal(7, r.Saved.Count);
        Assert.All(r.Saved, f => Assert.True(File.Exists(f), f));
        Assert.Equal(5, r.Saved.Count(f => f.EndsWith(".jpg")));
        Assert.Equal(2, r.Saved.Count(f => f.EndsWith(".mp4")));
        Assert.Contains("-> The post has videos, downloading them.", lines);
    }

    [Fact]
    public async Task Instagram_single_photo_needs_no_video_pass()
    {
        using var dir = new TempDir();
        var (r, lines) = await Download(TestUrls.InstagramPhoto, dir.Path);
        AssertOk(r);
        Assert.Equal((1, 0), (r.Photos, r.Videos));
        Assert.Equal(TestUrls.InstagramPhotoFile, Path.GetFileName(Assert.Single(r.Saved)));
        Assert.DoesNotContain("-> The post has videos, downloading them.", lines);

        var (again, _) = await Download(TestUrls.InstagramPhoto, dir.Path);
        AssertOk(again);
        Assert.Equal(r.Saved, again.Saved);
        Assert.Equal([TestUrls.InstagramPhotoFile], dir.Files());
    }

    [Fact]
    public async Task Instagram_reel_is_one_video_without_a_photo_pass()
    {
        using var dir = new TempDir();
        var (r, lines) = await Download(TestUrls.InstagramReel, dir.Path, cover: true);
        AssertOk(r);
        Assert.Equal((0, 1), (r.Photos, r.Videos));
        Assert.Equal(TestUrls.InstagramReelFile + ".mp4", Path.GetFileName(Assert.Single(r.Saved)));
        Assert.Equal(TestUrls.InstagramReelFile + ".jpg", Path.GetFileName(Assert.Single(r.Covers)));
        Assert.DoesNotContain(lines, l => l.StartsWith("-> Checking what the post contains"));
    }

    [Fact]
    public async Task Cancel_stops_yt_dlp_and_ffmpeg()
    {
        using var dir = new TempDir();
        var uri = new Uri(TestUrls.YouTubeLong);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var progress = new TaskCompletionSource();
        var run = Downloader.RunAsync(new(Platform.YouTube, uri, DownloadMode.Video, 0, dir.Path, null, false),
            l => { if (l.StartsWith(DownloadPlan.ProgressPrefix)) progress.TrySetResult(); }, cts.Token);
        await Task.WhenAny(progress.Task, run);
        Assert.True(progress.Task.IsCompleted, "the download never started");
        Assert.NotEmpty(ProcessesWorkingIn(dir.Path));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(ProcessesWorkingIn(dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Cancelling_a_list_keeps_the_videos_that_are_done()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var second = new TaskCompletionSource();
        int extracting = 0;
        var request = new Downloader.Request(Platform.YouTube, new Uri("https://www.youtube.com/shorts/41f_EDdLubo"), DownloadMode.Video, 0, dir.Path, null, false)
        {
            Items = ["https://www.youtube.com/shorts/41f_EDdLubo", TestUrls.YouTubeLong],
        };
        var lines = new List<string>();
        var run = Downloader.RunAsync(request, l =>
        {
            lock (lines) lines.Add(l);
            if (l.Contains("Extracting URL:")) Interlocked.Increment(ref extracting);
            if (Volatile.Read(ref extracting) >= 2 && l.StartsWith(DownloadPlan.ProgressPrefix) && !l.Contains(" 0.0%")) second.TrySetResult();
        }, cts.Token);
        await Task.WhenAny(second.Task, run);
        Assert.True(second.Task.IsCompleted, "the second video never started downloading");
        Assert.Contains(Directory.GetFiles(dir.Path), f => Leftovers.IsTemporary(Path.GetFileName(f)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var left = dir.Files();
        Assert.Single(left);
        Assert.EndsWith("[41f_EDdLubo].mp4", left[0]);
        Assert.Contains(lines, l => l.StartsWith("-> Removed ") && l.Contains("unfinished file"));
    }

    internal static string[] ProcessesWorkingIn(string dir)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{dir}*' -and $_.Name -ne 'powershell.exe' }} | ForEach-Object {{ $_.Name }}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

[Trait("Category", "Integration")]
[Collection(nameof(Network))]
public class EngineTests
{
    [Fact]
    public async Task YtDlp_is_found_and_reports_its_version()
    {
        var (code, output) = await YtDlp.RunAsync(["--version"], null);
        Assert.Equal(0, code);
        Assert.Matches(@"^\d{4}\.\d{2}\.\d{2}", output.Trim());
        Assert.True(YtDlp.HasFfmpeg(), "ffmpeg + ffprobe are needed for merging and mp3");
        Assert.True(YtDlp.HasJsRuntime(), "YouTube needs node or deno");
    }

    [Fact]
    public async Task Update_check_lists_the_tracked_packages()
    {
        var (_, version) = await YtDlp.RunAsync(["--version"], null);
        var info = await YtDlp.CheckUpdatesAsync(version.Trim());
        Assert.NotNull(info);
        Assert.Contains("yt-dlp", info.Installed.Keys);
        if (YtDlp.UsesPython)
        {
            Assert.Contains("curl_cffi", info.Installed.Keys);
            Assert.Contains("yt-dlp-ejs", info.Installed.Keys);
        }
        Assert.All(info.Updates, u => Assert.NotEqual(u.Installed, u.Latest));
    }
}
