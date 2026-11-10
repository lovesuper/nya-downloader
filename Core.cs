using System.Diagnostics;
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

public static class PlatformDetector
{
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

public enum DownloadMode { Video, Audio, Photo }

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

public static class DownloadPlan
{
    public const string ProgressPrefix = "[P] ";

    public static List<string> Build(Platform platform, string url, DownloadMode mode, int maxHeight, string outDir,
                                     string? cookies, string? ffmpegDir, string? jsRuntime, string finalPathFile,
                                     bool videoItemsOnly = false, bool saveCover = false)
    {
        bool audioOnly = mode == DownloadMode.Audio;
        List<string> a =
        [
            "--newline", "--color", "never", "--no-playlist", "--no-mtime",
            "--progress-template",
            "download:" + ProgressPrefix + "%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print-to-file", "after_move:%(resolution)s|%(filepath)s", finalPathFile,
            "-P", outDir,
            "--replace-in-metadata", "title", "!", "",
        ];
        if (!string.IsNullOrWhiteSpace(cookies)) a.AddRange(["--cookies", cookies]);
        if (ffmpegDir != null) a.AddRange(["--ffmpeg-location", ffmpegDir]);

        if (platform == Platform.YouTube)
        {
            if (jsRuntime != null) a.AddRange(["--js-runtimes", jsRuntime]);
            a.AddRange(["--remote-components", "ejs:github"]);
        }

        if (mode == DownloadMode.Photo)
        {
            a.AddRange(["--ignore-no-formats-error", "--skip-download",
                        "--write-thumbnail", "--convert-thumbnails", "jpg",
                        "--match-filters", "!format_id",
                        "-o", "%(channel,uploader_id)s_%(id)s.%(ext)s", "--", url]);
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
        if (audioOnly)
            a.AddRange(["-f", platform == Platform.TikTok ? "b" : "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K"]);
        else if (platform == Platform.TikTok)
            a.AddRange(["-f", "b", "-S", $"{res},vcodec:h264"]);
        else
            a.AddRange(["-f", "bv*+ba/b*", "-S", $"{res},vcodec:h264,acodec:aac", "--merge-output-format", "mp4"]);

        a.AddRange(["--", url]);
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

    public static List<(string Path, string? Resolution)> ParseFinal(string file)
    {
        try
        {
            return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().Select(line =>
            {
                var bar = line.IndexOf('|');
                var res = bar < 0 ? "" : line[..bar];
                return (line[(bar + 1)..], res is "" or "NA" or "audio only" ? null : res);
            }).ToList();
        }
        catch { return []; }
    }
}

public static class Downloader
{
    public sealed record Request(Platform Platform, Uri Uri, DownloadMode Mode, int MaxHeight, string OutDir, string? Cookies, bool SaveCover);

    public sealed record Result(int ExitCode, string Output, List<string> Saved, List<string> Covers,
                                int Photos, int Videos, string? Resolution);

    public const string RetryNote = "-> Network error, trying again...";

    public static bool IsInstagramAuto(Request r) => r.Platform == Platform.Instagram && r.Mode == DownloadMode.Video;

    public static async Task<Result> RunAsync(Request r, Action<string> onLine, CancellationToken ct)
    {
        var finalPathFile = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}_{Guid.NewGuid():N}.txt");
        bool photoPass = IsInstagramAuto(r) && !PlatformDetector.IsInstagramReel(r.Uri);
        List<string> Plan(DownloadMode m) => DownloadPlan.Build(r.Platform, r.Uri.AbsoluteUri, m, r.MaxHeight, r.OutDir, r.Cookies,
            YtDlp.FfmpegDirArgument(), YtDlp.JsRuntimeArgument(), finalPathFile, videoItemsOnly: photoPass, saveCover: r.SaveCover);
        async Task<(int, string)> Run(List<string> args)
        {
            var (code, output) = await YtDlp.RunAsync(args, onLine, ct);
            if (code == 0 || !Hints.IsTransient(output)) return (code, output);
            onLine("");
            onLine(RetryNote);
            return await YtDlp.RunAsync(args, onLine, ct);
        }

        var saved = new List<string>();
        var covers = new List<string>();
        int code = 0, photos = 0, videos = 0;
        string output = "";
        string? resolution = null;
        bool videoPass = true;
        try
        {
            if (photoPass)
            {
                onLine("-> Checking what the post contains: photos, videos or a carousel...");
                (code, output) = await Run(Plan(DownloadMode.Photo));
                if (code == 0)
                {
                    saved.AddRange(DownloadPlan.ParsePhotos(output));
                    photos = saved.Count;
                    videoPass = output.Contains("does not pass filter");
                    if (videoPass) onLine("-> The post has videos, downloading them.");
                }
            }
            if (code == 0 && videoPass)
            {
                (code, output) = await Run(Plan(r.Mode));
                if (code == 0)
                {
                    foreach (var (path, res) in DownloadPlan.ParseFinal(finalPathFile)) { saved.Add(path); videos++; resolution ??= res; }
                    if (r.SaveCover) covers.AddRange(DownloadPlan.ParsePhotos(output));
                }
            }
        }
        finally
        {
            try { File.Delete(finalPathFile); } catch { }
        }
        return new Result(code, output, saved, covers, photos, videos, resolution);
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
        if (H("Video unavailable", "has been removed"))
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

public sealed class Settings
{
    public string? OutputDir { get; set; }
    public bool AudioOnly { get; set; }
    public int MaxHeight { get; set; }
    public bool SaveCover { get; set; }
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
