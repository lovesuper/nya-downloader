using System.Text.Json;
using Xunit;

namespace VideoDownloader.Tests.E2E;

[Trait("Category", "E2E")]
[Collection(nameof(Gui))]
public class GuiFeatureTests
{
    const int Space = 0x20, Down = 0x28;

    static string TextIn(IntPtr window, string id) => NyaApp.In(window, id).Current.Name;

    static void WaitIdle(NyaApp app, double seconds) =>
        app.WaitFor(() => !app.Enabled("cancel") && app.Enabled("download"), seconds, "the app to finish");

    [Fact]
    public void Playlist_link_lets_you_pick_videos_with_the_keyboard()
    {
        using var app = new NyaApp(s => s.AudioOnly = true);
        app.SetValue("url", TestUrls.YouTubeInPlaylist);
        app.Press("download");
        var dialog = app.WaitForWindow("Choose videos", 90);
        Assert.NotEqual(dialog, NyaApp.GetForegroundWindow());
        Assert.Equal("1 of 11 selected, 49:55 in total", TextIn(dialog, "pickSummary"));
        Assert.Equal("Download 1 video", TextIn(dialog, "pickOk"));
        Assert.StartsWith(TestUrls.YouTubePlaylistTitle + ": 11 videos.", TextIn(dialog, "pickHeading"));

        var list = NyaApp.Hwnd(NyaApp.In(dialog, "pickList"));
        NyaApp.Key(list, Space);
        app.WaitFor(() => TextIn(dialog, "pickSummary") == "0 of 11 selected", 5, "the first video to be unchecked");
        Assert.False(NyaApp.In(dialog, "pickOk").Current.IsEnabled);
        for (int i = 0; i < 9; i++) NyaApp.Key(list, Down);
        NyaApp.Key(list, Space);
        app.WaitFor(() => TextIn(dialog, "pickSummary") == "1 of 11 selected, 16:50 in total", 5, "the tenth video to be checked");

        NyaApp.PressIn(dialog, "pickOk");
        app.WaitFor(() => NyaApp.FindWindow(null, "Choose videos") == IntPtr.Zero, 5, "the dialog to close");
        WaitIdle(app, 300);
        Assert.Equal("Done: Mega Man 2 - GameShelf #27 [GE6PenmTCKM].mp3", app.Text("status"));
        Assert.Equal(["Mega Man 2 - GameShelf #27 [GE6PenmTCKM].mp3"], app.Out.Files());
    }

    [Fact]
    public void Whole_playlist_is_checked_and_Cancel_downloads_nothing()
    {
        using var app = new NyaApp();
        app.SetValue("url", TestUrls.YouTubePlaylist);
        app.Press("download");
        var dialog = app.WaitForWindow("Choose videos", 90);
        Assert.StartsWith("11 of 11 selected, ", TextIn(dialog, "pickSummary"));
        Assert.Equal("Download 11 videos", TextIn(dialog, "pickOk"));
        NyaApp.PressIn(dialog, "pickNone");
        app.WaitFor(() => TextIn(dialog, "pickSummary") == "0 of 11 selected", 5, "Select none");
        NyaApp.PressIn(dialog, "pickAll");
        app.WaitFor(() => TextIn(dialog, "pickSummary").StartsWith("11 of 11"), 5, "Select all");
        NyaApp.PressIn(dialog, "pickCancel");
        app.WaitFor(() => app.Text("status") == "Canceled.", 5, "Canceled.");
        Assert.True(app.Enabled("download"));
        Assert.Empty(Directory.GetFileSystemEntries(app.Out.Path));
    }

    [Fact]
    public void TikTok_profile_shows_a_list_or_says_why_it_cannot()
    {
        using var app = new NyaApp();
        app.SetValue("url", TestUrls.TikTokProfile);
        app.Press("download");
        app.WaitFor(() => NyaApp.FindWindow(null, "Choose videos") != IntPtr.Zero || app.Text("status").StartsWith("Couldn't"), 90, "a list or an error");
        var dialog = NyaApp.FindWindow(null, "Choose videos");
        if (dialog != IntPtr.Zero)
        {
            NyaApp.PressIn(dialog, "pickCancel");
            return;
        }
        Assert.Equal("Couldn't get the list of videos. yt-dlp can't read TikTok profiles right now. Paste a link to a single video.", app.Text("status"));
        Assert.True(app.Enabled("download"));
        Assert.True(app.Shown("errorLogLink"));
    }

    [Fact]
    public void Fragment_with_subtitles_from_the_window()
    {
        using var app = new NyaApp(s => { s.MaxHeight = 360; s.Subtitles = "ru"; });
        Assert.Equal("Russian", app.Value("subtitles"));
        app.SetValue("url", TestUrls.YouTube);
        app.SetValue("from", "0:05");
        app.SetValue("to", "0:15");
        Assert.StartsWith("Fragment: type times like 1:30", app.Text("qualityHint"));
        var status = app.Download(TestUrls.YouTube);
        var name = $"[{TestUrls.YouTubeId}] 640x360 (0.05-0.15)";
        Assert.StartsWith("Done: ", status);
        Assert.EndsWith($"{name}.mp4 + subtitles", status);
        var files = Directory.GetFileSystemEntries(app.Out.Path).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(2, files.Length);
        Assert.EndsWith($"{name}.mp4", files[0]);
        Assert.EndsWith($"{name}.ru.srt", files[1]);
        Assert.Contains("Subtitles: ", app.Value("log"));
    }

    [Fact]
    public void Fragment_mistakes_and_a_new_link_clears_it()
    {
        using var app = new NyaApp();
        app.SetValue("url", TestUrls.YouTube);
        app.SetValue("from", "5:00");
        app.SetValue("to", "1:00");
        app.Press("download");
        app.WaitFor(() => app.Text("status") == "Fragment: the end must be later than the start.", 5, "the order error");
        app.SetValue("to", "1:2:3:4");
        app.Press("download");
        app.WaitFor(() => app.Text("status").StartsWith("Fragment: type times like 1:30"), 5, "the format error");
        Assert.Empty(app.Out.Files());

        app.SetValue("url", TestUrls.YouTubeChapters);
        Assert.Equal(("", ""), (app.Value("from"), app.Value("to")));
        Assert.DoesNotContain("Fragment", app.Text("qualityHint"));
    }

    [Fact]
    public void Chapters_button_lists_chapters_or_says_there_are_none()
    {
        using var app = new NyaApp();
        app.SetValue("url", TestUrls.YouTubeChapters);
        app.Press("chapters");
        var dialog = app.WaitForWindow("Choose chapters", 90);
        Assert.Matches(@"^0 of \d+ selected$", TextIn(dialog, "pickSummary"));
        Assert.Equal("Download", TextIn(dialog, "pickOk"));
        NyaApp.PressIn(dialog, "pickCancel");
        app.WaitFor(() => app.Text("status") == "Canceled.", 5, "Canceled.");

        app.SetValue("url", "https://www.youtube.com/shorts/41f_EDdLubo");
        app.Press("chapters");
        app.WaitFor(() => app.Text("status").StartsWith("This video has no chapters."), 90, "the no-chapters message");
        Assert.Empty(Directory.GetFileSystemEntries(app.Out.Path));
    }

    [Fact]
    public void Channel_videos_go_into_a_folder_named_after_the_list()
    {
        using var app = new NyaApp(s => s.AudioOnly = true);
        app.SetValue("url", "https://www.youtube.com/@YouTube/shorts");
        app.Press("download");
        var dialog = app.WaitForWindow("Choose videos", 120);
        Assert.Matches(@"^0 of \d{3,} selected$", TextIn(dialog, "pickSummary"));
        var list = NyaApp.Hwnd(NyaApp.In(dialog, "pickList"));
        NyaApp.Key(list, Space);
        NyaApp.Key(list, Down);
        NyaApp.Key(list, Space);
        app.WaitFor(() => TextIn(dialog, "pickSummary").StartsWith("2 of "), 5, "two shorts to be checked");
        Assert.Equal("Download 2 videos", TextIn(dialog, "pickOk"));
        NyaApp.PressIn(dialog, "pickOk");

        var sawSecond = false;
        app.WaitFor(() => app.Enabled("cancel"), 10, "the download to start");
        app.WaitFor(() =>
        {
            sawSecond |= app.Text("status").StartsWith("Video 2 of 2: ");
            return !app.Enabled("cancel") && app.Enabled("download");
        }, 300, "the download to finish");
        Assert.True(sawSecond, "the status never said which video is downloading");
        Assert.StartsWith("Done: 2 videos (", app.Text("status"));
        var folder = Path.Combine(app.Out.Path, "YouTube - Shorts");
        Assert.Equal([folder], Directory.GetDirectories(app.Out.Path));
        Assert.Equal(2, Directory.GetFiles(folder, "*.mp3").Length);
        Assert.Contains("Folder: " + folder, app.Value("log"));
    }

    [Fact]
    public void A_picked_chapter_is_downloaded_as_its_own_file()
    {
        using var app = new NyaApp(s => s.AudioOnly = true);
        app.SetValue("url", TestUrls.YouTubeChapters);
        app.Press("chapters");
        var dialog = app.WaitForWindow("Choose chapters", 90);
        var list = NyaApp.Hwnd(NyaApp.In(dialog, "pickList"));
        for (int i = 0; i < 9; i++) NyaApp.Key(list, Down);
        NyaApp.Key(list, Space);
        app.WaitFor(() => TextIn(dialog, "pickSummary").StartsWith("1 of ") && TextIn(dialog, "pickSummary").EndsWith(", 1:02 in total"), 5, "chapter 10 to be checked");
        Assert.Equal("Download 1 chapter", TextIn(dialog, "pickOk"));
        NyaApp.PressIn(dialog, "pickOk");
        app.WaitFor(() => app.Enabled("cancel"), 10, "the download to start");
        WaitIdle(app, 420);
        var file = Assert.Single(app.Out.Files());
        Assert.EndsWith("[2NJw2T98yMk] - 10 Берсерки. безумные воины Одина.mp3", file);
        Assert.Equal($"Done: {file}", app.Text("status"));
        Assert.Empty(Directory.GetDirectories(app.Out.Path));
    }

    [Fact]
    public void Subtitle_choice_is_saved()
    {
        using var app = new NyaApp(s => s.Subtitles = "uk");
        Assert.Equal("Ukrainian", app.Value("subtitles"));
        Assert.True(app.CloseAndWait());
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(app.Data.Path, "settings.json")))!;
        Assert.Equal("uk", saved.Subtitles);
    }
}
