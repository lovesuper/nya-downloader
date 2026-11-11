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
    readonly RadioButton _m4a = new() { Text = "Audio (m4a)", AutoSize = true }, _mp3 = new() { Text = "Audio (mp3)", AutoSize = true };
    readonly CheckBox _cover = new() { Text = "Save cover (jpg)", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
    readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Left, Width = 190, MinimumSize = new Size(190, 0) };
    readonly ComboBox _subtitles = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Left, Width = 150, MinimumSize = new Size(150, 0) };
    readonly CueTextBox _from = new() { BorderStyle = BorderStyle.None, Cue = "start" }, _to = new() { BorderStyle = BorderStyle.None, Cue = "end" };
    readonly Button _chapters = Btn("Chapters...");
    readonly Label _qualityHint = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6) };
    readonly Button _download = Btn("Download", 20), _cancel = Btn("Cancel"), _showFile = Btn("Show in folder"), _setup = Btn("Install components");
    readonly ProgressBar _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Maximum = 1000, Height = 18 };
    readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 6, 3, 6) };
    readonly LogView _log = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly FieldBorder _urlBox, _outDirBox, _logBox, _fromBox, _toBox;
    readonly Label _engine = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = DimColor, Margin = new Padding(3, 3, 0, 3) };
    readonly LinkLabel _update = Link("Update yt-dlp"), _errorLogLink = Link("Open error.log");
    readonly Panel _updateBanner = Banner(), _appBanner = Banner();
    readonly Label _updateText = BannerText(), _appUpdateText = BannerText();
    readonly Button _updateNow = BannerButton("Update"), _appUpdateNow = BannerButton("Update");
    readonly LinkLabel _appNotes = new() { Text = "What's new", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 3, 12, 3) };
    readonly TaskbarProgress _taskbar = new();
    AppRelease? _appRelease;

    static Panel Banner() => new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, BackColor = Color.FromArgb(0xFF, 0xF4, 0xCE), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3, 6, 3, 0) };
    static Label BannerText() => new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Color.FromArgb(0x5C, 0x40, 0x00) };
    static Button BannerButton(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 2, 12, 2), Anchor = AnchorStyles.Right, BackColor = SystemColors.Control, UseVisualStyleBackColor = true };

    bool _selectAllOnMouseUp;
    YtDlp.UpdateInfo? _updateInfo;
    string? _localYtDlp;
    bool _hasYtDlp = true;
    CancellationTokenSource? _cts;
    string? _lastFile;
    Platform _current = Platform.Unknown;
    string? _lastLink;
    bool _hideNoVideoWarnings;

    public MainForm()
    {
        _urlBox = new FieldBorder(_url, new Padding(6, 4, 6, 4));
        _outDirBox = new FieldBorder(_outDir, new Padding(6, 4, 6, 4));
        _logBox = new FieldBorder(_log, new Padding(6, 4, 2, 4));
        _fromBox = new FieldBorder(_from, new Padding(6, 4, 6, 4)) { Width = 84 };
        _toBox = new FieldBorder(_to, new Padding(6, 4, 6, 4)) { Width = 84 };
        _settings.Look = _settings.Look.Checked(_gifNames.Length);
        LoadGif(_settings.Look.Gif);
        _canvas.IceReady += () => PostToUi(RefreshBackground);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Text = $"{AppInfo.Name} - YouTube, TikTok, Instagram";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        Padding = new Padding(ResizeBorder);
        ClientSize = new Size(780, 660);
        MinimumSize = new Size(620, 540);
        StartPosition = FormStartPosition.CenterScreen;
        if (LaunchMinimized) WindowState = FormWindowState.Minimized;
        AcceptButton = _download;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (iconStream != null) Icon = new Icon(iconStream);

        var fields = Grid(new() { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 8, Padding = new Padding(12, 12, 12, 0) }, stretchColumn: 1);
        void Add(Control c, int col, int row, int span = 1) { fields.Controls.Add(c, col, row); fields.SetColumnSpan(c, span); }
        Add(Caption("Link:"), 0, 0); Add(_urlBox, 1, 0); Add(_platform, 2, 0);
        Add(Caption("Cookies:"), 0, 1); Add(_cookiesStatus, 1, 1); Add(Row(_cookiesPick, _cookiesClear), 2, 1);
        Add(_cookiesHelp, 1, 2);
        Add(Caption("Folder:"), 0, 3); Add(_outDirBox, 1, 3); Add(_outPick, 2, 3);
        Add(Caption("Mode:"), 0, 4); Add(Row(_video, _m4a, _mp3, _cover), 1, 4, 2);
        Add(Caption("Quality:"), 0, 5); Add(Row(_quality, Caption("Subtitles:", 18), _subtitles), 1, 5, 2);
        Add(Caption("Fragment:"), 0, 6); Add(Row(_fromBox, Caption("to"), _toBox, _chapters), 1, 6, 2);
        Add(_qualityHint, 1, 7);

        var bannerGrid = Grid(new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }, stretchColumn: 0);
        bannerGrid.Controls.AddRange([_updateText, _updateNow]);
        _updateBanner.Controls.Add(bannerGrid);
        var appGrid = Grid(new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 }, stretchColumn: 0);
        appGrid.Controls.AddRange([_appUpdateText, _appNotes, _appUpdateNow]);
        _appBanner.Controls.Add(appGrid);
        var lower = Grid(new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12, 12, 12, 12) }, stretchColumn: 0, stretchRow: 3);
        (_fieldsGrid, _lowerGrid) = (fields, lower);
        lower.Controls.AddRange([Row(_download, _cancel, _showFile, _setup), _progress, _status, _logBox, Row(_engine, _update, _errorLogLink), _appBanner, _updateBanner]);

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
        if (HasBackground)
        {
            foreach (var grid in new[] { fields, lower, titleBar }) MakeTransparent(grid);
            _logBox.MakeGlass();
            _log.BackColor = Color.Transparent;
            _log.Padding = new Padding(4, 4, 0, 4);
            _transparentControls.AddRange([_logBox, _log]);
            foreach (var c in OpaqueControls().Concat([fields, lower]))
            {
                c.Move += (_, _) => BackgroundMoved();
                c.Resize += (_, _) => BackgroundMoved();
                c.VisibleChanged += (_, _) => BackgroundMoved();
            }
            foreach (var c in TextControls())
            {
                c.TextChanged += (_, _) => TextMoved();
                c.SizeChanged += (_, _) => TextMoved();
                c.LocationChanged += (_, _) => TextMoved();
                c.VisibleChanged += (_, _) => TextMoved();
            }
        }

        _outDir.Text = string.IsNullOrWhiteSpace(_settings.OutputDir) ? AppContext.BaseDirectory.TrimEnd('\\') : _settings.OutputDir;
        _quality.Items.AddRange(Quality.All);
        _quality.SelectedItem = Quality.All.FirstOrDefault(q => q.MaxHeight == _settings.MaxHeight) ?? Quality.All[0];
        _quality.Enabled = !_settings.AudioOnly;
        _m4a.Checked = _settings.AudioOnly && _settings.AudioFormat == "m4a";
        _mp3.Checked = _settings.AudioOnly && !_m4a.Checked;
        _video.Checked = !_settings.AudioOnly;
        _cover.Checked = _settings.SaveCover;
        _subtitles.Items.AddRange(SubtitleLanguage.All);
        _subtitles.SelectedItem = SubtitleLanguage.From(_settings.Subtitles);
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
        foreach (var rb in new[] { _video, _m4a, _mp3 })
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked) return;
                _quality.Enabled = Mode == DownloadMode.Video;
                UpdateQualityHint();
                UsageLog.Write($"Mode: {rb.Text}");
            };
        _quality.SelectedIndexChanged += (_, _) => { UpdateQualityHint(); UsageLog.Write($"Quality: {SelectedQuality.Label}"); };
        _cover.CheckedChanged += (_, _) => UsageLog.Write($"Save cover: {(_cover.Checked ? "on" : "off")}");
        _subtitles.SelectedIndexChanged += (_, _) => UsageLog.Write($"Subtitles: {SelectedSubtitles.Name}");
        _from.TextChanged += (_, _) => UpdateQualityHint();
        _to.TextChanged += (_, _) => UpdateQualityHint();
        _chapters.Click += async (_, _) => await PickChaptersAsync();
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
        _appUpdateNow.Click += async (_, _) => await UpdateAppAsync();
        _appNotes.LinkClicked += (_, _) => { UsageLog.Write("Opened release notes"); if (_appRelease != null) OpenUrl(_appRelease.PageUrl); };
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

    static Label Caption(string text, int left = 3) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(left, 3, 3, 3) };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        f.Controls.AddRange(controls);
        return f;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (HasBackground) StartGif();
        UsageLog.Write($"App started | background: {_gifName ?? "none"} ({_settings.Look}) | mode: {Mode} | quality: {SelectedQuality.Label} | " +
                       $"subtitles: {SelectedSubtitles.Name} | folder: {_outDir.Text} | " +
                       "saved cookies: " + (_settings.Cookies.Count > 0
                           ? string.Join(", ", _settings.Cookies.Select(kv => $"{kv.Key}={Path.GetFileName(kv.Value)}"))
                           : "none"));
        if (Environment.ProcessPath is { } exe) AppUpdate.CleanUp(exe);
        var appCheck = CheckAppUpdateAsync();
        await CheckEngineAsync();
        if (IsDisposed) return;
        UsageLog.Write($"yt-dlp: {_localYtDlp ?? "not found"} ({YtDlp.Locate().Description})" +
                       (MissingComponents() is { Count: > 0 } m ? " | missing: " + string.Join(", ", m.Select(x => x.Name)) : ""));
        await CheckForUpdatesAsync();
        await appCheck;
    }

    async Task CheckAppUpdateAsync()
    {
        var release = await AppUpdate.CheckAsync();
        if (IsDisposed) return;
        if (release == null)
        {
            UsageLog.Write("App update check: couldn't check (offline?)");
            return;
        }
        bool newer = AppUpdate.IsNewer(release, AppInfo.Version);
        UsageLog.Write(newer ? $"App update check: {AppInfo.Name} {release.Version} is out (you have {AppInfo.Version})"
                             : $"App update check: {AppInfo.Version} is the latest");
        if (!newer) return;
        _appRelease = release;
        _appUpdateText.Text = $"{AppInfo.Name} {release.Version} is out (you have {AppInfo.Version}).";
        _appUpdateNow.Visible = release.ExeUrl != null;
        _appBanner.Visible = true;
    }

    async Task UpdateAppAsync()
    {
        if (_appRelease is not { } release || _cts != null) return;
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        UsageLog.Write($"Update app: {AppInfo.Version} -> {release.Version}");
        bool ready = false;
        await RunBusyAsync("Update app", async ct =>
        {
            Log($"Downloading {AppInfo.Name} {release.Version}: {release.ExeUrl}");
            ShowStatus($"Downloading {AppInfo.Name} {release.Version}...", NeutralColor);
            try
            {
                await AppUpdate.DownloadAsync(release, exe, f => PostToUi(() =>
                {
                    ShowProgress((int)(f * 1000));
                    ShowStatus($"Downloading {AppInfo.Name} {release.Version}: {(int)(f * 100)}%", NeutralColor);
                }), ct);
                AppUpdate.Swap(exe);
                ready = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(ex.Message);
                LogError("APP UPDATE ERROR", $"{AppInfo.Version} -> {release.Version}: {ex.Message}", ex.ToString());
                if (ex is UnauthorizedAccessException)
                {
                    ShowStatus("Can't replace the app in this folder, so the download page is opening instead.", ErrorColor);
                    OpenUrl(release.PageUrl);
                }
                else ShowStatus("Couldn't update the app: " + ex.Message, ErrorColor);
            }
        });
        if (!ready || IsDisposed) return;
        UsageLog.Write($"Update app: installed {release.Version}, restarting");
        SaveSettings();
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = LaunchMinimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal });
        }
        catch (Exception ex)
        {
            LogError("APP UPDATE ERROR", "Couldn't restart: " + ex.Message, ex.ToString());
            ShowStatus($"Updated to {release.Version}. Close the app and start it again.", OkColor);
            return;
        }
        Close();
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
        _tray?.Dispose();
        base.OnFormClosing(e);
    }

    void SaveSettings()
    {
        _settings.OutputDir = _outDir.Text.Trim();
        _settings.AudioOnly = !_video.Checked;
        _settings.AudioFormat = _m4a.Checked ? "m4a" : "mp3";
        _settings.SaveCover = _cover.Checked;
        _settings.MaxHeight = SelectedQuality.MaxHeight;
        _settings.Subtitles = SelectedSubtitles.Code;
        _settings.Save();
    }

    static readonly Color PageColor = Color.White;
    string? _gifName;
    BackgroundPack? _pack;
    readonly string[] _gifNames = GifNames();
    readonly List<Control> _transparentControls = new();
    readonly System.Windows.Forms.Timer _gifTimer = new() { Interval = 125 };
    readonly Stopwatch _gifClock = new();
    readonly BackdropCanvas _canvas = new();
    Bitmap? _firstFrame;
    List<Bitmap>? _frames;
    int[] _delaysMs = [];
    int _frameIndex, _decodeVersion;
    long _frameShownAt;
    Bitmap? _backgroundCache;
    bool _backgroundStale, _gifHooked;
    TableLayoutPanel? _fieldsGrid, _lowerGrid;
    LookDialog? _lookDialog;

    void BackgroundMoved()
    {
        if (_backgroundCache == null || _backgroundStale) return;
        _backgroundStale = true;
        Invalidate(true);
    }

    void TextMoved()
    {
        if (_backgroundCache == null || _backgroundStale) return;
        _backgroundStale = true;
        Invalidate(false);
        foreach (var c in _transparentControls) c.Invalidate(false);
    }
    bool HasBackground => _firstFrame != null;

    static string[] GifNames()
    {
        try
        {
            return typeof(MainForm).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("bg/", StringComparison.Ordinal))
                .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToArray();
        }
        catch { return []; }
    }

    bool LoadGif(int choice)
    {
        if (_gifNames.Length == 0) return false;
        var name = _gifNames[choice >= 1 && choice <= _gifNames.Length ? choice - 1 : Random.Shared.Next(_gifNames.Length)];
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("the background is missing");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var pack = BackgroundPack.Read(ms.ToArray());
            using var image = pack.Frame(0);
            var first = Backdrop.Frame(image, _settings.Look.Blur);
            (_pack, _gifName) = (pack, name[3..]);
            ReplaceFrames(first, null, []);
            return true;
        }
        catch (Exception e)
        {
            UsageLog.Write($"Background {name[3..]} could not be loaded: {e.Message}");
            return false;
        }
    }

    void ReplaceFrames(Bitmap? first, List<Bitmap>? frames, int[] delays)
    {
        var old = new List<Bitmap>();
        if (_firstFrame != null && _firstFrame != first) old.Add(_firstFrame);
        if (_frames != null) old.AddRange(_frames.Where(f => frames == null || !frames.Contains(f)));
        (_firstFrame, _frames, _delaysMs, _frameIndex) = (first, frames, delays, 0);
        foreach (var b in old) b.Dispose();
    }

    void DecodeFrames()
    {
        if (_pack is not { } pack) return;
        int version = ++_decodeVersion, blur = _settings.Look.Blur;
        Task.Run(() => DecodeAllFrames(pack, blur)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            var (frames, delays) = t.Result;
            PostToUi(() =>
            {
                if (version != _decodeVersion || frames.Count < 2)
                {
                    foreach (var f in frames) f.Dispose();
                    return;
                }
                ReplaceFrames(_firstFrame, frames, delays);
                RefreshBackground();
                ResumeGif();
            });
        });
    }

    void RefreshBackground()
    {
        if (!HasBackground) return;
        RedrawBackground();
        Invalidate(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.P))
        {
            ShowLook();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal LookDialog? LookWindow => _lookDialog is { IsDisposed: false } d ? d : null;

    void ShowLook()
    {
        if (LookWindow is { } open)
        {
            if (open.Visible) open.Activate();
            return;
        }
        UsageLog.Write("Opened background settings");
        var dialog = new LookDialog(_settings.Look, _gifNames.Length);
        dialog.Changed += ApplyLook;
        dialog.FormClosed += (_, _) =>
        {
            _lookDialog = null;
            SaveSettings();
            UsageLog.Write("Background settings: " + _settings.Look);
        };
        _lookDialog = dialog;
        if (!Visible) return;
        var area = Screen.FromControl(this).WorkingArea;
        int x = Right + 8 + dialog.Width <= area.Right ? Right + 8 : Math.Max(area.Left, Right - dialog.Width - 24);
        dialog.Location = new Point(x, Math.Clamp(Top + 60, area.Top, Math.Max(area.Top, area.Bottom - dialog.Height)));
        dialog.Show(this);
    }

    void ApplyLook(BackgroundLook next)
    {
        var prev = _settings.Look;
        next = next.Checked(_gifNames.Length);
        _settings.Look = next;
        if (next.Gif != prev.Gif)
        {
            PauseGif();
            if (LoadGif(next.Gif)) DecodeFrames();
        }
        else if (next.Blur != prev.Blur && _pack != null)
        {
            PauseGif();
            using var image = _pack.Frame(0);
            ReplaceFrames(Backdrop.Frame(image, next.Blur), null, []);
            DecodeFrames();
        }
        RefreshBackground();
    }

    List<GlassPanel> GlassPanels()
    {
        if (_fieldsGrid == null || _lowerGrid == null) return [];
        float k = DeviceDpi / 96f;
        int m = (int)Math.Round(6 * k), radius = (int)Math.Round(14 * k);
        Rectangle top = _fieldsGrid.Bounds, bottom = _lowerGrid.Bounds;
        return
        [
            new(Rectangle.FromLTRB(top.Left + m, top.Top + m, top.Right - m, top.Bottom), radius),
            new(Rectangle.FromLTRB(bottom.Left + m, bottom.Top + m, bottom.Right - m, bottom.Bottom - m), radius),
        ];
    }

    IEnumerable<Control> TextControls() =>
        _transparentControls.Where(c => c is Label or RadioButton or CheckBox && c != _platform);

    Rectangle InForm(Control c)
    {
        var r = c.Bounds;
        for (var p = c.Parent; p != null && p != this; p = p.Parent) r.Offset(p.Location);
        return r;
    }

    internal List<Backing> Backings()
    {
        float k = DeviceDpi / 96f;
        int padX = (int)Math.Round(5 * k), padY = (int)Math.Round(1 * k), radius = (int)Math.Round(7 * k), touch = (int)Math.Round(2 * k);
        var list = new List<Backing>();
        if (_logBox.Glass && _logBox.Visible) list.Add(new Backing(InForm(_logBox), (int)Math.Round(10 * k), Card: true));
        var pills = new List<Rectangle>();
        foreach (var c in TextControls())
        {
            if (!c.Visible || c.Text.Length == 0 || c.Width <= 0 || c.Height <= 0) continue;
            var r = InForm(c);
            if (c is Label label)
            {
                var text = TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.Width - label.Padding.Horizontal), int.MaxValue),
                                                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                r = new Rectangle(r.X + label.Padding.Left, r.Y, Math.Min(text.Width, r.Width), r.Height);
            }
            r.Inflate(padX, padY);
            for (int i = 0; i < pills.Count; i++)
            {
                var p = pills[i];
                int overlap = Math.Min(p.Bottom, r.Bottom) - Math.Max(p.Top, r.Top);
                int gap = Math.Max(p.Left, r.Left) - Math.Min(p.Right, r.Right);
                if (overlap * 2 < Math.Min(p.Height, r.Height) || gap > touch) continue;
                r = Rectangle.Union(p, r);
                pills.RemoveAt(i);
                i = -1;
            }
            pills.Add(r);
        }
        list.AddRange(pills.Select(r => new Backing(r, radius)));
        return list;
    }

    static (List<Bitmap> Frames, int[] Delays) DecodeAllFrames(BackgroundPack pack, int blur)
    {
        var frames = new List<Bitmap>(pack.Count);
        for (int i = 0; i < pack.Count; i++)
        {
            using var image = pack.Frame(i);
            frames.Add(Backdrop.Frame(image, blur));
        }
        return (frames, pack.Delays);
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

    void StartGif()
    {
        if (!_gifHooked)
        {
            _gifHooked = true;
            _gifTimer.Tick += (_, _) => OnGifTick();
        }
        DecodeFrames();
    }

    static readonly bool StillBackground = Environment.GetEnvironmentVariable("NYADOWNLOADER_STILL") == "1";

    void ResumeGif()
    {
        if (StillBackground || _frames == null || _gifTimer.Enabled) return;
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
        _backgroundStale = false;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (_backgroundCache == null || _backgroundCache.Size != size)
        {
            _backgroundCache?.Dispose();
            _backgroundCache = new Bitmap(size.Width, size.Height, Backdrop.Format);
        }
        var frame = _frames != null ? _frames[Math.Min(_frameIndex, _frames.Count - 1)] : _firstFrame;
        if (frame == null)
        {
            using var g = Graphics.FromImage(_backgroundCache);
            g.Clear(PageColor);
            return;
        }
        _canvas.Render(_backgroundCache, frame, _settings.Look, GlassPanels(), DeviceDpi / 96f, Backings());
    }

    Control[] OpaqueControls() =>
    [
        _logBox, _urlBox, _outDirBox, _quality, _progress, _updateBanner, _appBanner, _platform, _subtitles, _fromBox, _toBox,
        _download, _cancel, _showFile, _setup, _cookiesPick, _cookiesClear, _outPick, _chapters,
    ];

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!HasBackground || ClientSize.Width <= 0 || ClientSize.Height <= 0) { base.OnPaintBackground(e); return; }
        if (_backgroundStale || _backgroundCache == null || _backgroundCache.Size != ClientSize) RedrawBackground();
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

    DownloadMode Mode => _m4a.Checked ? DownloadMode.Audio : _mp3.Checked ? DownloadMode.Mp3 : DownloadMode.Video;

    SubtitleLanguage SelectedSubtitles => _subtitles.SelectedItem as SubtitleLanguage ?? SubtitleLanguage.None;

    bool FragmentTyped => _from.Text.Trim().Length > 0 || _to.Text.Trim().Length > 0;

    void UpdateQualityHint()
    {
        var q = SelectedQuality;
        _qualityHint.Text = FragmentTyped
            ? "Fragment: type times like 1:30 or 1:02:03. The whole video is downloaded first, then the fragment is cut out exactly. " +
              "An empty start means from the beginning, an empty end means to the end."
            : Mode == DownloadMode.Video && _current == Platform.Instagram
            ? "Instagram: the app figures out whether the link is a reel, a photo or a carousel. " +
              "Photos are saved as jpg, videos as mp4, and a carousel is saved in full. Quality only affects videos."
            : Mode == DownloadMode.Audio
            ? "m4a keeps the site's original sound as it is, without re-encoding (or opus if the site has no m4a). The quality setting is for video only."
            : Mode == DownloadMode.Mp3
            ? "mp3 plays everywhere, but it is re-encoded from the original (about 190 kbps), so m4a sounds a little better. The quality setting is for video only."
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
        if (uri != null)
        {
            if (_lastLink != null && _lastLink != uri.AbsoluteUri) _from.Text = _to.Text = "";
            _lastLink = uri.AbsoluteUri;
        }
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
        ShowProgress(0);
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
                    ShowProgress((int)(fraction * _progress.Maximum));
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

    internal static string PartialStatus(int saved, int total, int failed, string? hint) =>
        $"Saved {saved} of {Count(total, "video", "videos")}, {failed} failed. " + (hint ?? "See the log below.");

    static string Describe(int photos, int videos) =>
        photos > 0 && videos > 0 ? $"{Count(photos, "photo", "photos")} and {Count(videos, "video", "videos")}"
        : photos > 0 ? Count(photos, "photo", "photos")
        : Count(videos, "video", "videos");

    async Task<bool> EnsureEngineAsync()
    {
        if (_hasYtDlp) return true;
        var missing = MissingComponents();
        var answer = MessageBox.Show(this,
            $"Downloading needs these components: {string.Join(", ", missing.Select(m => m.Name))} " +
            $"(~{missing.Sum(m => m.ApproxMb)} MB, one time). Download them now?",
            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        UsageLog.Write($"Asked to install missing components before downloading: {answer}");
        if (answer != DialogResult.Yes) return false;
        await InstallComponentsAsync();
        return _hasYtDlp;
    }

    Uri? LinkOrComplain(string action)
    {
        var uri = PlatformDetector.ParseUrl(_url.Text);
        if (uri != null) return uri;
        UsageLog.Write($"Clicked {action} without a valid link: \"{_url.Text.Trim()}\"");
        ShowStatus("Paste a video link first.", ErrorColor);
        FocusLink();
        return null;
    }

    bool CreateFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception ex)
        {
            UsageLog.Write($"Can't create the folder \"{dir}\": {ex.Message}");
            ShowStatus($"Can't create the folder: {ex.Message}", ErrorColor);
            return false;
        }
    }

    bool TryGetFragment(out Section? fragment)
    {
        fragment = null;
        if (!TimeText.TryParse(_from.Text, out var from) || !TimeText.TryParse(_to.Text, out var to))
        {
            UsageLog.Write($"Bad fragment: \"{_from.Text.Trim()}\" - \"{_to.Text.Trim()}\"");
            ShowStatus("Fragment: type times like 1:30 or 1:02:03, or leave both boxes empty for the whole video.", ErrorColor);
            return false;
        }
        double start = from ?? 0, end = to ?? double.PositiveInfinity;
        if (end <= start)
        {
            UsageLog.Write($"Bad fragment: \"{_from.Text.Trim()}\" - \"{_to.Text.Trim()}\"");
            ShowStatus("Fragment: the end must be later than the start.", ErrorColor);
            return false;
        }
        if (start > 0 || to != null) fragment = new Section(start, end);
        return true;
    }

    async Task<Listing?> ListAsync(Uri uri, Platform platform, string? cookies, LinkKind kind)
    {
        Listing? listing = null;
        var site = PlatformDetector.DisplayName(platform);
        UsageLog.Write($"Getting the list: {uri.AbsoluteUri}");
        await RunBusyAsync("Get the list", async ct =>
        {
            Log($"{site} | getting the list of videos | {uri.AbsoluteUri}");
            StartHeartbeat("Getting the list of videos");
            listing = await Listing.RunAsync(platform, uri, cookies,
                n => PostToUi(() => _phase = $"Getting the list of videos: {Count(n, "video", "videos")} so far"), ct);
        });
        if (listing == null || IsDisposed) return null;
        if (listing.IsList && listing.Entries.Count > 0)
        {
            UsageLog.Write($"List \"{listing.Title}\": {Count(listing.Entries.Count, "video", "videos")}" +
                           (listing.Skipped > 0 ? $", {listing.Skipped} hidden" : ""));
            return listing;
        }
        if (!listing.IsList && listing.ExitCode == 0) return listing;
        if (kind == LinkKind.VideoInList)
        {
            UsageLog.Write("Couldn't open the playlist, downloading just the video");
            return listing with { IsList = false };
        }
        var (hint, _) = Hints.Diagnose(listing.Output, platform);
        var why = listing.IsList ? "The list is empty, or all its videos are private or not out yet."
                : platform == Platform.TikTok ? "yt-dlp can't read TikTok profiles right now. Paste a link to a single video."
                : platform == Platform.Instagram ? "yt-dlp can't read Instagram profiles. Paste a link to a post or a reel."
                : hint ?? "See the log below.";
        ShowStatus("Couldn't get the list of videos. " + why, ErrorColor);
        Log(ErrorLog.Tail(listing.Output));
        LogError("LIST ERROR", $"{site} | {uri.AbsoluteUri} | exit code {listing.ExitCode}", ErrorLog.Tail(listing.Output));
        return null;
    }

    List<PlaylistEntry>? PickFromList(Listing listing, Uri uri, LinkKind kind)
    {
        var current = PlatformDetector.VideoId(uri);
        bool playlist = PlatformDetector.ListId(uri) != null;
        bool Checked(PlaylistEntry e) => kind == LinkKind.VideoInList ? e.Id == current : playlist;
        var items = listing.Entries.Select((e, i) => new PickDialog.Item(
            [(i + 1).ToString(), e.Title, e.Duration is { } d ? TimeText.Format(d) : "", e.Section ?? ""], e.Duration, Checked(e))).ToList();
        PickDialog.Column[] columns = listing.Entries.Any(e => e.Section != null)
            ? [new("#", 48), new("Title"), new("Length", 70), new("Section", 90)]
            : [new("#", 48), new("Title"), new("Length", 70)];
        var heading = $"{listing.Title}: {Count(listing.Entries.Count, "video", "videos")}" +
                      (listing.Skipped > 0 ? $" ({listing.Skipped} more are private, deleted, live or not out yet)" : "") +
                      ". Pick the ones to download. Several videos go into a folder named after the list.";
        using var dialog = new PickDialog("Choose videos", heading, columns, items, "video", "videos");
        UsageLog.Write("Shown dialog: Choose videos");
        if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed)
        {
            UsageLog.Write("Choose videos: canceled");
            if (!IsDisposed) ShowStatus("Canceled.", NeutralColor);
            return null;
        }
        var picked = dialog.Picked.Select(i => listing.Entries[i]).ToList();
        UsageLog.Write($"Choose videos: picked {picked.Count} of {listing.Entries.Count}: {string.Join(", ", picked.Take(20).Select(e => e.Id))}" +
                       (picked.Count > 20 ? ", ..." : ""));
        return picked;
    }

    async Task PickChaptersAsync()
    {
        if (_cts != null || !await EnsureEngineAsync()) return;
        var uri = LinkOrComplain("Chapters");
        if (uri == null) return;
        var platform = PlatformDetector.FromHost(uri.Host);
        var cookies = CookiesFor(platform) is { } c && File.Exists(c) ? c : null;
        UsageLog.Write($"Getting chapters: {uri.AbsoluteUri}");
        VideoInfo? info = null;
        await RunBusyAsync("Get chapters", async ct =>
        {
            Log($"{PlatformDetector.DisplayName(platform)} | getting the chapters | {uri.AbsoluteUri}");
            StartHeartbeat("Getting the chapters");
            info = await VideoInfo.RunAsync(platform, uri, cookies, ct);
        });
        if (info == null || IsDisposed) return;
        if (info.Chapters.Count == 0)
        {
            if (info.ExitCode != 0)
            {
                var (hint, _) = Hints.Diagnose(info.Output, platform);
                ShowStatus("Couldn't get the chapters. " + (hint ?? "See the log below."), ErrorColor);
                Log(ErrorLog.Tail(info.Output));
                LogError("CHAPTERS ERROR", $"{uri.AbsoluteUri} | exit code {info.ExitCode}", ErrorLog.Tail(info.Output));
            }
            else
            {
                ShowStatus("This video has no chapters. Type the start and the end of the fragment instead.", WarnColor);
                UsageLog.Write("Chapters: the video has none");
            }
            return;
        }
        var items = info.Chapters.Select(ch => new PickDialog.Item(
            [ch.Number.ToString(), ch.Title!, $"{TimeText.Format(ch.Start)} - {(ch.ToEnd ? "end" : TimeText.Format(ch.End))}",
             ch.ToEnd ? "" : TimeText.Format(ch.End - ch.Start)], ch.ToEnd ? null : ch.End - ch.Start, false)).ToList();
        using var dialog = new PickDialog("Choose chapters",
            $"{info.Title}: {Count(info.Chapters.Count, "chapter", "chapters")}. Pick the ones to download. Each one is saved as a separate file.",
            [new("#", 40), new("Chapter"), new("Time", 140), new("Length", 70)], items, "chapter", "chapters");
        UsageLog.Write("Shown dialog: Choose chapters");
        if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed)
        {
            UsageLog.Write("Choose chapters: canceled");
            if (!IsDisposed) ShowStatus("Canceled.", NeutralColor);
            return;
        }
        var picked = dialog.Picked.Select(i => info.Chapters[i]).ToList();
        UsageLog.Write($"Choose chapters: picked {string.Join(", ", picked.Select(p => p.Number))}");
        await DownloadAsync(picked);
    }

    async Task DownloadAsync(IReadOnlyList<Section>? chapters = null)
    {
        if (_cts != null || !await EnsureEngineAsync()) return;
        var uri = LinkOrComplain("Download");
        if (uri == null) return;
        Section? fragment = null;
        if (chapters == null && !TryGetFragment(out fragment)) return;
        var outDir = _outDir.Text.Trim();
        if (outDir.Length == 0) outDir = AppContext.BaseDirectory.TrimEnd('\\');
        if (!CreateFolder(outDir)) return;
        SaveSettings();

        var platform = PlatformDetector.FromHost(uri.Host);
        var site = PlatformDetector.DisplayName(platform);
        var cookies = CookiesFor(platform) is { } c && File.Exists(c) ? c : null;
        var cookieName = Path.GetFileName(cookies) ?? "none";
        var kind = chapters == null ? PlatformDetector.Kind(uri) : LinkKind.Single;
        List<PlaylistEntry>? items = null;
        string listTitle = "";
        if (kind != LinkKind.Single)
        {
            var listing = await ListAsync(uri, platform, cookies, kind);
            if (listing == null) return;
            if (listing.IsList)
            {
                var picked = PickFromList(listing, uri, kind);
                if (picked == null) return;
                if (picked.Count == 1) uri = new Uri(picked[0].Url);
                else (items, listTitle) = (picked, listing.Title);
            }
        }
        IReadOnlyList<Section>? sections = chapters ?? (fragment != null ? [fragment] : null);
        bool fragmentDropped = items != null && sections != null, newFolder = false;
        if (items != null)
        {
            outDir = Path.Combine(outDir, Section.SafeName(listTitle.Length > 0 ? listTitle : "Playlist", 100));
            newFolder = !Directory.Exists(outDir);
            if (!CreateFolder(outDir)) return;
        }

        var subtitles = SelectedSubtitles;
        var request = new Downloader.Request(platform, uri, Mode, SelectedQuality.MaxHeight, outDir, cookies, _cover.Checked)
        {
            Items = items?.Select(i => i.Url).ToList(),
            Sections = items == null ? sections : null,
            Subtitles = subtitles,
        };
        bool instagramAuto = Downloader.IsInstagramAuto(request) && items == null;
        var quality = SelectedQuality.Label.ToLowerInvariant();
        var what = request.Mode == DownloadMode.Audio ? "audio (m4a, original)"
                 : request.Mode == DownloadMode.Mp3 ? "audio (mp3)"
                 : instagramAuto ? $"photos and videos (post type detected automatically), {quality}"
                 : $"video (mp4), {quality}";
        if (items != null) what = $"{Count(items.Count, "video", "videos")} from \"{listTitle}\", {what}";
        if (request.Sections is { } parts)
            what += parts[0].Title != null ? $", {Count(parts.Count, "chapter", "chapters")}" : $", fragment {parts[0].Describe}";
        if (!subtitles.IsNone) what += $", {subtitles.Name} subtitles";
        var loginProblem = Hints.LoginProblem.None;
        (string Title, ToolTipIcon Icon)? notice = null;

        await RunBusyAsync("Download", async ct =>
        {
            _lastFile = null;
            _showFile.Enabled = false;
            _hideNoVideoWarnings = instagramAuto;
            Log($"{site} | {what} | {uri.AbsoluteUri}");
            if (items != null) Log("Folder: " + outDir);
            if (fragmentDropped) Log("The fragment boxes apply to a single video, so whole videos are downloaded.");
            if (cookies != null) Log("Cookies: " + cookieName);
            if (platform == Platform.YouTube && cookies != null && !CookieFile.Analyze(cookies).YouTubeLoggedIn)
                Log("Warning: these YouTube cookies are not signed in, so age-restricted videos won't download.");
            Log("");
            ShowStatus("Preparing download...", NeutralColor);
            UsageLog.Write($"Download started: {uri.AbsoluteUri} | {site} | {what} | cookies: {cookieName} | folder: {outDir}");
            StartHeartbeat("Preparing download", items?.Count ?? 0);

            var r = await Downloader.RunAsync(request, line => PostToUi(() => OnOutput(line)), ct);
            bool partial = r.ExitCode != 0 && items != null && r.Saved.Count > 0;
            if (r.ExitCode != 0 && !partial)
            {
                var (hint, login) = Hints.Diagnose(r.Output, platform);
                ShowStatus((r.Photos > 0 ? $"Photos saved ({r.Photos}), but the videos failed. " : "Failed. ") +
                           (hint ?? "See the log below for details."), ErrorColor);
                if (hint != null) Log("-> " + hint);
                LogError("DOWNLOAD ERROR", $"{site} | {what} | {uri.AbsoluteUri} | exit code {r.ExitCode}" + (hint != null ? $"\n    hint: {hint}" : ""),
                         $"yt-dlp: {YtDlp.Locate().Description} | cookies: {cookieName}\n" + ErrorLog.Tail(r.Output));
                Log("-> Details saved to " + ErrorLog.FilePath);
                loginProblem = login;
                notice = ("Download failed", ToolTipIcon.Error);
                return;
            }

            ShowProgress(_progress.Maximum);
            _lastFile = r.Saved.FirstOrDefault();
            _showFile.Enabled = _lastFile != null && File.Exists(_lastFile);
            var first = Path.GetFileName(_lastFile) ?? "";
            var kinds = Describe(r.Photos, r.Videos);
            var coverNote = r.Covers.Count switch { 0 => "", 1 => " + cover", var n => $" + {n} covers" };
            var got = r.Videos == 1 && request.Mode == DownloadMode.Video && r.Resolution is { } res && !first.Contains(res) ? $" ({res})" : "";
            bool wantedSubtitles = !subtitles.IsNone && platform != Platform.Instagram && r.Videos > 0;
            var subtitlesNote = r.Subtitles.Count > 0 ? " + subtitles" : wantedSubtitles ? $" (no {subtitles.Name} subtitles)" : "";
            if (r.Saved.Count == 0)
                ShowStatus(instagramAuto ? "Nothing to download in this post." : "Done.", instagramAuto ? WarnColor : OkColor);
            else
                ShowStatus((r.Saved.Count == 1 ? $"Done: {first}{got}" : $"Done: {kinds} ({first} and {r.Saved.Count - 1} more)") + coverNote + subtitlesNote, OkColor);
            notice = partial || (instagramAuto && r.Saved.Count == 0) ? ("Download finished with problems", ToolTipIcon.Warning) : ("Download finished", ToolTipIcon.Info);

            if ((instagramAuto || items != null || request.Sections != null) && r.Saved.Count > 0)
            {
                Log("");
                if (instagramAuto) Log($"Post: {kinds}");
                foreach (var f in r.Saved) Log("Saved: " + f);
            }
            foreach (var cover in r.Covers) Log("Cover: " + cover);
            foreach (var sub in r.Subtitles) Log("Subtitles: " + sub);
            if (wantedSubtitles && r.Subtitles.Count == 0)
                Log($"-> No {subtitles.Name} subtitles: the site has none for this video.");
            if (request.SaveCover && r.Covers.Count == 0 && r.Videos > 0)
                Log("-> No cover was saved: the site didn't provide one.");
            if (partial)
            {
                var (hint, _) = Hints.Diagnose(r.Output, platform);
                ShowStatus(PartialStatus(r.Videos, items!.Count, r.Failed, hint), WarnColor);
                if (hint != null) Log("-> " + hint);
                LogError("DOWNLOAD ERROR", $"{site} | {what} | {uri.AbsoluteUri} | {r.Failed} of {items.Count} failed",
                         $"yt-dlp: {YtDlp.Locate().Description} | cookies: {cookieName}\n" + ErrorLog.Tail(r.Output));
            }
            UsageLog.Write((r.Saved.Count > 0
                ? $"Download finished: {kinds}: {string.Join(", ", r.Saved.Take(30).Select(Path.GetFileName))}{(r.Saved.Count > 30 ? ", ..." : "")}"
                : "Download finished: nothing saved") +
                (partial ? $" | {r.Failed} failed" : "") +
                (r.Covers.Count > 0 ? $" | covers: {string.Join(", ", r.Covers.Select(Path.GetFileName))}" : "") +
                (r.Subtitles.Count > 0 ? $" | subtitles: {string.Join(", ", r.Subtitles.Select(Path.GetFileName))}" : ""));
        });
        if (newFolder) DeleteIfEmpty(outDir);
        if (IsDisposed) return;
        if (notice is { } n) Notify(n.Title, _status.Text, n.Icon);
        if (loginProblem != Hints.LoginProblem.None) ShowLoginProblem(loginProblem, platform, cookies);
    }

    static void DeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    NotifyIcon? _tray;
    static readonly bool QuietNotifications = Environment.GetEnvironmentVariable("NYADOWNLOADER_QUIET") == "1";

    void Notify(string title, string text, ToolTipIcon icon)
    {
        if (ContainsFocus && WindowState != FormWindowState.Minimized) return;
        if (text.Length > 200) text = text[..197] + "...";
        UsageLog.Write($"Notified: {title} | {text}" + (QuietNotifications ? " (quiet)" : ""));
        if (QuietNotifications) return;
        try
        {
            if (_tray == null)
            {
                _tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = AppInfo.Name };
                _tray.BalloonTipClicked += (_, _) => BringBack();
                _tray.MouseClick += (_, _) => BringBack();
                _tray.BalloonTipClosed += (_, _) => _tray.Visible = false;
            }
            _tray.Visible = true;
            _tray.ShowBalloonTip(8000, title, text, icon);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ExternalException)
        {
            UsageLog.Write("Notification failed: " + ex.Message);
        }
    }

    void BringBack()
    {
        if (_tray != null) _tray.Visible = false;
        if (IsDisposed) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
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
    string? _fixedPhase;
    int _itemIndex, _itemCount;
    bool _progressLineOpen;
    static readonly Regex ExtractingRx = new(@"^\[[\w:]+\] Extracting URL: ", RegexOptions.Compiled);

    string ItemPrefix => _itemCount > 1 && _itemIndex > 0 ? $"Video {_itemIndex} of {_itemCount}: " : "";

    void StartHeartbeat(string phase, int items = 0)
    {
        _runStarted = DateTime.Now;
        _lastProgressAt = _lastProgressLogAt = DateTime.MinValue;
        _phase = phase;
        _fixedPhase = null;
        (_itemIndex, _itemCount) = (0, items);
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
        ShowStatus($"{ItemPrefix}{_phase}... {(int)(DateTime.Now - _runStarted).TotalSeconds}s", NeutralColor);
    }

    void SetIndeterminate(bool on)
    {
        var style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (_progress.Style == style) return;
        _progress.Style = style;
        if (on) _progress.MarqueeAnimationSpeed = 30;
        _taskbar.Set(this, on ? TaskbarProgress.State.Indeterminate : TaskbarProgress.State.Normal, _progress.Value);
    }

    void ShowProgress(int perMille)
    {
        _progress.Value = Math.Clamp(perMille, 0, _progress.Maximum);
        if (_progress.Style == ProgressBarStyle.Continuous) _taskbar.Set(this, TaskbarProgress.State.Normal, _progress.Value);
    }

    void ShowResultOnTaskbar() =>
        _taskbar.Set(this, _status.ForeColor == ErrorColor ? TaskbarProgress.State.Error
                         : _status.ForeColor == WarnColor ? TaskbarProgress.State.Paused
                         : TaskbarProgress.State.None, 1000);

    string? PhaseFor(string line) =>
        line == Downloader.RetryNote ? "Network error, trying again" :
        line.StartsWith(Downloader.CutNote, StringComparison.Ordinal) ? "Cutting the fragment" :
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
        if (_progressLineOpen) _log.ReplaceLastLine(text);
        else _log.AppendLine(text);
        _progressLineOpen = true;
    }

    void ClearLog()
    {
        _log.Clear();
        _progressLineOpen = false;
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
                var overall = _itemCount > 1 && _itemIndex > 0 ? ((_itemIndex - 1) * 100 + pct) / _itemCount : pct;
                ShowProgress((int)(overall * 10));
                var text = $"{pct.ToString("0.0", CultureInfo.InvariantCulture)}%";
                if (parts.Length > 1 && IsKnown(parts[1])) text += ", " + parts[1].Trim();
                if (parts.Length > 2 && IsKnown(parts[2])) text += ", " + parts[2].Trim() + " left";
                ShowStatus(ItemPrefix + "Downloading: " + text, NeutralColor);
                if (pct >= 100 || (DateTime.Now - _lastProgressLogAt).TotalMilliseconds >= 250)
                {
                    _lastProgressLogAt = DateTime.Now;
                    SetProgressLogLine("[progress] " + text);
                }
            }
            return;
        }
        if (line.StartsWith("[info] Writing '%(") || line.StartsWith("[MetadataParser]")) return;
        if (_itemCount > 1 && ExtractingRx.IsMatch(line)) _itemIndex = Math.Min(_itemIndex + 1, _itemCount);
        if (line == Downloader.RetryNote) _itemIndex = 0;
        if (line == Downloader.SubtitlesNote) (_fixedPhase, _itemCount) = ("Getting subtitles", 0);
        if (_hideNoVideoWarnings &&
            (line.Contains("There is no video in this post") || line.Contains("No video formats found") ||
             line.Contains("Requested format is not available") || line.Contains("does not pass filter")))
            return;
        if ((_fixedPhase ?? PhaseFor(line)) is { } phase)
        {
            _phase = phase;
            if ((DateTime.Now - _lastProgressAt).TotalSeconds >= 1.5 || phase != "Starting download")
            {
                _lastProgressAt = DateTime.MinValue;
                SetIndeterminate(true);
                ShowStatus($"{ItemPrefix}{_phase}... {(int)(DateTime.Now - _runStarted).TotalSeconds}s", NeutralColor);
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
        foreach (var c in new Control[] { _download, _setup, _update, _updateNow, _appUpdateNow, _cookiesPick, _cookiesClear, _outPick, _video, _m4a, _mp3, _cover, _subtitles, _chapters })
            c.Enabled = !busy;
        _cancel.Enabled = busy;
        _url.ReadOnly = _outDir.ReadOnly = _from.ReadOnly = _to.ReadOnly = busy;
        _quality.Enabled = !busy && Mode == DownloadMode.Video;
        if (busy) return;
        StopHeartbeat();
        ShowResultOnTaskbar();
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
        _progressLineOpen = false;
        _log.AppendLine(line);
    }
}
