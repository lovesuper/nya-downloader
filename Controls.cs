using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VideoDownloader;

sealed class CueTextBox : TextBox
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Cue { get; init; } = "";

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, 0x1501, 1, Cue);
    }
}

sealed class CaptionButton : Control
{
    readonly bool _close;
    bool _hover, _down;

    public CaptionButton(bool close)
    {
        _close = close;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.StandardClick, false);
        BackColor = Color.Transparent;
        Size = new Size(46, 30);
        Margin = Padding.Empty;
        TabStop = false;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = close ? "Close" : "Minimize";
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool click = _down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
        if (click) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_hover)
        {
            var fill = _close ? (_down ? Color.FromArgb(241, 112, 122) : Color.FromArgb(232, 17, 35))
                              : Color.FromArgb(_down ? 90 : 55, 0, 0, 0);
            using var b = new SolidBrush(fill);
            g.FillRectangle(b, ClientRectangle);
        }
        float k = DeviceDpi / 96f, half = 5 * k, cx = Width / 2f, cy = Height / 2f;
        using var pen = new Pen(_hover && _close ? Color.White : ForeColor, Math.Max(1f, k));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_close)
        {
            g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
            g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
        }
        else
            g.DrawLine(pen, cx - half, cy, cx + half, cy);
    }
}

static partial class Backdrop
{
    public const PixelFormat Format = PixelFormat.Format32bppPArgb;

    public static Bitmap Frame(Image gif, int blur)
    {
        int width = FrameWidth(blur);
        int midW = Math.Min(gif.Width, width * 4), midH = Math.Max(1, (int)Math.Round((double)gif.Height * midW / gif.Width));
        int w = Math.Min(midW, width), h = Math.Max(1, (int)Math.Round((double)gif.Height * w / gif.Width));
        using var mid = new Bitmap(midW, midH, Format);
        using (var g = Graphics.FromImage(mid))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(gif, new Rectangle(0, 0, midW, midH), 0, 0, gif.Width, gif.Height, GraphicsUnit.Pixel);
        }
        var bmp = new Bitmap(w, h, Format);
        using (var g = Graphics.FromImage(bmp))
        using (var wrap = new ImageAttributes())
        {
            wrap.SetWrapMode(WrapMode.TileFlipXY);
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(mid, new Rectangle(0, 0, w, h), 0, 0, midW, midH, GraphicsUnit.Pixel, wrap);
        }
        if (blur > 0) Soften(bmp);
        return bmp;
    }

    static void Soften(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, Format);
        try
        {
            var pixels = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            SoftenPixels(pixels, bmp.Width, bmp.Height, data.Stride);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    public static void Paint(Graphics g, Image frame, Size size, int visibility, int scalePercent = 100) =>
        Paint(g, frame, Placement(frame.Size, size, scalePercent), size, visibility);

    static void Paint(Graphics g, Image frame, Rectangle dest, Size size, int visibility)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (dest.Left > 0 || dest.Top > 0 || dest.Right < size.Width || dest.Bottom < size.Height)
        {
            using var paper = new SolidBrush(Color.White);
            g.FillRectangle(paper, 0, 0, size.Width, size.Height);
        }
        using (var wrap = new ImageAttributes())
        {
            wrap.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(frame, dest, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, wrap);
        }
        int veil = (int)Math.Round(255 * (100 - Math.Clamp(visibility, 0, 100)) / 100.0);
        if (veil == 0) return;
        g.CompositingMode = CompositingMode.SourceOver;
        using var white = new SolidBrush(Color.FromArgb(veil, 255, 255, 255));
        g.FillRectangle(white, 0, 0, size.Width, size.Height);
    }

    public static Bitmap Frosted(Image frame)
    {
        int w = Math.Clamp(FrostedWidth, 2, Math.Max(2, frame.Width));
        int h = Math.Max(2, (int)Math.Round((double)frame.Height * w / frame.Width));
        var bmp = new Bitmap(w, h, Format);
        using (var g = Graphics.FromImage(bmp))
        using (var wrap = new ImageAttributes())
        {
            wrap.SetWrapMode(WrapMode.TileFlipXY);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(frame, new Rectangle(0, 0, w, h), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, wrap);
        }
        Soften(bmp);
        return bmp;
    }

    public static void Back(Graphics g, Image frame, Size size, BackgroundLook look, IReadOnlyList<Backing> backings, float scale)
    {
        if (look.Backing <= 0 || backings.Count == 0) return;
        using var area = new GraphicsPath(FillMode.Winding);
        foreach (var b in backings)
        {
            using var shape = RoundedRect(b.Bounds, b.Radius);
            area.AddPath(shape, false);
        }
        var state = g.Save();
        using (var frosted = Frosted(frame))
        {
            g.SetClip(area);
            Paint(g, frosted, Placement(frame.Size, size, look.Scale), size, look.Visibility);
        }
        g.Restore(state);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var milk = new SolidBrush(Color.FromArgb(MilkAlpha(look), 255, 255, 255));
        foreach (var b in backings)
        {
            using var shape = RoundedRect(b.Bounds, b.Radius);
            g.FillPath(milk, shape);
            if (b.Card) Decorate(g, new GlassPanel(b.Bounds, b.Radius), look, scale, frost: false);
        }
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Decorate(Graphics g, GlassPanel panel, BackgroundLook look, float scale, bool frost = true)
    {
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF r = panel.Bounds;
        r.Inflate(-0.5f, -0.5f);
        using var path = RoundedRect(r, panel.Radius);
        if (frost && look.Frost > 0)
        {
            using var milk = new SolidBrush(Color.FromArgb((int)Math.Round(look.Frost * 1.8), 255, 255, 255));
            g.FillPath(milk, path);
        }
        if (look.Highlight > 0)
        {
            int a = (int)Math.Round(look.Highlight * 2.4);
            using (var rim = new LinearGradientBrush(Rectangle.Inflate(panel.Bounds, 1, 1), Color.FromArgb(a, 255, 255, 255),
                                                     Color.FromArgb(a / 6, 255, 255, 255), LinearGradientMode.ForwardDiagonal))
            using (var pen = new Pen(rim, Math.Max(1f, 1.6f * scale)))
                g.DrawPath(pen, path);
            var inner = RectangleF.Inflate(r, -2.5f * scale, -2.5f * scale);
            using var innerPath = RoundedRect(inner, Math.Max(1, panel.Radius - 2.5f * scale));
            using var glow = new LinearGradientBrush(Rectangle.Inflate(panel.Bounds, 1, 1), Color.FromArgb(a / 8, 255, 255, 255),
                                                     Color.FromArgb(a / 2, 255, 255, 255), LinearGradientMode.ForwardDiagonal);
            using var glowPen = new Pen(glow, Math.Max(1f, scale));
            g.DrawPath(glowPen, innerPath);
        }
        var outer = RectangleF.Inflate(r, 1f, 1f);
        using var outline = RoundedRect(outer, panel.Radius + 1);
        using var shade = new Pen(Color.FromArgb((int)Math.Round(look.Glass * 0.5), 0, 0, 0), 1f);
        g.DrawPath(shade, outline);
    }
}

sealed class IceLayer : IDisposable
{
    public Bitmap Overlay { get; private init; } = null!;
    public int[] Offset { get; private init; } = [];
    public string Key { get; private init; } = "";

    public static string KeyFor(IceGeometry geometry, int ice) => geometry.Key + "|" + ice;

    public static IceLayer Compose(IceGeometry geo, int ice, float scale)
    {
        int w = geo.Size.Width, h = geo.Size.Height;
        var (pixels, offset) = IcePixels.Compose(geo, ice, scale);
        var overlay = new Bitmap(w, h, Backdrop.Format);
        var data = overlay.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, Backdrop.Format);
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w, data.Scan0 + y * data.Stride, w);
        }
        finally { overlay.UnlockBits(data); }
        return new IceLayer { Overlay = overlay, Offset = offset, Key = KeyFor(geo, ice) };
    }

    public void Dispose() => Overlay.Dispose();
}

sealed class BackdropCanvas(bool iceInBackground = true) : IDisposable
{
    Bitmap? _plain;
    int[] _src = [], _dst = [], _iced = [];
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
            if (iceInBackground) _building = key;
        }
        if (!iceInBackground) return _geometry = IceGeometry.Build(size, scale);
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

    public void Render(Bitmap target, Image frame, BackgroundLook look, IReadOnlyList<GlassPanel> panels, float scale,
                       IReadOnlyList<Backing>? backings = null)
    {
        var size = target.Size;
        bool glass = look.Glass > 0 && panels.Count > 0;
        var geometry = look.Ice > 0 ? Geometry(size, scale) : null;
        bool ice = geometry != null;
        if (!glass && !ice)
        {
            using var g = Graphics.FromImage(target);
            Backdrop.Paint(g, frame, size, look.Visibility, look.Scale);
            Backdrop.Back(g, frame, size, look, backings ?? [], scale);
            return;
        }
        if (_plain?.Size != size)
        {
            _plain?.Dispose();
            _plain = new Bitmap(size.Width, size.Height, Backdrop.Format);
            _src = new int[size.Width * size.Height];
            _dst = new int[_src.Length];
            _iced = new int[_src.Length];
        }
        using (var g = Graphics.FromImage(_plain)) Backdrop.Paint(g, frame, size, look.Visibility, look.Scale);
        Copy(_plain, _src, toBitmap: false);
        Array.Copy(_src, _dst, _src.Length);
        if (glass)
        {
            var key = GlassMap.KeyFor(size, panels, look.Glass, scale);
            if (_map?.Key != key) _map = GlassMap.Build(size, panels, look.Glass, scale);
            var (dst, src) = (_map.Dst, _map.Src);
            for (int i = 0; i < dst.Length; i++) _dst[dst[i]] = _src[src[i]];
        }
        var result = _dst;
        if (ice)
        {
            var key = IceLayer.KeyFor(geometry!, look.Ice);
            if (_ice?.Key != key)
            {
                _ice?.Dispose();
                _ice = IceLayer.Compose(geometry!, look.Ice, scale);
            }
            var (offset, from, to) = (_ice.Offset, _dst, _iced);
            for (int p = 0; p < to.Length; p++) to[p] = from[p + offset[p]];
            result = _iced;
        }
        Copy(target, result, toBitmap: true);
        using var tg = Graphics.FromImage(target);
        if (glass) foreach (var p in panels) Backdrop.Decorate(tg, p, look, scale);
        if (ice)
        {
            tg.CompositingMode = CompositingMode.SourceOver;
            tg.DrawImageUnscaled(_ice!.Overlay, 0, 0);
        }
        Backdrop.Back(tg, frame, size, look, backings ?? [], scale);
    }

    static void Copy(Bitmap bmp, int[] pixels, bool toBitmap)
    {
        var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), toBitmap ? ImageLockMode.WriteOnly : ImageLockMode.ReadOnly, Backdrop.Format);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
                if (toBitmap) Marshal.Copy(pixels, y * bmp.Width, data.Scan0 + y * data.Stride, bmp.Width);
                else Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bmp.Width, bmp.Width);
        }
        finally { bmp.UnlockBits(data); }
    }

    public void Dispose()
    {
        _plain?.Dispose();
        _ice?.Dispose();
    }
}

sealed class LookDialog : Form
{
    public event Action<BackgroundLook>? Changed;

    readonly BackgroundLook _look;
    readonly ComboBox _gif = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Anchor = AnchorStyles.Left, Name = "lookGif" };
    readonly List<(TrackBar Bar, Label Value, Func<BackgroundLook, int> Get, Action<BackgroundLook, int> Set, string Unit)> _sliders = [];
    readonly Button _reset = new() { Text = "Reset", AutoSize = true, Padding = new Padding(10, 3, 10, 3), Name = "lookReset" };
    readonly Button _close = new() { Text = "Close", AutoSize = true, Padding = new Padding(10, 3, 10, 3), Name = "lookClose" };
    bool _loading;

    public LookDialog(BackgroundLook look, int gifCount)
    {
        _look = look.Clone();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Text = "Background";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        KeyPreview = true;

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 10, 3) };
        grid.Controls.Add(Caption("GIF:"), 0, 0);
        _gif.Items.Add("Random");
        for (int i = 1; i <= gifCount; i++) _gif.Items.Add(i.ToString());
        grid.Controls.Add(_gif, 1, 0);
        grid.SetColumnSpan(_gif, 2);

        void Slider(string caption, string name, Func<BackgroundLook, int> get, Action<BackgroundLook, int> set, string unit, int min = 0, int max = 100)
        {
            var bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = (max - min) / 10, SmallChange = 1, LargeChange = 10, Width = 260, AutoSize = false, Height = 32, Name = name };
            var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(44, 0), Name = name + "Value" };
            int row = grid.RowCount++;
            grid.Controls.Add(Caption(caption), 0, row);
            grid.Controls.Add(bar, 1, row);
            grid.Controls.Add(value, 2, row);
            bar.ValueChanged += (_, _) => OnChanged();
            _sliders.Add((bar, value, get, set, unit));
        }
        grid.RowCount = 1;
        Slider("Visibility:", "lookVisibility", l => l.Visibility, (l, v) => l.Visibility = v, "%");
        Slider("Blur:", "lookBlur", l => l.Blur, (l, v) => l.Blur = v, "");
        Slider("Scale:", "lookScale", l => l.Scale, (l, v) => l.Scale = v, "%", BackgroundLook.MinScale, BackgroundLook.MaxScale);
        Slider("Liquid glass:", "lookGlass", l => l.Glass, (l, v) => l.Glass = v, "");
        Slider("Glass highlight:", "lookHighlight", l => l.Highlight, (l, v) => l.Highlight = v, "%");
        Slider("Glass frost:", "lookFrost", l => l.Frost, (l, v) => l.Frost = v, "%");
        Slider("Ice:", "lookIce", l => l.Ice, (l, v) => l.Ice = v, "");
        Slider("Text backing:", "lookBacking", l => l.Backing, (l, v) => l.Backing = v, "%");
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([_close, _reset]);
        grid.Controls.Add(buttons, 0, grid.RowCount++);
        grid.SetColumnSpan(buttons, 3);
        Controls.Add(grid);

        _gif.SelectedIndexChanged += (_, _) => OnChanged();
        _reset.Click += (_, _) => { Fill(new BackgroundLook()); OnChanged(); };
        _close.Click += (_, _) => Close();
        CancelButton = _close;
        Fill(_look);
        ResumeLayout(false);
        PerformLayout();
    }

    public BackgroundLook Look => _look.Clone();

    void Fill(BackgroundLook look)
    {
        _loading = true;
        _gif.SelectedIndex = Math.Clamp(look.Gif, 0, _gif.Items.Count - 1);
        foreach (var s in _sliders) s.Bar.Value = Math.Clamp(s.Get(look), s.Bar.Minimum, s.Bar.Maximum);
        _loading = false;
        ShowValues();
    }

    void ShowValues()
    {
        foreach (var s in _sliders) s.Value.Text = s.Bar.Value + s.Unit;
    }

    void OnChanged()
    {
        if (_loading) return;
        _look.Gif = Math.Max(0, _gif.SelectedIndex);
        foreach (var s in _sliders) s.Set(_look, s.Bar.Value);
        ShowValues();
        Changed?.Invoke(_look.Clone());
    }
}

sealed class TaskbarProgress
{
    public enum State { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarList;

    readonly Action<IntPtr, State, int> _apply;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    (IntPtr Window, State State, int Value)? _pending;
    Thread? _worker;
    ITaskbarList3? _list;
    bool _failed;

    public TaskbarProgress() : this(null) { }

    internal TaskbarProgress(Action<IntPtr, State, int>? apply) => _apply = apply ?? Apply;

    public State Current { get; private set; }
    public int Value { get; private set; }

    public void Set(Control window, State state, int perMille = 0)
    {
        perMille = Math.Clamp(perMille, 0, 1000);
        bool showsValue = state is State.Normal or State.Error or State.Paused;
        if (state == Current && (!showsValue || perMille == Value)) return;
        (Current, Value) = (state, showsValue ? perMille : 0);
        if (!window.IsHandleCreated) return;
        lock (_gate)
        {
            _pending = (window.Handle, Current, Value);
            if (_worker == null)
            {
                _worker = new Thread(Work) { IsBackground = true, Name = "Taskbar progress" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }
        _wake.Set();
    }

    void Work()
    {
        while (true)
        {
            _wake.WaitOne();
            (IntPtr Window, State State, int Value)? next;
            lock (_gate) (next, _pending) = (_pending, null);
            if (next is { } n)
                try { _apply(n.Window, n.State, n.Value); }
                catch (Exception) { }
        }
    }

    void Apply(IntPtr window, State state, int value)
    {
        if (_failed) return;
        try
        {
            _list ??= Create();
            _list.SetProgressState(window, (int)state);
            if (state is State.Normal or State.Error or State.Paused) _list.SetProgressValue(window, (ulong)value, 1000);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or NotSupportedException)
        {
            _failed = true;
            UsageLog.Write("Taskbar progress is unavailable: " + e.Message.Split('\n')[0].Trim());
        }
    }

    static ITaskbarList3 Create()
    {
        var list = (ITaskbarList3)new TaskbarList();
        list.HrInit();
        return list;
    }

    internal static void CheckAvailable()
    {
        var list = Create();
        Marshal.ReleaseComObject(list);
    }
}

sealed class PickDialog : Form
{
    public sealed record Column(string Header, int Width = 0);
    public sealed record Item(string[] Cells, double? Seconds, bool Checked);

    readonly ListView _list = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(3, 6, 3, 3) };
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
    readonly Button _all = Btn("Select all"), _none = Btn("Select none"), _ok = Btn("Download"), _cancel = Btn("Cancel");
    readonly IReadOnlyList<Item> _items;
    readonly Column[] _columns;
    readonly string _one, _many;
    bool _filling;

    static Button Btn(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };

    public PickDialog(string caption, string heading, Column[] columns, IReadOnlyList<Item> items, string one, string many)
    {
        (_items, _columns, _one, _many) = (items, columns, one, many);
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Text = caption;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ShowIcon = false;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(680, 480);
        MinimumSize = new Size(440, 320);
        Padding = new Padding(9);

        foreach (var c in columns) _list.Columns.Add(c.Header, c.Width > 0 ? c.Width : 100);
        _list.BeginUpdate();
        _filling = true;
        foreach (var item in items) _list.Items.Add(new ListViewItem(item.Cells) { Checked = item.Checked });
        _filling = false;
        _list.EndUpdate();
        var first = items.Select((x, i) => (x, i)).FirstOrDefault(p => p.x.Checked);
        if (first.x != null) { _list.Items[first.i].Focused = _list.Items[first.i].Selected = true; _list.EnsureVisible(first.i); }
        else if (_list.Items.Count > 0) _list.Items[0].Focused = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var h in new[] { SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize })
            layout.RowStyles.Add(h == SizeType.Percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
        for (int i = 0; i < 5; i++) buttons.ColumnStyles.Add(i == 2 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.AddRange([_all, _none, new Label { AutoSize = true }, _ok, _cancel]);
        layout.Controls.AddRange([new Label { Name = "pickHeading", Text = heading, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(3, 0, 3, 0) }, _list, _summary, buttons]);
        Controls.Add(layout);

        AcceptButton = _ok;
        CancelButton = _cancel;
        _ok.DialogResult = DialogResult.OK;
        _cancel.DialogResult = DialogResult.Cancel;
        _all.Click += (_, _) => CheckAll(true);
        _none.Click += (_, _) => CheckAll(false);
        _list.ItemChecked += (_, _) => { if (!_filling) UpdateSummary(); };
        _list.ClientSizeChanged += (_, _) => FitColumns();
        (_list.Name, _summary.Name, _all.Name, _none.Name, _ok.Name, _cancel.Name) = ("pickList", "pickSummary", "pickAll", "pickNone", "pickOk", "pickCancel");
        UpdateSummary();
        ActiveControl = _list;
        ResumeLayout(false);
        PerformLayout();
        FitColumns();
    }

    public List<int> Picked => _list.CheckedIndices.Cast<int>().ToList();

    void CheckAll(bool on)
    {
        _filling = true;
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items) item.Checked = on;
        _list.EndUpdate();
        _filling = false;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var picked = Picked;
        var seconds = picked.Sum(i => _items[i].Seconds ?? 0);
        _summary.Text = $"{picked.Count} of {_items.Count} selected" + (seconds > 0 ? ", " + TimeText.Format(seconds) + " in total" : "");
        _ok.Enabled = picked.Count > 0;
        _ok.Text = picked.Count == 0 ? "Download" : $"Download {picked.Count} {(picked.Count == 1 ? _one : _many)}";
    }

    void FitColumns()
    {
        int stretch = Array.FindIndex(_columns, c => c.Width == 0);
        if (stretch < 0 || _list.Columns.Count != _columns.Length) return;
        int fixedWidth = _columns.Where(c => c.Width > 0).Sum(c => LogicalToDeviceUnits(c.Width));
        for (int i = 0; i < _columns.Length; i++)
            if (_columns[i].Width > 0) _list.Columns[i].Width = LogicalToDeviceUnits(_columns[i].Width);
        _list.Columns[stretch].Width = Math.Max(LogicalToDeviceUnits(120), _list.ClientSize.Width - fixedWidth);
    }
}

sealed class FieldBorder : Panel
{
    static readonly Color NormalLine = Color.FromArgb(0xB4, 0xB4, 0xB4);
    static readonly Color FocusLine = Color.FromArgb(0x00, 0x67, 0xC0);
    readonly Control _inner;

    public FieldBorder(Control inner, Padding padding)
    {
        _inner = inner;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
        Padding = padding;
        Margin = new Padding(3);
        if (inner.Dock == DockStyle.Fill) Dock = DockStyle.Fill;
        else { Anchor = inner.Anchor; AutoSize = true; }
        inner.Margin = Padding.Empty;
        inner.Dock = DockStyle.Fill;
        Controls.Add(inner);
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        MouseDown += (_, _) => inner.Focus();
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Math.Max(Width, 40), _inner.PreferredSize.Height + Padding.Vertical);

    public bool Glass { get; private set; }

    public void MakeGlass()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Glass = true;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Glass)
        {
            if (!_inner.ContainsFocus) return;
            float k = DeviceDpi / 96f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = Backdrop.RoundedRect(new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f), 10 * k - 1);
            using var focus = new Pen(Color.FromArgb(170, FocusLine), Math.Max(1f, 1.5f * k));
            e.Graphics.DrawPath(focus, ring);
            return;
        }
        using var pen = new Pen(_inner.ContainsFocus ? FocusLine : NormalLine);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
