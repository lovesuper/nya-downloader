namespace VideoDownloader.Tests;

static class TestUrls
{
    public const string YouTube = "https://www.youtube.com/watch?v=6ItexNkuSJg";
    public const string YouTubeShort = "https://youtu.be/6ItexNkuSJg";
    public const string YouTubeId = "6ItexNkuSJg";
    public const string YouTubeAgeRestricted = "https://www.youtube.com/watch?v=M6vD-iLc_PI";
    public const string YouTubeLong = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
    public const string TikTok = "https://www.tiktok.com/@footage_me1/video/7689177252239674631";
    public const string TikTokFile = "footage_me1_7689177252239674631";
    public const string InstagramCarousel = "https://www.instagram.com/p/DfkeBxnFJh3/";
    public const string InstagramPhoto = "https://www.instagram.com/p/BsOGulcndj-/";
    public const string InstagramPhotoFile = "world_record_egg_BsOGulcndj-.jpg";
    public const string InstagramReel = "https://www.instagram.com/reel/DejDD7XIBB1/";
    public const string InstagramReelFile = "sofiviax_DejDD7XIBB1";
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nya_tests", Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name, string? content = null)
    {
        var p = System.IO.Path.Combine(Path, name);
        if (content != null) System.IO.File.WriteAllText(p, content);
        return p;
    }
    public string[] Files(string pattern = "*") => Directory.GetFiles(Path, pattern).Select(System.IO.Path.GetFileName).ToArray()!;
    public void Dispose()
    {
        for (int i = 0; i < 10 && Directory.Exists(Path); i++)
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}

static class CookieLines
{
    static readonly long Future = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
    static readonly long Past = DateTimeOffset.UtcNow.AddYears(-1).ToUnixTimeSeconds();

    public static string Line(string domain, string name, bool expired = false, bool httpOnly = false) =>
        $"{(httpOnly ? "#HttpOnly_" : "")}{domain}\tTRUE\t/\tTRUE\t{(expired ? Past : Future)}\t{name}\tvalue";

    public static string File(params string[] lines) =>
        "# Netscape HTTP Cookie File\n# This is a generated file! Do not edit.\n\n" + string.Join("\n", lines) + "\n";

    public static string YouTubeSignedIn => File(Line(".youtube.com", "LOGIN_INFO"), Line(".youtube.com", "SAPISID"), Line(".youtube.com", "PREF"));
    public static string YouTubeSignedOut => File(Line(".youtube.com", "SAPISID"), Line(".youtube.com", "PREF"));
    public static string TikTok => File(Line(".tiktok.com", "sessionid"), Line(".tiktok.com", "tt_csrf_token"));
}
