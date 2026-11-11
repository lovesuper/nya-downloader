using System.Drawing;

namespace VideoDownloader;

// The background look that doesn't depend on a drawing library: shared by the Windows (GDI+) and macOS (Skia) builds.

sealed record GlassPanel(Rectangle Bounds, int Radius);

sealed record Backing(Rectangle Bounds, int Radius, bool Card = false);

static partial class Backdrop
{
    public const int SharpWidth = 240, BlurryWidth = 12, FrostedWidth = 24;

    public static int FrameWidth(int blur) =>
        (int)Math.Round(SharpWidth * Math.Pow((double)BlurryWidth / SharpWidth, Math.Clamp(blur, 0, 100) / 100.0));

    public static Rectangle Placement(Size frame, Size size, int scalePercent)
    {
        float k = Math.Max((float)size.Width / frame.Width, (float)size.Height / frame.Height) *
                  Math.Clamp(scalePercent, BackgroundLook.MinScale, BackgroundLook.MaxScale) / 100f;
        float w = frame.Width * k, h = frame.Height * k;
        int top = h >= size.Height ? 0 : (int)Math.Floor((size.Height - h) / 2);
        return new Rectangle((int)Math.Floor((size.Width - w) / 2), top, (int)Math.Ceiling(w) + 1, (int)Math.Ceiling(h) + 1);
    }

    public static int BackingAlpha(int backing) => (int)Math.Round(Math.Clamp(backing, 0, 100) * 1.8);

    public static int MilkAlpha(BackgroundLook look)
    {
        double frost = Math.Clamp(look.Frost, 0, 100) * 1.8 / 255, backing = BackingAlpha(look.Backing) / 255.0;
        return (int)Math.Round(255 * (1 - (1 - frost) * (1 - backing)));
    }

    // A 5-tap binomial blur over 32-bit pixels, run horizontally and then vertically.
    public static void SoftenPixels(byte[] pixels, int w, int h, int stride)
    {
        var pass = new byte[pixels.Length];
        int[] kernel = [1, 4, 6, 4, 1];
        void Blur(byte[] from, byte[] to, int dx, int dy)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int i = -2; i <= 2; i++)
                            sum += from[Math.Clamp(y + i * dy, 0, h - 1) * stride + Math.Clamp(x + i * dx, 0, w - 1) * 4 + c] * kernel[i + 2];
                        to[y * stride + x * 4 + c] = (byte)((sum + 8) / 16);
                    }
        }
        Blur(pixels, pass, 1, 0);
        Blur(pass, pixels, 0, 1);
    }
}

sealed class GlassMap
{
    public int[] Dst { get; private init; } = [];
    public int[] Src { get; private init; } = [];
    public string Key { get; private init; } = "";

    public static string KeyFor(Size size, IReadOnlyList<GlassPanel> panels, int glass, float scale) =>
        $"{size}|{glass}|{scale}|" + string.Join(";", panels.Select(p => $"{p.Bounds}:{p.Radius}"));

    public static GlassMap Build(Size size, IReadOnlyList<GlassPanel> panels, int glass, float scale)
    {
        float shift = glass / 100f * 18 * scale, band = 24 * scale;
        var dst = new List<int>();
        var src = new List<int>();
        foreach (var p in panels)
        {
            var b = p.Bounds;
            float r = p.Radius, cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f, hx = b.Width / 2f, hy = b.Height / 2f;
            for (int y = Math.Max(0, b.Top); y < Math.Min(size.Height, b.Bottom); y++)
                for (int x = Math.Max(0, b.Left); x < Math.Min(size.Width, b.Right); x++)
                {
                    float px = x + 0.5f - cx, py = y + 0.5f - cy;
                    float qx = Math.Abs(px) - (hx - r), qy = Math.Abs(py) - (hy - r);
                    float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                    float d = MathF.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
                    if (d >= 0 || d <= -band) continue;
                    float t = 1 + d / band, s = shift * t * t;
                    float nx, ny;
                    if (qx > 0 && qy > 0)
                    {
                        float len = MathF.Sqrt(qx * qx + qy * qy);
                        (nx, ny) = (qx / len, qy / len);
                    }
                    else if (qx > qy) (nx, ny) = (1, 0);
                    else (nx, ny) = (0, 1);
                    nx *= px < 0 ? -1 : 1;
                    ny *= py < 0 ? -1 : 1;
                    int sx = Math.Clamp((int)MathF.Round(x + nx * s), 0, size.Width - 1);
                    int sy = Math.Clamp((int)MathF.Round(y + ny * s), 0, size.Height - 1);
                    if (sx == x && sy == y) continue;
                    dst.Add(y * size.Width + x);
                    src.Add(sy * size.Width + sx);
                }
        }
        return new GlassMap { Dst = dst.ToArray(), Src = src.ToArray(), Key = KeyFor(size, panels, glass, scale) };
    }
}

sealed class IceGeometry
{
    public Size Size { get; private init; }
    public byte[] Crack { get; private init; } = [];
    public byte[] Deep { get; private init; } = [];
    public byte[] Cloud { get; private init; } = [];
    public sbyte[] Rim { get; private init; } = [];
    public ushort[] Cell { get; private init; } = [];
    public float[] CellTint { get; private init; } = [];
    public PointF[] CellShift { get; private init; } = [];
    public string Key { get; private init; } = "";

    public const int DepthSteps = 16;

    public static string KeyFor(Size size, float scale) => $"{size}|{scale}";

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 982451653);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    static float Noise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi, u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    static float Fbm(float x, float y, int seed)
    {
        float sum = 0, amp = 0.5f;
        for (int i = 0; i < 4; i++, x *= 2, y *= 2, amp *= 0.5f) sum += amp * Noise(x, y, seed + i);
        return sum / 0.9375f;
    }

    static float Smooth(float from, float to, float v)
    {
        float t = Math.Clamp((v - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static (float D1, float D2, int I, int J, float Sx, float Sy) Nearest(float x, float y, float size, int salt)
    {
        int ci = (int)MathF.Floor(x / size), cj = (int)MathF.Floor(y / size);
        float d1 = float.MaxValue, d2 = float.MaxValue, bx = 0, by = 0;
        int bi = 0, bj = 0;
        for (int j = cj - 1; j <= cj + 1; j++)
            for (int i = ci - 1; i <= ci + 1; i++)
            {
                float sx = (i + 0.12f + 0.76f * Hash(i, j, salt)) * size, sy = (j + 0.12f + 0.76f * Hash(i, j, salt + 1)) * size;
                float d = MathF.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < d1) { d2 = d1; d1 = d; (bi, bj, bx, by) = (i, j, sx, sy); }
                else if (d < d2) d2 = d;
            }
        return (d1, d2, bi, bj, bx, by);
    }

    public static IceGeometry Build(Size size, float scale)
    {
        int w = size.Width, h = size.Height, n = w * h;
        float coarse = 140 * scale, fine = 46 * scale, rimWidth = 8 * scale;
        int cols = (int)(w / coarse) + 4, rows = (int)(h / coarse) + 4;
        var tint = new float[cols * rows];
        var shift = new PointF[cols * rows];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                float angle = Hash(i - 1, j - 1, 103) * MathF.Tau, magnitude = 0.4f + 0.6f * Hash(i - 1, j - 1, 107);
                tint[j * cols + i] = Hash(i - 1, j - 1, 101);
                shift[j * cols + i] = new PointF(MathF.Cos(angle) * magnitude, MathF.Sin(angle) * magnitude);
            }

        const int step = 4;
        int gw = w / step + 2, gh = h / step + 2;
        var warpX = new float[gw * gh];
        var warpY = new float[gw * gh];
        var fineMask = new float[gw * gh];
        var cloudGrid = new float[gw * gh];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                float x = gx * step, y = gy * step;
                int k = gy * gw + gx;
                warpX[k] = (Fbm(x / (55 * scale), y / (55 * scale), 3) - 0.5f) * 22 * scale;
                warpY[k] = (Fbm(x / (55 * scale), y / (55 * scale), 4) - 0.5f) * 22 * scale;
                fineMask[k] = Smooth(0.5f, 0.68f, Fbm(x / (110 * scale), y / (110 * scale), 31));
                cloudGrid[k] = Fbm(x / (170 * scale), y / (170 * scale), 9);
            }
        float At(float[] grid, int x, int y)
        {
            float fx = (float)x / step, fy = (float)y / step;
            int x0 = Math.Min((int)fx, gw - 2), y0 = Math.Min((int)fy, gh - 2);
            float tx = fx - x0, ty = fy - y0;
            float a = grid[y0 * gw + x0], b = grid[y0 * gw + x0 + 1], c = grid[(y0 + 1) * gw + x0], d = grid[(y0 + 1) * gw + x0 + 1];
            return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
        }

        var crack = new byte[n];
        var cloud = new byte[n];
        var rim = new sbyte[n];
        var cell = new ushort[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                float wx = x + At(warpX, x, y) + (Noise(x / (6 * scale), y / (6 * scale), 5) - 0.5f) * 2.5f * scale;
                float wy = y + At(warpY, x, y) + (Noise(x / (6 * scale), y / (6 * scale), 6) - 0.5f) * 2.5f * scale;
                var c = Nearest(wx, wy, coarse, 11);
                float edge = (c.D2 - c.D1) * 0.5f;
                var f = Nearest(wx, wy, fine, 41);
                float main = Smooth(1.5f * scale, 0.25f * scale, edge);
                float minor = Smooth(1.0f * scale, 0.15f * scale, (f.D2 - f.D1) * 0.5f) * At(fineMask, x, y) * 0.75f;
                crack[p] = (byte)(255 * Math.Max(main, minor));
                cloud[p] = (byte)(255 * Math.Clamp(At(cloudGrid, x, y), 0, 1));
                cell[p] = (ushort)(Math.Clamp(c.J + 1, 0, rows - 1) * cols + Math.Clamp(c.I + 1, 0, cols - 1));
                if (edge < rimWidth)
                {
                    float nx = wx - c.Sx, ny = wy - c.Sy, len = MathF.Max(1e-3f, MathF.Sqrt(nx * nx + ny * ny));
                    float light = (-0.6f * nx - 0.8f * ny) / len;
                    rim[p] = (sbyte)Math.Clamp(127 * (1 - edge / rimWidth) * light, -127, 127);
                }
            }

        var deep = new byte[n];
        var fade = new float[DepthSteps + 1];
        var offsets = new (int X, int Y)[DepthSteps + 1];
        for (int s = 1; s <= DepthSteps; s++)
        {
            fade[s] = MathF.Pow(1 - s / (DepthSteps + 1f), 1.3f);
            offsets[s] = ((int)MathF.Round(s * 0.35f * scale), (int)MathF.Round(s * 0.75f * scale));
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float best = 0;
                for (int s = 1; s <= DepthSteps; s++)
                {
                    int sx = x - offsets[s].X, sy = y - offsets[s].Y;
                    if (sx < 0 || sy < 0) break;
                    float v = crack[sy * w + sx] * fade[s];
                    if (v > best) best = v;
                }
                deep[y * w + x] = (byte)best;
            }

        return new IceGeometry
        {
            Size = size, Crack = crack, Deep = deep, Cloud = cloud, Rim = rim, Cell = cell,
            CellTint = tint, CellShift = shift, Key = KeyFor(size, scale),
        };
    }
}

static class IcePixels
{
    static void Over(ref float a, ref float r, ref float g, ref float b, float sr, float sg, float sb, float sa)
    {
        sa = Math.Clamp(sa, 0, 1);
        if (sa <= 0) return;
        float oa = sa + a * (1 - sa);
        r = (sr * sa + r * a * (1 - sa)) / oa;
        g = (sg * sa + g * a * (1 - sa)) / oa;
        b = (sb * sa + b * a * (1 - sa)) / oa;
        a = oa;
    }

    // The ice overlay as premultiplied 0xAARRGGBB pixels (BGRA in memory), and for every pixel the offset
    // to the pixel it shows through the ice.
    public static (int[] Pixels, int[] Offset) Compose(IceGeometry geo, int ice, float scale)
    {
        int w = geo.Size.Width, h = geo.Size.Height, n = w * h;
        float k = Math.Clamp(ice, 0, 100) / 100f, push = 3.5f * scale * k;
        var pixels = new int[n];
        var offset = new int[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                float cloud = geo.Cloud[p] / 255f, crack = geo.Crack[p] / 255f, deep = geo.Deep[p] / 255f, rim = geo.Rim[p] / 127f;
                float tint = geo.CellTint[geo.Cell[p]];
                float a = k * (0.22f + 0.32f * cloud), r = 208 - 18 * tint, g = 230 - 10 * tint, b = 248;
                Over(ref a, ref r, ref g, ref b, 92, 142, 198, k * deep * 0.7f);
                if (rim > 0) Over(ref a, ref r, ref g, ref b, 255, 255, 255, k * rim * 0.6f);
                else Over(ref a, ref r, ref g, ref b, 80, 120, 165, k * -rim * 0.3f);
                Over(ref a, ref r, ref g, ref b, 250, 253, 255, crack * (0.3f + 0.65f * k));
                int A = (int)(a * 255);
                pixels[p] = (A << 24) | ((int)(r * a) << 16) | ((int)(g * a) << 8) | (int)(b * a);
                var s = geo.CellShift[geo.Cell[p]];
                int sx = Math.Clamp(x + (int)MathF.Round(s.X * push), 0, w - 1), sy = Math.Clamp(y + (int)MathF.Round(s.Y * push), 0, h - 1);
                offset[p] = sy * w + sx - p;
            }
        return (pixels, offset);
    }
}
