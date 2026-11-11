using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Size = System.Drawing.Size;

namespace VideoDownloader;

// The animated background, drawn with Skia the same way the Windows build draws it with GDI+ (Controls.cs).
// The geometry and pixel math is shared in Glass.cs. All bitmaps are premultiplied BGRA, as on Windows.

static partial class Backdrop
{
    static readonly SKImageInfo Bgra = new(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);

    public static SKBitmap NewBitmap(int w, int h) => new(Bgra.WithSize(w, h));

    public static SKBitmap Frame(SKBitmap gif, int blur)
    {
        int width = FrameWidth(blur);
        int midW = Math.Min(gif.Width, width * 4), midH = Math.Max(1, (int)Math.Round((double)gif.Height * midW / gif.Width));
        int w = Math.Min(midW, width), h = Math.Max(1, (int)Math.Round((double)gif.Height * w / gif.Width));
        using var mid = Scaled(gif, midW, midH);
        var bmp = Scaled(mid, w, h);
        if (blur > 0) Soften(bmp);
        return bmp;
    }

    static SKBitmap Scaled(SKBitmap source, int w, int h)
    {
        var bmp = NewBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        canvas.DrawBitmap(source, new SKRect(0, 0, w, h), paint);
        return bmp;
    }

    static void Soften(SKBitmap bmp)
    {
        var pixels = new byte[bmp.RowBytes * bmp.Height];
        Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        SoftenPixels(pixels, bmp.Width, bmp.Height, bmp.RowBytes);
        Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        bmp.NotifyPixelsChanged();
    }

    static Size SizeOf(SKBitmap b) => new(b.Width, b.Height);

    public static void Paint(SKCanvas c, SKBitmap frame, Size size, int visibility, int scalePercent = 100) =>
        Paint(c, frame, Placement(SizeOf(frame), size, scalePercent), size, visibility);

    static void Paint(SKCanvas c, SKBitmap frame, Rectangle dest, Size size, int visibility)
    {
        if (dest.Left > 0 || dest.Top > 0 || dest.Right < size.Width || dest.Bottom < size.Height)
        {
            using var paper = new SKPaint { Color = SKColors.White };
            c.DrawRect(0, 0, size.Width, size.Height, paper);
        }
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Low })
            c.DrawBitmap(frame, new SKRect(dest.Left, dest.Top, dest.Right, dest.Bottom), paint);
        int veil = (int)Math.Round(255 * (100 - Math.Clamp(visibility, 0, 100)) / 100.0);
        if (veil == 0) return;
        using var white = new SKPaint { Color = new SKColor(255, 255, 255, (byte)veil) };
        c.DrawRect(0, 0, size.Width, size.Height, white);
    }

    public static SKBitmap Frosted(SKBitmap frame)
    {
        int w = Math.Clamp(FrostedWidth, 2, Math.Max(2, frame.Width));
        int h = Math.Max(2, (int)Math.Round((double)frame.Height * w / frame.Width));
        var bmp = Scaled(frame, w, h);
        Soften(bmp);
        return bmp;
    }

    public static void Back(SKCanvas c, SKBitmap frame, Size size, BackgroundLook look, IReadOnlyList<Backing> backings, float scale)
    {
        if (look.Backing <= 0 || backings.Count == 0) return;
        using var area = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var b in backings) area.AddRoundRect(RoundedRect(b.Bounds, b.Radius));
        c.Save();
        using (var frosted = Frosted(frame))
        {
            c.ClipPath(area, antialias: true);
            Paint(c, frosted, Placement(SizeOf(frame), size, look.Scale), size, look.Visibility);
        }
        c.Restore();
        using var milk = new SKPaint { Color = new SKColor(255, 255, 255, (byte)MilkAlpha(look)), IsAntialias = true };
        foreach (var b in backings)
        {
            c.DrawRoundRect(RoundedRect(b.Bounds, b.Radius), milk);
            if (b.Card) Decorate(c, new GlassPanel(b.Bounds, b.Radius), look, scale, frost: false);
        }
    }

    public static SKRoundRect RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        return new SKRoundRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), Math.Max(0, d / 2));
    }

    // GDI+'s ForwardDiagonal gradient: the top-left corner gets the first color, the bottom-right one the last,
    // and the other two corners are halfway.
    static SKShader Diagonal(Rectangle r, SKColor from, SKColor to)
    {
        float w = r.Width, h = r.Height, k = w * w + h * h > 0 ? 2 * w * h / (w * w + h * h) : 0;
        return SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Left + h * k, r.Top + w * k),
                                             [from, to], SKShaderTileMode.Clamp);
    }

    static SKColor White(int alpha) => new(255, 255, 255, (byte)Math.Clamp(alpha, 0, 255));

    public static void Decorate(SKCanvas c, GlassPanel panel, BackgroundLook look, float scale, bool frost = true)
    {
        RectangleF r = panel.Bounds;
        r.Inflate(-0.5f, -0.5f);
        var path = RoundedRect(r, panel.Radius);
        if (frost && look.Frost > 0)
        {
            using var milk = new SKPaint { Color = White((int)Math.Round(look.Frost * 1.8)), IsAntialias = true };
            c.DrawRoundRect(path, milk);
        }
        if (look.Highlight > 0)
        {
            int a = (int)Math.Round(look.Highlight * 2.4);
            var box = Rectangle.Inflate(panel.Bounds, 1, 1);
            using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 1.6f * scale),
                                           Shader = Diagonal(box, White(a), White(a / 6)) })
                c.DrawRoundRect(path, rim);
            var inner = RectangleF.Inflate(r, -2.5f * scale, -2.5f * scale);
            using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, scale),
                                           Shader = Diagonal(box, White(a / 8), White(a / 2)) };
            c.DrawRoundRect(RoundedRect(inner, Math.Max(1, panel.Radius - 2.5f * scale)), glow);
        }
        var outer = RectangleF.Inflate(r, 1f, 1f);
        using var shade = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
                                        Color = new SKColor(0, 0, 0, (byte)Math.Round(look.Glass * 0.5)) };
        c.DrawRoundRect(RoundedRect(outer, panel.Radius + 1), shade);
    }
}

sealed class IceLayer : IDisposable
{
    public SKBitmap Overlay { get; private init; } = null!;
    public int[] Offset { get; private init; } = [];
    public bool Shifts { get; private init; }
    public string Key { get; private init; } = "";

    public static string KeyFor(IceGeometry geometry, int ice) => geometry.Key + "|" + ice;

    public static IceLayer Compose(IceGeometry geo, int ice, float scale)
    {
        int w = geo.Size.Width, h = geo.Size.Height;
        var (pixels, offset) = IcePixels.Compose(geo, ice, scale);
        var overlay = Backdrop.NewBitmap(w, h);
        var address = overlay.GetPixels();
        for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w, address + y * overlay.RowBytes, w);
        overlay.NotifyPixelsChanged();
        return new IceLayer { Overlay = overlay, Offset = offset, Shifts = offset.Any(o => o != 0), Key = KeyFor(geo, ice) };
    }

    public void Dispose() => Overlay.Dispose();
}

sealed class BackdropCanvas : IDisposable
{
    SKBitmap? _plain;
    int[] _src = [], _dst = [];
    GlassMap? _map;
    IceLayer? _ice;
    IceGeometry? _geometry;
    string? _building;
    readonly object _gate = new();

    public event Action? IceReady;

    IceGeometry? Geometry(Size size, float scale)
    {
        var key = IceGeometry.KeyFor(size, scale);
        lock (_gate)
        {
            if (_geometry?.Key == key) return _geometry;
            if (_building == key) return null;
            _building = key;
        }
        // Building the ice takes a while at Retina sizes, so the background is drawn without it until it's ready.
        Task.Run(() => IceGeometry.Build(size, scale)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            lock (_gate)
            {
                if (_building != key) return;
                (_geometry, _building) = (t.Result, null);
            }
            IceReady?.Invoke();
        });
        return null;
    }

    // Draws into target, a premultiplied BGRA bitmap (here: the pixels of a locked WriteableBitmap).
    public unsafe void Render(SKBitmap target, SKBitmap frame, BackgroundLook look, IReadOnlyList<GlassPanel> panels, float scale,
                              IReadOnlyList<Backing>? backings = null)
    {
        var size = new Size(target.Width, target.Height);
        bool glass = look.Glass > 0 && panels.Count > 0;
        var geometry = look.Ice > 0 ? Geometry(size, scale) : null;
        bool ice = geometry != null;
        if (!glass && !ice)
        {
            using var g = new SKCanvas(target);
            Backdrop.Paint(g, frame, size, look.Visibility, look.Scale);
            Backdrop.Back(g, frame, size, look, backings ?? [], scale);
            return;
        }
        int w = size.Width, h = size.Height, n = w * h;
        if (_plain == null || _plain.Width != w || _plain.Height != h)
        {
            _plain?.Dispose();
            _plain = Backdrop.NewBitmap(w, h);
            _src = new int[n];
            _dst = new int[n];
        }
        using (var g = new SKCanvas(_plain)) Backdrop.Paint(g, frame, size, look.Visibility, look.Scale);
        var plain = _plain.GetPixels();
        for (int y = 0; y < h; y++) Marshal.Copy(plain + y * _plain.RowBytes, _src, y * w, w);
        Array.Copy(_src, _dst, n);
        if (glass)
        {
            var key = GlassMap.KeyFor(size, panels, look.Glass, scale);
            if (_map?.Key != key) _map = GlassMap.Build(size, panels, look.Glass, scale);
            var (dst, src) = (_map.Dst, _map.Src);
            for (int i = 0; i < dst.Length; i++) _dst[dst[i]] = _src[src[i]];
        }
        if (ice)
        {
            var key = IceLayer.KeyFor(geometry!, look.Ice);
            if (_ice?.Key != key)
            {
                _ice?.Dispose();
                _ice = IceLayer.Compose(geometry!, look.Ice, scale);
            }
        }
        var address = (byte*)target.GetPixels();
        if (ice && _ice!.Shifts)
        {
            var offset = _ice.Offset;
            for (int y = 0; y < h; y++)
            {
                var row = (int*)(address + y * target.RowBytes);
                for (int x = 0, p = y * w; x < w; x++, p++) row[x] = _dst[p + offset[p]];
            }
        }
        else
            for (int y = 0; y < h; y++) Marshal.Copy(_dst, y * w, (IntPtr)(address + y * target.RowBytes), w);
        target.NotifyPixelsChanged();

        using var tg = new SKCanvas(target);
        if (glass) foreach (var p in panels) Backdrop.Decorate(tg, p, look, scale);
        if (ice) tg.DrawBitmap(_ice!.Overlay, 0, 0);
        Backdrop.Back(tg, frame, size, look, backings ?? [], scale);
    }

    public void Dispose()
    {
        _plain?.Dispose();
        _ice?.Dispose();
    }
}

// Reads the backgrounds/*.nyabg packs made by the Windows build's BackgroundPack.Pack: JPEG frames with their delays.
sealed class BackgroundPack
{
    static readonly byte[] Magic = "NYAB"u8.ToArray();
    const byte FormatVersion = 1;

    readonly byte[] _data;
    readonly (int Offset, int Length)[] _frames;

    BackgroundPack(byte[] data, (int, int)[] frames, int[] delays) => (_data, _frames, Delays) = (data, frames, delays);

    public int Count => _frames.Length;
    public int[] Delays { get; }

    public SKBitmap Frame(int index)
    {
        var (offset, length) = _frames[index];
        return SKBitmap.Decode(_data.AsSpan(offset, length)) ?? throw new InvalidDataException($"frame {index} can't be decoded");
    }

    public static BackgroundPack Read(byte[] data)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false));
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadByte() != FormatVersion)
                throw new InvalidDataException("not a background pack");
            reader.ReadBytes(8);
            int count = reader.ReadUInt16();
            if (count == 0) throw new InvalidDataException("the pack has no frames");
            var frames = new (int, int)[count];
            var delays = new int[count];
            for (int i = 0; i < count; i++)
            {
                delays[i] = reader.ReadInt32();
                int length = reader.ReadInt32();
                int offset = (int)reader.BaseStream.Position;
                if (length <= 0 || offset + (long)length > data.Length) throw new InvalidDataException($"frame {i} is cut off");
                frames[i] = (offset, length);
                reader.BaseStream.Position = offset + length;
            }
            return new BackgroundPack(data, frames, delays);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("the pack is cut off", e);
        }
    }
}

// Shows the composed background bitmap under the window's controls.
sealed class BackgroundView : Control
{
    public Bitmap? Image { get; set; }

    public BackgroundView() => IsHitTestVisible = false;

    public override void Render(DrawingContext context)
    {
        var rect = new Avalonia.Rect(Bounds.Size);
        // The bitmap has the window's size in device pixels (and 96 dpi, so its Size is its pixel size).
        if (Image is { } image) context.DrawImage(image, new Avalonia.Rect(image.Size), rect);
        else context.FillRectangle(Brushes.White, rect);
    }
}
