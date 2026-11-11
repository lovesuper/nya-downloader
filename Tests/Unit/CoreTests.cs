using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
public class PlatformDetectorTests
{
    [Theory]
    [InlineData("youtube.com", Platform.YouTube)]
    [InlineData("www.youtube.com", Platform.YouTube)]
    [InlineData("m.youtube.com", Platform.YouTube)]
    [InlineData("music.youtube.com", Platform.YouTube)]
    [InlineData("youtu.be", Platform.YouTube)]
    [InlineData("www.youtube-nocookie.com", Platform.YouTube)]
    [InlineData("WWW.YOUTUBE.COM.", Platform.YouTube)]
    [InlineData("tiktok.com", Platform.TikTok)]
    [InlineData("vm.tiktok.com", Platform.TikTok)]
    [InlineData("vt.tiktok.com", Platform.TikTok)]
    [InlineData("www.instagram.com", Platform.Instagram)]
    [InlineData("instagr.am", Platform.Instagram)]
    [InlineData("vimeo.com", Platform.Unknown)]
    [InlineData("notyoutube.com", Platform.Unknown)]
    [InlineData("youtube.com.evil.net", Platform.Unknown)]
    [InlineData("", Platform.Unknown)]
    public void FromHost(string host, Platform expected) => Assert.Equal(expected, PlatformDetector.FromHost(host));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=6ItexNkuSJg", "https://www.youtube.com/watch?v=6ItexNkuSJg")]
    [InlineData("youtu.be/6ItexNkuSJg", "https://youtu.be/6ItexNkuSJg")]
    [InlineData("  \thttps://youtu.be/x  \r\n", "https://youtu.be/x")]
    [InlineData("\uFEFFhttps://youtu.be/x", "https://youtu.be/x")]
    [InlineData("https://youtu.be/a https://youtu.be/b", "https://youtu.be/a")]
    [InlineData("HTTP://YOUTU.BE/x", "http://youtu.be/x")]
    [InlineData("youtu.be/x?next=https://example.com", "https://youtu.be/x?next=https://example.com")]
    public void ParseUrl_accepts(string text, string expected) => Assert.Equal(expected, PlatformDetector.ParseUrl(text)?.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("ftp://example.com/x")]
    public void ParseUrl_rejects(string text) => Assert.Null(PlatformDetector.ParseUrl(text));

    [Theory]
    [InlineData("https://www.instagram.com/reel/DejDD7XIBB1/", true)]
    [InlineData("https://www.instagram.com/reels/DejDD7XIBB1/", true)]
    [InlineData("https://www.instagram.com/tv/CDUMkliABpa/", true)]
    [InlineData("https://www.instagram.com/p/DfkeBxnFJh3/", false)]
    public void IsInstagramReel(string url, bool reel) => Assert.Equal(reel, PlatformDetector.IsInstagramReel(new Uri(url)));

    [Fact]
    public void DisplayName()
    {
        Assert.Equal("YouTube", PlatformDetector.DisplayName(Platform.YouTube));
        Assert.Equal("TikTok", PlatformDetector.DisplayName(Platform.TikTok));
        Assert.Equal("Instagram", PlatformDetector.DisplayName(Platform.Instagram));
        Assert.Equal("Other site", PlatformDetector.DisplayName(Platform.Unknown));
    }
}

[Trait("Category", "Unit")]
public class CookieFileTests
{
    [Fact]
    public void YouTube_signed_in()
    {
        using var dir = new TempDir();
        var info = CookieFile.Analyze(dir.File("cookies.txt", CookieLines.YouTubeSignedIn));
        Assert.True(info.IsNetscape);
        Assert.False(info.IsJson);
        Assert.Equal([Platform.YouTube], info.Platforms);
        Assert.True(info.YouTubeLoggedIn);
    }

    [Fact]
    public void YouTube_signed_out_when_LOGIN_INFO_is_gone()
    {
        using var dir = new TempDir();
        Assert.False(CookieFile.Analyze(dir.File("cookies.txt", CookieLines.YouTubeSignedOut)).YouTubeLoggedIn);
    }

    [Fact]
    public void Secure_SAPISID_variant_and_HttpOnly_lines_count()
    {
        using var dir = new TempDir();
        var text = CookieLines.File(CookieLines.Line(".youtube.com", "LOGIN_INFO", httpOnly: true),
                                    CookieLines.Line(".youtube.com", "__Secure-3PAPISID"));
        Assert.True(CookieFile.Analyze(dir.File("c.txt", text)).YouTubeLoggedIn);
    }

    [Fact]
    public void Expired_cookies_are_ignored()
    {
        using var dir = new TempDir();
        var text = CookieLines.File(CookieLines.Line(".youtube.com", "LOGIN_INFO", expired: true), CookieLines.Line(".youtube.com", "SAPISID"),
                                    CookieLines.Line(".instagram.com", "sessionid", expired: true));
        var info = CookieFile.Analyze(dir.File("c.txt", text));
        Assert.False(info.YouTubeLoggedIn);
        Assert.DoesNotContain(Platform.Instagram, info.Platforms);
    }

    [Fact]
    public void Several_sites_in_one_file()
    {
        using var dir = new TempDir();
        var text = CookieLines.File(CookieLines.Line(".tiktok.com", "sessionid"), CookieLines.Line(".instagram.com", "sessionid"),
                                    CookieLines.Line(".google.com", "NID"));
        var platforms = CookieFile.Analyze(dir.File("c.txt", text)).Platforms;
        Assert.True(platforms.SetEquals([Platform.TikTok, Platform.Instagram]), string.Join(", ", platforms));
    }

    [Fact]
    public void Json_export_is_recognised()
    {
        using var dir = new TempDir();
        var info = CookieFile.Analyze(dir.File("c.json", "[{\"domain\": \".youtube.com\", \"name\": \"SID\"}]"));
        Assert.True(info.IsJson);
        Assert.False(info.IsNetscape);
    }

    [Fact]
    public void Not_a_cookie_file()
    {
        using var dir = new TempDir();
        var info = CookieFile.Analyze(dir.File("notes.txt", "just some text\nmore text"));
        Assert.False(info.IsNetscape);
        Assert.Empty(info.Platforms);
    }

    [Fact]
    public void Missing_and_huge_files_give_nothing()
    {
        using var dir = new TempDir();
        Assert.False(CookieFile.Analyze(dir.File("missing.txt")).IsNetscape);
        var big = dir.File("video.mp4");
        using (var f = File.Create(big)) f.SetLength(6 * 1024 * 1024);
        Assert.False(CookieFile.Analyze(big).IsNetscape);
    }
}

[Trait("Category", "Unit")]
public class QualityTests
{
    [Fact]
    public void Choices()
    {
        Assert.Equal([0, 2160, 1440, 1080, 720, 480, 360], Quality.All.Select(q => q.MaxHeight));
        Assert.Equal("Best available", Quality.All[0].ToString());
        Assert.All(Quality.All, q => Assert.All(q.Label, ch => Assert.InRange(ch, ' ', '~')));
    }
}

[Trait("Category", "Unit")]
public class DownloadPlanTests
{
    const string Out = @"C:\out", Final = @"C:\tmp\final.txt";

    static List<string> Build(Platform p, DownloadMode mode = DownloadMode.Video, int maxHeight = 0, string? cookies = null,
                              string? ffmpeg = null, string? js = null, bool videoItemsOnly = false, bool saveCover = false) =>
        DownloadPlan.Build(p, "https://example.com/v", mode, maxHeight, Out, cookies, ffmpeg, js, Final, videoItemsOnly, saveCover);

    static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    [Fact]
    public void Common_parts()
    {
        var a = Build(Platform.YouTube);
        Assert.Equal(["--", "https://example.com/v"], a.TakeLast(2));
        Assert.Equal(Out, After(a, "-P"));
        Assert.Equal("after_move:%(id)s|%(duration)s|%(resolution)s|%(filepath)s", After(a, "--print-to-file"));
        Assert.Equal(Final, a[a.IndexOf("--print-to-file") + 2]);
        Assert.StartsWith("download:" + DownloadPlan.ProgressPrefix, After(a, "--progress-template"));
        Assert.EndsWith("|%(info.format_id)s", After(a, "--progress-template"));
        Assert.Single(a, x => x == "--print-to-file");
        var sized = DownloadPlan.Build(Platform.YouTube, "u", DownloadMode.Video, 0, Out, null, null, null, Final, sizesFile: @"C:\tmp\sizes.txt");
        Assert.Equal(2, sized.Count(x => x == "--print-to-file"));
        Assert.Equal([DownloadPlan.SizesTemplate, @"C:\tmp\sizes.txt"], sized.Skip(sized.LastIndexOf("--print-to-file") + 1).Take(2));
        Assert.Contains("--no-playlist", a);
        Assert.Equal(["title", "!", ""], a.Skip(a.IndexOf("--replace-in-metadata") + 1).Take(3));
        Assert.DoesNotContain("--cookies", a);
        Assert.DoesNotContain("--ffmpeg-location", a);
    }

    [Fact]
    public void YouTube_video_best()
    {
        var a = Build(Platform.YouTube, js: "node");
        Assert.Equal("bv*+ba/b*", After(a, "-f"));
        Assert.Equal("res,vcodec:h264,acodec:aac", After(a, "-S"));
        Assert.Equal("mp4", After(a, "--merge-output-format"));
        Assert.Equal("%(title).120B [%(id)s].%(ext)s", After(a, "-o"));
        Assert.Equal("node", After(a, "--js-runtimes"));
        Assert.Equal("ejs:github", After(a, "--remote-components"));
        Assert.DoesNotContain("--write-thumbnail", a);
    }

    [Fact]
    public void Capped_quality_puts_the_resolution_into_the_name()
    {
        var a = Build(Platform.YouTube, maxHeight: 720);
        Assert.Equal("res:720,vcodec:h264,acodec:aac", After(a, "-S"));
        Assert.Equal("%(title).120B [%(id)s]%(resolution& {}|)s.%(ext)s", After(a, "-o"));
    }

    [Fact]
    public void Mp3_is_variable_bitrate_and_ignores_the_quality()
    {
        var a = Build(Platform.YouTube, DownloadMode.Mp3, maxHeight: 720);
        Assert.Equal("ba/b", After(a, "-f"));
        Assert.Contains("-x", a);
        Assert.Equal("mp3", After(a, "--audio-format"));
        Assert.Equal("2", After(a, "--audio-quality"));
        Assert.Equal("%(title).120B [%(id)s].%(ext)s", After(a, "-o"));
        Assert.DoesNotContain("-S", a);
    }

    [Fact]
    public void Original_audio_is_extracted_without_re_encoding()
    {
        var a = Build(Platform.YouTube, DownloadMode.Audio, maxHeight: 720);
        Assert.Equal("ba[ext=m4a]/ba/b", After(a, "-f"));
        Assert.Contains("-x", a);
        Assert.DoesNotContain("--audio-format", a);
        Assert.DoesNotContain("--audio-quality", a);
        Assert.Equal("%(title).120B [%(id)s].%(ext)s", After(a, "-o"));
        Assert.Equal("b", After(Build(Platform.TikTok, DownloadMode.Audio), "-f"));
    }

    [Fact]
    public void TikTok_never_merges_the_music_track()
    {
        var a = Build(Platform.TikTok, maxHeight: 1080);
        Assert.Equal("b", After(a, "-f"));
        Assert.Equal("res:1080,vcodec:h264", After(a, "-S"));
        Assert.StartsWith("%(uploader)s_%(id)s", After(a, "-o"));
        Assert.DoesNotContain("--merge-output-format", a);
        Assert.DoesNotContain("--remote-components", a);
    }

    [Fact]
    public void TikTok_audio_gets_its_own_name()
    {
        Assert.Equal("%(uploader)s_%(id)s_audio.%(ext)s", After(Build(Platform.TikTok, DownloadMode.Audio), "-o"));
        Assert.Equal("%(uploader)s_%(id)s_audio.%(ext)s", After(Build(Platform.TikTok, DownloadMode.Mp3), "-o"));
    }

    [Fact]
    public void Instagram_names_and_video_items_pass()
    {
        var a = Build(Platform.Instagram, videoItemsOnly: true);
        Assert.Equal("%(channel,uploader_id)s_%(id)s.%(ext)s", After(a, "-o"));
        Assert.Equal("format_id", After(a, "--match-filters"));
        Assert.Contains("--ignore-no-formats-error", a);
    }

    [Fact]
    public void Photo_pass_saves_only_the_pictures()
    {
        var a = Build(Platform.Instagram, DownloadMode.Photo, saveCover: true);
        Assert.Contains("--skip-download", a);
        Assert.Contains("--write-thumbnail", a);
        Assert.Equal("jpg", After(a, "--convert-thumbnails"));
        Assert.Equal("!format_id", After(a, "--match-filters"));
        Assert.Equal(["--", "https://example.com/v"], a.TakeLast(2));
        Assert.DoesNotContain("-f", a);
    }

    [Fact]
    public void Cover_cookies_ffmpeg()
    {
        var a = Build(Platform.YouTube, cookies: @"C:\c\cookies.txt", ffmpeg: @"C:\tools", saveCover: true);
        Assert.Equal(@"C:\c\cookies.txt", After(a, "--cookies"));
        Assert.Equal(@"C:\tools", After(a, "--ffmpeg-location"));
        Assert.Contains("--write-thumbnail", a);
        Assert.Equal("jpg", After(a, "--convert-thumbnails"));
    }
}

[Trait("Category", "Unit")]
public class OutputParsingTests
{
    [Fact]
    public void ParsePhotos_finds_written_files_and_converted_jpgs()
    {
        using var dir = new TempDir();
        var a = dir.File("user_1.jpg", "x");
        var b = dir.File("user_2.jpg", "x");
        var output = $"[info] Writing video thumbnail 12 to: {a}\n" +
                     $"[info] Writing video thumbnail 3 to: {Path.ChangeExtension(b, ".webp")}\r\n" +
                     $"[info] Writing video thumbnail 12 to: {a}\n" +
                     $"[info] Writing video thumbnail 1 to: {dir.File("gone.jpg")}\n";
        Assert.Equal([a, b], DownloadPlan.ParsePhotos(output));
    }

    [Fact]
    public void ParseFinal_reads_id_duration_resolution_and_path()
    {
        using var dir = new TempDir();
        var file = dir.File("final.txt",
            "abc|212.5|640x360|C:\\out\\a 640x360.mp4\n" +
            "def|NA|NA|C:\\out\\b.mp4\n" +
            "ghi|61|audio only|C:\\out\\c.mp3\n" +
            "\n" +
            "abc|212.5|640x360|C:\\out\\a 640x360.mp4\n" +
            "C:\\out\\d.mp4\n");
        Assert.Equal(
            [new SavedFile("C:\\out\\a 640x360.mp4", "abc", "640x360", 212.5), new SavedFile("C:\\out\\b.mp4", "def"),
             new SavedFile("C:\\out\\c.mp3", "ghi", null, 61), new SavedFile("C:\\out\\d.mp4")],
            DownloadPlan.ParseFinal(file));
    }

    [Fact]
    public void ParseFinal_without_a_file_is_empty()
    {
        using var dir = new TempDir();
        Assert.Empty(DownloadPlan.ParseFinal(dir.File("never-written.txt")));
    }

    [Fact]
    public void ErrorLog_tail_drops_progress_lines()
    {
        var output = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}")) + "\n" +
                     DownloadPlan.ProgressPrefix + " 50.0%|1MiB/s|00:10\r\nERROR: last";
        var tail = ErrorLog.Tail(output, 5).Split('\n');
        Assert.Equal(["line 57", "line 58", "line 59", "line 60", "ERROR: last"], tail);
    }
}

[Trait("Category", "Unit")]
public class HintsTests
{
    [Theory]
    [InlineData("ERROR: [youtube] M6vD-iLc_PI: Sign in to confirm your age. This video may be inappropriate for some users.", Platform.YouTube, Hints.LoginProblem.AgeRestricted, "age-restricted")]
    [InlineData("ERROR: [youtube] x: The provided YouTube account cookies are no longer valid.", Platform.YouTube, Hints.LoginProblem.CookiesRevoked, "no longer accepts these cookies")]
    [InlineData("ERROR: [youtube] x: Sign in to confirm you're not a bot.", Platform.YouTube, Hints.LoginProblem.LoginRequired, "not a bot")]
    [InlineData("ERROR: [youtube] x: Private video. Sign in if you've been granted access", Platform.YouTube, Hints.LoginProblem.LoginRequired, "private")]
    [InlineData("ERROR: [Instagram] x: Requested content is not available, rate-limit reached or login required.", Platform.Instagram, Hints.LoginProblem.LoginRequired, "Instagram wants you to sign in")]
    [InlineData("ERROR: [TikTok] x: This post is age-restricted", Platform.TikTok, Hints.LoginProblem.AgeRestricted, "TikTok only shows it")]
    [InlineData("ERROR: [TikTok] x: Your IP address is blocked from accessing this post", Platform.TikTok, Hints.LoginProblem.None, "VPN")]
    [InlineData("ERROR: [youtube] x: Video unavailable. This video has been removed by the uploader", Platform.YouTube, Hints.LoginProblem.None, "removed")]
    [InlineData("ERROR: [youtube] aaaaaaaaaaa: This video is unavailable", Platform.YouTube, Hints.LoginProblem.None, "removed")]
    [InlineData("ERROR: Unsupported URL: https://example.com/", Platform.Unknown, Hints.LoginProblem.None, "not supported")]
    [InlineData("ERROR: Postprocessing: ffprobe and ffmpeg not found.", Platform.YouTube, Hints.LoginProblem.None, "ffmpeg not found")]
    [InlineData("C:\\Python\\python.exe: No module named yt_dlp", Platform.YouTube, Hints.LoginProblem.None, "Install components")]
    [InlineData("ERROR: [youtube] x: No supported JavaScript runtime could be found", Platform.YouTube, Hints.LoginProblem.None, "Deno")]
    [InlineData("ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", Platform.TikTok, Hints.LoginProblem.None, "Network error")]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", Platform.YouTube, Hints.LoginProblem.None, "refused")]
    [InlineData("ERROR: Requested format is not available", Platform.YouTube, Hints.LoginProblem.None, "Try the other mode")]
    public void Diagnose(string output, Platform platform, Hints.LoginProblem login, string hintPart)
    {
        var (hint, problem) = Hints.Diagnose(output, platform);
        Assert.Equal(login, problem);
        Assert.NotNull(hint);
        Assert.Contains(hintPart, hint, StringComparison.OrdinalIgnoreCase);
        Assert.All(hint, ch => Assert.InRange(ch, ' ', '~'));
    }

    [Fact]
    public void Instagram_login_text_on_another_site_is_not_an_Instagram_hint()
    {
        var (hint, login) = Hints.Diagnose("ERROR: rate-limit reached or login required", Platform.YouTube);
        Assert.Equal(Hints.LoginProblem.None, login);
        Assert.Null(hint);
    }

    [Fact]
    public void Unknown_errors_get_no_hint() => Assert.Equal((null, Hints.LoginProblem.None), Hints.Diagnose("ERROR: something new", Platform.YouTube));

    [Theory]
    [InlineData("ERROR: [TikTok] x: TransportError: HTTP/2 stream reset", true)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", true)]
    [InlineData("ERROR: The read operation timed out", true)]
    [InlineData("ConnectionResetError: Connection reset by peer", true)]
    [InlineData("ERROR: HTTP Error 403: Your IP address is blocked", false)]
    [InlineData("ERROR: Sign in to confirm your age", false)]
    public void IsTransient(string output, bool transient) => Assert.Equal(transient, Hints.IsTransient(output));
}

[Trait("Category", "Unit")]
public class LogFileTests
{
    [Fact]
    public void Header_once_then_lines_and_rotation_at_1MB()
    {
        var log = new LogFile($"test_{Guid.NewGuid():N}.log");
        try
        {
            log.Append("first\n");
            log.Append("second\n");
            var lines = File.ReadAllLines(log.FilePath);
            Assert.StartsWith($"=== {AppInfo.Name} ", lines[0]);
            Assert.Equal(["first", "second"], lines[1..]);

            log.Append(new string('x', 1024 * 1024 + 10) + "\n");
            log.Append("after rotation\n");
            Assert.True(File.Exists(log.FilePath + ".old"));
            Assert.Equal(["after rotation"], File.ReadAllLines(log.FilePath));
        }
        finally
        {
            File.Delete(log.FilePath);
            File.Delete(log.FilePath + ".old");
        }
    }

    [Fact]
    public void ErrorLog_entry_is_mirrored_into_using_log()
    {
        var marker = Guid.NewGuid().ToString("N");
        ErrorLog.Write("TEST ERROR", $"message {marker}\nsecond line", "detail 1\ndetail 2");
        var error = File.ReadAllText(ErrorLog.FilePath);
        Assert.Contains($"TEST ERROR: message {marker}", error);
        Assert.Contains("    detail 2", error);
        var usage = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath)!, "using.log"));
        Assert.Contains($"TEST ERROR: message {marker} (details in error.log)", usage);
    }
}

[Trait("Category", "Unit")]
[Collection(nameof(SettingsEnvironment))]
public class SettingsTests : IDisposable
{
    readonly TempDir _dir = new();
    public SettingsTests() => Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", _dir.Path);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", null);
        _dir.Dispose();
    }

    [Fact]
    public void Save_and_load()
    {
        new Settings { OutputDir = @"D:\videos", AudioOnly = true, MaxHeight = 720, SaveCover = true,
                       Cookies = { ["YouTube"] = @"D:\c.txt" } }.Save();
        Assert.True(File.Exists(Path.Combine(_dir.Path, "settings.json")));
        var s = Settings.Load();
        Assert.Equal((@"D:\videos", true, 720, true), (s.OutputDir, s.AudioOnly, s.MaxHeight, s.SaveCover));
        Assert.Equal(@"D:\c.txt", s.Cookies["YouTube"]);
    }

    [Fact]
    public void Missing_or_broken_file_gives_defaults()
    {
        Assert.Equal(0, Settings.Load().MaxHeight);
        File.WriteAllText(Path.Combine(_dir.Path, "settings.json"), "{ not json");
        var s = Settings.Load();
        Assert.Null(s.OutputDir);
        Assert.Empty(s.Cookies);
    }
}

[CollectionDefinition(nameof(SettingsEnvironment), DisableParallelization = true)]
public class SettingsEnvironment;
