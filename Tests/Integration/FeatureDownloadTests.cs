using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace VideoDownloader.Tests.Integration;

[Trait("Category", "Integration")]
[Collection(nameof(Network))]
public class FeatureDownloadTests
{
    const string ShortA = "https://www.youtube.com/shorts/41f_EDdLubo";
    const string ShortB = "https://www.youtube.com/shorts/gFOK8XE0Rto";

    static async Task<(Downloader.Result R, List<string> Lines)> Run(Downloader.Request request)
    {
        var lines = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var r = await Downloader.RunAsync(request, l => { lock (lines) lines.Add(l); }, timeout.Token);
        return (r, lines);
    }

    static Downloader.Request Request(string url, string outDir, DownloadMode mode = DownloadMode.Video, int maxHeight = 360)
    {
        var uri = PlatformDetector.ParseUrl(url)!;
        return new(PlatformDetector.FromHost(uri.Host), uri, mode, maxHeight, outDir, null, false);
    }

    static void AssertOk(Downloader.Result r) => Assert.True(r.ExitCode == 0, "failed:\n" + ErrorLog.Tail(r.Output));

    static string Probe(string file, string entries)
    {
        var ffmpeg = YtDlp.FfmpegExe();
        var ffprobe = Path.IsPathRooted(ffmpeg) ? Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe") : "ffprobe";
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", entries, "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }

    static double Duration(string file) => double.Parse(Probe(file, "format=duration"), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Original_audio_is_kept_as_m4a()
    {
        using var dir = new TempDir();
        var (yt, _) = await Run(Request(ShortA, dir.Path, DownloadMode.Audio));
        AssertOk(yt);
        var m4a = Assert.Single(yt.Saved);
        Assert.EndsWith("[41f_EDdLubo].m4a", m4a);
        Assert.Equal("aac", Probe(m4a, "stream=codec_name"));
        Assert.Contains("[41f_EDdLubo].m4a", Assert.Single(dir.Files()));

        var (tt, lines) = await Run(Request(TestUrls.TikTok, dir.Path, DownloadMode.Audio, maxHeight: 0));
        AssertOk(tt);
        Assert.Equal(Path.Combine(dir.Path, TestUrls.TikTokFile + "_audio.m4a"), Assert.Single(tt.Saved));
        Assert.Equal("aac", Probe(tt.Saved[0], "stream=codec_name"));
        Assert.DoesNotContain(lines, l => l.Contains("Destination") && l.EndsWith(".mp3"));
    }

    [Fact]
    public async Task Mp3_is_re_encoded_at_a_sensible_bitrate()
    {
        using var dir = new TempDir();
        var (r, _) = await Run(Request(ShortA, dir.Path, DownloadMode.Mp3));
        AssertOk(r);
        var mp3 = Assert.Single(r.Saved);
        Assert.Equal("mp3", Probe(mp3, "stream=codec_name"));
        var kbps = double.Parse(Probe(mp3, "format=bit_rate"), CultureInfo.InvariantCulture) / 1000;
        Assert.InRange(kbps, 100, 260);
    }

    [Fact]
    public async Task Progress_of_video_and_audio_streams_never_goes_back()
    {
        using var dir = new TempDir();
        var (r, lines) = await Run(Request(TestUrls.YouTube, dir.Path, maxHeight: 144));
        AssertOk(r);
        var values = lines.Where(l => l.StartsWith(DownloadPlan.ProgressPrefix))
            .Select(l => double.Parse(l[DownloadPlan.ProgressPrefix.Length..].Split('|')[0].Trim().TrimEnd('%'), CultureInfo.InvariantCulture))
            .ToList();
        Assert.True(values.Count > 3, $"{values.Count} progress lines");
        Assert.All(lines.Where(l => l.StartsWith(DownloadPlan.ProgressPrefix)), l => Assert.Equal(3, l.Split('|').Length));
        for (int i = 1; i < values.Count; i++) Assert.True(values[i] >= values[i - 1], $"went back from {values[i - 1]} to {values[i]}");
        Assert.Equal(100, values[^1]);
        Assert.Contains(lines, l => l.Contains("Downloading 1 format(s): ") && l.Contains('+'));
        Assert.Contains(values, v => v is > 5 and < 95);
    }

    [Fact]
    public async Task Cancelling_a_fragment_leaves_no_temporary_folder()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var started = new TaskCompletionSource();
        var request = Request(TestUrls.YouTubeLong, dir.Path, maxHeight: 720) with { Sections = [new Section(10, 20)] };
        var run = Downloader.RunAsync(request, l => { if (l.StartsWith(DownloadPlan.ProgressPrefix)) started.TrySetResult(); }, cts.Token);
        await Task.WhenAny(started.Task, run);
        Assert.True(started.Task.IsCompleted, "the download never started");
        Assert.Single(Directory.GetDirectories(dir.Path, ".nya-*"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task GitHub_tells_the_latest_app_release()
    {
        var r = await AppUpdate.CheckAsync();
        Assert.NotNull(r);
        Assert.True(r.Version >= new Version(1, 7, 1), r.Version.ToString());
        Assert.StartsWith("https://github.com/lovesuper/nya-downloader/releases/tag/v", r.PageUrl);
        Assert.EndsWith("/NyaDownloader.exe", r.ExeUrl);
        Assert.Matches("^[0-9a-f]{64}$", r.Sha256);
        Assert.True(r.Size > 30_000_000);
    }

    static string[] Everything(string dir) =>
        Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).Order().ToArray();

    [Fact]
    public async Task Playlist_link_with_a_video_lists_the_whole_playlist()
    {
        var uri = new Uri(TestUrls.YouTubeInPlaylist);
        var counts = new List<int>();
        var l = await Listing.RunAsync(Platform.YouTube, uri, null, counts.Add, CancellationToken.None);
        Assert.True(l.IsList, ErrorLog.Tail(l.Output));
        Assert.Equal(TestUrls.YouTubePlaylistTitle, l.Title);
        Assert.Equal(11, l.Entries.Count);
        Assert.Equal("nBwADVXLHwg", l.Entries[0].Id);
        Assert.Equal("GE6PenmTCKM", l.Entries[9].Id);
        Assert.All(l.Entries, e => Assert.True(e.Duration > 60, e.Title));
        Assert.All(l.Entries, e => Assert.StartsWith("https://www.youtube.com/watch?v=" + e.Id, e.Url));
        Assert.All(l.Entries, e => Assert.Null(e.Section));
        Assert.Equal(11, counts.Max());

        var plain = await Listing.RunAsync(Platform.YouTube, new Uri(TestUrls.YouTubePlaylist), null, _ => { }, CancellationToken.None);
        Assert.Equal(l.Entries.Select(e => e.Id), plain.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task A_single_video_is_not_a_list()
    {
        var l = await Listing.RunAsync(Platform.YouTube, new Uri(TestUrls.YouTube), null, _ => { }, CancellationToken.None);
        Assert.Equal(0, l.ExitCode);
        Assert.False(l.IsList);
    }

    [Fact]
    public async Task TikTok_profile_gives_a_list_or_a_clean_failure()
    {
        var l = await Listing.RunAsync(Platform.TikTok, new Uri(TestUrls.TikTokProfile), null, _ => { }, CancellationToken.None);
        if (l.ExitCode != 0) Assert.Empty(l.Entries);
        else Assert.True(l.IsList && l.Entries.Count > 0, ErrorLog.Tail(l.Output));
    }

    [Fact]
    public async Task Chapters_of_a_long_video()
    {
        var info = await VideoInfo.RunAsync(Platform.YouTube, new Uri(TestUrls.YouTubeChapters), null, CancellationToken.None);
        Assert.True(info.ExitCode == 0, ErrorLog.Tail(info.Output));
        Assert.StartsWith("Скандинавская мифология", info.Title);
        Assert.True(info.Chapters.Count >= 12, $"{info.Chapters.Count} chapters");
        Assert.Equal(new Section(3442, 3504, "Берсерки: безумные воины Одина", 10), info.Chapters[9]);
        Assert.Equal(info.Duration, info.Chapters[^1].End);
        Assert.All(info.Chapters.Zip(info.Chapters.Skip(1)), p => Assert.Equal(p.First.End, p.Second.Start));
    }

    [Fact]
    public async Task Video_without_chapters()
    {
        var info = await VideoInfo.RunAsync(Platform.YouTube, new Uri(ShortA), null, CancellationToken.None);
        Assert.Equal(0, info.ExitCode);
        Assert.Empty(info.Chapters);
        Assert.Equal(36, info.Duration);
    }

    [Fact]
    public async Task Several_videos_from_a_list_and_one_that_is_gone()
    {
        using var dir = new TempDir();
        var (r, lines) = await Run(Request(ShortA, dir.Path) with { Items = [ShortA, TestUrls.YouTubeMissing, ShortB] });
        Assert.NotEqual(0, r.ExitCode);
        Assert.Equal(2, r.Videos);
        Assert.Equal(1, r.Failed);
        Assert.Contains(r.Saved, f => f.Contains("[41f_EDdLubo]"));
        Assert.Contains(r.Saved, f => f.Contains("[gFOK8XE0Rto]"));
        Assert.All(r.Saved, f => Assert.True(File.Exists(f), f));
        Assert.Equal(3, lines.Count(l => l.Contains("Extracting URL:")));
        Assert.Equal("The video was removed or is not available.", Hints.Diagnose(r.Output, Platform.YouTube).Hint);
        Assert.Equal(2, dir.Files("*.mp4").Length);
    }

    [Fact]
    public async Task Fragment_is_cut_exactly_and_the_subtitles_follow_it()
    {
        using var dir = new TempDir();
        var (r, lines) = await Run(Request(TestUrls.YouTube, dir.Path) with
        {
            Sections = [new Section(5, 15)],
            Subtitles = SubtitleLanguage.From("ru"),
        });
        AssertOk(r);
        var video = Assert.Single(r.Saved);
        Assert.EndsWith($"[{TestUrls.YouTubeId}] 640x360 (0.05-0.15).mp4", video);
        Assert.InRange(Duration(video), 9.9, 10.2);
        var subs = Assert.Single(r.Subtitles);
        Assert.Equal(Path.ChangeExtension(video, null) + ".ru.srt", subs);
        var srt = File.ReadAllText(subs);
        Assert.StartsWith("1\r\n00:00:0", srt);
        var stamps = System.Text.RegularExpressions.Regex.Matches(srt, @"\d\d:\d\d:\d\d,\d{3}")
            .Select(m => TimeSpan.ParseExact(m.Value, @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture).TotalSeconds).ToList();
        Assert.NotEmpty(stamps);
        Assert.All(stamps, s => Assert.InRange(s, 0, 10));
        Assert.Contains("-> Cutting 0:05-0:15...", lines);
        Assert.Contains(Downloader.SubtitlesNote, lines);
        Assert.Equal([Path.GetFileName(video), Path.GetFileName(subs)], Everything(dir.Path));
    }

    [Fact]
    public async Task Chapters_become_separate_files()
    {
        using var dir = new TempDir();
        var (r, _) = await Run(Request(ShortA, dir.Path) with
        {
            SaveCover = true,
            Sections = [new Section(0, 5, "Intro: one", 1), new Section(30, double.PositiveInfinity, "Outro", 3)],
        });
        AssertOk(r);
        Assert.Equal(2, r.Videos);
        Assert.EndsWith(" - 01 Intro. one.mp4", r.Saved[0]);
        Assert.EndsWith(" - 03 Outro.mp4", r.Saved[1]);
        Assert.InRange(Duration(r.Saved[0]), 4.9, 5.2);
        Assert.InRange(Duration(r.Saved[1]), 5, 7);
        var cover = Assert.Single(r.Covers);
        Assert.Equal(dir.Path, Path.GetDirectoryName(cover));
        Assert.Matches(@"\[41f_EDdLubo\] \d+x\d+\.jpg$", cover);
        Assert.True(File.Exists(cover));
        Assert.Equal(3, Everything(dir.Path).Length);
    }

    [Fact]
    public async Task Audio_fragment_and_a_fragment_after_the_end()
    {
        using var dir = new TempDir();
        var (audio, _) = await Run(Request(ShortA, dir.Path, DownloadMode.Mp3) with { Sections = [new Section(10, 20)] });
        AssertOk(audio);
        var mp3 = Assert.Single(audio.Saved);
        Assert.EndsWith("[41f_EDdLubo] (0.10-0.20).mp3", mp3);
        Assert.InRange(Duration(mp3), 9.8, 10.3);

        var (late, _) = await Run(Request(ShortA, dir.Path) with { Sections = [new Section(100, 110)] });
        Assert.NotEqual(0, late.ExitCode);
        Assert.Empty(late.Saved);
        Assert.Contains("The fragment 1:40-1:50 starts after the end of the video (0:36).", late.Output);
        Assert.Equal([Path.GetFileName(mp3)], Everything(dir.Path));
    }

    [Fact]
    public async Task TikTok_subtitles_are_converted_to_srt()
    {
        using var dir = new TempDir();
        var (r, _) = await Run(Request(TestUrls.TikTok, dir.Path, maxHeight: 0) with { Subtitles = SubtitleLanguage.From("ru") });
        AssertOk(r);
        var subs = Assert.Single(r.Subtitles);
        Assert.Equal(Path.Combine(dir.Path, TestUrls.TikTokFile + ".rus-RU.srt"), subs);
        Assert.Contains("-->", File.ReadAllText(subs));
        Assert.Equal([TestUrls.TikTokFile + ".mp4", TestUrls.TikTokFile + ".rus-RU.srt"], Everything(dir.Path));
    }

    [Fact]
    public async Task No_subtitles_in_that_language_is_not_an_error()
    {
        using var dir = new TempDir();
        var (r, _) = await Run(Request(ShortA, dir.Path) with { Subtitles = SubtitleLanguage.From("ja") });
        AssertOk(r);
        Assert.Single(r.Saved);
        Assert.Empty(r.Subtitles);
    }
}
