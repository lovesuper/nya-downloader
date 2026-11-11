using System.Text.RegularExpressions;
using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
public class LinkKindTests
{
    [Theory]
    [InlineData(TestUrls.YouTubeInPlaylist, LinkKind.VideoInList)]
    [InlineData("https://youtu.be/6ItexNkuSJg?list=PLJFfueMLIN0Y", LinkKind.VideoInList)]
    [InlineData(TestUrls.YouTubePlaylist, LinkKind.List)]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abc", LinkKind.List)]
    [InlineData("https://www.youtube.com/@YouTube", LinkKind.List)]
    [InlineData("https://www.youtube.com/@YouTube/videos", LinkKind.List)]
    [InlineData("https://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ", LinkKind.List)]
    [InlineData("https://www.youtube.com/c/YouTube", LinkKind.List)]
    [InlineData("https://www.youtube.com/user/YouTube", LinkKind.List)]
    [InlineData(TestUrls.YouTube, LinkKind.Single)]
    [InlineData(TestUrls.YouTubeShort, LinkKind.Single)]
    [InlineData("https://www.youtube.com/shorts/41f_EDdLubo", LinkKind.Single)]
    [InlineData("https://www.youtube.com/watch?v=6ItexNkuSJg&list=", LinkKind.Single)]
    [InlineData(TestUrls.TikTokProfile, LinkKind.List)]
    [InlineData("https://www.tiktok.com/@footage_me1/", LinkKind.List)]
    [InlineData(TestUrls.TikTok, LinkKind.Single)]
    [InlineData("https://vm.tiktok.com/ZMabcdef/", LinkKind.Single)]
    [InlineData("https://www.instagram.com/instagram/", LinkKind.List)]
    [InlineData(TestUrls.InstagramCarousel, LinkKind.Single)]
    [InlineData(TestUrls.InstagramReel, LinkKind.Single)]
    [InlineData("https://www.instagram.com/stories/someone/123/", LinkKind.Single)]
    [InlineData("https://www.instagram.com/explore/", LinkKind.Single)]
    [InlineData("https://vimeo.com/123", LinkKind.Single)]
    public void Kind(string url, LinkKind kind) => Assert.Equal(kind, PlatformDetector.Kind(new Uri(url)));

    [Theory]
    [InlineData(TestUrls.YouTubeInPlaylist, "nBwADVXLHwg", "PL7FAuHvhKY86Itb81N03ficveT4QFN2y3")]
    [InlineData("https://youtu.be/6ItexNkuSJg?list=PLJ%46fueMLIN0Y", "6ItexNkuSJg", "PLJFfueMLIN0Y")]
    [InlineData(TestUrls.YouTubePlaylist, null, "PL7FAuHvhKY86Itb81N03ficveT4QFN2y3")]
    [InlineData("https://www.youtube.com/@YouTube", null, null)]
    public void Video_and_list_ids(string url, string? video, string? list)
    {
        var uri = new Uri(url);
        Assert.Equal((video, list), (PlatformDetector.VideoId(uri), PlatformDetector.ListId(uri)));
    }
}

[Trait("Category", "Unit")]
public class TimeTextTests
{
    [Theory]
    [InlineData("90", 90.0)]
    [InlineData(" 1:30 ", 90.0)]
    [InlineData("01:30", 90.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("0:05.5", 5.5)]
    [InlineData("1:00:00", 3600.0)]
    public void Parses(string text, double seconds)
    {
        Assert.True(TimeText.TryParse(text, out var s));
        Assert.Equal(seconds, s);
    }

    [Fact]
    public void Empty_means_not_set()
    {
        Assert.True(TimeText.TryParse("  ", out var s));
        Assert.Null(s);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:00")]
    [InlineData("-5")]
    [InlineData("1:")]
    [InlineData(":30")]
    [InlineData("1,5")]
    public void Rejects(string text) => Assert.False(TimeText.TryParse(text, out _));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5.4, "0:05")]
    [InlineData(90, "1:30")]
    [InlineData(2995, "49:55")]
    [InlineData(3723, "1:02:03")]
    [InlineData(36000, "10:00:00")]
    public void Formats(double seconds, string text) => Assert.Equal(text, TimeText.Format(seconds));
}

[Trait("Category", "Unit")]
public class SectionTests
{
    static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    [Fact]
    public void Time_range_names_and_text()
    {
        var s = new Section(90, 125.5);
        Assert.Equal(" (1.30-2.06)", s.Suffix);
        Assert.Equal("1:30-2:06", s.Describe);
        Assert.False(s.ToEnd);
        var open = new Section(3723, double.PositiveInfinity);
        Assert.Equal(" (1.02.03-end)", open.Suffix);
        Assert.Equal("1:02:03-end", open.Describe);
    }

    [Fact]
    public void Chapter_names_are_numbered_and_safe_for_Windows()
    {
        var s = new Section(3442, 3504, "Берсерки: безумные воины Одина?", 10);
        Assert.Equal(" - 10 Берсерки. безумные воины Одина_", s.Suffix);
        Assert.Equal("chapter 10 \"Берсерки: безумные воины Одина?\" (57:22-58:24)", s.Describe);
        Assert.Equal(" - 03 _", new Section(0, 1, "...", 3).Suffix);
        Assert.Equal("chapter 4 \"Outro\" (1:00-end)", new Section(60, double.PositiveInfinity, "Outro", 4).Describe);
        Assert.Equal(80, Section.SafeName(new string('a', 200), 80).Length);
    }

    [Fact]
    public void Video_is_cut_exactly_by_reencoding_only_the_fragment()
    {
        var a = new Section(5, 15).CutArgs(@"C:\w\v.mp4", @"C:\o\v (0.05-0.15).mp4", reencode: true);
        Assert.True(a.IndexOf("-ss") < a.IndexOf("-i"), "seek before the input, so ffmpeg doesn't decode everything before the start");
        Assert.Equal("5", After(a, "-ss"));
        Assert.Equal(@"C:\w\v.mp4", After(a, "-i"));
        Assert.Equal("10", After(a, "-t"));
        Assert.Equal("libx264", After(a, "-c:v"));
        Assert.Equal("aac", After(a, "-c:a"));
        Assert.Contains("-nostdin", a);
        Assert.Equal(@"C:\o\v (0.05-0.15).mp4", a[^1]);
    }

    [Fact]
    public void Fallback_and_audio_copy_the_streams()
    {
        var keyframes = new Section(5, 15).CutArgs("v.mp4", "o.mp4", reencode: false);
        Assert.Equal("copy", After(keyframes, "-c"));
        Assert.DoesNotContain("libx264", keyframes);
        var mp3 = new Section(5.25, double.PositiveInfinity).CutArgs("a.mp3", "o.mp3", reencode: true);
        Assert.Equal("copy", After(mp3, "-c"));
        Assert.Equal("5.25", After(mp3, "-ss"));
        Assert.DoesNotContain("-t", mp3);
    }
}

[Trait("Category", "Unit")]
public class SubtitleLanguageTests
{
    static bool Picks(SubtitleLanguage l, string code) =>
        l.Pattern.Split(',').Any(p => Regex.IsMatch(code, "^(?:" + p + ")$", RegexOptions.IgnoreCase));

    [Theory]
    [InlineData("ru", true)]
    [InlineData("RU", true)]
    [InlineData("ru-RU", true)]
    [InlineData("rus-RU", true)]
    [InlineData("ru-orig", false)]
    [InlineData("ru-ru", false)]
    [InlineData("en-ru", false)]
    [InlineData("rue", false)]
    [InlineData("uk", false)]
    public void Russian(string code, bool picked) => Assert.Equal(picked, Picks(SubtitleLanguage.From("ru"), code));

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("en-GB", true)]
    [InlineData("eng-US", true)]
    [InlineData("en-orig", false)]
    [InlineData("en-ru", false)]
    public void English(string code, bool picked) => Assert.Equal(picked, Picks(SubtitleLanguage.From("en"), code));

    [Fact]
    public void Regional_codes()
    {
        Assert.True(Picks(SubtitleLanguage.From("es"), "es-419"));
        Assert.True(Picks(SubtitleLanguage.From("zh"), "zh-Hans"));
        Assert.True(Picks(SubtitleLanguage.From("pt"), "pt-BR"));
    }

    [Fact]
    public void Choices_and_unknown_codes()
    {
        Assert.Equal("No subtitles", SubtitleLanguage.All[0].Name);
        Assert.True(SubtitleLanguage.From("").IsNone);
        Assert.True(SubtitleLanguage.From("xx").IsNone);
        Assert.True(SubtitleLanguage.From(null).IsNone);
        Assert.Equal("Russian", SubtitleLanguage.From("ru").ToString());
        Assert.Equal(SubtitleLanguage.All.Length, SubtitleLanguage.All.Select(l => l.Code).Distinct().Count());
    }
}

[Trait("Category", "Unit")]
public class SrtTests
{
    const string Sample =
        "1\r\n00:00:01,000 --> 00:00:04,000\r\nbefore\r\n\r\n" +
        "2\r\n00:00:09,500 --> 00:00:11,000\r\nacross the start\r\n\r\n" +
        "3\r\n00:00:12,000 --> 00:00:13,250\r\ninside\r\nsecond line\r\n\r\n" +
        "4\r\n00:00:19,000 --> 00:00:21,000\r\nacross the end\r\n\r\n" +
        "5\r\n00:00:25,000 --> 00:00:26,000\r\nafter\r\n";

    [Fact]
    public void Keeps_the_cues_in_the_fragment_and_shifts_them_to_zero()
    {
        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:01,000\r\nacross the start\r\n\r\n" +
            "2\r\n00:00:02,000 --> 00:00:03,250\r\ninside\r\nsecond line\r\n\r\n" +
            "3\r\n00:00:09,000 --> 00:00:10,000\r\nacross the end\r\n\r\n",
            Srt.Slice(Sample, 10, 20));
    }

    [Fact]
    public void Open_end_and_LF_files()
    {
        var sliced = Srt.Slice(Sample.Replace("\r\n", "\n"), 20, double.PositiveInfinity);
        Assert.StartsWith("1\r\n00:00:00,000 --> 00:00:01,000\r\nacross the end\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\nafter", sliced);
        Assert.Equal("", Srt.Slice(Sample, 100, 200));
        Assert.Equal("1\r\n01:00:00,000 --> 01:00:01,500\r\nx\r\n\r\n", Srt.Slice("1\n01:00:10,000 --> 01:00:11,500\nx\n", 10, double.PositiveInfinity));
    }
}

[Trait("Category", "Unit")]
public class ListingTests
{
    static string Entry(string id, string title, string? list = "PL1", string? listTitle = "My list", double? duration = 60,
                        string ie = "Youtube", string? live = null) =>
        Listing.Prefix + System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["url"] = "https://www.youtube.com/watch?v=" + id, ["title"] = title, ["duration"] = duration,
            ["playlist_id"] = list, ["playlist_title"] = listTitle, ["ie_key"] = ie, ["live_status"] = live,
        });

    [Fact]
    public void Playlist_entries_in_order_without_duplicates_and_hidden_ones()
    {
        var output = string.Join("\n",
            "[youtube:tab] Extracting URL: https://www.youtube.com/playlist?list=PL1",
            Entry("a", "First \"one\" | with bar"),
            "WARNING: [youtube:tab] 1 unavailable video is hidden",
            Entry("b", "Second", duration: null),
            Entry("a", "First again"),
            Entry("c", "[Private video]"),
            Entry("d", "Soon", live: "is_upcoming"),
            Entry("e", "Now", live: "is_live"),
            Entry("f", "Nested", ie: "YoutubeTab"),
            Listing.Prefix + "{broken json",
            Entry("g", "Was live", live: "was_live"));
        var l = Listing.Parse(0, output);
        Assert.True(l.IsList);
        Assert.Equal("My list", l.Title);
        Assert.Equal(["a", "b", "g"], l.Entries.Select(e => e.Id));
        Assert.Equal("First \"one\" | with bar", l.Entries[0].Title);
        Assert.Equal("https://www.youtube.com/watch?v=a", l.Entries[0].Url);
        Assert.Equal([60.0, null, 60.0], l.Entries.Select(e => e.Duration));
        Assert.All(l.Entries, e => Assert.Null(e.Section));
        Assert.Equal(4, l.Skipped);
    }

    [Fact]
    public void Channel_tabs_become_sections()
    {
        var output = string.Join("\n",
            Entry("v1", "Video", "UC1", "Channel - Videos"),
            Entry("s1", "Short", "UC2", "Channel - Shorts", duration: null),
            Entry("l1", "Stream", "UC3", "Channel - Live"));
        var l = Listing.Parse(0, output);
        Assert.Equal("Channel", l.Title);
        Assert.Equal(["Videos", "Shorts", "Live"], l.Entries.Select(e => e.Section));
    }

    [Fact]
    public void Different_lists_keep_their_full_names()
    {
        var l = Listing.Parse(0, Entry("a", "A", "P1", "One") + "\n" + Entry("b", "B", "P2", "Two"));
        Assert.Equal("One", l.Title);
        Assert.Equal(["One", "Two"], l.Entries.Select(e => e.Section));
    }

    [Fact]
    public void A_single_video_is_not_a_list()
    {
        var l = Listing.Parse(0, Entry("x", "Just a video", list: null, listTitle: null));
        Assert.False(l.IsList);
        Assert.Empty(l.Entries);
    }

    [Fact]
    public void A_failure_has_no_entries()
    {
        var l = Listing.Parse(1, "ERROR: footage_me1: Failed to parse JSON");
        Assert.False(l.IsList);
        Assert.Empty(l.Entries);
        Assert.Equal(1, l.ExitCode);
    }

    [Fact]
    public void Args_list_flat_without_downloading()
    {
        var a = Listing.Args(Platform.YouTube, TestUrls.YouTubePlaylist, @"C:\c.txt", "node");
        Assert.Contains("--flat-playlist", a);
        Assert.DoesNotContain("--no-playlist", a);
        Assert.StartsWith(Listing.Prefix + "%(.{id,url,title,duration,playlist_id", a[a.IndexOf("--print") + 1]);
        Assert.Equal(@"C:\c.txt", a[a.IndexOf("--cookies") + 1]);
        Assert.Equal("node", a[a.IndexOf("--js-runtimes") + 1]);
        Assert.Equal(["--", TestUrls.YouTubePlaylist], a.TakeLast(2));
    }
}

[Trait("Category", "Unit")]
public class VideoInfoTests
{
    [Fact]
    public void Chapters_are_numbered_and_the_last_one_ends_with_the_video()
    {
        var output = "[youtube] Extracting URL: x\n" + VideoInfo.Prefix +
            "{\"title\": \"Talk\", \"duration\": 300, \"chapters\": [" +
            "{\"start_time\": 0, \"title\": \"Intro\", \"end_time\": 60}, " +
            "{\"start_time\": 60, \"title\": \"\", \"end_time\": 200}, " +
            "{\"start_time\": 200, \"title\": \"End\"}]}";
        var info = VideoInfo.Parse(0, output);
        Assert.Equal("Talk", info.Title);
        Assert.Equal(300, info.Duration);
        Assert.Equal([new Section(0, 60, "Intro", 1), new Section(60, 200, "Chapter 2", 2), new Section(200, 300, "End", 3)], info.Chapters);
    }

    [Fact]
    public void No_chapters_or_no_output()
    {
        Assert.Empty(VideoInfo.Parse(0, VideoInfo.Prefix + "{\"title\": \"T\", \"duration\": 5, \"chapters\": null}").Chapters);
        Assert.Empty(VideoInfo.Parse(1, "ERROR: Video unavailable").Chapters);
        Assert.Empty(VideoInfo.Parse(0, VideoInfo.Prefix + "{oops").Chapters);
    }
}

[Trait("Category", "Unit")]
public class LeftoversTests
{
    [Theory]
    [InlineData("Video [abc].mp4.part", true)]
    [InlineData("Video [abc].f137.mp4.part", true)]
    [InlineData("Video [abc].f137.mp4", true)]
    [InlineData("Video [abc].f140.m4a", true)]
    [InlineData("Video [abc].f251-0.webm", true)]
    [InlineData("Video [abc].mp4.part-Frag12", true)]
    [InlineData("Video [abc].mp4.ytdl", true)]
    [InlineData("Video [abc].temp.mp4", true)]
    [InlineData("Video [abc].mp4", false)]
    [InlineData("Video [abc] 640x360.mp4", false)]
    [InlineData("Video [abc].jpg", false)]
    [InlineData("Video [abc].ru.srt", false)]
    [InlineData("footage_me1_123_audio.m4a", false)]
    [InlineData("Party.final.mp4", false)]
    [InlineData("Clip [x].fhls-720p.mp4", true)]
    public void Knows_what_yt_dlp_leaves_behind(string name, bool temporary) => Assert.Equal(temporary, Leftovers.IsTemporary(name));

    [Fact]
    public async Task Removes_only_new_unfinished_files()
    {
        using var dir = new TempDir();
        dir.File("old.mp4.part", "from an earlier download");
        dir.File("kept.mp4", "x");
        var before = Leftovers.Snapshot(dir.Path);
        foreach (var name in new[] { "a.mp4.part", "a.f137.mp4", "a.f140.m4a.part", "a.mp4.ytdl", "a.temp.mp4", "done.mp4", "done.jpg" })
            dir.File(name, "x");
        var removed = await Leftovers.CleanAsync(dir.Path, before);
        Assert.Equal(["a.f137.mp4", "a.f140.m4a.part", "a.mp4.part", "a.mp4.ytdl", "a.temp.mp4"], removed.Select(Path.GetFileName).Order());
        Assert.Equal(["done.jpg", "done.mp4", "kept.mp4", "old.mp4.part"], dir.Files().Order());
        Assert.Empty(Leftovers.Snapshot(Path.Combine(dir.Path, "missing")));
        Assert.Empty(await Leftovers.CleanAsync(Path.Combine(dir.Path, "missing"), before));
    }
}

[Trait("Category", "Unit")]
public class NewPlanTests
{
    static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    [Fact]
    public void Several_videos_come_from_a_batch_file()
    {
        var a = DownloadPlan.Build(Platform.YouTube, "https://example.com/v", DownloadMode.Video, 360, @"C:\out", null, null, null,
                                   @"C:\tmp\final.txt", batchFile: @"C:\tmp\urls.txt");
        Assert.Equal(["-a", @"C:\tmp\urls.txt"], a.TakeLast(2));
        Assert.DoesNotContain("--", a);
        Assert.Contains("--no-playlist", a);
    }

    [Fact]
    public void Subtitles_pass_downloads_only_subtitles_by_id()
    {
        var a = DownloadPlan.SubtitlesPass(Platform.YouTube, "https://example.com/v", null, SubtitleLanguage.From("ru"), @"C:\tmp\subs",
                                           @"C:\c.txt", @"C:\tools", "node");
        Assert.Contains("--skip-download", a);
        Assert.Contains("--write-subs", a);
        Assert.Contains("--write-auto-subs", a);
        Assert.Contains("--ignore-errors", a);
        Assert.Equal(SubtitleLanguage.From("ru").Pattern, After(a, "--sub-langs"));
        Assert.Equal("srt/best", After(a, "--sub-format"));
        Assert.Equal("srt", After(a, "--convert-subs"));
        Assert.Equal(@"C:\tmp\subs", After(a, "-P"));
        Assert.Equal("%(id)s.%(ext)s", After(a, "-o"));
        Assert.Equal(@"C:\c.txt", After(a, "--cookies"));
        Assert.Equal(@"C:\tools", After(a, "--ffmpeg-location"));
        Assert.Equal(["--", "https://example.com/v"], a.TakeLast(2));
        Assert.Equal(["-a", "u.txt"], DownloadPlan.SubtitlesPass(Platform.TikTok, "x", "u.txt", SubtitleLanguage.From("en"), "d", null, null, null).TakeLast(2));
    }
}
