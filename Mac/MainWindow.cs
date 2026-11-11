using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;
using DRect = System.Drawing.Rectangle;

namespace VideoDownloader;

// The macOS window: the same features and texts as MainForm.cs on Windows.
public sealed class MainWindow : Window
{
    static readonly Regex PercentRx = new(@"([\d.]+)%", RegexOptions.Compiled);

    static readonly Color OkColor = Color.FromRgb(0x1E, 0x7B, 0x34);
    static readonly Color WarnColor = Color.FromRgb(0xB3, 0x5C, 0x00);
    static readonly Color ErrorColor = Color.FromRgb(0xB2, 0x22, 0x22);
    static readonly Color NeutralColor = Color.FromRgb(0x1D, 0x1D, 0x1F);
    static readonly Color DimColor = Color.FromRgb(0x6D, 0x6D, 0x72);
    static readonly Color LinkColor = Color.FromRgb(0x00, 0x67, 0xC0);

    static Color? PlatformColor(Platform p) => p switch
    {
        Platform.YouTube => Color.FromRgb(0xE0, 0x00, 0x2A),
        Platform.TikTok => Color.FromRgb(0x16, 0x18, 0x23),
        Platform.Instagram => Color.FromRgb(0xE8, 0x59, 0x0C),
        _ => null,
    };

    const string ExtensionUrl = "https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc";

    const string CookieHowTo =
        "1. Install the Get cookies.txt LOCALLY extension in your browser (button below).\n" +
        "2. Open the site (YouTube, TikTok or Instagram) where you are signed in.\n" +
        "3. Click the extension icon, then Export.\n" +
        "4. Drop the downloaded file into this window.";

    readonly Settings _settings = Settings.Load();

    static TextBlock Text(Color? color = null, bool wrap = false) => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Foreground = color is { } c ? new SolidColorBrush(c) : null,
        Margin = new Thickness(3, 3),
    };

    static HyperlinkButton Link(string text) => new()
    {
        Content = text, IsVisible = false, Padding = new Thickness(2, 0), Margin = new Thickness(12, 0, 3, 0),
        VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(LinkColor),
    };

    // A box is stretched to the height of its row (the buttons next to it are taller), and the Fluent template puts
    // the text and the placeholder at the top of it unless the content is centered.
    static TextBox Field(string? watermark = null, double width = double.NaN) => new()
    {
        Watermark = watermark, Width = width, Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center, Padding = Ui.Pad(4, 2, 4),
    };

    readonly TextBox _url = Field("Paste a YouTube, TikTok or Instagram link and press Enter");
    readonly TextBlock _platformText = new() { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold };
    readonly Border _platform = new() { Padding = Ui.Pad(8, 3, 8), MinWidth = 96, CornerRadius = new CornerRadius(5), Margin = new Thickness(6, 3, 3, 3), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly Button _cookiesPick = Ui.Button("Choose..."), _cookiesClear = Ui.Button("Remove");
    readonly TextBlock _cookiesStatus = Text(wrap: true);
    readonly HyperlinkButton _cookiesHelp = Link("How do I get cookies?");
    readonly TextBox _outDir = Field();
    readonly Button _outPick = Ui.Button("Browse...");
    readonly RadioButton _video = new() { Content = "Video (mp4)", GroupName = "mode", IsChecked = true, Margin = new Thickness(3, 0, 12, 0) };
    readonly RadioButton _m4a = new() { Content = "Audio (m4a)", GroupName = "mode", Margin = new Thickness(3, 0, 12, 0) };
    readonly RadioButton _mp3 = new() { Content = "Audio (mp3)", GroupName = "mode", Margin = new Thickness(3, 0, 12, 0) };
    readonly CheckBox _cover = new() { Content = "Save cover (jpg)", Margin = new Thickness(18, 0, 3, 0) };
    readonly ComboBox _quality = Ui.ComboBox(190), _subtitles = Ui.ComboBox(160);
    readonly TextBox _from = Field("start", 84), _to = Field("end", 84);
    readonly Button _chapters = Ui.Button("Chapters...");
    readonly TextBlock _qualityHint = Text(DimColor, wrap: true);
    readonly Button _download = Ui.Button("Download", 22), _cancel = Ui.Button("Cancel"), _showFile = Ui.Button("Show in Finder"), _setup = Ui.Button("Install components");
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1000, Height = 8, MinHeight = 8, Margin = new Thickness(3, 8, 3, 2) };
    readonly TextBlock _status = Text(wrap: true);
    readonly LogBox _log = new() { Margin = new Thickness(3) };
    readonly TextBlock _engine = Text(DimColor);
    readonly HyperlinkButton _update = Link("Update yt-dlp"), _errorLogLink = Link("Open error.log");
    readonly Border _updateBanner = Banner(), _appBanner = Banner();
    readonly TextBlock _updateText = BannerText(), _appUpdateText = BannerText();
    readonly Button _updateNow = Ui.Button("Update"), _appUpdateNow = Ui.Button("Update");
    readonly HyperlinkButton _appNotes = new() { Content = "What's new", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 12, 0), Foreground = new SolidColorBrush(LinkColor) };
    readonly Border _fieldsHost, _lowerHost;
    readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), FontWeight = FontWeight.SemiBold, Padding = new Thickness(6, 1) };
    readonly List<Control> _textControls = [];
    AppRelease? _appRelease;
    Color _statusColor = NeutralColor;

    static Border Banner() => new()
    {
        IsVisible = false, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)), Padding = new Thickness(10, 6),
        Margin = new Thickness(3, 6, 3, 0), CornerRadius = new CornerRadius(6),
    };
    static TextBlock BannerText() => new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x40, 0x00)) };

    bool _selectAllOnRelease;
    YtDlp.UpdateInfo? _updateInfo;
    string? _localYtDlp;
    bool _hasYtDlp = true;
    CancellationTokenSource? _cts;
    string? _lastFile;
    Platform _current = Platform.Unknown;
    string? _lastLink;
    bool _hideNoVideoWarnings;
    bool _closed;

    public MainWindow()
    {
        _settings.Look = _settings.Look.Checked(_gifNames.Length);
        LoadGif(_settings.Look.Gif);
        _canvas.IceReady += () => PostToUi(RedrawBackground);

        Title = $"{AppInfo.Name} - YouTube, TikTok, Instagram";
        Width = 780;
        Height = 660;
        MinWidth = 620;
        MinHeight = 540;
        FontSize = Ui.FontSize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 30;
        if (Ui.AppIcon != null) Icon = new WindowIcon(Ui.AppIcon);
        _title.Text = $"{AppInfo.Name} {AppInfo.Version}";

        _platform.Child = _platformText;
        _cookiesHelp.IsVisible = true;
        _cookiesHelp.Margin = new Thickness(0, -2, 3, 6);
        _cookiesHelp.HorizontalAlignment = HorizontalAlignment.Left;
        _download.IsDefault = true;

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        void Add(Control c, int col, int row, int span = 1)
        {
            Grid.SetColumn(c, col);
            Grid.SetRow(c, row);
            Grid.SetColumnSpan(c, span);
            fields.Children.Add(c);
        }
        Add(Caption("Link:"), 0, 0); Add(_url, 1, 0); Add(_platform, 2, 0);
        Add(Caption("Cookies:"), 0, 1); Add(_cookiesStatus, 1, 1); Add(Ui.Row(_cookiesPick, _cookiesClear), 2, 1);
        Add(_cookiesHelp, 1, 2);
        Add(Caption("Folder:"), 0, 3); Add(_outDir, 1, 3); Add(_outPick, 2, 3);
        Add(Caption("Mode:"), 0, 4); Add(Row(_video, _m4a, _mp3, _cover), 1, 4, 2);
        var subtitlesCaption = Caption("Subtitles:", 18);
        Add(Caption("Quality:"), 0, 5); Add(Row(_quality, subtitlesCaption, _subtitles), 1, 5, 2);
        var toCaption = Caption("to");
        Add(Caption("Fragment:"), 0, 6); Add(Row(_from, toCaption, _to, _chapters), 1, 6, 2);
        Add(_qualityHint, 1, 7, 2);
        foreach (var b in new[] { _cookiesPick, _cookiesClear, _outPick, _chapters }) b.Margin = new Thickness(3);
        _quality.Margin = _subtitles.Margin = new Thickness(3);

        _updateBanner.Child = BannerGrid(_updateText, _updateNow);
        _appBanner.Child = BannerGrid(_appUpdateText, _appNotes, _appUpdateNow);
        var lower = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto") };
        var buttons = Row(_download, _cancel, _showFile, _setup);
        buttons.Spacing = 6;
        buttons.Margin = new Thickness(3, 0);
        Control[] lowerRows = [buttons, _progress, _status, _log, Row(_engine, _update, _errorLogLink), _appBanner, _updateBanner];
        for (int i = 0; i < lowerRows.Length; i++)
        {
            Grid.SetRow(lowerRows[i], i);
            lower.Children.Add(lowerRows[i]);
        }
        _log.MinHeight = 80;

        _fieldsHost = new Border { Padding = new Thickness(12, 12, 12, 0), Child = fields };
        _lowerHost = new Border { Padding = new Thickness(12), Child = lower };
        var titleBar = new Border { Height = 30, Background = Brushes.Transparent, Child = _title };
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        DockPanel.SetDock(titleBar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_fieldsHost, Avalonia.Controls.Dock.Top);
        var content = new DockPanel { Children = { titleBar, _fieldsHost, _lowerHost } };
        Content = new Panel { Children = { _backgroundView, content } };

        _textControls.AddRange(fields.Children.OfType<TextBlock>());
        _textControls.AddRange([subtitlesCaption, toCaption, _cookiesHelp, _video, _m4a, _mp3, _cover, _status, _engine, _update, _errorLogLink, _title]);
        if (HasBackground) _log.Glass = true;
        LayoutUpdated += (_, _) => CheckBackgroundGeometry();
        ScalingChanged += (_, _) => RedrawBackground();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _outDir.Text = string.IsNullOrWhiteSpace(_settings.OutputDir) ? DefaultFolder : _settings.OutputDir;
        _quality.ItemsSource = Quality.All;
        _quality.SelectedItem = Quality.All.FirstOrDefault(q => q.MaxHeight == _settings.MaxHeight) ?? Quality.All[0];
        _quality.IsEnabled = !_settings.AudioOnly;
        _m4a.IsChecked = _settings.AudioOnly && _settings.AudioFormat == "m4a";
        _mp3.IsChecked = _settings.AudioOnly && _m4a.IsChecked != true;
        _video.IsChecked = !_settings.AudioOnly;
        _cover.IsChecked = _settings.SaveCover;
        _subtitles.ItemsSource = SubtitleLanguage.All;
        _subtitles.SelectedItem = SubtitleLanguage.From(_settings.Subtitles);
        _setup.IsVisible = false;
        _cancel.IsEnabled = _showFile.IsEnabled = false;
        _errorLogLink.IsVisible = ErrorLog.Exists;
        UpdateCookiesView();
        UpdateQualityHint();

        _heartbeat.Tick += (_, _) => OnHeartbeat();
        _gifTimer.Tick += (_, _) => OnGifTick();
        _url.TextChanged += (_, _) => UpdatePlatform();
        _url.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod == NavigationMethod.Pointer) _selectAllOnRelease = true;
            else _url.SelectAll();
        };
        _url.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_selectAllOnRelease && _url.SelectionStart == _url.SelectionEnd) _url.SelectAll();
            _selectAllOnRelease = false;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        foreach (var rb in new[] { _video, _m4a, _mp3 })
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked != true) return;
                _quality.IsEnabled = _cts == null && Mode == DownloadMode.Video;
                UpdateQualityHint();
                UsageLog.Write($"Mode: {rb.Content}");
            };
        _quality.SelectionChanged += (_, _) => { UpdateQualityHint(); UsageLog.Write($"Quality: {SelectedQuality.Label}"); };
        _cover.IsCheckedChanged += (_, _) => UsageLog.Write($"Save cover: {(_cover.IsChecked == true ? "on" : "off")}");
        _subtitles.SelectionChanged += (_, _) => UsageLog.Write($"Subtitles: {SelectedSubtitles.Name}");
        _from.TextChanged += (_, _) => UpdateQualityHint();
        _to.TextChanged += (_, _) => UpdateQualityHint();
        _chapters.Click += async (_, _) => await PickChaptersAsync();
        _cookiesHelp.Click += async (_, _) => await ShowCookieHelpAsync();
        _cookiesPick.Click += async (_, _) => await PickCookiesAsync();
        _cookiesClear.Click += (_, _) => ClearCookies();
        _outPick.Click += async (_, _) => await PickOutDirAsync();
        _download.Click += async (_, _) => await DownloadAsync();
        _cancel.Click += (_, _) => { UsageLog.Write("Clicked Cancel"); _cts?.Cancel(); };
        _showFile.Click += (_, _) => ShowInFolder();
        _setup.Click += async (_, _) => await InstallComponentsAsync();
        _update.Click += async (_, _) => await UpdateYtDlpAsync();
        _updateNow.Click += async (_, _) => await UpdateYtDlpAsync();
        _appUpdateNow.Click += async (_, _) => await UpdateAppAsync();
        _appNotes.Click += (_, _) => { UsageLog.Write("Opened release notes"); if (_appRelease != null) OpenUrl(_appRelease.PageUrl); };
        _errorLogLink.Click += (_, _) => OpenErrorLog();
        Activated += (_, _) => { if (_cts == null) DockTile.SetBadge(null); };
    }

    // Windows saves next to the exe; a Mac app saves to Downloads.
    static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static TextBlock Caption(string text, int left = 3) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 3, 8, 3) };

    static StackPanel Row(params Control[] controls)
    {
        var row = Ui.Row(controls);
        row.Spacing = 0;
        return row;
    }

    static Grid BannerGrid(params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*" + string.Concat(Enumerable.Repeat(",Auto", cells.Length - 1))) };
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (HasBackground) DecodeFrames();
        UsageLog.Write($"App started | background: {_gifName ?? "none"} ({_settings.Look}) | mode: {Mode} | quality: {SelectedQuality.Label} | " +
                       $"subtitles: {SelectedSubtitles.Name} | folder: {_outDir.Text} | " +
                       "saved cookies: " + (_settings.Cookies.Count > 0
                           ? string.Join(", ", _settings.Cookies.Select(kv => $"{kv.Key}={Path.GetFileName(kv.Value)}"))
                           : "none"));
        MacUpdate.CleanUp();
        _url.Focus();
        var appCheck = CheckAppUpdateAsync();
        await CheckEngineAsync();
        if (_closed) return;
        UsageLog.Write($"yt-dlp: {_localYtDlp ?? "not found"} ({YtDlp.Locate().Description})" +
                       (MissingComponents() is { Count: > 0 } m ? " | missing: " + string.Join(", ", m.Select(x => x.Name)) : ""));
        await CheckForUpdatesAsync();
        await appCheck;
    }

    async Task CheckAppUpdateAsync()
    {
        var release = await AppUpdate.CheckAsync();
        if (_closed) return;
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
        _appUpdateNow.IsVisible = release.ExeUrl != null;
        _appBanner.IsVisible = true;
    }

    async Task UpdateAppAsync()
    {
        if (_appRelease is not { } release || _cts != null) return;
        UsageLog.Write($"Update app: {AppInfo.Version} -> {release.Version}");
        if (MacUpdate.Bundle == null)
        {
            UsageLog.Write("Update app: not running from NyaDownloader.app, opening the release page");
            OpenUrl(release.PageUrl);
            return;
        }
        bool ready = false;
        await RunBusyAsync("Update app", async ct =>
        {
            Log($"Downloading {AppInfo.Name} {release.Version}: {release.ExeUrl}");
            ShowStatus($"Downloading {AppInfo.Name} {release.Version}...", NeutralColor);
            try
            {
                await MacUpdate.InstallAsync(release, f => PostToUi(() =>
                {
                    ShowProgress((int)(f * 1000));
                    ShowStatus($"Downloading {AppInfo.Name} {release.Version}: {(int)(f * 100)}%", NeutralColor);
                }), ct);
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
        if (!ready || _closed) return;
        UsageLog.Write($"Update app: installed {release.Version}, restarting");
        SaveSettings();
        try
        {
            MacUpdate.Relaunch();
        }
        catch (Exception ex)
        {
            LogError("APP UPDATE ERROR", "Couldn't restart: " + ex.Message, ex.ToString());
            ShowStatus($"Updated to {release.Version}. Quit the app and start it again.", OkColor);
            return;
        }
        Close();
    }

    async Task CheckForUpdatesAsync()
    {
        if (!_hasYtDlp) return;
        _updateInfo = await YtDlp.CheckUpdatesAsync(_localYtDlp);
        if (_closed) return;
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
        _updateBanner.IsVisible = show;
        _update.IsVisible = show;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Cmd+, comes from the app menu; Cmd+P is the Windows build's Ctrl+P.
        if (e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.P)
        {
            ShowLook();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.W)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        UsageLog.Write(_cts != null ? "App closed while busy (the running job was canceled)" : "App closed");
        _closed = true;
        PauseGif();
        _heartbeat.Stop();
        _cts?.Cancel();
        SaveSettings();
        DockTile.SetBadge(null);
    }

    void SaveSettings()
    {
        _settings.OutputDir = (_outDir.Text ?? "").Trim();
        _settings.AudioOnly = _video.IsChecked != true;
        _settings.AudioFormat = _m4a.IsChecked == true ? "m4a" : "mp3";
        _settings.SaveCover = _cover.IsChecked == true;
        _settings.MaxHeight = SelectedQuality.MaxHeight;
        _settings.Subtitles = SelectedSubtitles.Code;
        _settings.Save();
    }

    // ---- The animated background

    string? _gifName;
    BackgroundPack? _pack;
    readonly string[] _gifNames = GifNames();
    readonly DispatcherTimer _gifTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    readonly Stopwatch _gifClock = new();
    readonly BackdropCanvas _canvas = new();
    readonly BackgroundView _backgroundView = new();
    readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[2];
    int _nextBuffer;
    SKBitmap? _firstFrame;
    List<SKBitmap>? _frames;
    int[] _delaysMs = [];
    int _frameIndex, _decodeVersion;
    long _frameShownAt;
    string? _geometryKey;
    LookDialog? _lookDialog;

    bool HasBackground => _firstFrame != null;

    static string[] GifNames()
    {
        try
        {
            return typeof(MainWindow).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("bg/", StringComparison.Ordinal))
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
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("the background is missing");
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

    void ReplaceFrames(SKBitmap? first, List<SKBitmap>? frames, int[] delays)
    {
        var old = new List<SKBitmap>();
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
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || version != _decodeVersion || frames.Count < 2)
                {
                    foreach (var f in frames) f.Dispose();
                    return;
                }
                ReplaceFrames(_firstFrame, frames, delays);
                RedrawBackground();
                ResumeGif();
            });
        });
    }

    static (List<SKBitmap> Frames, int[] Delays) DecodeAllFrames(BackgroundPack pack, int blur)
    {
        var frames = new List<SKBitmap>(pack.Count);
        for (int i = 0; i < pack.Count; i++)
        {
            using var image = pack.Frame(i);
            frames.Add(Backdrop.Frame(image, blur));
        }
        return (frames, pack.Delays);
    }

    public void ShowLook()
    {
        if (!IsVisible) return;
        if (_lookDialog is { } open)
        {
            open.Activate();
            return;
        }
        UsageLog.Write("Opened background settings");
        var dialog = new LookDialog(_settings.Look, _gifNames.Length);
        dialog.Changed += ApplyLook;
        dialog.Closed += (_, _) =>
        {
            _lookDialog = null;
            SaveSettings();
            UsageLog.Write("Background settings: " + _settings.Look);
        };
        _lookDialog = dialog;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        double k = DesktopScaling;
        var area = Screens.ScreenFromWindow(this)?.WorkingArea;
        int right = Position.X + (int)(Bounds.Width * k), width = (int)(400 * k);
        int x = area is not { } a || right + 8 + width <= a.Right ? right + 8 : Math.Max(a.X, right - width - 24);
        dialog.Position = new PixelPoint(x, Position.Y + (int)(60 * k));
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
        RedrawBackground();
    }

    static int Px(double v) => (int)Math.Round(v);

    DRect DeviceRect(Control c, double scale)
    {
        var p = c.TranslatePoint(new Point(0, 0), this) ?? default;
        return DRect.FromLTRB(Px(p.X * scale), Px(p.Y * scale), Px((p.X + c.Bounds.Width) * scale), Px((p.Y + c.Bounds.Height) * scale));
    }

    List<GlassPanel> GlassPanels(double k)
    {
        int m = Px(6 * k), radius = Px(14 * k);
        DRect top = DeviceRect(_fieldsHost, k), bottom = DeviceRect(_lowerHost, k);
        return
        [
            new(DRect.FromLTRB(top.Left + m, top.Top + m, top.Right - m, top.Bottom), radius),
            new(DRect.FromLTRB(bottom.Left + m, bottom.Top + m, bottom.Right - m, bottom.Bottom - m), radius),
        ];
    }

    static string TextOf(Control c) => c switch
    {
        TextBlock t => t.Text ?? "",
        ContentControl { Content: string s } => s,
        _ => "",
    };

    List<Backing> Backings(double k)
    {
        int padX = Px(5 * k), padY = Px(1 * k), radius = Px(7 * k), touch = Px(2 * k);
        var list = new List<Backing>();
        if (_log.Glass && _log.IsEffectivelyVisible) list.Add(new Backing(DeviceRect(_log, k), Px(10 * k), Card: true));
        var pills = new List<DRect>();
        foreach (var c in _textControls)
        {
            if (!c.IsEffectivelyVisible || TextOf(c).Length == 0 || c.Bounds.Width <= 0 || c.Bounds.Height <= 0) continue;
            var r = DeviceRect(c, k);
            if (c is TextBlock t)
                r = new DRect(r.X + Px(t.Padding.Left * k), r.Y, Math.Min(Px((t.TextLayout.Width + t.Padding.Left + t.Padding.Right) * k), r.Width), r.Height);
            r.Inflate(padX, padY);
            for (int i = 0; i < pills.Count; i++)
            {
                var p = pills[i];
                int overlap = Math.Min(p.Bottom, r.Bottom) - Math.Max(p.Top, r.Top);
                int gap = Math.Max(p.Left, r.Left) - Math.Min(p.Right, r.Right);
                if (overlap * 2 < Math.Min(p.Height, r.Height) || gap > touch) continue;
                r = DRect.Union(p, r);
                pills.RemoveAt(i);
                i = -1;
            }
            pills.Add(r);
        }
        list.AddRange(pills.Select(r => new Backing(r, radius)));
        return list;
    }

    // Controls moved or a text changed: the glass and the text backings have to follow.
    void CheckBackgroundGeometry()
    {
        if (!HasBackground || _closed) return;
        double k = RenderScaling;
        var key = string.Join(";", GlassPanels(k).Select(p => p.Bounds)) + "|" + string.Join(";", Backings(k).Select(b => b.Bounds)) +
                  $"|{ClientSize}|{k}";
        if (key == _geometryKey) return;
        _geometryKey = key;
        Dispatcher.UIThread.Post(RedrawBackground, DispatcherPriority.Render);
    }

    static readonly bool StillBackground = Environment.GetEnvironmentVariable("NYADOWNLOADER_STILL") == "1";

    void ResumeGif()
    {
        if (StillBackground || _frames == null || _gifTimer.IsEnabled) return;
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
        if (_frames == null || WindowState == WindowState.Minimized || !IsVisible) return;
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
    }

    // Draws the background into the spare of two bitmaps and shows it, so the one on screen is never half-drawn.
    void RedrawBackground()
    {
        if (_closed) return;
        double k = RenderScaling;
        var size = new PixelSize((int)Math.Ceiling(ClientSize.Width * k), (int)Math.Ceiling(ClientSize.Height * k));
        if (size.Width <= 0 || size.Height <= 0) return;
        var frame = _frames != null ? _frames[Math.Min(_frameIndex, _frames.Count - 1)] : _firstFrame;
        if (frame == null)
        {
            _backgroundView.Image = null;
            _backgroundView.InvalidateVisual();
            return;
        }
        var target = _buffers[_nextBuffer];
        if (target == null || target.PixelSize != size)
        {
            target?.Dispose();
            target = _buffers[_nextBuffer] = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var fb = target.Lock())
        using (var pixels = new SKBitmap())
        {
            pixels.InstallPixels(new SKImageInfo(fb.Size.Width, fb.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), fb.Address, fb.RowBytes);
            _canvas.Render(pixels, frame, _settings.Look, GlassPanels(k), (float)k, Backings(k));
        }
        _backgroundView.Image = target;
        _nextBuffer ^= 1;
        _backgroundView.InvalidateVisual();
    }

    // ---- Fields

    Quality SelectedQuality => _quality.SelectedItem as Quality ?? Quality.All[0];

    DownloadMode Mode => _m4a.IsChecked == true ? DownloadMode.Audio : _mp3.IsChecked == true ? DownloadMode.Mp3 : DownloadMode.Video;

    SubtitleLanguage SelectedSubtitles => _subtitles.SelectedItem as SubtitleLanguage ?? SubtitleLanguage.None;

    static string Trimmed(TextBox box) => (box.Text ?? "").Trim();

    bool FragmentTyped => Trimmed(_from).Length > 0 || Trimmed(_to).Length > 0;

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

    async Task ShowCookieHelpAsync()
    {
        UsageLog.Write("Opened cookie help");
        await MessageDialog.Show(this, "How to get cookies.txt", "Getting cookies", CookieHowTo,
        [
            new DialogButton("Open extension page", Closes: false, OnClick: () => { UsageLog.Write("Opened extension page"); OpenUrl(ExtensionUrl); }),
            new DialogButton("Close"),
        ], defaultButton: 1, cancelButton: 1);
    }

    static void OpenUrl(string url) => MacSystem.OpenUrl(url);

    void LogError(string kind, string message, string? details = null)
    {
        ErrorLog.Write(kind, message, details);
        _errorLogLink.IsVisible = true;
    }

    void OpenErrorLog()
    {
        UsageLog.Write("Opened error.log");
        if (!ErrorLog.Exists) { _errorLogLink.IsVisible = false; return; }
        MacSystem.OpenTextFile(ErrorLog.FilePath);
    }

    void UpdatePlatform()
    {
        var uri = PlatformDetector.ParseUrl(_url.Text ?? "");
        var platform = uri == null ? Platform.Unknown : PlatformDetector.FromHost(uri.Host);
        if (uri != null)
        {
            if (_lastLink != null && _lastLink != uri.AbsoluteUri) _from.Text = _to.Text = "";
            _lastLink = uri.AbsoluteUri;
        }
        _platform.IsVisible = uri != null;
        _platformText.Text = PlatformDetector.DisplayName(platform);
        _platform.Background = new SolidColorBrush(PlatformColor(platform) ?? Color.FromRgb(0x69, 0x69, 0x69));
        if (platform == _current) return;
        _current = platform;
        StyleDownloadButton(PlatformColor(platform));
        UpdateCookiesView();
        UpdateQualityHint();
    }

    static readonly string[] ButtonKeys =
    [
        "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
        "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
    ];

    // The Download button takes the site's color, overriding the Fluent theme's brushes for this one button.
    void StyleDownloadButton(Color? color)
    {
        foreach (var key in ButtonKeys) _download.Resources.Remove(key);
        if (color is not { } c) return;
        _download.Resources["ButtonBackground"] = new SolidColorBrush(c);
        _download.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Blend(c, Colors.White, 0.18f));
        _download.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Blend(c, Colors.Black, 0.22f));
        foreach (var key in ButtonKeys.Skip(3)) _download.Resources[key] = Brushes.White;
    }

    static Color Blend(Color a, Color b, float t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    string? CookiesFor(Platform p) =>
        p != Platform.Unknown && _settings.Cookies.TryGetValue(p.ToString(), out var path) ? path : null;

    void UpdateCookiesView()
    {
        var name = PlatformDetector.DisplayName(_current);
        var path = CookiesFor(_current);
        var file = Path.GetFileName(path);
        _cookiesClear.IsEnabled = path != null && _cts == null;

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

    void SetCookieStatus(string text, Color color) => (_cookiesStatus.Text, _cookiesStatus.Foreground) = (text, new SolidColorBrush(color));

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

    async Task<IStorageFolder?> FolderOrNull(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(path); }
        catch { return null; }
    }

    async Task PickCookiesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose cookies.txt",
            AllowMultiple = false,
            SuggestedStartLocation = await FolderOrNull(Path.GetDirectoryName(CookiesFor(_current)) ?? DefaultFolder),
            FileTypeFilter = [new FilePickerFileType("Cookie files") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) AssignCookies(path);
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

    async Task PickOutDirAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where to save downloads",
            SuggestedStartLocation = await FolderOrNull(Trimmed(_outDir)) ?? await FolderOrNull(DefaultFolder),
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        _outDir.Text = path.TrimEnd('/') is { Length: > 0 } p ? p : path;
        UsageLog.Write($"Folder chosen: {_outDir.Text}");
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        bool accept = _cts == null && (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text));
        e.DragEffects = accept ? DragDropEffects.Copy & e.DragEffects : DragDropEffects.None;
        if (accept && e.DragEffects == DragDropEffects.None) e.DragEffects = DragDropEffects.Link;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_cts != null) return;
        if (e.DataTransfer.TryGetFiles() is { } items)
        {
            var files = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
            if (files.Count > 0)
            {
                UsageLog.Write($"Dropped {files.Count} file(s): {string.Join(", ", files.Select(Path.GetFileName))}");
                foreach (var f in files) AssignCookies(f);
                return;
            }
        }
        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;
        UsageLog.Write($"Dropped link: {text.Trim()}");
        _url.Text = text.Trim();
    }

    // ---- Jobs

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
            if (!_closed) ShowStatus("Canceled.", NeutralColor);
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
            if (!_closed) SetBusy(false);
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
        if (_closed) return;
        _localYtDlp = version;
        _hasYtDlp = version != null;
        ShowEngineState();
    }

    void ShowEngineState()
    {
        RefreshUpdateBanner();
        var missing = MissingComponents();
        _setup.IsVisible = missing.Count > 0;
        _setup.Content = $"Install components (~{missing.Sum(m => m.ApproxMb)} MB)";
        (_engine.Text, _engine.Foreground) = missing.Count == 0
            ? ($"yt-dlp {_localYtDlp} | {YtDlp.Locate().Description}", new SolidColorBrush(DimColor))
            : ("Missing: " + string.Join(", ", missing.Select(m => m.Name)) + ". Click Install components.", new SolidColorBrush(WarnColor));
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
            if (!_closed) await CheckEngineAsync();
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

    static string PartialStatus(int saved, int total, int failed, string? hint) =>
        $"Saved {saved} of {Count(total, "video", "videos")}, {failed} failed. " + (hint ?? "See the log below.");

    static string Describe(int photos, int videos) =>
        photos > 0 && videos > 0 ? $"{Count(photos, "photo", "photos")} and {Count(videos, "video", "videos")}"
        : photos > 0 ? Count(photos, "photo", "photos")
        : Count(videos, "video", "videos");

    async Task<bool> EnsureEngineAsync()
    {
        if (_hasYtDlp) return true;
        var missing = MissingComponents();
        var answer = await MessageDialog.Show(this, AppInfo.Name, "Components are missing",
            $"Downloading needs these components: {string.Join(", ", missing.Select(m => m.Name))} " +
            $"(~{missing.Sum(m => m.ApproxMb)} MB, one time). Download them now?",
            [new DialogButton("No"), new DialogButton("Yes")], defaultButton: 1, cancelButton: 0);
        UsageLog.Write($"Asked to install missing components before downloading: {(answer == 1 ? "Yes" : "No")}");
        if (answer != 1) return false;
        await InstallComponentsAsync();
        return _hasYtDlp;
    }

    Uri? LinkOrComplain(string action)
    {
        var uri = PlatformDetector.ParseUrl(_url.Text ?? "");
        if (uri != null) return uri;
        UsageLog.Write($"Clicked {action} without a valid link: \"{Trimmed(_url)}\"");
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
        if (!TimeText.TryParse(_from.Text ?? "", out var from) || !TimeText.TryParse(_to.Text ?? "", out var to))
        {
            UsageLog.Write($"Bad fragment: \"{Trimmed(_from)}\" - \"{Trimmed(_to)}\"");
            ShowStatus("Fragment: type times like 1:30 or 1:02:03, or leave both boxes empty for the whole video.", ErrorColor);
            return false;
        }
        double start = from ?? 0, end = to ?? double.PositiveInfinity;
        if (end <= start)
        {
            UsageLog.Write($"Bad fragment: \"{Trimmed(_from)}\" - \"{Trimmed(_to)}\"");
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
        if (listing == null || _closed) return null;
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

    async Task<List<PlaylistEntry>?> PickFromListAsync(Listing listing, Uri uri, LinkKind kind)
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
        UsageLog.Write("Shown dialog: Choose videos");
        var picked = await PickDialog.Show(this, "Choose videos", heading, columns, items, "video", "videos");
        if (picked == null || _closed)
        {
            UsageLog.Write("Choose videos: canceled");
            if (!_closed) ShowStatus("Canceled.", NeutralColor);
            return null;
        }
        var entries = picked.Select(i => listing.Entries[i]).ToList();
        UsageLog.Write($"Choose videos: picked {entries.Count} of {listing.Entries.Count}: {string.Join(", ", entries.Take(20).Select(e => e.Id))}" +
                       (entries.Count > 20 ? ", ..." : ""));
        return entries;
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
        if (info == null || _closed) return;
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
        UsageLog.Write("Shown dialog: Choose chapters");
        var picked = await PickDialog.Show(this, "Choose chapters",
            $"{info.Title}: {Count(info.Chapters.Count, "chapter", "chapters")}. Pick the ones to download. Each one is saved as a separate file.",
            [new("#", 40), new("Chapter"), new("Time", 140), new("Length", 70)], items, "chapter", "chapters");
        if (picked == null || _closed)
        {
            UsageLog.Write("Choose chapters: canceled");
            if (!_closed) ShowStatus("Canceled.", NeutralColor);
            return;
        }
        var chapters = picked.Select(i => info.Chapters[i]).ToList();
        UsageLog.Write($"Choose chapters: picked {string.Join(", ", chapters.Select(p => p.Number))}");
        await DownloadAsync(chapters);
    }

    async Task DownloadAsync(IReadOnlyList<Section>? chapters = null)
    {
        if (_cts != null || !await EnsureEngineAsync()) return;
        var uri = LinkOrComplain("Download");
        if (uri == null) return;
        Section? fragment = null;
        if (chapters == null && !TryGetFragment(out fragment)) return;
        var outDir = Trimmed(_outDir);
        if (outDir.Length == 0) outDir = DefaultFolder;
        // An app started from Finder runs in /, so ~ and relative folders are taken from the home folder.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (outDir == "~" || outDir.StartsWith("~/", StringComparison.Ordinal)) outDir = home + outDir[1..];
        else if (!Path.IsPathRooted(outDir)) outDir = Path.Combine(home, outDir);
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
                var picked = await PickFromListAsync(listing, uri, kind);
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
        var request = new Downloader.Request(platform, uri, Mode, SelectedQuality.MaxHeight, outDir, cookies, _cover.IsChecked == true)
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
        string? notice = null;

        await RunBusyAsync("Download", async ct =>
        {
            _lastFile = null;
            _showFile.IsEnabled = false;
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
                notice = "Download failed";
                return;
            }

            ShowProgress((int)_progress.Maximum);
            _lastFile = r.Saved.FirstOrDefault();
            _showFile.IsEnabled = _lastFile != null && File.Exists(_lastFile);
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
            notice = partial || (instagramAuto && r.Saved.Count == 0) ? "Download finished with problems" : "Download finished";

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
        if (_closed) return;
        if (notice != null) Notify(notice, _status.Text ?? "");
        if (loginProblem != Hints.LoginProblem.None) await ShowLoginProblemAsync(loginProblem, platform, cookies);
    }

    static void DeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static readonly bool QuietNotifications = Environment.GetEnvironmentVariable("NYADOWNLOADER_QUIET") == "1";

    // Like the Windows tray balloon: only when the window is in the background.
    void Notify(string title, string text)
    {
        if (IsActive && WindowState != WindowState.Minimized) return;
        if (text.Length > 200) text = text[..197] + "...";
        UsageLog.Write($"Notified: {title} | {text}" + (QuietNotifications ? " (quiet)" : ""));
        if (QuietNotifications) return;
        MacSystem.Notify(title, text);
        DockTile.Bounce();
    }

    async Task ShowLoginProblemAsync(Hints.LoginProblem problem, Platform platform, string? cookies)
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
        string[] labels = ["How do I get cookies?", "Choose cookies file...", "Close"];
        UsageLog.Write($"Shown dialog: {caption}");
        var clicked = await MessageDialog.Show(this, caption, heading, text, labels.Select(l => new DialogButton(l)).ToList(),
                                               defaultButton: 2, cancelButton: 2);
        UsageLog.Write($"Dialog {caption}: clicked {labels[clicked]}");
        if (clicked == 0) await ShowCookieHelpAsync();
        else if (clicked == 1) await PickCookiesAsync();
    }

    // ---- Progress

    readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(1) };
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
        if (_progress.IsIndeterminate == on) return;
        _progress.IsIndeterminate = on;
        UpdateDockBadge();
    }

    void ShowProgress(int perMille)
    {
        _progress.Value = Math.Clamp(perMille, 0, (int)_progress.Maximum);
        UpdateDockBadge();
    }

    // The Dock badge shows the percentage while something downloads, like the taskbar button on Windows.
    void UpdateDockBadge() =>
        DockTile.SetBadge(_cts == null || _progress.IsIndeterminate ? null : $"{(int)(_progress.Value / 10)}%");

    void ShowResultOnDock() =>
        DockTile.SetBadge(!IsActive && (_statusColor == ErrorColor || _statusColor == WarnColor) ? "!" : null);

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

    void PostToUi(Action action) => Dispatcher.UIThread.Post(() => { if (!_closed) action(); });

    void SetBusy(bool busy)
    {
        foreach (var c in new Control[] { _download, _setup, _update, _updateNow, _appUpdateNow, _cookiesPick, _cookiesClear, _outPick, _video, _m4a, _mp3, _cover, _subtitles, _chapters })
            c.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
        _url.IsReadOnly = _outDir.IsReadOnly = _from.IsReadOnly = _to.IsReadOnly = busy;
        _quality.IsEnabled = !busy && Mode == DownloadMode.Video;
        if (busy) return;
        StopHeartbeat();
        ShowResultOnDock();
        UpdateCookiesView();
        FocusLink();
    }

    void FocusLink()
    {
        if (IsActive) _url.Focus();
    }

    void ShowInFolder()
    {
        if (_lastFile == null || !File.Exists(_lastFile)) return;
        UsageLog.Write($"Show in folder: {_lastFile}");
        MacSystem.Reveal(_lastFile);
    }

    void ShowStatus(string text, Color color)
    {
        _status.Text = text;
        _status.Foreground = new SolidColorBrush(color);
        _statusColor = color;
    }

    void Log(string line)
    {
        _progressLineOpen = false;
        _log.AppendLine(line);
    }
}
