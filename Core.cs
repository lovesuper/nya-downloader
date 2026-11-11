using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader;

public static class AppInfo
{
    public const string Name = "NyaDownloader";
    public const string OldName = "VideoDownloader";
    public static string Version => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    public static string? DataDirOverride => Environment.GetEnvironmentVariable("NYADOWNLOADER_DATA") is { Length: > 0 } d ? d : null;
}

public enum Platform { Unknown, YouTube, TikTok, Instagram }

public enum LinkKind { Single, VideoInList, List }

public static class PlatformDetector
{
    static readonly HashSet<string> InstagramPages = ["p", "reel", "reels", "tv", "stories", "explore", "accounts", "direct", "share"];

    static string? Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0].Equals(name, StringComparison.OrdinalIgnoreCase) && p[1].Length > 0) is { } kv
            ? Uri.UnescapeDataString(kv[1]) : null;

    public static string? VideoId(Uri uri) =>
        Query(uri, "v") ?? (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Trim('/') is { Length: > 0 } id && !id.Contains('/') ? id : null);

    public static string? ListId(Uri uri) => Query(uri, "list");

    public static LinkKind Kind(Uri uri)
    {
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (FromHost(uri.Host))
        {
            case Platform.YouTube:
                if (ListId(uri) != null) return VideoId(uri) != null ? LinkKind.VideoInList : LinkKind.List;
                return path is [var first, ..] && (first.StartsWith('@') || first is "channel" or "c" or "user") ? LinkKind.List : LinkKind.Single;
            case Platform.TikTok:
                return path is [var user] && user.StartsWith('@') ? LinkKind.List : LinkKind.Single;
            case Platform.Instagram:
                return path is [var page] && !InstagramPages.Contains(page.ToLowerInvariant()) ? LinkKind.List : LinkKind.Single;
            default:
                return LinkKind.Single;
        }
    }

    static readonly (Platform Platform, string[] Domains)[] Known =
    [
        (Platform.YouTube, ["youtube.com", "youtu.be", "youtube-nocookie.com"]),
        (Platform.TikTok, ["tiktok.com"]),
        (Platform.Instagram, ["instagram.com", "instagr.am"]),
    ];

    public static Platform FromHost(string host)
    {
        host = host.Trim().Trim('.').ToLowerInvariant();
        foreach (var (platform, domains) in Known)
            if (domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)))
                return platform;
        return Platform.Unknown;
    }

    public static Uri? ParseUrl(string text)
    {
        var s = text.Replace("﻿", "").Trim();
        var cut = s.IndexOfAny([' ', '\t', '\r', '\n']);
        if (cut > 0) s = s[..cut];
        if (s.Length == 0) return null;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri : null;
    }

    public static bool IsInstagramReel(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.Contains("/reel/") || path.Contains("/reels/") || path.Contains("/tv/");
    }

    public static string DisplayName(Platform p) => p == Platform.Unknown ? "Other site" : p.ToString();
}

public sealed record CookieInfo(bool IsNetscape, bool IsJson, IReadOnlySet<Platform> Platforms, bool YouTubeLoggedIn);

public static class CookieFile
{
    static readonly string[] YouTubeAuth = ["SAPISID", "__Secure-1PAPISID", "__Secure-3PAPISID"];

    public static CookieInfo Analyze(string path)
    {
        var none = new CookieInfo(false, false, new HashSet<Platform>(), false);
        string[] lines;
        try
        {
            if (new FileInfo(path).Length > 5 * 1024 * 1024) return none;
            lines = File.ReadAllLines(path);
        }
        catch { return none; }

        var first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        bool isJson = first.StartsWith('[') || first.StartsWith('{');
        var platforms = new HashSet<Platform>();
        var youTubeNames = new HashSet<string>();
        bool anyCookie = false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line["#HttpOnly_".Length..];
            else if (line.StartsWith('#') || line.Trim().Length == 0) continue;

            var f = line.Split('\t');
            if (f.Length < 7) continue;
            anyCookie = true;
            if (long.TryParse(f[4], out var expires) && expires > 0 && expires < now) continue;

            var platform = PlatformDetector.FromHost(f[0]);
            if (platform == Platform.Unknown) continue;
            platforms.Add(platform);
            if (platform == Platform.YouTube) youTubeNames.Add(f[5]);
        }

        bool loggedIn = youTubeNames.Contains("LOGIN_INFO") && YouTubeAuth.Any(youTubeNames.Contains);
        return new CookieInfo(anyCookie, isJson, platforms, loggedIn);
    }
}

public enum DownloadMode { Video, Audio, Mp3, Photo }

public sealed class ProgressTracker(string sizesFile)
{
    static readonly Regex FormatsRx = new(@"^\[info\] (?<id>[^:]+): Downloading \d+ format\(s\): (?<f>\S+)$");
    static readonly Regex PercentRx = new(@"([\d.]+)%");
    string[] _formats = [];
    string _id = "";
    double[]? _weights;

    public string Rewrite(string line)
    {
        if (FormatsRx.Match(line) is { Success: true } m)
        {
            (_id, _formats, _weights) = (m.Groups["id"].Value, m.Groups["f"].Value.Split('+'), null);
            return line;
        }
        if (!line.StartsWith(DownloadPlan.ProgressPrefix, StringComparison.Ordinal)) return line;
        var parts = line[DownloadPlan.ProgressPrefix.Length..].Split('|');
        if (parts.Length < 4) return line;
        int index = Array.IndexOf(_formats, parts[3].Trim());
        var pct = PercentRx.Match(parts[0]);
        if (_formats.Length < 2 || index < 0 || !pct.Success ||
            !double.TryParse(pct.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return DownloadPlan.ProgressPrefix + string.Join('|', parts.Take(3));
        var w = _weights ?? (_weights = Weights()) ?? _formats.Select(_ => 1.0).ToArray();
        double overall = (w.Take(index).Sum() + w[index] * Math.Clamp(p, 0, 100) / 100) / w.Sum() * 100;
        return DownloadPlan.ProgressPrefix + overall.ToString("0.0", CultureInfo.InvariantCulture) + "%|" + parts[1] + "|" + parts[2];
    }

    double[]? Weights()
    {
        try
        {
            using var stream = new FileStream(sizesFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var p = new StreamReader(stream).ReadToEnd().Split('\n').Select(l => l.Trim().Split('|')).LastOrDefault(l => l[0] == _id);
            if (p == null) return null;
            var sizes = new Dictionary<string, double>();
            for (int i = 1; i + 1 < p.Length; i += 2)
                if (double.TryParse(p[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0) sizes[p[i]] = s;
            return _formats.Select(f => sizes.GetValueOrDefault(f)).ToArray() is var w && w.All(x => x > 0) ? w : _formats.Select(_ => 1.0).ToArray();
        }
        catch (IOException) { return null; }
    }
}

public sealed record Quality(string Label, int MaxHeight)
{
    public override string ToString() => Label;

    public static readonly Quality[] All =
    [
        new("Best available", 0),
        new("Up to 2160p (4K)", 2160),
        new("Up to 1440p", 1440),
        new("Up to 1080p", 1080),
        new("Up to 720p", 720),
        new("Up to 480p", 480),
        new("Up to 360p", 360),
    ];
}

public static class TimeText
{
    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static bool TryParse(string text, out double? seconds)
    {
        seconds = null;
        var parts = text.Trim().Split(':');
        if (parts is [""]) return true;
        if (parts.Length > 3) return false;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ||
                !last && parts[i].Contains('.') || i > 0 && v >= 60)
                return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}

public sealed record Section(double Start, double End, string? Title = null, int Number = 0)
{
    public bool ToEnd => double.IsPositiveInfinity(End);

    string Range => $"{TimeText.Format(Start)}-{(ToEnd ? "end" : TimeText.Format(End))}";

    public string Describe => Title != null ? $"chapter {Number} \"{Title}\" ({Range})" : Range;

    public string Suffix => Title != null
        ? $" - {Number:00} {SafeName(Title, 80)}"
        : $" ({TimeText.Format(Start).Replace(':', '.')}-{(ToEnd ? "end" : TimeText.Format(End).Replace(':', '.'))})";

    public static string SafeName(string text, int max)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(text.Select(c => c == ':' ? '.' : bad.Contains(c) ? '_' : c).ToArray()).Trim();
        if (s.Length > max) s = s[..max].Trim();
        return s.TrimEnd('.', ' ') is { Length: > 0 } t ? t : "_";
    }

    static string Seconds(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool IsAudio(string file) => Path.GetExtension(file).ToLowerInvariant() is ".mp3" or ".m4a" or ".opus" or ".ogg";

    public List<string> CutArgs(string input, string output, bool reencode)
    {
        List<string> a = ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", Seconds(Start), "-i", input];
        if (!ToEnd) a.AddRange(["-t", Seconds(End - Start)]);
        a.AddRange(["-map", "0:v:0?", "-map", "0:a:0?", "-map_chapters", "-1"]);
        if (IsAudio(input) || !reencode) a.AddRange(["-c", "copy"]);
        else a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        a.Add(output);
        return a;
    }
}

public sealed record SubtitleLanguage(string Name, string Code, string Code3)
{
    public override string ToString() => Name;
    public bool IsNone => Code.Length == 0;
    public string Pattern => $"{Code},{Code}(?-i:-(?:[A-Z]{{2}}|[0-9]{{3}}|[A-Z][a-z]{{3}})),{Code3}-.*";

    public static readonly SubtitleLanguage None = new("No subtitles", "", "");
    public static readonly SubtitleLanguage[] All =
    [
        None,
        new("English", "en", "eng"), new("Russian", "ru", "rus"), new("Ukrainian", "uk", "ukr"),
        new("German", "de", "deu"), new("French", "fr", "fra"), new("Spanish", "es", "spa"),
        new("Italian", "it", "ita"), new("Portuguese", "pt", "por"), new("Polish", "pl", "pol"),
        new("Turkish", "tr", "tur"), new("Japanese", "ja", "jpn"), new("Korean", "ko", "kor"), new("Chinese", "zh", "cmn"),
    ];

    public static SubtitleLanguage From(string? code) => All.FirstOrDefault(l => l.Code == (code ?? "")) ?? None;

    public string? Pick(IEnumerable<string> found, IReadOnlySet<string> manual) =>
        found.OrderBy(l => manual.Contains(l) ? 0 : 1)
             .ThenBy(l => l.Equals(Code, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
             .ThenBy(l => l, StringComparer.Ordinal)
             .FirstOrDefault();
}

public static class Srt
{
    static readonly Regex Times = new(@"(\d+):(\d\d):(\d\d)[,.](\d{3})\s*-->\s*(\d+):(\d\d):(\d\d)[,.](\d{3})");

    static double At(Match m, int g) =>
        int.Parse(m.Groups[g].Value) * 3600 + int.Parse(m.Groups[g + 1].Value) * 60 + int.Parse(m.Groups[g + 2].Value) + int.Parse(m.Groups[g + 3].Value) / 1000.0;

    static string Stamp(double s)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(s * 1000));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    public static string Slice(string srt, double start, double end)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var block in Regex.Split(srt.Replace("\r\n", "\n").Trim(), @"\n[ \t]*\n"))
        {
            var lines = block.Split('\n');
            int at = Array.FindIndex(lines, l => Times.IsMatch(l));
            if (at < 0) continue;
            var m = Times.Match(lines[at]);
            double a = At(m, 1), b = At(m, 5);
            if (b <= start || a >= end) continue;
            sb.Append(++n).Append("\r\n").Append(Stamp(Math.Max(a, start) - start)).Append(" --> ").Append(Stamp(Math.Min(b, end) - start)).Append("\r\n");
            foreach (var text in lines.Skip(at + 1)) sb.Append(text).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

public static class DownloadPlan
{
    public const string ProgressPrefix = "[P] ";

    public static List<string> Site(Platform platform, string? cookies, string? jsRuntime)
    {
        var a = new List<string>();
        if (!string.IsNullOrWhiteSpace(cookies)) a.AddRange(["--cookies", cookies]);
        if (platform == Platform.YouTube)
        {
            if (jsRuntime != null) a.AddRange(["--js-runtimes", jsRuntime]);
            a.AddRange(["--remote-components", "ejs:github"]);
        }
        return a;
    }

    static string[] Source(string url, string? batchFile) => batchFile != null ? ["-a", batchFile] : ["--", url];

    public const string SizesTemplate =
        "video:%(id)s|%(requested_formats.0.format_id)s|%(requested_formats.0.filesize,requested_formats.0.filesize_approx)s" +
        "|%(requested_formats.1.format_id)s|%(requested_formats.1.filesize,requested_formats.1.filesize_approx)s";

    public static List<string> Build(Platform platform, string url, DownloadMode mode, int maxHeight, string outDir,
                                     string? cookies, string? ffmpegDir, string? jsRuntime, string finalPathFile,
                                     bool videoItemsOnly = false, bool saveCover = false, string? batchFile = null, string? sizesFile = null)
    {
        bool audioOnly = mode is DownloadMode.Audio or DownloadMode.Mp3;
        List<string> a =
        [
            "--newline", "--color", "never", "--no-playlist", "--no-mtime",
            "--progress-template",
            "download:" + ProgressPrefix + "%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(info.format_id)s",
            "--print-to-file", "after_move:%(id)s|%(duration)s|%(resolution)s|%(filepath)s", finalPathFile,
            "-P", outDir,
            "--replace-in-metadata", "title", "!", "",
            .. Site(platform, cookies, jsRuntime),
        ];
        if (sizesFile != null) a.AddRange(["--print-to-file", SizesTemplate, sizesFile]);
        if (ffmpegDir != null) a.AddRange(["--ffmpeg-location", ffmpegDir]);

        if (mode == DownloadMode.Photo)
        {
            a.AddRange(["--ignore-no-formats-error", "--skip-download",
                        "--write-thumbnail", "--convert-thumbnails", "jpg",
                        "--match-filters", "!format_id",
                        "-o", "%(channel,uploader_id)s_%(id)s.%(ext)s", .. Source(url, batchFile)]);
            return a;
        }

        var name = platform switch
        {
            Platform.TikTok => "%(uploader)s_%(id)s",
            Platform.Instagram => "%(channel,uploader_id)s_%(id)s",
            _ => "%(title).120B [%(id)s]",
        };
        if (audioOnly && platform == Platform.TikTok) name += "_audio";
        if (!audioOnly && maxHeight > 0) name += "%(resolution& {}|)s";
        a.AddRange(["-o", name + ".%(ext)s"]);

        if (videoItemsOnly) a.AddRange(["--ignore-no-formats-error", "--match-filters", "format_id"]);
        if (saveCover) a.AddRange(["--write-thumbnail", "--convert-thumbnails", "jpg"]);

        var res = maxHeight > 0 ? $"res:{maxHeight}" : "res";
        if (mode == DownloadMode.Mp3)
            a.AddRange(["-f", platform == Platform.TikTok ? "b" : "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "2"]);
        else if (audioOnly)
            a.AddRange(["-f", platform == Platform.TikTok ? "b" : "ba[ext=m4a]/ba/b", "-x"]);
        else if (platform == Platform.TikTok)
            a.AddRange(["-f", "b", "-S", $"{res},vcodec:h264"]);
        else
            a.AddRange(["-f", "bv*+ba/b*", "-S", $"{res},vcodec:h264,acodec:aac", "--merge-output-format", "mp4"]);

        a.AddRange(Source(url, batchFile));
        return a;
    }

    public const string ManualSubtitlesTemplate = "video:%(id)s|%(subtitles)j";

    public static List<string> SubtitlesPass(Platform platform, string url, string? batchFile, SubtitleLanguage language, string outDir,
                                             string? cookies, string? ffmpegDir, string? jsRuntime, string? manualFile = null)
    {
        List<string> a =
        [
            "--newline", "--color", "never", "--no-playlist", "--ignore-errors", "--skip-download", "--ignore-no-formats-error",
            "--write-subs", "--write-auto-subs", "--sub-langs", language.Pattern, "--sub-format", "srt/best", "--convert-subs", "srt",
            "-P", outDir, "-o", "%(id)s.%(ext)s",
            .. manualFile != null ? new[] { "--print-to-file", ManualSubtitlesTemplate, manualFile } : [],
            .. Site(platform, cookies, jsRuntime),
        ];
        if (ffmpegDir != null) a.AddRange(["--ffmpeg-location", ffmpegDir]);
        a.AddRange(Source(url, batchFile));
        return a;
    }

    static readonly Regex ThumbWrittenRx = new(@"Writing video thumbnail \S+ to: (?<p>.+)$", RegexOptions.Multiline);

    public static List<string> ParsePhotos(string output)
    {
        var files = new List<string>();
        foreach (Match m in ThumbWrittenRx.Matches(output))
        {
            var p = m.Groups["p"].Value.Trim();
            if (!File.Exists(p)) p = Path.ChangeExtension(p, ".jpg");
            if (File.Exists(p) && !files.Contains(p)) files.Add(p);
        }
        return files;
    }

    public static List<SavedFile> ParseFinal(string file)
    {
        try
        {
            return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().Select(line =>
            {
                var p = line.Split('|');
                string? At(int i) => i >= 0 && i < p.Length - 1 && p[i] is not ("" or "NA") ? p[i] : null;
                string? res = At(p.Length - 2), id = At(p.Length - 4);
                double? duration = double.TryParse(At(p.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
                return new SavedFile(p[^1], id, res == "audio only" ? null : res, duration);
            }).ToList();
        }
        catch { return []; }
    }
}

public sealed record SavedFile(string Path, string? Id = null, string? Resolution = null, double? Duration = null, double? Start = null, double? End = null);

public sealed record PlaylistEntry(string Id, string Url, string Title, double? Duration, string? Section);

public sealed record Listing(int ExitCode, string Output, bool IsList, string Title, List<PlaylistEntry> Entries, int Skipped)
{
    public const string Prefix = "[entry] ";

    public static List<string> Args(Platform platform, string url, string? cookies, string? jsRuntime) =>
    [
        "--newline", "--color", "never", "--flat-playlist", "--ignore-no-formats-error",
        "--print", Prefix + "%(.{id,url,title,duration,playlist_id,playlist_title,ie_key,live_status})j",
        .. DownloadPlan.Site(platform, cookies, jsRuntime), "--", url,
    ];

    public static async Task<Listing> RunAsync(Platform platform, Uri uri, string? cookies, Action<int> onCount, CancellationToken ct)
    {
        int count = 0;
        var (code, output) = await YtDlp.RunAsync(Args(platform, uri.AbsoluteUri, cookies, YtDlp.JsRuntimeArgument()),
            line => { if (line.StartsWith(Prefix, StringComparison.Ordinal)) onCount(Interlocked.Increment(ref count)); }, ct);
        return Parse(code, output);
    }

    public static Listing Parse(int exitCode, string output)
    {
        var entries = new List<PlaylistEntry>();
        var seen = new HashSet<string>();
        bool isList = false;
        int skipped = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            JsonElement e;
            try
            {
                using var doc = JsonDocument.Parse(line[Prefix.Length..]);
                e = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }
            string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("playlist_id") == null) continue;
            isList = true;
            string? id = S("id"), url = S("url"), ie = S("ie_key") ?? "", title = S("title");
            if (id == null || url == null || ie.EndsWith("Tab") || ie.Contains("Playlist") || ie.Contains("User") ||
                S("live_status") is "is_live" or "is_upcoming" || title is "[Private video]" or "[Deleted video]")
            {
                skipped++;
                continue;
            }
            if (!seen.Add(id)) continue;
            double? duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            entries.Add(new(id, url, title ?? id, duration, S("playlist_title") ?? ""));
        }

        var lists = entries.Select(x => x.Section!).Distinct().ToList();
        var owner = lists.Count > 1 && lists.All(l => l.Contains(" - ")) && lists.Select(l => l[..l.LastIndexOf(" - ")]).Distinct().Count() == 1
            ? lists[0][..lists[0].LastIndexOf(" - ")] : null;
        var name = lists.Count == 1 ? lists[0] : owner ?? lists.FirstOrDefault() ?? "";
        entries = entries.Select(x => x with { Section = lists.Count < 2 ? null : owner != null ? x.Section![(owner.Length + 3)..] : x.Section }).ToList();
        return new(exitCode, output, isList, name, entries, skipped);
    }
}

public sealed record VideoInfo(int ExitCode, string Output, string? Title, double? Duration, List<Section> Chapters)
{
    public const string Prefix = "[chapters] ";

    public static List<string> Args(Platform platform, string url, string? cookies, string? jsRuntime) =>
    [
        "--newline", "--color", "never", "--no-playlist", "--skip-download", "--ignore-no-formats-error",
        "--print", Prefix + "%(.{title,duration,chapters})j",
        .. DownloadPlan.Site(platform, cookies, jsRuntime), "--", url,
    ];

    public static async Task<VideoInfo> RunAsync(Platform platform, Uri uri, string? cookies, CancellationToken ct)
    {
        var (code, output) = await YtDlp.RunAsync(Args(platform, uri.AbsoluteUri, cookies, YtDlp.JsRuntimeArgument()), null, ct);
        return Parse(code, output);
    }

    public static VideoInfo Parse(int exitCode, string output)
    {
        var line = output.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line == null) return new(exitCode, output, null, null, []);
        try
        {
            using var doc = JsonDocument.Parse(line[Prefix.Length..]);
            var root = doc.RootElement;
            double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            var chapters = new List<Section>();
            if (root.TryGetProperty("chapters", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                {
                    double start = c.GetProperty("start_time").GetDouble();
                    double end = c.TryGetProperty("end_time", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetDouble() : duration ?? double.PositiveInfinity;
                    var title = c.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s ? s : $"Chapter {chapters.Count + 1}";
                    chapters.Add(new(start, end, title, chapters.Count + 1));
                }
            var name = root.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            return new(exitCode, output, name, duration, chapters);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(exitCode, output, null, null, []);
        }
    }
}

public static class Leftovers
{
    static readonly Regex StreamRx = new(@"\.f(?=[0-9A-Za-z_=-]*\d)[0-9A-Za-z_=-]+\.(mp4|m4a|webm|mkv|mp3|ogg|opus|aac|flv|3gp|mov)$", RegexOptions.IgnoreCase);

    public static bool IsTemporary(string name) =>
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) || name.Contains(".temp.", StringComparison.OrdinalIgnoreCase) ||
        StreamRx.IsMatch(name);

    public static HashSet<string> Snapshot(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public static async Task<List<string>> CleanAsync(string dir, IReadOnlySet<string> before)
    {
        var removed = new List<string>();
        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return removed; }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (before.Contains(name) || !IsTemporary(name)) continue;
            for (int i = 0; i < 15 && File.Exists(file); i++)
            {
                try
                {
                    File.Delete(file);
                    removed.Add(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(200); }
            }
        }
        return removed;
    }
}

public static class Downloader
{
    public sealed record Request(Platform Platform, Uri Uri, DownloadMode Mode, int MaxHeight, string OutDir, string? Cookies, bool SaveCover)
    {
        public IReadOnlyList<string>? Items { get; init; }
        public IReadOnlyList<Section>? Sections { get; init; }
        public SubtitleLanguage Subtitles { get; init; } = SubtitleLanguage.None;
    }

    public sealed record Result(int ExitCode, string Output, List<string> Saved, List<string> Covers,
                                int Photos, int Videos, string? Resolution)
    {
        public List<string> Subtitles { get; init; } = [];
        public int Failed { get; init; }
    }

    public const string RetryNote = "-> Network error, trying again...";
    public const string SubtitlesNote = "-> Getting subtitles...";
    public const string CutNote = "-> Cutting ";

    public static bool IsInstagramAuto(Request r) => r.Platform == Platform.Instagram && r.Mode == DownloadMode.Video;

    public static async Task<Result> RunAsync(Request r, Action<string> onLine, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}_{Guid.NewGuid():N}");
        var finalPathFile = temp + ".txt";
        var sizesFile = temp + ".sizes.txt";
        var batch = r.Items is { Count: > 0 } ? temp + ".urls.txt" : null;
        bool cutting = r.Sections is { Count: > 0 } && batch == null;
        var videoDir = cutting ? Path.Combine(r.OutDir, ".nya-" + Guid.NewGuid().ToString("N")[..8]) : r.OutDir;
        bool photoPass = IsInstagramAuto(r) && !PlatformDetector.IsInstagramReel(r.Uri) && batch == null;
        var tracker = new ProgressTracker(sizesFile);
        void Line(string l) => onLine(tracker.Rewrite(l));
        List<string> Plan(DownloadMode m, string dir) => DownloadPlan.Build(r.Platform, r.Uri.AbsoluteUri, m, r.MaxHeight, dir, r.Cookies,
            YtDlp.FfmpegDirArgument(), YtDlp.JsRuntimeArgument(), finalPathFile, videoItemsOnly: photoPass, saveCover: r.SaveCover,
            batchFile: batch, sizesFile: sizesFile);
        async Task<(int, string)> Run(List<string> args)
        {
            var (code, output) = await YtDlp.RunAsync(args, Line, ct);
            if (code == 0 || !Hints.IsTransient(output)) return (code, output);
            onLine("");
            onLine(RetryNote);
            return await YtDlp.RunAsync(args, Line, ct);
        }

        var saved = new List<SavedFile>();
        var covers = new List<string>();
        var subtitles = new List<string>();
        int code = 0, photos = 0;
        string output = "";
        bool videoPass = true;
        var before = Leftovers.Snapshot(r.OutDir);
        try
        {
            if (batch != null) File.WriteAllLines(batch, r.Items!);
            if (photoPass)
            {
                onLine("-> Checking what the post contains: photos, videos or a carousel...");
                (code, output) = await Run(Plan(DownloadMode.Photo, r.OutDir));
                if (code == 0)
                {
                    saved.AddRange(DownloadPlan.ParsePhotos(output).Select(p => new SavedFile(p)));
                    photos = saved.Count;
                    videoPass = output.Contains("does not pass filter");
                    if (videoPass) onLine("-> The post has videos, downloading them.");
                }
            }
            if (code == 0 && videoPass)
            {
                if (cutting) File.SetAttributes(Directory.CreateDirectory(videoDir).FullName, FileAttributes.Directory | FileAttributes.Hidden);
                (code, output) = await Run(Plan(r.Mode, videoDir));
                var videos = DownloadPlan.ParseFinal(finalPathFile);
                var pictures = r.SaveCover && code == 0 ? DownloadPlan.ParsePhotos(output) : [];
                if (cutting && code == 0)
                {
                    (code, output, videos) = await CutAsync(r, videos, output, onLine, ct);
                    pictures = pictures.Select(p => MoveInto(p, r.OutDir)).ToList();
                }
                if (code == 0 || batch != null) { saved.AddRange(videos); covers.AddRange(pictures); }
            }
            if (!r.Subtitles.IsNone && r.Platform != Platform.Instagram && saved.Any(s => s.Id != null))
                subtitles = await SubtitlesAsync(r, saved, batch, temp, onLine, ct);
        }
        catch (OperationCanceledException)
        {
            var removed = await Leftovers.CleanAsync(r.OutDir, before);
            if (removed.Count > 0) onLine($"-> Removed {removed.Count} unfinished file(s): {string.Join(", ", removed.Select(Path.GetFileName))}");
            throw;
        }
        finally
        {
            foreach (var f in new[] { finalPathFile, sizesFile, batch }) if (f != null) try { File.Delete(f); } catch { }
            foreach (var d in new[] { temp, cutting ? videoDir : null }) if (d != null) await DeleteDirAsync(d);
        }
        int failed = batch != null && code != 0 ? Math.Max(0, r.Items!.Count - saved.Select(s => s.Id).Distinct().Count()) : 0;
        return new Result(code, output, saved.Select(s => s.Path).ToList(), covers, photos, saved.Count - photos,
                          saved.Select(s => s.Resolution).FirstOrDefault(x => x != null)) { Subtitles = subtitles, Failed = failed };
    }

    static async Task DeleteDirAsync(string dir)
    {
        for (int i = 0; i < 25 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }

    static string MoveInto(string file, string dir)
    {
        var target = Path.Combine(dir, Path.GetFileName(file));
        File.Move(file, target, overwrite: true);
        return target;
    }

    static async Task<(int, string, List<SavedFile>)> CutAsync(Request r, List<SavedFile> files, string output, Action<string> onLine, CancellationToken ct)
    {
        var ffmpeg = new YtDlp.Engine(YtDlp.FfmpegExe(), [], "ffmpeg");
        var cut = new List<SavedFile>();
        foreach (var f in files)
            foreach (var s in r.Sections!)
            {
                if (f.Duration is { } length && s.Start >= length)
                    return (1, output + $"\nERROR: The fragment {s.Describe} starts after the end of the video ({TimeText.Format(length)}).", cut);
                var target = Path.Combine(r.OutDir, Path.GetFileNameWithoutExtension(f.Path) + s.Suffix + Path.GetExtension(f.Path));
                onLine("");
                onLine(CutNote + s.Describe + "...");
                var (code, log) = await YtDlp.RunAsync(s.CutArgs(f.Path, target, reencode: true), onLine, ct, ffmpeg);
                if (code != 0 && !Section.IsAudio(f.Path))
                {
                    onLine("-> Exact cutting failed, cutting at the nearest keyframes instead.");
                    (code, log) = await YtDlp.RunAsync(s.CutArgs(f.Path, target, reencode: false), onLine, ct, ffmpeg);
                }
                if (code != 0 || !File.Exists(target))
                    return (code != 0 ? code : 1, output + "\n" + log + $"\nERROR: Couldn't cut {s.Describe} out of the video.", cut);
                cut.Add(f with { Path = target, Start = s.Start, End = s.End });
            }
        return (0, output, cut);
    }

    static async Task<List<string>> SubtitlesAsync(Request r, List<SavedFile> saved, string? batch, string tempDir, Action<string> onLine, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var manualFile = Path.Combine(tempDir, "manual.txt");
        onLine("");
        onLine(SubtitlesNote);
        await YtDlp.RunAsync(DownloadPlan.SubtitlesPass(r.Platform, r.Uri.AbsoluteUri, batch, r.Subtitles, tempDir, r.Cookies,
                                                        YtDlp.FfmpegDirArgument(), YtDlp.JsRuntimeArgument(), manualFile), onLine, ct);
        var manual = ManualSubtitles(manualFile);
        var placed = new List<string>();
        var subs = Directory.GetFiles(tempDir, "*.srt").Select(Path.GetFileName).ToList();
        foreach (var f in saved.Where(f => f.Id != null))
        {
            var langs = subs.Where(n => n!.StartsWith(f.Id + ".", StringComparison.Ordinal)).Select(n => n![(f.Id!.Length + 1)..^".srt".Length]).ToList();
            if (r.Subtitles.Pick(langs, manual.GetValueOrDefault(f.Id!) ?? new HashSet<string>()) is not { } lang) continue;
            var target = Path.ChangeExtension(f.Path, null) + "." + lang + ".srt";
            var text = File.ReadAllText(Path.Combine(tempDir, f.Id + "." + lang + ".srt"));
            if (f.Start is { } a) text = Srt.Slice(text, a, f.End ?? double.PositiveInfinity);
            File.WriteAllText(target, text, new UTF8Encoding(false));
            placed.Add(target);
        }
        return placed;
    }

    public static Dictionary<string, HashSet<string>> ManualSubtitles(string file)
    {
        var result = new Dictionary<string, HashSet<string>>();
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var bar = line.IndexOf('|');
                if (bar <= 0) continue;
                var langs = new HashSet<string>();
                try
                {
                    using var doc = JsonDocument.Parse(line[(bar + 1)..]);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject()) langs.Add(p.Name);
                }
                catch (JsonException) { }
                result[line[..bar]] = langs;
            }
        }
        catch (IOException) { }
        return result;
    }
}

public static class YtDlp
{
    public static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "tools");

    static string[] LocalDirs => [AppContext.BaseDirectory, ToolsDir];

    public sealed record Engine(string File, string[] Prefix, string Description);

    public static Engine Locate()
    {
        foreach (var (dir, where) in new[] { (AppContext.BaseDirectory, "next to the app"), (ToolsDir, "installed components") })
            if (File.Exists(Path.Combine(dir, "yt-dlp.exe")))
                return new(Path.Combine(dir, "yt-dlp.exe"), [], $"yt-dlp.exe ({where})");
        return new("python", ["-m", "yt_dlp"], "python -m yt_dlp");
    }

    public static bool UsesPython => Locate().Prefix.Length > 0;

    static bool HasExe(string dir, string exe) => File.Exists(Path.Combine(dir, exe));
    static bool HasBoth(string dir) => HasExe(dir, "ffmpeg.exe") && HasExe(dir, "ffprobe.exe");

    public static string? FfmpegDirArgument()
    {
        bool onPath = FindOnPath("ffmpeg.exe") != null;
        if (onPath && FindOnPath("ffprobe.exe") != null) return null;
        var dir = LocalDirs.FirstOrDefault(HasBoth) ?? (onPath ? null : LocalDirs.FirstOrDefault(d => HasExe(d, "ffmpeg.exe")));
        return dir?.TrimEnd('\\');
    }

    public static string FfmpegExe() => FfmpegDirArgument() is { } dir ? Path.Combine(dir, "ffmpeg.exe") : FindOnPath("ffmpeg.exe") ?? "ffmpeg.exe";

    public static bool HasFfmpeg() =>
        FindOnPath("ffmpeg.exe") != null && FindOnPath("ffprobe.exe") != null || LocalDirs.Any(HasBoth);

    public static string? JsRuntimeArgument()
    {
        if (FindOnPath("node.exe") != null) return "node";
        var deno = Path.Combine(ToolsDir, "deno.exe");
        return File.Exists(deno) ? "deno:" + deno : null;
    }

    public static bool HasJsRuntime() => JsRuntimeArgument() != null || FindOnPath("deno.exe") != null;

    static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> args, Action<string>? onLine,
        CancellationToken ct = default, Engine? program = null)
    {
        var (file, prefix, _) = program ?? Locate();
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in prefix.Concat(args)) psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        }
        proc.OutputDataReceived += OnData;
        proc.ErrorDataReceived += OnData;

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using (ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } }))
            await proc.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        lock (output) return (proc.ExitCode, output.ToString());
    }

    public sealed record PackageUpdate(string Name, string Installed, string Latest);
    public sealed record UpdateInfo(IReadOnlyDictionary<string, string> Installed, IReadOnlyList<PackageUpdate> Updates);

    static readonly string[] TrackedPackages = ["yt-dlp", "curl_cffi", "yt-dlp-ejs"];
    const string PipPackage = "yt-dlp[default,curl-cffi]";
    static readonly Engine Python = new("python", [], "python"), Pip = new("python", ["-m", "pip"], "pip");
    static string NormalizePackage(string name) => name.Trim().ToLowerInvariant().Replace('_', '-');

    public static async Task<UpdateInfo?> CheckUpdatesAsync(string? localYtDlp)
    {
        try
        {
            if (!UsesPython)
            {
                if (localYtDlp == null) return null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var release = JsonDocument.Parse(await Components.Http.GetStringAsync(
                    "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", timeout.Token));
                var latest = release.RootElement.GetProperty("tag_name").GetString()!.Trim();
                bool newer = Version.TryParse(latest, out var l) && Version.TryParse(localYtDlp, out var c) && l > c;
                return new UpdateInfo(new Dictionary<string, string> { ["yt-dlp"] = localYtDlp },
                                      newer ? [new PackageUpdate("yt-dlp", localYtDlp, latest)] : []);
            }

            var (_, vOut) = await RunAsync(
            [
                "-c",
                "import importlib.metadata as m, sys\nfor n in sys.argv[1:]:\n    try: print(n, m.version(n))\n    except Exception: print(n, '-')",
                .. TrackedPackages,
            ], null, default, Python);
            var have = new Dictionary<string, string>();
            foreach (var line in vOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (line.Split(' ', 2) is [var name, var version] && version != "-" && TrackedPackages.Contains(name))
                    have[name] = version;

            var (code, output) = await RunAsync(
                ["install", "--dry-run", "--quiet", "--disable-pip-version-check", "--report", "-", "--upgrade", PipPackage],
                null, default, Pip);
            int start = output.IndexOf('{'), end = output.LastIndexOf('}');
            if (code != 0 || start < 0 || end <= start) return null;
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            var wouldInstall = doc.RootElement.GetProperty("install").EnumerateArray()
                .Select(i => i.GetProperty("metadata"))
                .ToDictionary(m => NormalizePackage(m.GetProperty("name").GetString() ?? ""), m => m.GetProperty("version").GetString() ?? "?");
            var list = TrackedPackages
                .Where(p => wouldInstall.ContainsKey(NormalizePackage(p)))
                .Select(p => new PackageUpdate(p, have.GetValueOrDefault(p, "not installed"), wouldInstall[NormalizePackage(p)]))
                .ToList();
            return new UpdateInfo(have, list);
        }
        catch { return null; }
    }

    public static Task<(int ExitCode, string Output)> UpdateAsync(Action<string>? onLine, CancellationToken ct) =>
        UsesPython
            ? RunAsync(["install", "--user", "-U", PipPackage], onLine, ct, Pip)
            : RunAsync(["-U"], onLine, ct);
}

public static class Components
{
    public sealed record Item(string Name, string Url, int ApproxMb, Func<string, bool>? ZipEntryFilter);

    public static readonly Item YtDlpExe = new("yt-dlp",
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", 22, null);
    public static readonly Item Ffmpeg = new("ffmpeg",
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip", 86,
        e => e.Contains("/bin/") && (e.EndsWith(".dll") || e.EndsWith("/ffmpeg.exe") || e.EndsWith("/ffprobe.exe")));
    public static readonly Item Deno = new("Deno (for YouTube)",
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip", 40,
        e => e == "deno.exe");

    public static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        return http;
    }

    public static async Task InstallAsync(IEnumerable<Item> items, Action<string, double> onProgress, Action<string> log, CancellationToken ct,
                                          string? toDir = null)
    {
        var dir = toDir ?? YtDlp.ToolsDir;
        Directory.CreateDirectory(dir);
        foreach (var item in items)
        {
            log($"Downloading {item.Name} (~{item.ApproxMb} MB): {item.Url}");
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (var resp = await Http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? item.ApproxMb * 1024L * 1024L;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(tmp);
                    var buf = new byte[1 << 16];
                    long done = 0;
                    var lastReport = Environment.TickCount64;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (Environment.TickCount64 - lastReport > 150)
                        {
                            onProgress(item.Name, Math.Min(1.0, (double)done / total));
                            lastReport = Environment.TickCount64;
                        }
                    }
                }
                onProgress(item.Name, 1.0);

                if (item.ZipEntryFilter == null)
                {
                    File.Move(tmp, Path.Combine(dir, Path.GetFileName(new Uri(item.Url).LocalPath)), overwrite: true);
                    continue;
                }
                int extracted = 0;
                using (var zip = ZipFile.OpenRead(tmp))
                    foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0 && item.ZipEntryFilter(e.FullName)))
                    {
                        entry.ExtractToFile(Path.Combine(dir, entry.Name), overwrite: true);
                        extracted++;
                    }
                if (extracted == 0) throw new InvalidDataException($"The {item.Name} archive doesn't contain the expected files.");
                log($"  extracted {extracted} files");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        log("Components installed to " + dir);
    }
}

public sealed record AppRelease(Version Version, string PageUrl, string? ExeUrl, string? Sha256, long Size);

public static class AppUpdate
{
    public const string LatestRelease = "https://api.github.com/repos/lovesuper/nya-downloader/releases/latest";
    public static string Api => Environment.GetEnvironmentVariable("NYADOWNLOADER_UPDATES_URL") is { Length: > 0 } u ? u : LatestRelease;

    public static AppRelease? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.Trim().TrimStart('v', 'V'), out var version)) return null;
            string? url = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    if (!string.Equals(a.GetProperty("name").GetString(), AppInfo.Name + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    url = a.GetProperty("browser_download_url").GetString();
                    sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null;
                    size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                }
            return new(version, root.GetProperty("html_url").GetString() ?? "", url, sha, size);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static bool IsNewer(AppRelease r, string current) => Version.TryParse(current, out var c) && r.Version > c;

    public static async Task<AppRelease?> CheckAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, Api);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Components.Http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(timeout.Token)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { return null; }
    }

    public static string NewPath(string exe) => exe + ".new";
    public static string OldPath(string exe) => Path.ChangeExtension(exe, ".old.exe");

    public static async Task DownloadAsync(AppRelease r, string exe, Action<double> onProgress, CancellationToken ct)
    {
        if (r.ExeUrl == null) throw new InvalidDataException("The release has no " + AppInfo.Name + ".exe.");
        var target = NewPath(exe);
        try
        {
            using (var response = await Components.Http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? r.Size;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(target);
                using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    sha.AppendData(buf, 0, n);
                    done += n;
                    if (total > 0) onProgress(Math.Min(1.0, (double)done / total));
                }
                var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (r.Sha256 != null && hash != r.Sha256)
                    throw new InvalidDataException("The downloaded file is damaged (its SHA-256 doesn't match the release).");
                if (done < 1024 * 1024) throw new InvalidDataException("The downloaded file is too small to be the app.");
            }
            using (var check = File.OpenRead(target))
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z') throw new InvalidDataException("The downloaded file is not a program.");
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
    }

    public static void Swap(string exe)
    {
        var old = OldPath(exe);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(NewPath(exe), exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    public static void CleanUp(string exe)
    {
        foreach (var leftover in new[] { OldPath(exe), NewPath(exe) })
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { }
    }
}

public static class Hints
{
    public enum LoginProblem { None, AgeRestricted, CookiesRevoked, LoginRequired }

    static bool Has(string text, params string[] any) => any.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static bool IsTransient(string output) =>
        !Has(output, "IP address is blocked") && Has(output, "TransportError", "HTTP Error 403", "timed out", "Connection reset");

    public static (string? Hint, LoginProblem Login) Diagnose(string output, Platform platform)
    {
        bool H(params string[] any) => Has(output, any);
        var site = platform == Platform.Unknown ? "the site" : PlatformDetector.DisplayName(platform);

        if (H("cookies are no longer valid"))
            return ("YouTube no longer accepts these cookies. Export them again and drop the new file into the window.", LoginProblem.CookiesRevoked);
        if (H("Sign in to confirm your age") || H("age-restricted") && H("ERROR"))
            return ($"This video is age-restricted. {site} only shows it to signed-in users, so add a cookies.txt from {site}.", LoginProblem.AgeRestricted);
        if (H("not a bot"))
            return ("YouTube wants to check that you are not a bot. Add a cookies.txt from YouTube.", LoginProblem.LoginRequired);
        if (H("IP address is blocked"))
            return ("This video is blocked in your country. Turn on a VPN and try again.", default);
        if (H("No module named yt_dlp"))
            return ("yt-dlp not found. Click Install components.", default);
        if (H("no impersonate target"))
            return (YtDlp.UsesPython
                ? "TikTok needs the curl_cffi package: python -m pip install --user -U \"yt-dlp[default,curl-cffi]\""
                : "TikTok needs a newer yt-dlp. Install the update when the app offers one.", default);
        if (platform == Platform.Instagram && H("login required", "rate-limit", "Requested content is not available", "Restricted Video"))
            return ("Instagram wants you to sign in for this. Add a cookies.txt from instagram.com.", LoginProblem.LoginRequired);
        if (H("Private video", "This video is private"))
            return ("This video is private. You need cookies from an account that can see it.", LoginProblem.LoginRequired);
        if (H("Video unavailable", "video is unavailable", "has been removed"))
            return ("The video was removed or is not available.", default);
        if (H("Requested format is not available"))
            return ("That format is not available (for example, the video has no sound). Try the other mode.", default);
        if (H("Unsupported URL"))
            return ("This site or link type is not supported.", default);
        if (H("JavaScript runtime"))
            return ("YouTube needs a JavaScript runtime (Deno). Click Install components.", default);
        if (H("ffmpeg") && H("not found", "not installed"))
            return ("ffmpeg not found. Click Install components.", default);
        if (H("getaddrinfo", "timed out", "Connection refused", "TransportError", "Connection reset"))
            return ("Network error. Click Download again, or check your connection or VPN.", default);
        if (H("HTTP Error 403", "Unable to extract", "Unexpected response"))
            return ("The site refused the request or has changed. If an update is offered at the bottom of the window, install it and try again.", default);
        return (null, default);
    }
}

public sealed class LogFile(string name)
{
    readonly Lazy<string> _path = new(() => ChoosePath(name));
    readonly object _gate = new();
    bool _sessionHeaderWritten;

    public string FilePath => _path.Value;
    public bool Exists => File.Exists(FilePath);

    static string ChoosePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(dir, name);
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, name);
        }
    }

    public void Append(string text)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1024 * 1024) File.Move(FilePath, FilePath + ".old", overwrite: true);
                if (!_sessionHeaderWritten)
                {
                    text = $"=== {AppInfo.Name} {AppInfo.Version} | {System.Runtime.InteropServices.RuntimeInformation.OSDescription} | " +
                           $".NET {Environment.Version} | started {Process.GetCurrentProcess().StartTime:yyyy-MM-dd HH:mm:ss}" +
                           Environment.NewLine + text;
                    _sessionHeaderWritten = true;
                }
                File.AppendAllText(FilePath, text, new UTF8Encoding(true));
            }
        }
        catch { }
    }
}

public static class UsageLog
{
    static readonly LogFile Log = new("using.log");

    public static void Write(string action) =>
        Log.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action}{Environment.NewLine}");
}

public static class ErrorLog
{
    static readonly LogFile Log = new("error.log");

    public static string FilePath => Log.FilePath;
    public static bool Exists => Log.Exists;

    public static void Write(string kind, Exception ex, string? context = null) =>
        Write(kind, (context != null ? context + ": " : "") + ex.Message, ex.ToString());

    public static void Write(string kind, string message, string? details = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {message}");
        if (!string.IsNullOrWhiteSpace(details))
            foreach (var line in details.TrimEnd().Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
        sb.AppendLine();
        Log.Append(sb.ToString());
        UsageLog.Write($"{kind}: {message.Split('\n')[0]} (details in error.log)");
    }

    public static string Tail(string output, int lines = 40) =>
        string.Join("\n", output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith(DownloadPlan.ProgressPrefix, StringComparison.Ordinal))
            .TakeLast(lines));
}

public sealed class BackgroundLook
{
    public const int MinScale = 50, MaxScale = 250;

    public int Gif { get; set; }
    public int Visibility { get; set; } = 100;
    public int Blur { get; set; }
    public int Scale { get; set; } = 100;
    public int Glass { get; set; } = 100;
    public int Highlight { get; set; } = 100;
    public int Frost { get; set; } = 64;
    public int Ice { get; set; } = 9;
    public int Backing { get; set; } = 60;

    public BackgroundLook Clone() => (BackgroundLook)MemberwiseClone();

    public BackgroundLook Checked(int gifCount)
    {
        var c = Clone();
        c.Gif = Math.Clamp(c.Gif, 0, Math.Max(0, gifCount));
        c.Scale = Math.Clamp(c.Scale, MinScale, MaxScale);
        (c.Visibility, c.Blur, c.Glass, c.Highlight, c.Frost, c.Ice, c.Backing) =
            (Math.Clamp(c.Visibility, 0, 100), Math.Clamp(c.Blur, 0, 100), Math.Clamp(c.Glass, 0, 100), Math.Clamp(c.Highlight, 0, 100),
             Math.Clamp(c.Frost, 0, 100), Math.Clamp(c.Ice, 0, 100), Math.Clamp(c.Backing, 0, 100));
        return c;
    }

    public override string ToString() =>
        $"gif {(Gif == 0 ? "random" : Gif.ToString())}, visibility {Visibility}%, blur {Blur}, scale {Scale}%, glass {Glass}, highlight {Highlight}%, frost {Frost}%, ice {Ice}, text backing {Backing}%";
}

public sealed class Settings
{
    public string? OutputDir { get; set; }
    public bool AudioOnly { get; set; }
    public string AudioFormat { get; set; } = "mp3";
    public int MaxHeight { get; set; }
    public bool SaveCover { get; set; }
    public string Subtitles { get; set; } = "";
    public BackgroundLook Look { get; set; } = new();
    public Dictionary<string, string> Cookies { get; set; } = new();

    static string PathFor(string appName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName, "settings.json");
    static string FilePath => AppInfo.DataDirOverride is { } dir ? Path.Combine(dir, "settings.json") : PathFor(AppInfo.Name);

    public static Settings Load()
    {
        var path = File.Exists(FilePath) || AppInfo.DataDirOverride != null ? FilePath : PathFor(AppInfo.OldName);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
