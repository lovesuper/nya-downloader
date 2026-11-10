using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VideoDownloader;

public sealed class MainForm : Form
{
    static readonly Regex PercentRx = new(@"([\d.]+)%", RegexOptions.Compiled);

    static readonly Color OkColor = Color.FromArgb(0x1E, 0x7B, 0x34);
    static readonly Color WarnColor = Color.FromArgb(0xB3, 0x5C, 0x00);
    static readonly Color ErrorColor = Color.Firebrick;
    static readonly Color NeutralColor = SystemColors.ControlText;
    static readonly Color DimColor = SystemColors.GrayText;

    static Color? PlatformColor(Platform p) => p switch
    {
        Platform.YouTube => Color.FromArgb(0xE0, 0x00, 0x2A),
        Platform.TikTok => Color.FromArgb(0x16, 0x18, 0x23),
        Platform.Instagram => Color.FromArgb(0xE8, 0x59, 0x0C),
        _ => null,
    };

    const string ExtensionUrl = "https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc";

    const string CookieHowTo =
        "1. Install the <a href=\"ext\">Get cookies.txt LOCALLY</a> extension in your browser (button below).\n" +
        "2. Open the site (YouTube, TikTok or Instagram) where you are signed in.\n" +
        "3. Click the extension icon, then Export.\n" +
        "4. Drop the downloaded file into this window.";

    readonly Settings _settings = Settings.Load();

    static Button Btn(string text, int padX = 10) =>
        new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(padX, 3, padX, 3) };
    static LinkLabel Link(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Visible = false, Margin = new Padding(12, 3, 3, 3) };

    readonly CueTextBox _url = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.None, Cue = "Paste a YouTube, TikTok or Instagram link and press Enter" };
    readonly Label _platform = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 3, 8, 3), MinimumSize = new Size(96, 0), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Visible = false };
    readonly Button _cookiesPick = Btn("Choose..."), _cookiesClear = Btn("Remove");
    readonly Label _cookiesStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 6, 3, 6) };
    readonly LinkLabel _cookiesHelp = new() { Text = "How do I get cookies?", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 0, 3, 8) };
    readonly TextBox _outDir = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.None };
    readonly Button _outPick = Btn("Browse...");
    readonly RadioButton _video = new() { Text = "Video (mp4)", AutoSize = true, Checked = true };
    readonly RadioButton _audio = new() { Text = "Audio only (mp3)", AutoSize = true };
    readonly CheckBox _cover = new() { Text = "Save cover (jpg)", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
    readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Left, Width = 190, MinimumSize = new Size(190, 0) };
    readonly Label _qualityHint = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6) };
    readonly Button _download = Btn("Download", 20), _cancel = Btn("Cancel"), _showFile = Btn("Show in folder"), _setup = Btn("Install components");
    readonly ProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Maximum = 1000, Height = 18 };
    readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 6, 3, 6) };
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, BorderStyle = BorderStyle.None };
    readonly FieldBorder _urlBox, _outDirBox, _logBox;
    readonly Label _engine = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = DimColor, Margin = new Padding(3, 3, 0, 3) };
    readonly LinkLabel _update = Link("Update yt-dlp"), _errorLogLink = Link("Open error.log");
    readonly Panel _updateBanner = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, BackColor = Color.FromArgb(0xFF, 0xF4, 0xCE), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3, 6, 3, 0) };
    readonly Label _updateText = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Color.FromArgb(0x5C, 0x40, 0x00) };
    readonly Button _updateNow = new() { Text = "Update", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 2, 12, 2), Anchor = AnchorStyles.Right, BackColor = SystemColors.Control, UseVisualStyleBackColor = true };

    bool _selectAllOnMouseUp;
    YtDlp.UpdateInfo? _updateInfo;
    string? _localYtDlp;
    bool _hasYtDlp = true;
    CancellationTokenSource? _cts;
    string? _lastFile;
    Platform _current = Platform.Unknown;
    bool _hideNoVideoWarnings;

    public MainForm()
    {
        _urlBox = new FieldBorder(_url, new Padding(6, 4, 6, 4));
        _outDirBox = new FieldBorder(_outDir, new Padding(6, 4, 6, 4));
        _logBox = new FieldBorder(_log, new Padding(6, 4, 2, 4));
        (_gifData, _gifName) = LoadRandomGif();
        if (_gifData != null)
        {
            try
            {
                using var ms = new MemoryStream(_gifData);
                using var gif = Image.FromStream(ms);
                _veilAlpha = VeilFor(gif);
                _firstFrame = SmallVeiledFrame(gif, _veilAlpha);
            }
            catch { _gifData = null; }
        }

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Text = $"{AppInfo.Name} - YouTube, TikTok, Instagram";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        Padding = new Padding(ResizeBorder);
        ClientSize = new Size(780, 612);
        MinimumSize = new Size(620, 490);
        StartPosition = FormStartPosition.CenterScreen;
        if (LaunchMinimized) WindowState = FormWindowState.Minimized;
        AcceptButton = _download;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (iconStream != null) Icon = new Icon(iconStream);

        var fields = Grid(new() { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 7, Padding = new Padding(12, 12, 12, 0) }, stretchColumn: 1);
        void Add(Control c, int col, int row, int span = 1) { fields.Controls.Add(c, col, row); fields.SetColumnSpan(c, span); }
        Add(Caption("Link:"), 0, 0); Add(_urlBox, 1, 0); Add(_platform, 2, 0);
        Add(Caption("Cookies:"), 0, 1); Add(_cookiesStatus, 1, 1); Add(Row(_cookiesPick, _cookiesClear), 2, 1);
        Add(_cookiesHelp, 1, 2);
        Add(Caption("Folder:"), 0, 3); Add(_outDirBox, 1, 3); Add(_outPick, 2, 3);
        Add(Caption("Mode:"), 0, 4); Add(Row(_video, _audio, _cover), 1, 4, 2);
        Add(Caption("Quality:"), 0, 5); Add(_quality, 1, 5, 2);
        Add(_qualityHint, 1, 6);

        var bannerGrid = Grid(new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }, stretchColumn: 0);
        bannerGrid.Controls.AddRange([_updateText, _updateNow]);
        _updateBanner.Controls.Add(bannerGrid);
        var lower = Grid(new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12, 4, 12, 12) }, stretchColumn: 0, stretchRow: 3);
        lower.Controls.AddRange([Row(_download, _cancel, _showFile, _setup), _progress, _status, _logBox, Row(_engine, _update, _errorLogLink), _updateBanner]);

        var titleBar = Grid(new() { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1, Padding = new Padding(10, 0, 0, 0) }, stretchColumn: 1);
        var minimize = new CaptionButton(close: false);
        var close = new CaptionButton(close: true);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        close.Click += (_, _) => Close();
        titleBar.Controls.AddRange([
            new PictureBox { Size = new Size(18, 18), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0), Image = Icon != null ? new Icon(Icon, 32, 32).ToBitmap() : null },
            new Label { Text = $"{AppInfo.Name} {AppInfo.Version}", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(0x40, 0x40, 0x40) },
            minimize, close]);
        Controls.AddRange([lower, fields, titleBar]);
        (fields.TabIndex, lower.TabIndex, titleBar.TabIndex) = (0, 1, 2);

        HookWindowDrag(this);
        HookDrop(this);

        BackColor = PageColor;
        DoubleBuffered = true;
        if (HasBackground) foreach (var grid in new[] { fields, lower, titleBar }) MakeTransparent(grid);

        _outDir.Text = string.IsNullOrWhiteSpace(_settings.OutputDir) ? AppContext.BaseDirectory.TrimEnd('\\') : _settings.OutputDir;
        _quality.Items.AddRange(Quality.All);
        _quality.SelectedItem = Quality.All.FirstOrDefault(q => q.MaxHeight == _settings.MaxHeight) ?? Quality.All[0];
        _quality.Enabled = !_settings.AudioOnly;
        _audio.Checked = _settings.AudioOnly;
        _video.Checked = !_settings.AudioOnly;
        _cover.Checked = _settings.SaveCover;
        _setup.Visible = _cancel.Enabled = _showFile.Enabled = false;
        _errorLogLink.Visible = ErrorLog.Exists;
        UpdateCookiesView();
        UpdateQualityHint();

        _heartbeat.Tick += (_, _) => OnHeartbeat();
        _url.TextChanged += (_, _) => UpdatePlatform();
        _url.Enter += (_, _) =>
        {
            _url.SelectAll();
            _selectAllOnMouseUp = (MouseButtons & MouseButtons.Left) != 0;
        };
        _url.MouseUp += (_, _) =>
        {
            if (_selectAllOnMouseUp && _url.SelectionLength == 0) _url.SelectAll();
            _selectAllOnMouseUp = false;
        };
        foreach (var rb in new[] { _video, _audio })
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked) return;
                _quality.Enabled = Mode == DownloadMode.Video;
                UpdateQualityHint();
                UsageLog.Write($"Mode: {rb.Text}");
            };
        _quality.SelectedIndexChanged += (_, _) => { UpdateQualityHint(); UsageLog.Write($"Quality: {SelectedQuality.Label}"); };
        _cover.CheckedChanged += (_, _) => UsageLog.Write($"Save cover: {(_cover.Checked ? "on" : "off")}");
        _cookiesHelp.LinkClicked += (_, _) => ShowCookieHelp();
        _cookiesPick.Click += (_, _) => PickCookies();
        _cookiesClear.Click += (_, _) => ClearCookies();
        _outPick.Click += (_, _) => PickOutDir();
        _download.Click += async (_, _) => await DownloadAsync();
        _cancel.Click += (_, _) => { UsageLog.Write("Clicked Cancel"); _cts?.Cancel(); };
        _showFile.Click += (_, _) => ShowInFolder();
        _setup.Click += async (_, _) => await InstallComponentsAsync();
        _update.LinkClicked += async (_, _) => await UpdateYtDlpAsync();
        _updateNow.Click += async (_, _) => await UpdateYtDlpAsync();
        _errorLogLink.LinkClicked += (_, _) => OpenErrorLog();
        foreach (var f in typeof(MainForm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (f.GetValue(this) is Control c) c.Name = f.Name.TrimStart('_');

        ResumeLayout(false);
        PerformLayout();
    }

    static TableLayoutPanel Grid(TableLayoutPanel t, int stretchColumn, int stretchRow = -1)
    {
        for (int i = 0; i < t.ColumnCount; i++)
            t.ColumnStyles.Add(i == stretchColumn ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < t.RowCount; i++)
            t.RowStyles.Add(i == stretchRow ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        t.Margin = Padding.Empty;
        if (stretchRow < 0) { t.AutoSize = true; t.AutoSizeMode = AutoSizeMode.GrowAndShrink; }
        return t;
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        f.Controls.AddRange(controls);
        return f;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_gifData != null) { StartGif(_gifData, _veilAlpha); _gifData = null; }
        UsageLog.Write($"App started | background: {_gifName ?? "none"} | mode: {Mode} | quality: {SelectedQuality.Label} | folder: {_outDir.Text} | " +
                       "saved cookies: " + (_settings.Cookies.Count > 0
                           ? string.Join(", ", _settings.Cookies.Select(kv => $"{kv.Key}={Path.GetFileName(kv.Value)}"))
                           : "none"));
        await CheckEngineAsync();
        if (IsDisposed) return;
        UsageLog.Write($"yt-dlp: {_localYtDlp ?? "not found"} ({YtDlp.Locate().Description})" +
                       (MissingComponents() is { Count: > 0 } m ? " | missing: " + string.Join(", ", m.Select(x => x.Name)) : ""));
        await CheckForUpdatesAsync();
    }

    async Task CheckForUpdatesAsync()
    {
        if (!_hasYtDlp) return;
        _updateInfo = await YtDlp.CheckUpdatesAsync(_localYtDlp);
        if (IsDisposed) return;
        RefreshUpdateBanner();
        var have = _updateInfo == null ? "" : " (" + string.Join(", ", _updateInfo.Installed.Select(kv => $"{kv.Key} {kv.Value}")) + ")";
        UsageLog.Write(_updateInfo == null ? "Update check: couldn't check (offline?)"
            : _updateInfo.Updates.Count == 0 ? "Update check: everything up to date" + have
            : "Update check: available " + string.Join(", ", _updateInfo.Updates.Select(u => $"{u.Name} {u.Installed} -> {u.Latest}")));
    }

    void RefreshUpdateBanner()
    {
        var updates = _hasYtDlp ? _updateInfo?.Updates : null;
        bool show = updates is { Count: > 0 };
        if (show)
            _updateText.Text = "Updates available: " +
                               string.Join(", ", updates!.Select(u => $"{u.Name} {u.Latest} (you have {u.Installed})")) +
                               ". Sites change often, so downloads may stop working until you update.";
        _updateBanner.Visible = show;
        _update.Visible = show;
    }

    const int ResizeBorder = 5;

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("kernel32.dll")] static extern void GetStartupInfoW(out StartupInfo si);

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    static readonly short LaunchShow = GetLaunchShow();
    static short GetLaunchShow()
    {
        GetStartupInfoW(out var si);
        return (si.dwFlags & 1) != 0 ? si.wShowWindow : (short)1;
    }
    static bool LaunchMinimized => LaunchShow is 2 or 6 or 7;
    protected override bool ShowWithoutActivation => LaunchShow is 4 or 7 or 8;

    void HookWindowDrag(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TableLayoutPanel or FlowLayoutPanel or PictureBox || (c is Label && c is not LinkLabel))
                c.MouseDown += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    ReleaseCapture();
                    SendMessage(Handle, 0x00A1, 2, IntPtr.Zero);
                };
            HookWindowDrag(c);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000 | 0x00080000;
            cp.Style &= ~0x00010000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            int border = ColorTranslator.ToWin32(Color.FromArgb(0xA8, 0xA8, 0xA8));
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084, WM_SYSCOMMAND = 0x0112, WM_NCLBUTTONDBLCLK = 0x00A3, SC_MAXIMIZE = 0xF030;
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_MAXIMIZE) return;
        if (m.Msg == WM_NCLBUTTONDBLCLK) return;
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && (int)m.Result == 1)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int b = LogicalToDeviceUnits(ResizeBorder);
            bool left = p.X < b, right = p.X >= ClientSize.Width - b, top = p.Y < b, bottom = p.Y >= ClientSize.Height - b;
            m.Result = (IntPtr)(top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17
                              : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 2);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UsageLog.Write(_cts != null ? "App closed while busy (the running job was canceled)" : "App closed");
        PauseGif();
        _heartbeat.Stop();
        _cts?.Cancel();
        SaveSettings();
        base.OnFormClosing(e);
    }

    void SaveSettings()
    {
        _settings.OutputDir = _outDir.Text.Trim();
        _settings.AudioOnly = _audio.Checked;
        _settings.SaveCover = _cover.Checked;
        _settings.MaxHeight = SelectedQuality.MaxHeight;
        _settings.Save();
    }

    static readonly Color PageColor = Color.White;
    readonly string? _gifName;
    byte[]? _gifData;
    int _veilAlpha;
    readonly List<Control> _transparentControls = new();
    readonly System.Windows.Forms.Timer _gifTimer = new() { Interval = 125 };
    readonly Stopwatch _gifClock = new();
    Bitmap? _firstFrame;
    List<Bitmap>? _frames;
    int[] _delaysMs = [];
    int _frameIndex;
    long _frameShownAt;
    Bitmap? _backgroundCache;
    bool HasBackground => _firstFrame != null;

    const PixelFormat FastFormat = PixelFormat.Format32bppPArgb;

    static (byte[]? Data, string? Name) LoadRandomGif()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("bg/", StringComparison.Ordinal)).ToArray();
            if (names.Length == 0) return (null, null);
            var name = names[Random.Shared.Next(names.Length)];
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return (ms.ToArray(), name[3..]);
        }
        catch { return (null, null); }
    }

    static Bitmap SmallVeiledFrame(Image gif, int veilAlpha)
    {
        int sw = Math.Max(8, gif.Width / 2), sh = Math.Max(8, gif.Height / 2);
        var bmp = new Bitmap(sw, sh, FastFormat);
        using var g = Graphics.FromImage(bmp);
        using var veil = new ImageAttributes();
        float a = veilAlpha / 255f, k = 1 - a;
        veil.SetColorMatrix(new ColorMatrix(
        [
            [k, 0, 0, 0, 0f],
            [0, k, 0, 0, 0f],
            [0, 0, k, 0, 0f],
            [0, 0, 0, 1, 0f],
            [a, a, a, 0, 1f],
        ]));
        g.Clear(PageColor);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(gif, new Rectangle(0, 0, sw, sh), 0, 0, gif.Width, gif.Height, GraphicsUnit.Pixel, veil);
        return bmp;
    }

    static int[] FrameDelays(Image gif, int count)
    {
        var delays = Enumerable.Repeat(100, count).ToArray();
        try
        {
            var raw = gif.GetPropertyItem(0x5100)?.Value;
            if (raw != null)
                for (int i = 0; i < count && i * 4 + 3 < raw.Length; i++)
                {
                    int d = BitConverter.ToInt32(raw, i * 4) * 10;
                    delays[i] = d < 20 ? 100 : d;
                }
        }
        catch { }
        return delays;
    }

    static (List<Bitmap> Frames, int[] Delays) DecodeAllFrames(byte[] data, int veilAlpha)
    {
        using var ms = new MemoryStream(data);
        using var gif = Image.FromStream(ms);
        var dim = FrameDimension.Time;
        int count = gif.GetFrameCount(dim);
        var frames = new List<Bitmap>(count);
        for (int i = 0; i < count; i++)
        {
            gif.SelectActiveFrame(dim, i);
            frames.Add(SmallVeiledFrame(gif, veilAlpha));
        }
        return (frames, FrameDelays(gif, count));
    }

    static int VeilFor(Image gif)
    {
        using var probe = new Bitmap(24, 24);
        using (var g = Graphics.FromImage(probe)) g.DrawImage(gif, 0, 0, 24, 24);
        double sum = 0;
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 24; x++)
            {
                var c = probe.GetPixel(x, y);
                sum += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            }
        double lum = sum / (24 * 24), target = 0.88;
        double alpha = lum >= target ? 0.5 : (target - lum) / (1 - lum);
        return (int)(Math.Clamp(alpha, 0.5, 0.9) * 255);
    }

    void MakeTransparent(Control root)
    {
        root.BackColor = Color.Transparent;
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(root, true);
        _transparentControls.Add(root);
        foreach (Control c in root.Controls)
        {
            if (c is Label or RadioButton or CheckBox or TableLayoutPanel or FlowLayoutPanel or PictureBox or CaptionButton && c != _platform)
                MakeTransparent(c);
            else if (c is Button b)
                KeepButtonThemed(b);
        }
    }

    static void KeepButtonThemed(Button b)
    {
        b.BackColor = SystemColors.Control;
        b.UseVisualStyleBackColor = true;
    }

    void StartGif(byte[] data, int veilAlpha)
    {
        _gifTimer.Tick += (_, _) => OnGifTick();
        Activated += (_, _) => ResumeGif();
        Deactivate += (_, _) => PauseGif();
        Task.Run(() => DecodeAllFrames(data, veilAlpha)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            PostToUi(() =>
            {
                if (t.Result.Frames.Count < 2) return;
                _frames = t.Result.Frames;
                _delaysMs = t.Result.Delays;
                if (GetForegroundWindow() == Handle) ResumeGif();
            });
        });
    }

    void ResumeGif()
    {
        if (_frames == null || _gifTimer.Enabled) return;
        _gifClock.Start();
        _frameShownAt = _gifClock.ElapsedMilliseconds;
        _gifTimer.Start();
    }

    void PauseGif()
    {
        _gifTimer.Stop();
        _gifClock.Stop();
    }

    void OnGifTick()
    {
        if (_frames == null || WindowState == FormWindowState.Minimized || !Visible) return;
        long now = _gifClock.ElapsedMilliseconds;
        int index = _frameIndex;
        while (now - _frameShownAt >= _delaysMs[index])
        {
            _frameShownAt += _delaysMs[index];
            index = (index + 1) % _frames.Count;
        }
        if (index == _frameIndex) return;
        _frameIndex = index;
        RedrawBackground();
        foreach (var c in _transparentControls) c.Invalidate(false);
    }

    void RedrawBackground()
    {
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (_backgroundCache == null || _backgroundCache.Size != size)
        {
            _backgroundCache?.Dispose();
            _backgroundCache = new Bitmap(size.Width, size.Height, FastFormat);
        }
        using var g = Graphics.FromImage(_backgroundCache);
        var frame = _frames != null ? _frames[_frameIndex] : _firstFrame;
        if (frame == null) { g.Clear(PageColor); return; }
        int sw = frame.Width, sh = frame.Height;

        float scale = Math.Max((float)size.Width / sw, (float)size.Height / sh);
        float w = sw * scale, h = sh * scale;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        using var visible = new Region(new Rectangle(Point.Empty, size));
        foreach (var c in OpaqueControls())
            if (c is { Visible: true, IsHandleCreated: true })
                visible.Exclude(RectangleToClient(c.RectangleToScreen(c.ClientRectangle)));
        g.Clip = visible;
        g.DrawImage(frame, new Rectangle((int)((size.Width - w) / 2), 0, (int)Math.Ceiling(w), (int)Math.Ceiling(h)));
    }

    Control[] OpaqueControls() =>
    [
        _logBox, _urlBox, _outDirBox, _quality, _progress, _updateBanner, _platform,
        _download, _cancel, _showFile, _setup, _cookiesPick, _cookiesClear, _outPick,
    ];

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!HasBackground || ClientSize.Width <= 0 || ClientSize.Height <= 0) { base.OnPaintBackground(e); return; }
        if (_backgroundCache == null || _backgroundCache.Size != ClientSize) RedrawBackground();
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.None;
        e.Graphics.DrawImage(_backgroundCache!, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
    }

    protected override void OnResize(EventArgs e)
    {
        if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; return; }
        base.OnResize(e);
        if (!HasBackground) return;
        RedrawBackground();
        Invalidate(true);
    }

    Quality SelectedQuality => _quality.SelectedItem as Quality ?? Quality.All[0];

    DownloadMode Mode => _audio.Checked ? DownloadMode.Audio : DownloadMode.Video;

    void UpdateQualityHint()
    {
        var q = SelectedQuality;
        _qualityHint.Text = Mode == DownloadMode.Video && _current == Platform.Instagram
            ? "Instagram: the app figures out whether the link is a reel, a photo or a carousel. " +
              "Photos are saved as jpg, videos as mp4, and a carousel is saved in full. Quality only affects videos."
            : Mode == DownloadMode.Audio
            ? "Quality doesn't matter for mp3: the best available audio is saved at 320 kbps."
            : q.MaxHeight == 0
                ? "Downloads the best quality the site has. Long 4K videos can take several GB."
                : $"Downloads the best quality up to {q.MaxHeight}p, or the closest one if the video doesn't have it. " +
                  "For vertical videos (TikTok, Reels, Shorts) the short side counts: 720p means 720x1280.";
    }

    void ShowCookieHelp()
    {
        UsageLog.Write("Opened cookie help");
        var open = new TaskDialogButton("Open extension page") { AllowCloseDialog = false };
        open.Click += (_, _) => { UsageLog.Write("Opened extension page"); OpenUrl(ExtensionUrl); };
        var page = new TaskDialogPage
        {
            Caption = "How to get cookies.txt",
            Heading = "Getting cookies",
            Text = CookieHowTo,
            Icon = TaskDialogIcon.Information,
            EnableLinks = true,
            AllowCancel = true,
            Buttons = { open, new TaskDialogButton("Close") },
        };
        page.LinkClicked += (_, e) => OpenUrl(e.LinkHref == "ext" ? ExtensionUrl : e.LinkHref);
        TaskDialog.ShowDialog(this, page);
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    void LogError(string kind, string message, string? details = null)
    {
        ErrorLog.Write(kind, message, details);
        _errorLogLink.Visible = true;
    }

    void OpenErrorLog()
    {
        UsageLog.Write("Opened error.log");
        if (!ErrorLog.Exists) { _errorLogLink.Visible = false; return; }
        try { Process.Start(new ProcessStartInfo(ErrorLog.FilePath) { UseShellExecute = true }); }
        catch { Process.Start("notepad.exe", $"\"{ErrorLog.FilePath}\""); }
    }

    void UpdatePlatform()
    {
        var uri = PlatformDetector.ParseUrl(_url.Text);
        var platform = uri == null ? Platform.Unknown : PlatformDetector.FromHost(uri.Host);
        _platform.Visible = uri != null;
        _platform.Text = PlatformDetector.DisplayName(platform);
        _platform.BackColor = PlatformColor(platform) ?? Color.DimGray;
        if (platform == _current) return;
        _current = platform;
        StyleDownloadButton(PlatformColor(platform));
        UpdateCookiesView();
        UpdateQualityHint();
    }

    void StyleDownloadButton(Color? color)
    {
        if (color is not { } c)
        {
            _download.FlatStyle = FlatStyle.Standard;
            KeepButtonThemed(_download);
            _download.ForeColor = SystemColors.ControlText;
            return;
        }
        _download.FlatStyle = FlatStyle.Flat;
        _download.FlatAppearance.BorderSize = 0;
        _download.FlatAppearance.MouseOverBackColor = Blend(c, Color.White, 0.18f);
        _download.FlatAppearance.MouseDownBackColor = Blend(c, Color.Black, 0.22f);
        _download.BackColor = c;
        _download.ForeColor = Color.White;
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    string? CookiesFor(Platform p) =>
        p != Platform.Unknown && _settings.Cookies.TryGetValue(p.ToString(), out var path) ? path : null;

    void UpdateCookiesView()
    {
        var name = PlatformDetector.DisplayName(_current);
        var path = CookiesFor(_current);
        var file = Path.GetFileName(path);
        _cookiesClear.Enabled = path != null && _cts == null;

        if (_current == Platform.Unknown)
        {
            var saved = Enum.GetValues<Platform>().Where(p => CookiesFor(p) != null).Select(PlatformDetector.DisplayName).ToList();
            SetCookieStatus(saved.Count > 0
                ? "Saved cookies for: " + string.Join(", ", saved) + ". They are used automatically."
                : "Not needed for most videos. For age-restricted or private ones, drop a cookies.txt file here.", DimColor);
        }
        else if (path == null)
            SetCookieStatus($"None set. Not needed for most videos. For age-restricted or private ones, drop a cookies.txt from {name} here.", DimColor);
        else if (!File.Exists(path))
            SetCookieStatus($"File {file} not found. Choose it again.", WarnColor);
        else if (_current != Platform.YouTube)
            SetCookieStatus($"OK: {file} ({name} cookies)", OkColor);
        else if (CookieFile.Analyze(path).YouTubeLoggedIn)
            SetCookieStatus($"OK: {file}, signed in to YouTube", OkColor);
        else
            SetCookieStatus($"{file}: YouTube has signed this session out. Export the cookies again.", WarnColor);
    }

    void SetCookieStatus(string text, Color color) => (_cookiesStatus.Text, _cookiesStatus.ForeColor) = (text, color);

    void AssignCookies(string path)
    {
        var info = CookieFile.Analyze(path);
        var file = Path.GetFileName(path);
        List<Platform> targets = info.Platforms.Count > 0 ? [.. info.Platforms] : _current != Platform.Unknown ? [_current] : [];
        var problem = info.IsJson ? ("JSON format", "These cookies are in JSON format. Export them in Netscape format (cookies.txt) instead.")
                    : !info.IsNetscape ? ("not a cookies.txt file", $"\"{file}\" doesn't look like a cookies.txt file.")
                    : targets.Count == 0 ? ("no valid YouTube/TikTok/Instagram cookies", "The file has no valid YouTube, TikTok or Instagram cookies.")
                    : default((string Log, string Status)?);
        if (problem is var (why, message))
        {
            UsageLog.Write($"Cookies rejected: {file} ({why})");
            ShowStatus(message, ErrorColor);
            return;
        }

        foreach (var p in targets) _settings.Cookies[p.ToString()] = path;
        _settings.Save();
        UpdateCookiesView();
        var sites = string.Join(", ", targets.Select(PlatformDetector.DisplayName));
        UsageLog.Write($"Cookies added: {file} for {sites}" +
                       (targets.Contains(Platform.YouTube) ? (info.YouTubeLoggedIn ? " (YouTube: signed in)" : " (YouTube: not signed in)") : ""));
        ShowStatus("Cookies saved for: " + sites, OkColor);
    }

    void PickCookies()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose cookies.txt",
            Filter = "Cookie files (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(CookiesFor(_current)) ?? AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AssignCookies(dlg.FileName);
        else UsageLog.Write("Choose cookies: canceled");
    }

    void ClearCookies()
    {
        if (_current == Platform.Unknown) return;
        UsageLog.Write($"Cookies removed for {PlatformDetector.DisplayName(_current)}");
        _settings.Cookies.Remove(_current.ToString());
        _settings.Save();
        UpdateCookiesView();
    }

    void PickOutDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Where to save downloads",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_outDir.Text) ? _outDir.Text : AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _outDir.Text = dlg.SelectedPath;
        UsageLog.Write($"Folder chosen: {dlg.SelectedPath}");
    }

    void HookDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += OnDragEnter;
        c.DragDrop += OnDragDrop;
        foreach (Control child in c.Controls) HookDrop(child);
    }

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool accept = _cts == null && e.Data != null &&
                      (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text));
        e.Effect = !accept ? DragDropEffects.None
                 : (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : DragDropEffects.Link;
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            UsageLog.Write($"Dropped {files.Length} file(s): {string.Join(", ", files.Select(Path.GetFileName))}");
            foreach (var f in files) AssignCookies(f);
            return;
        }
        var text = e.Data?.GetData(DataFormats.UnicodeText) as string ?? e.Data?.GetData(DataFormats.Text) as string;
        if (string.IsNullOrWhiteSpace(text)) return;
        UsageLog.Write($"Dropped link: {text.Trim()}");
        _url.Text = text.Trim();
    }

    async Task RunBusyAsync(string job, Func<CancellationToken, Task> work)
    {
        if (_cts != null) return;
        using var cts = _cts = new CancellationTokenSource();
        ClearLog();
        _progress.Value = 0;
        SetBusy(true);
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            UsageLog.Write(job + ": canceled");
            if (!IsDisposed) ShowStatus("Canceled.", NeutralColor);
        }
        catch (Win32Exception ex)
        {
            LogError(job.ToUpperInvariant() + " ERROR", "yt-dlp not found: " + ex.Message);
            ShowStatus("yt-dlp not found. Click Install components.", ErrorColor);
            _hasYtDlp = false;
            ShowEngineState();
        }
        finally
        {
            _cts = null;
            if (!IsDisposed) SetBusy(false);
        }
    }

    List<Components.Item> MissingComponents()
    {
        var missing = new List<Components.Item>();
        if (!_hasYtDlp) missing.Add(Components.YtDlpExe);
        if (!YtDlp.HasFfmpeg()) missing.Add(Components.Ffmpeg);
        if (!YtDlp.HasJsRuntime()) missing.Add(Components.Deno);
        return missing;
    }

    async Task CheckEngineAsync()
    {
        string? version = null;
        try
        {
            var (code, output) = await YtDlp.RunAsync(["--version"], null);
            if (code == 0)
                version = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        }
        catch (Win32Exception) { }
        if (IsDisposed) return;
        _localYtDlp = version;
        _hasYtDlp = version != null;
        ShowEngineState();
    }

    void ShowEngineState()
    {
        RefreshUpdateBanner();
        var missing = MissingComponents();
        _setup.Visible = missing.Count > 0;
        _setup.Text = $"Install components (~{missing.Sum(m => m.ApproxMb)} MB)";
        (_engine.Text, _engine.ForeColor) = missing.Count == 0
            ? ($"yt-dlp {_localYtDlp} | {YtDlp.Locate().Description}", DimColor)
            : ("Missing: " + string.Join(", ", missing.Select(m => m.Name)) + ". Click Install components.", WarnColor);
    }

    Task InstallComponentsAsync() => RunBusyAsync("Install components", async ct =>
    {
        var missing = MissingComponents();
        var names = string.Join(", ", missing.Select(m => m.Name));
        UsageLog.Write("Install components: " + names);
        try
        {
            await Components.InstallAsync(missing,
                (name, fraction) => PostToUi(() =>
                {
                    _progress.Value = Math.Clamp((int)(fraction * _progress.Maximum), 0, _progress.Maximum);
                    ShowStatus($"Downloading {name}: {(int)(fraction * 100)}%", NeutralColor);
                }),
                line => PostToUi(() => Log(line)),
                ct);
            ShowStatus("Components installed. You can download now.", OkColor);
            UsageLog.Write("Components installed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowStatus("Couldn't download components: " + ex.Message, ErrorColor);
            Log(ex.ToString());
            LogError("COMPONENT INSTALL ERROR", names + ": " + ex.Message, ex.ToString());
        }
        finally
        {
            if (!IsDisposed) await CheckEngineAsync();
        }
    });

    Task UpdateYtDlpAsync() => RunBusyAsync("Update yt-dlp", async ct =>
    {
        UsageLog.Write($"Update yt-dlp: started (current {_localYtDlp ?? "unknown"}, {YtDlp.Locate().Description})");
        ShowStatus("Updating yt-dlp...", NeutralColor);
        var (code, output) = await YtDlp.UpdateAsync(line => PostToUi(() => Log(line)), ct);
        if (code != 0)
        {
            ShowStatus("Update failed. See the log below.", ErrorColor);
            LogError("YT-DLP UPDATE ERROR", $"{YtDlp.Locate().Description} | exit code {code}", ErrorLog.Tail(output));
            return;
        }
        UsageLog.Write("Update yt-dlp: done");
        ShowStatus("Updated. Checking that everything is current...", OkColor);
        _updateInfo = null;
        await CheckEngineAsync();
        await CheckForUpdatesAsync();
        if (_updateInfo is { Updates.Count: 0 }) ShowStatus("Everything is up to date.", OkColor);
    });

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    static string Describe(int photos, int videos) =>
        photos > 0 && videos > 0 ? $"{Count(photos, "photo", "photos")} and {Count(videos, "video", "videos")}"
        : photos > 0 ? Count(photos, "photo", "photos")
        : Count(videos, "video", "videos");

    async Task DownloadAsync()
    {
        if (_cts != null) return;
        if (!_hasYtDlp)
        {
            var missing = MissingComponents();
            var answer = MessageBox.Show(this,
                $"Downloading needs these components: {string.Join(", ", missing.Select(m => m.Name))} " +
                $"(~{missing.Sum(m => m.ApproxMb)} MB, one time). Download them now?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            UsageLog.Write($"Asked to install missing components before downloading: {answer}");
            if (answer != DialogResult.Yes) return;
            await InstallComponentsAsync();
            if (!_hasYtDlp) return;
        }
        var uri = PlatformDetector.ParseUrl(_url.Text);
        if (uri == null)
        {
            UsageLog.Write($"Clicked Download without a valid link: \"{_url.Text.Trim()}\"");
            ShowStatus("Paste a video link first.", ErrorColor);
            FocusLink();
            return;
        }
        var outDir = _outDir.Text.Trim();
        if (outDir.Length == 0) outDir = AppContext.BaseDirectory.TrimEnd('\\');
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            UsageLog.Write($"Can't create the folder \"{outDir}\": {ex.Message}");
            ShowStatus($"Can't create the folder: {ex.Message}", ErrorColor);
            return;
        }
        SaveSettings();

        var platform = PlatformDetector.FromHost(uri.Host);
        var site = PlatformDetector.DisplayName(platform);
        var cookies = CookiesFor(platform) is { } c && File.Exists(c) ? c : null;
        var cookieName = Path.GetFileName(cookies) ?? "none";
        var request = new Downloader.Request(platform, uri, Mode, SelectedQuality.MaxHeight, outDir, cookies, _cover.Checked);
        bool instagramAuto = Downloader.IsInstagramAuto(request);
        var quality = SelectedQuality.Label.ToLowerInvariant();
        var what = request.Mode == DownloadMode.Audio ? "audio (mp3)"
                 : instagramAuto ? $"photos and videos (post type detected automatically), {quality}"
                 : $"video (mp4), {quality}";
        var loginProblem = Hints.LoginProblem.None;

        await RunBusyAsync("Download", async ct =>
        {
            _lastFile = null;
            _showFile.Enabled = false;
            _hideNoVideoWarnings = instagramAuto;
            Log($"{site} | {what} | {uri.AbsoluteUri}");
            if (cookies != null) Log("Cookies: " + cookieName);
            if (platform == Platform.YouTube && cookies != null && !CookieFile.Analyze(cookies).YouTubeLoggedIn)
                Log("Warning: these YouTube cookies are not signed in, so age-restricted videos won't download.");
            Log("");
            ShowStatus("Preparing download...", NeutralColor);
            UsageLog.Write($"Download started: {uri.AbsoluteUri} | {site} | {what} | cookies: {cookieName} | folder: {outDir}");
            StartHeartbeat();

            var r = await Downloader.RunAsync(request, line => PostToUi(() => OnOutput(line)), ct);
            if (r.ExitCode != 0)
            {
                var (hint, login) = Hints.Diagnose(r.Output, platform);
                ShowStatus((r.Photos > 0 ? $"Photos saved ({r.Photos}), but the videos failed. " : "Failed. ") +
                           (hint ?? "See the log below for details."), ErrorColor);
                if (hint != null) Log("-> " + hint);
                LogError("DOWNLOAD ERROR", $"{site} | {what} | {uri.AbsoluteUri} | exit code {r.ExitCode}" + (hint != null ? $"\n    hint: {hint}" : ""),
                         $"yt-dlp: {YtDlp.Locate().Description} | cookies: {cookieName}\n" + ErrorLog.Tail(r.Output));
                Log("-> Details saved to " + ErrorLog.FilePath);
                loginProblem = login;
                return;
            }

            _progress.Value = _progress.Maximum;
            _lastFile = r.Saved.FirstOrDefault();
            _showFile.Enabled = _lastFile != null && File.Exists(_lastFile);
            var first = Path.GetFileName(_lastFile) ?? "";
            var kinds = Describe(r.Photos, r.Videos);
            var coverNote = r.Covers.Count switch { 0 => "", 1 => " + cover", var n => $" + {n} covers" };
            var got = r.Videos == 1 && request.Mode == DownloadMode.Video && r.Resolution is { } res && !first.Contains(res) ? $" ({res})" : "";
            if (r.Saved.Count == 0)
                ShowStatus(instagramAuto ? "Nothing to download in this post." : "Done.", instagramAuto ? WarnColor : OkColor);
            else
                ShowStatus(r.Saved.Count == 1 ? $"Done: {first}{got}{coverNote}" : $"Done: {kinds} ({first} and {r.Saved.Count - 1} more){coverNote}", OkColor);

            if (instagramAuto && r.Saved.Count > 0)
            {
                Log("");
                Log($"Post: {kinds}");
                foreach (var f in r.Saved) Log("Saved: " + f);
            }
            foreach (var cover in r.Covers) Log("Cover: " + cover);
            if (request.SaveCover && r.Covers.Count == 0 && r.Videos > 0)
                Log("-> No cover was saved: the site didn't provide one.");
            UsageLog.Write((r.Saved.Count > 0
                ? $"Download finished: {kinds}: {string.Join(", ", r.Saved.Select(Path.GetFileName))}"
                : "Download finished: nothing saved") +
                (r.Covers.Count > 0 ? $" | covers: {string.Join(", ", r.Covers.Select(Path.GetFileName))}" : ""));
        });
        if (loginProblem != Hints.LoginProblem.None && !IsDisposed) ShowLoginProblem(loginProblem, platform, cookies);
    }

    void ShowLoginProblem(Hints.LoginProblem problem, Platform platform, string? cookies)
    {
        var site = PlatformDetector.DisplayName(platform);
        var (caption, heading, text) = problem switch
        {
            Hints.LoginProblem.AgeRestricted => (
                "Age-restricted video",
                $"{site} only shows this video to signed-in users",
                cookies == null
                    ? $"To download it, you need a cookies.txt from {site}, taken from a browser where you are signed in. " +
                      "Drop the file into this window and click Download again."
                    : $"{Path.GetFileName(cookies)} is loaded, but it is no longer signed in. " +
                      $"Get new cookies from {site} and drop the new file into this window."),
            Hints.LoginProblem.CookiesRevoked => (
                "Cookies expired",
                $"{site} no longer accepts these cookies",
                $"Get new cookies from {site}, drop the new file into this window, then click Download again."),
            _ => (
                "Sign-in required",
                $"{site} won't show this without signing in",
                $"You need a cookies.txt from {site}, taken from a browser where you are signed in with an account that can see it. " +
                "Drop the file into this window and click Download again."),
        };

        var howTo = new TaskDialogButton("How do I get cookies?");
        var pick = new TaskDialogButton("Choose cookies file...");
        var page = new TaskDialogPage
        {
            Caption = caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            Buttons = { howTo, pick, new TaskDialogButton("Close") },
        };
        UsageLog.Write($"Shown dialog: {caption}");
        var clicked = TaskDialog.ShowDialog(this, page);
        UsageLog.Write($"Dialog {caption}: clicked {clicked.Text}");
        if (clicked == howTo) ShowCookieHelp();
        else if (clicked == pick) PickCookies();
    }

    readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 1000 };
    DateTime _runStarted, _lastProgressAt, _lastProgressLogAt;
    string _phase = "Preparing download";
    int _progressLineStart = -1;

    void StartHeartbeat()
    {
        _runStarted = DateTime.Now;
        _lastProgressAt = _lastProgressLogAt = DateTime.MinValue;
        _phase = "Preparing download";
        SetIndeterminate(true);
        _heartbeat.Start();
    }

    void StopHeartbeat()
    {
        _heartbeat.Stop();
        SetIndeterminate(false);
    }

    void OnHeartbeat()
    {
        if ((DateTime.Now - _lastProgressAt).TotalSeconds < 1.5) return;
        SetIndeterminate(true);
        ShowStatus($"{_phase}... {(int)(DateTime.Now - _runStarted).TotalSeconds}s", NeutralColor);
    }

    void SetIndeterminate(bool on)
    {
        var style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (_progress.Style == style) return;
        _progress.Style = style;
        if (on) _progress.MarqueeAnimationSpeed = 30;
    }

    string? PhaseFor(string line) =>
        line == Downloader.RetryNote ? "Network error, trying again" :
        line.Contains("Solving JS challenges") ? "YouTube: solving the JS challenge" :
        line.Contains("Downloading item") ? "Downloading post items" :
        line.StartsWith("[download] Destination") ? "Starting download" :
        line.StartsWith("[Merger]") ? "Merging video and audio" :
        line.StartsWith("[ExtractAudio]") ? "Converting to mp3" :
        line.StartsWith("[VideoConvertor]") || line.StartsWith("[Fixup") ? "Processing the file" :
        line.Contains("thumbnail") ? (_hideNoVideoWarnings ? "Saving photos" : "Saving the cover") :
        line.Contains("Retrying") ? "Slow connection, retrying" :
        line.Contains("Extracting URL") || line.Contains("Downloading webpage") || line.Contains("JSON") ||
        line.Contains("Downloading player") || line.Contains("client config") ? "Getting video info" :
        null;

    void SetProgressLogLine(string text)
    {
        if (_progressLineStart < 0 || _progressLineStart > _log.TextLength)
        {
            _progressLineStart = _log.TextLength;
            _log.AppendText(text + Environment.NewLine);
            return;
        }
        _log.Select(_progressLineStart, _log.TextLength - _progressLineStart);
        _log.SelectedText = text + Environment.NewLine;
    }

    void ClearLog()
    {
        _log.Clear();
        _progressLineStart = -1;
    }

    void OnOutput(string line)
    {
        if (line.StartsWith(DownloadPlan.ProgressPrefix, StringComparison.Ordinal))
        {
            var parts = line[DownloadPlan.ProgressPrefix.Length..].Split('|');
            var m = PercentRx.Match(parts[0]);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                _lastProgressAt = DateTime.Now;
                SetIndeterminate(false);
                _progress.Value = Math.Clamp((int)(pct * 10), 0, _progress.Maximum);
                var text = $"{pct.ToString("0.0", CultureInfo.InvariantCulture)}%";
                if (parts.Length > 1 && IsKnown(parts[1])) text += ", " + parts[1].Trim();
                if (parts.Length > 2 && IsKnown(parts[2])) text += ", " + parts[2].Trim() + " left";
                ShowStatus("Downloading: " + text, NeutralColor);
                if (pct >= 100 || (DateTime.Now - _lastProgressLogAt).TotalMilliseconds >= 250)
                {
                    _lastProgressLogAt = DateTime.Now;
                    SetProgressLogLine("[progress] " + text);
                }
            }
            return;
        }
        if (line.StartsWith("[info] Writing '%(") || line.StartsWith("[MetadataParser]")) return;
        if (_hideNoVideoWarnings &&
            (line.Contains("There is no video in this post") || line.Contains("No video formats found") ||
             line.Contains("Requested format is not available") || line.Contains("does not pass filter")))
            return;
        if (PhaseFor(line) is { } phase)
        {
            _phase = phase;
            if ((DateTime.Now - _lastProgressAt).TotalSeconds >= 1.5 || phase != "Starting download")
            {
                _lastProgressAt = DateTime.MinValue;
                SetIndeterminate(true);
                ShowStatus($"{_phase}... {(int)(DateTime.Now - _runStarted).TotalSeconds}s", NeutralColor);
            }
        }
        Log(line);
    }

    static bool IsKnown(string s)
    {
        s = s.Trim();
        return s.Length > 0 && s != "NA" && s != "N/A" && !s.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    void SetBusy(bool busy)
    {
        foreach (var c in new Control[] { _download, _setup, _update, _updateNow, _cookiesPick, _cookiesClear, _outPick, _video, _audio, _cover })
            c.Enabled = !busy;
        _cancel.Enabled = busy;
        _url.ReadOnly = _outDir.ReadOnly = busy;
        _quality.Enabled = !busy && Mode == DownloadMode.Video;
        if (busy) return;
        StopHeartbeat();
        UpdateCookiesView();
        FocusLink();
    }

    void FocusLink()
    {
        if (ContainsFocus) _url.Focus();
    }

    void ShowInFolder()
    {
        if (_lastFile == null || !File.Exists(_lastFile)) return;
        UsageLog.Write($"Show in folder: {_lastFile}");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFile}\"") { UseShellExecute = true });
    }

    void ShowStatus(string text, Color color) => (_status.Text, _status.ForeColor) = (text, color);

    void Log(string line)
    {
        _progressLineStart = -1;
        _log.AppendText(line + Environment.NewLine);
    }
}
