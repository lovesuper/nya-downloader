using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
public class BackgroundPackTests
{
    static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\backgrounds"));

    static double MeanDifference(Bitmap a, Bitmap b)
    {
        Assert.Equal(a.Size, b.Size);
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            }
        return sum / (a.Width * a.Height * 3);
    }

    [Fact]
    public void Every_gif_has_an_up_to_date_pack()
    {
        var gifs = Directory.GetFiles(Dir, "*.gif").Order().ToList();
        Assert.NotEmpty(gifs);
        if (Environment.GetEnvironmentVariable("NYA_REPACK") == "1")
            foreach (var gif in gifs)
                File.WriteAllBytes(Path.ChangeExtension(gif, ".nyabg"), BackgroundPack.Pack(File.ReadAllBytes(gif)));
        Assert.Equal(gifs.Select(g => Path.ChangeExtension(g, ".nyabg")), Directory.GetFiles(Dir, "*.nyabg").Order());
        foreach (var file in gifs)
        {
            var name = Path.GetFileName(file);
            var bytes = File.ReadAllBytes(file);
            var pack = BackgroundPack.Read(File.ReadAllBytes(Path.ChangeExtension(file, ".nyabg")));
            Assert.True(pack.Source == BackgroundPack.SourceOf(bytes), $"{name} changed: run the tests with NYA_REPACK=1 to pack it again");
            using var gif = Image.FromFile(file);
            int count = gif.GetFrameCount(FrameDimension.Time);
            Assert.Equal(count, pack.Count);
            Assert.Equal(BackgroundPack.GifDelays(gif, count), pack.Delays);
            using var first = pack.Frame(0);
            Assert.Equal(Math.Min(gif.Width, Backdrop.SharpWidth), first.Width);
        }
    }

    [Fact]
    public void Frames_look_like_the_gif_the_app_used_to_show()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, "bg2.gif"));
        var pack = BackgroundPack.Read(BackgroundPack.Pack(bytes));
        using var gif = Image.FromStream(new MemoryStream(bytes));
        var time = FrameDimension.Time;
        Assert.Equal(gif.GetFrameCount(time), pack.Count);
        foreach (var i in new[] { 0, pack.Count / 2, pack.Count - 1 })
        {
            gif.SelectActiveFrame(time, i);
            using var expected = Backdrop.Frame(gif, 0);
            using var image = pack.Frame(i);
            using var shown = Backdrop.Frame(image, 0);
            var difference = MeanDifference(expected, shown);
            Assert.True(difference < 4, $"frame {i} differs by {difference:0.0} per channel");
        }
        using var blurred = pack.Frame(0);
        Assert.Equal(Backdrop.FrameWidth(40), Backdrop.Frame(blurred, 40).Width);
    }

    [Fact]
    public void Broken_packs_are_rejected()
    {
        var good = BackgroundPack.Pack(File.ReadAllBytes(Path.Combine(Dir, "bg2.gif")));
        Assert.Throws<InvalidDataException>(() => BackgroundPack.Read(File.ReadAllBytes(Path.Combine(Dir, "bg2.gif"))));
        Assert.Throws<InvalidDataException>(() => BackgroundPack.Read(good[..(good.Length / 2)]));
        Assert.Throws<InvalidDataException>(() => BackgroundPack.Read(good[..10]));
        Assert.Throws<InvalidDataException>(() => BackgroundPack.Read([]));
    }
}
