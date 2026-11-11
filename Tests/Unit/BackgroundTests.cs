using System.Drawing;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
public class BackdropTests
{
    static Bitmap Image(int w, int h, Func<int, int, Color> pixel)
    {
        var b = new Bitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b.SetPixel(x, y, pixel(x, y));
        return b;
    }

    static Bitmap Render(Bitmap source, int w, int h, BackgroundLook look, GlassPanel[]? panels = null)
    {
        using var frame = Backdrop.Frame(source, look.Blur);
        var target = new Bitmap(w, h, Backdrop.Format);
        using var canvas = new BackdropCanvas(iceInBackground: false);
        canvas.Render(target, frame, look, panels ?? [], 1f);
        return target;
    }

    static IEnumerable<Color> Pixels(Bitmap b)
    {
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                yield return b.GetPixel(x, y);
    }

    static int Gray(Color c) => (c.R + c.G + c.B) / 3;

    [Fact]
    public void Defaults_and_blur_scale()
    {
        var d = new BackgroundLook();
        Assert.Equal((0, 43, 0, 100, 100, 100, 80, 0), (d.Gif, d.Visibility, d.Blur, d.Scale, d.Glass, d.Highlight, d.Frost, d.Ice));
        Assert.Equal((240, 72, 12), (Backdrop.FrameWidth(0), Backdrop.FrameWidth(40), Backdrop.FrameWidth(100)));
        var widths = Enumerable.Range(0, 101).Select(Backdrop.FrameWidth).ToList();
        Assert.True(widths.Zip(widths.Skip(1)).All(p => p.First >= p.Second), "more blur must never mean a bigger frame");
    }

    [Theory]
    [InlineData(0, 255, 255)]
    [InlineData(50, 125, 130)]
    [InlineData(100, 0, 2)]
    public void Visibility_blends_the_gif_with_white(int visibility, int low, int high)
    {
        using var black = Image(60, 80, (_, _) => Color.Black);
        using var shown = Render(black, 200, 150, new BackgroundLook { Visibility = visibility, Glass = 0 });
        Assert.All(Pixels(shown), c => Assert.InRange(Gray(c), low, high));
    }

    [Fact]
    public void Fine_detail_is_blurred_away()
    {
        using var stripes = Image(480, 480, (x, y) => (x + y / 3) % 2 == 0 ? Color.Black : Color.White);
        using var shown = Render(stripes, 780, 660, new BackgroundLook { Visibility = 50, Blur = 40, Glass = 0 });
        var values = Pixels(shown).Select(c => (int)c.G).ToList();
        Assert.True(values.Max() - values.Min() <= 4, $"stripes still show: {values.Min()}..{values.Max()}");
        Assert.InRange(values.Average(), 186, 196);
    }

    [Theory]
    [InlineData(0, 40, 255)]
    [InlineData(40, 0, 16)]
    [InlineData(100, 0, 6)]
    public void Blur_decides_how_sharp_shapes_are(int blur, int steepestMin, int steepestMax)
    {
        using var halves = Image(480, 480, (x, _) => x < 240 ? Color.Black : Color.White);
        using var shown = Render(halves, 800, 800, new BackgroundLook { Visibility = 100, Blur = blur, Glass = 0 });
        var row = Enumerable.Range(0, 800).Select(x => (int)shown.GetPixel(x, 400).G).ToList();
        Assert.True(row.Max() - row.Min() > 240, "the edge itself must survive");
        var steepest = row.Zip(row.Skip(1), (a, b) => Math.Abs(a - b)).Max();
        Assert.InRange(steepest, steepestMin, steepestMax);
    }

    [Fact]
    public void Every_background_gif_animates_and_shows()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\backgrounds"));
        var gifs = Directory.GetFiles(dir, "*.gif").Order().ToList();
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"bg{i}.gif"), gifs.Select(Path.GetFileName));
        Assert.Equal(gifs.Count, gifs.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))).Distinct().Count());
        foreach (var file in gifs)
        {
            using var gif = System.Drawing.Image.FromFile(file);
            var time = System.Drawing.Imaging.FrameDimension.Time;
            Assert.True(gif.GetFrameCount(time) > 10, $"{Path.GetFileName(file)} is not animated");
            gif.SelectActiveFrame(time, gif.GetFrameCount(time) / 2);
            using var frame = Backdrop.Frame(gif, 40);
            Assert.Equal(72, frame.Width);
            using var shown = new Bitmap(300, 300, Backdrop.Format);
            using (var g = Graphics.FromImage(shown)) Backdrop.Paint(g, frame, shown.Size, 50);
            var values = Pixels(shown).SelectMany(c => new[] { (int)c.R, c.G, c.B }).ToList();
            Assert.True(values.Min() >= 125, $"{Path.GetFileName(file)} shows through more than 50%: {values.Min()}");
            Assert.True(values.Max() - values.Min() >= 20, $"{Path.GetFileName(file)} is hardly visible: {values.Min()}..{values.Max()}");
        }
    }

    static readonly GlassPanel[] Panel = [new(Rectangle.FromLTRB(50, 50, 350, 250), 20)];

    [Fact]
    public void Glass_bends_the_background_at_its_rim_and_leaves_the_rest_alone()
    {
        using var ramp = Image(400, 300, (x, _) => Color.FromArgb(x * 255 / 399, x * 255 / 399, x * 255 / 399));
        var plainLook = new BackgroundLook { Visibility = 100, Blur = 0, Glass = 0, Highlight = 0, Frost = 0 };
        using var plain = Render(ramp, 400, 300, plainLook, Panel);
        using var glass = Render(ramp, 400, 300, new BackgroundLook { Visibility = 100, Blur = 0, Glass = 100, Highlight = 0, Frost = 0 }, Panel);
        foreach (var (x, y) in new[] { (10, 150), (395, 150), (200, 10), (200, 290), (46, 46), (200, 150), (120, 100), (280, 200) })
            Assert.True(Math.Abs(Gray(plain.GetPixel(x, y)) - Gray(glass.GetPixel(x, y))) <= 1, $"({x},{y}) changed but is outside the rim");
        Assert.True(Gray(glass.GetPixel(53, 150)) < Gray(plain.GetPixel(53, 150)) - 5, "the left rim should show what is further left");
        Assert.True(Gray(glass.GetPixel(346, 150)) > Gray(plain.GetPixel(346, 150)) + 5, "the right rim should show what is further right");
    }

    [Fact]
    public void Highlight_lights_the_top_left_rim_more_than_the_bottom_right()
    {
        using var gray = Image(400, 300, (_, _) => Color.FromArgb(100, 100, 100));
        using var lit = Render(gray, 400, 300, new BackgroundLook { Visibility = 100, Blur = 0, Glass = 50, Highlight = 100, Frost = 0 }, Panel);
        int topLeft = Enumerable.Range(48, 6).Max(y => Gray(lit.GetPixel(150, y)));
        int bottomRight = Enumerable.Range(246, 6).Max(y => Gray(lit.GetPixel(300, y)));
        Assert.True(topLeft > 160, $"the top rim is not lit: {topLeft}");
        Assert.True(topLeft > bottomRight + 40, $"light should come from the top left: {topLeft} vs {bottomRight}");
        Assert.InRange(Gray(lit.GetPixel(200, 150)), 98, 102);
    }

    [Fact]
    public void Frost_whitens_inside_the_glass_only()
    {
        using var black = Image(400, 300, (_, _) => Color.Black);
        using var frosted = Render(black, 400, 300, new BackgroundLook { Visibility = 100, Blur = 0, Glass = 30, Highlight = 0, Frost = 100 }, Panel);
        Assert.InRange(Gray(frosted.GetPixel(200, 150)), 170, 190);
        Assert.InRange(Gray(frosted.GetPixel(20, 150)), 0, 2);
    }

    [Fact]
    public void Zero_glass_draws_no_glass()
    {
        using var ramp = Image(400, 300, (x, y) => Color.FromArgb(x * 255 / 399, y * 255 / 299, 128));
        var look = new BackgroundLook { Glass = 0, Highlight = 100, Frost = 100 };
        using var withPanels = Render(ramp, 400, 300, look, Panel);
        using var without = Render(ramp, 400, 300, look);
        Assert.Equal(Pixels(without).Select(c => c.ToArgb()), Pixels(withPanels).Select(c => c.ToArgb()));
    }

    [Fact]
    public void Scale_zooms_the_gif_from_the_window_size()
    {
        var cover = Backdrop.Placement(new Size(100, 50), new Size(400, 400), 100);
        Assert.Equal((-200, 0), (cover.X, cover.Y));
        Assert.True(cover.Width >= 800 && cover.Height >= 400, $"{cover}");
        var zoomed = Backdrop.Placement(new Size(100, 50), new Size(400, 400), 200);
        Assert.Equal((-600, 0), (zoomed.X, zoomed.Y));
        Assert.InRange(zoomed.Width, 1600, 1602);
        var small = Backdrop.Placement(new Size(100, 50), new Size(400, 400), 50);
        Assert.Equal((0, 100), (small.X, small.Y));
        Assert.InRange(small.Width, 400, 402);
        Assert.InRange(small.Height, 200, 202);

        using var black = Image(100, 50, (_, _) => Color.Black);
        using var shrunk = Render(black, 400, 400, new BackgroundLook { Visibility = 100, Scale = 50, Glass = 0 });
        Assert.Equal(255, Gray(shrunk.GetPixel(200, 20)));
        Assert.Equal(0, Gray(shrunk.GetPixel(200, 200)));
        Assert.Equal(255, Gray(shrunk.GetPixel(200, 380)));
    }

    static readonly Lazy<IceGeometry> Sheet = new(() => IceGeometry.Build(new Size(600, 400), 1));

    [Fact]
    public void Ice_breaks_into_chunks_split_by_thin_cracks()
    {
        var g = Sheet.Value;
        double cracked = g.Crack.Count(v => v > 128) / (double)g.Crack.Length;
        Assert.InRange(cracked, 0.02, 0.25);
        Assert.True(g.Cell.Distinct().Count() >= 6, $"only {g.Cell.Distinct().Count()} chunks");
        var again = IceGeometry.Build(new Size(600, 400), 1);
        Assert.Equal(g.Crack, again.Crack);
        Assert.Equal(g.Cell, again.Cell);
    }

    [Fact]
    public void Cracks_reach_down_into_the_ice_like_parallax_layers()
    {
        var g = Sheet.Value;
        int w = g.Size.Width, h = g.Size.Height;
        double below = 0, above = 0;
        int count = 0;
        for (int y = 20; y < h - 20; y++)
            for (int x = 20; x < w - 20; x++)
                if (g.Crack[y * w + x] > 200 && g.Crack[(y + 8) * w + x + 4] < 60 && g.Crack[(y - 8) * w + x - 4] < 60)
                {
                    below += g.Deep[(y + 8) * w + x + 4];
                    above += g.Deep[(y - 8) * w + x - 4];
                    count++;
                }
        Assert.True(count > 100, $"{count} samples");
        Assert.True(below / count > above / count + 40, $"cracks should sink down-right: {below / count:0} vs {above / count:0}");
    }

    [Fact]
    public void Each_chunk_bends_the_background_its_own_way()
    {
        using var layer = IceLayer.Compose(Sheet.Value, 100, 1);
        var shifts = Sheet.Value.Cell.Select((c, p) => (c, layer.Offset[p])).Where(t => t.Item2 != 0).Select(t => t.Item2).Distinct().Count();
        Assert.True(shifts >= 5, $"only {shifts} different shifts");
        using var none = IceLayer.Compose(Sheet.Value, 0, 1);
        Assert.All(none.Offset, o => Assert.Equal(0, o));
    }

    [Fact]
    public void More_ice_covers_more_and_zero_ice_changes_nothing()
    {
        using var gray = Image(600, 400, (_, _) => Color.FromArgb(90, 90, 90));
        var plain = new BackgroundLook { Visibility = 100, Blur = 0, Glass = 0, Ice = 0 };
        using var none = Render(gray, 600, 400, plain);
        Assert.All(Pixels(none), c => Assert.InRange(Gray(c), 89, 91));
        double Mean(Bitmap b) => Pixels(b).Average(Gray);
        using var light = Render(gray, 600, 400, new BackgroundLook { Visibility = 100, Blur = 0, Glass = 0, Ice = 40 });
        using var heavy = Render(gray, 600, 400, new BackgroundLook { Visibility = 100, Blur = 0, Glass = 0, Ice = 100 });
        Assert.True(Mean(light) > 110, $"ice 40 hardly shows: {Mean(light):0}");
        Assert.True(Mean(heavy) > Mean(light) + 20, $"more ice should cover more: {Mean(heavy):0} vs {Mean(light):0}");
        Assert.True(Pixels(heavy).Count(c => Gray(c) > 235) > 2000, "no white cracks");
    }

    [Fact]
    public void Ice_is_built_in_the_background_and_announced()
    {
        using var gray = Image(300, 200, (_, _) => Color.FromArgb(90, 90, 90));
        using var frame = Backdrop.Frame(gray, 0);
        using var target = new Bitmap(300, 200, Backdrop.Format);
        using var canvas = new BackdropCanvas();
        using var ready = new ManualResetEventSlim(false);
        canvas.IceReady += ready.Set;
        var look = new BackgroundLook { Visibility = 100, Blur = 0, Glass = 0, Ice = 80 };
        canvas.Render(target, frame, look, [], 1);
        Assert.InRange(Gray(target.GetPixel(150, 100)), 89, 91);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "the ice was never built");
        canvas.Render(target, frame, look, [], 1);
        Assert.True(Pixels(target).Average(Gray) > 110, "the ice did not appear after it was built");
    }

    [Fact]
    public void Look_is_saved_with_the_settings_and_checked_on_load()
    {
        var s = new Settings { Look = new BackgroundLook { Gif = 3, Visibility = 70, Blur = 10, Scale = 180, Glass = 0, Highlight = 5, Frost = 99, Ice = 40 } };
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.Equal("gif 3, visibility 70%, blur 10, scale 180%, glass 0, highlight 5%, frost 99%, ice 40", back.Look.ToString());
        Assert.Equal("gif random, visibility 43%, blur 0, scale 100%, glass 100, highlight 100%, frost 80%, ice 0",
                     JsonSerializer.Deserialize<Settings>("{\"OutputDir\": \"x\"}")!.Look.ToString());
        Assert.Equal("gif 6, visibility 43%, blur 0, scale 100%, glass 100, highlight 100%, frost 80%, ice 0",
                     JsonSerializer.Deserialize<Settings>("{\"Look\": {\"Gif\": 6, \"Visibility\": 43, \"Blur\": 0, \"Glass\": 100, \"Highlight\": 100, \"Frost\": 80}}")!.Look.ToString());
        var wild = new BackgroundLook { Gif = 12, Visibility = 150, Blur = -5, Scale = 900, Glass = 101, Highlight = -1, Frost = 1000, Ice = -4 }.Checked(8);
        Assert.Equal("gif 8, visibility 100%, blur 0, scale 250%, glass 100, highlight 0%, frost 100%, ice 0", wild.ToString());
        Assert.Equal(50, new BackgroundLook { Scale = 0 }.Checked(8).Scale);
        Assert.Equal(0, new BackgroundLook { Gif = -3 }.Checked(8).Gif);
        Assert.Equal(0, new BackgroundLook { Gif = 2 }.Checked(0).Gif);
    }
}

[Trait("Category", "Unit")]
[Collection(nameof(SettingsEnvironment))]
public class LookDialogTests
{
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    static void Click(Control button) =>
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);

    [Fact]
    public void Sliders_show_the_look_and_report_every_change()
    {
        var (start, labels, changes, reset) = MainFormLayoutTests.OnSta(() =>
        {
            using var d = new LookDialog(new BackgroundLook { Gif = 2, Visibility = 70, Blur = 10, Glass = 0, Highlight = 5, Frost = 99 }, 8);
            Control C(string name) => All(d).Single(c => c.Name == name);
            var changes = new List<string>();
            d.Changed += l => changes.Add(l.ToString());
            var start = (((ComboBox)C("lookGif")).SelectedItem!.ToString(), ((TrackBar)C("lookVisibility")).Value, ((TrackBar)C("lookFrost")).Value,
                         ((TrackBar)C("lookScale")).Minimum, ((TrackBar)C("lookScale")).Maximum, ((TrackBar)C("lookIce")).Minimum, ((TrackBar)C("lookIce")).Maximum);
            var labels = string.Join(" ", new[] { "lookVisibilityValue", "lookBlurValue", "lookGlassValue", "lookHighlightValue", "lookFrostValue" }.Select(n => C(n).Text));
            ((TrackBar)C("lookGlass")).Value = 80;
            ((ComboBox)C("lookGif")).SelectedIndex = 0;
            Click(C("lookReset"));
            var reset = (d.Look.ToString(), C("lookGlassValue").Text);
            return (start, labels, changes, reset);
        });
        Assert.Equal(("2", 70, 99, 50, 250, 0, 100), start);
        Assert.Equal("70% 10 0 5% 99%", labels);
        Assert.Equal(
            ["gif 2, visibility 70%, blur 10, scale 100%, glass 80, highlight 5%, frost 99%, ice 0",
             "gif random, visibility 70%, blur 10, scale 100%, glass 80, highlight 5%, frost 99%, ice 0",
             "gif random, visibility 43%, blur 0, scale 100%, glass 100, highlight 100%, frost 80%, ice 0"],
            changes);
        Assert.Equal(("gif random, visibility 43%, blur 0, scale 100%, glass 100, highlight 100%, frost 80%, ice 0", "100"), reset);
    }

    [Fact]
    public void Ctrl_P_opens_the_background_settings_once_and_changes_apply_live()
    {
        var (first, same, look) = MainFormLayoutTests.OnSta(() =>
        {
            using var dir = new TempDir();
            Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", dir.Path);
            try
            {
                using var form = new MainForm();
                var key = typeof(MainForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var msg = new Message();
                Assert.Null(form.LookWindow);
                Assert.False((bool)key.Invoke(form, [msg, Keys.P])!);
                Assert.Null(form.LookWindow);
                Assert.True((bool)key.Invoke(form, [msg, Keys.Control | Keys.P])!);
                var first = form.LookWindow;
                key.Invoke(form, [msg, Keys.Control | Keys.P]);
                var same = ReferenceEquals(first, form.LookWindow);
                ((TrackBar)All(first!).Single(c => c.Name == "lookVisibility")).Value = 33;
                var settings = (Settings)typeof(MainForm).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var look = settings.Look.ToString();
                first!.Dispose();
                return (first != null && !first.Visible, same, look);
            }
            finally { Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", null); }
        });
        Assert.True(first, "Ctrl+P should create the settings window (hidden while the main window is)");
        Assert.True(same, "a second Ctrl+P must not open another window");
        Assert.StartsWith("gif random, visibility 33%", look);
    }
}
