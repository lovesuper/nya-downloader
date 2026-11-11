using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace VideoDownloader;

// The log under the buttons: selectable monospaced text that follows new lines while it is scrolled to the bottom.
sealed class LogBox : Border
{
    const int MaxLines = 5000;

    readonly SelectableTextBlock _text = new()
    {
        FontFamily = new FontFamily("Menlo, Monaco, monospace"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(6, 4),
    };
    readonly ScrollViewer _scroll;
    readonly List<string> _lines = [];
    bool _follow = true, _flushQueued;

    public LogBox()
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.C, Avalonia.Input.KeyModifiers.Meta) };
        copy.Click += (_, _) => _text.Copy();
        var all = new MenuItem { Header = "Select All", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.A, Avalonia.Input.KeyModifiers.Meta) };
        all.Click += (_, _) => _text.SelectAll();
        var menu = new ContextMenu { Items = { copy, all } };
        menu.Opening += (_, _) => copy.IsEnabled = _text.SelectionEnd != _text.SelectionStart;
        _text.ContextMenu = menu;
        _scroll = new ScrollViewer
        {
            Content = _text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        // Only the user's scrolling decides whether to follow; the text growing doesn't.
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.OffsetDelta.Y != 0) _follow = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 4;
        };
        Child = _scroll;
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        Glass = false;
    }

    public bool Glass
    {
        get;
        set
        {
            field = value;
            Background = value ? Brushes.Transparent : Brushes.White;
            BorderBrush = value ? null : new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4));
            BorderThickness = new Thickness(value ? 0 : 1);
        }
    }

    public void AppendLine(string text)
    {
        foreach (var part in Clean(text).Split('\n')) _lines.Add(part);
        Changed();
    }

    public void ReplaceLastLine(string text)
    {
        if (_lines.Count == 0) { AppendLine(text); return; }
        _lines[^1] = Clean(text).Replace('\n', ' ');
        Changed();
    }

    public void Clear()
    {
        _lines.Clear();
        _follow = true;
        Changed();
    }

    static string Clean(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");

    // yt-dlp can print hundreds of lines a second, so the text is rebuilt at most once per dispatcher pass.
    void Changed()
    {
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        if (_flushQueued) return;
        _flushQueued = true;
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    void Flush()
    {
        _flushQueued = false;
        _text.Text = string.Join("\n", _lines);
        if (_follow) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
