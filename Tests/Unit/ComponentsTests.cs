using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace VideoDownloader.Tests.Unit;

sealed class LocalHttp : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly Dictionary<string, byte[]> _files;

    public LocalHttp(Dictionary<string, byte[]> files)
    {
        _files = files;
        _listener.Start();
        _ = ServeAsync();
    }

    public string Url(string name) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{name}";

    public void Add(string name, byte[] body) => _files[name] = body;

    public TimeSpan Delay { get; init; }

    async Task ServeAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var request = await reader.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var name = request.Split(' ') is [_, var path, ..] ? path.TrimStart('/') : "";
                    if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
                    var found = _files.TryGetValue(name, out var body);
                    var head = found ? $"HTTP/1.1 200 OK\r\nContent-Length: {body!.Length}\r\nConnection: close\r\n\r\n"
                                     : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    if (found) await stream.WriteAsync(body);
                }
            });
        }
    }

    public void Dispose() => _listener.Stop();
}

[Trait("Category", "Unit")]
public class ComponentsTests
{
    static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in entries)
                using (var w = new StreamWriter(zip.CreateEntry(e).Open())) w.Write("content of " + e);
        return ms.ToArray();
    }

    static async Task<List<string>> Install(TempDir dir, params Components.Item[] items)
    {
        var log = new List<string>();
        var progress = new List<double>();
        await Components.InstallAsync(items, (_, f) => progress.Add(f), log.Add, CancellationToken.None, dir.Path);
        Assert.Equal(1.0, progress[^1]);
        return log;
    }

    [Fact]
    public async Task A_plain_file_is_saved_under_its_own_name()
    {
        var exe = Encoding.ASCII.GetBytes("MZ fake yt-dlp");
        using var http = new LocalHttp(new() { ["yt-dlp.exe"] = exe });
        using var dir = new TempDir();
        await Install(dir, Components.YtDlpExe with { Url = http.Url("yt-dlp.exe") });
        Assert.Equal(["yt-dlp.exe"], dir.Files());
        Assert.Equal(exe, File.ReadAllBytes(Path.Combine(dir.Path, "yt-dlp.exe")));
    }

    [Fact]
    public async Task Ffmpeg_zip_gives_ffmpeg_ffprobe_and_their_dlls_only()
    {
        const string root = "ffmpeg-master-latest-win64-gpl-shared/";
        using var http = new LocalHttp(new()
        {
            ["ffmpeg.zip"] = Zip(root + "bin/ffmpeg.exe", root + "bin/ffprobe.exe", root + "bin/ffplay.exe",
                                 root + "bin/avcodec-62.dll", root + "bin/swscale-9.dll", root + "doc/ffmpeg.html", root + "LICENSE.txt"),
        });
        using var dir = new TempDir();
        var log = await Install(dir, Components.Ffmpeg with { Url = http.Url("ffmpeg.zip") });
        Assert.Equal(["avcodec-62.dll", "ffmpeg.exe", "ffprobe.exe", "swscale-9.dll"], dir.Files().Order());
        Assert.Contains("  extracted 4 files", log);
    }

    [Fact]
    public async Task Deno_zip_gives_deno_exe()
    {
        using var http = new LocalHttp(new() { ["deno.zip"] = Zip("deno.exe") });
        using var dir = new TempDir();
        await Install(dir, Components.Deno with { Url = http.Url("deno.zip") });
        Assert.Equal(["deno.exe"], dir.Files());
    }

    [Fact]
    public async Task An_archive_without_the_expected_files_is_an_error()
    {
        using var http = new LocalHttp(new() { ["deno.zip"] = Zip("README.md", "bin/other.exe") });
        using var dir = new TempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(dir, Components.Deno with { Url = http.Url("deno.zip") }));
        Assert.Empty(dir.Files());
    }

    [Fact]
    public async Task A_missing_file_is_an_error()
    {
        using var http = new LocalHttp(new());
        using var dir = new TempDir();
        await Assert.ThrowsAsync<HttpRequestException>(() => Install(dir, Components.YtDlpExe with { Url = http.Url("yt-dlp.exe") }));
        Assert.Empty(dir.Files());
    }
}

[Trait("Category", "Integration")]
public class ComponentLinksTests
{
    [Theory]
    [InlineData("yt-dlp")]
    [InlineData("ffmpeg")]
    [InlineData("Deno (for YouTube)")]
    public async Task Download_links_are_alive(string name)
    {
        var item = new[] { Components.YtDlpExe, Components.Ffmpeg, Components.Deno }.Single(i => i.Name == name);
        using var request = new HttpRequestMessage(HttpMethod.Head, item.Url);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await Components.Http.SendAsync(request, timeout.Token);
        Assert.True(response.IsSuccessStatusCode, $"{item.Url}: {(int)response.StatusCode}");
        var mb = (response.Content.Headers.ContentLength ?? 0) / 1024.0 / 1024.0;
        Assert.InRange(mb, item.ApproxMb * 0.4, item.ApproxMb * 2.5);
    }
}
