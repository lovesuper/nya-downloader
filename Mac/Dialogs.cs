using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace VideoDownloader;

static class Ui
{
    public static readonly Bitmap? AppIcon = LoadIcon();

    static Bitmap? LoadIcon()
    {
        try
        {
            using var s = typeof(Ui).Assembly.GetManifestResourceStream("app.ico");
            return s == null ? null : new Bitmap(s);
        }
        catch { return null; }
    }

    public const double FontSize = 13;

    // Avalonia centers a line of text by its full height, ascent plus descent. Helvetica, the default font on a Mac, has
    // a short descent, so the capitals of centered text sit above the middle of a field or a button. This is how far
    // to move text down so that the capitals are centered instead.
    public static readonly double CapShift = ComputeCapShift();

    static double ComputeCapShift()
    {
        try
        {
            using var typeface = SKTypeface.FromFamilyName(new Typeface(FontFamily.Default).GlyphTypeface.FamilyName);
            using var font = new SKFont(typeface, (float)FontSize);
            var m = font.Metrics;
            return Math.Clamp((m.CapHeight + m.Descent + m.Ascent) / 2, 0, 3);
        }
        catch { return 0; }
    }

    // Padding for a box with centered text: the same space in total, with the text moved down by CapShift.
    public static Thickness Pad(double left, double vertical, double right)
    {
        double shift = Math.Min(CapShift, vertical);
        return new Thickness(left, vertical + shift, right, vertical - shift);
    }

    public static Button Button(string text, int padX = 12) => new() { Content = text, Padding = Pad(padX, 4, padX), MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };

    // The compact Fluent theme pads a combo box 1 at the top and 3 at the bottom, which lifts its text even more.
    public static ComboBox ComboBox(double width) => new() { Width = width, Padding = Pad(12, 2, 0), VerticalAlignment = VerticalAlignment.Center };

    public static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.AddRange(controls);
        return row;
    }
}

sealed record DialogButton(string Text, bool Closes = true, Action? OnClick = null);

// The macOS take on the Windows TaskDialog and MessageBox: the app icon, a bold heading, text, buttons on the right.
sealed class MessageDialog : Window
{
    int _result = -1;

    MessageDialog(string caption, string heading, string text, IReadOnlyList<DialogButton> buttons, int defaultButton, int cancelButton)
    {
        Title = caption;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = Ui.FontSize;

        var texts = new StackPanel { Spacing = 8 };
        texts.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var top = new DockPanel();
        if (Ui.AppIcon != null)
        {
            var icon = new Image { Source = Ui.AppIcon, Width = 52, Height = 52, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            top.Children.Add(icon);
        }
        top.Children.Add(texts);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = buttons[i];
            var button = Ui.Button(b.Text);
            button.IsDefault = i == defaultButton;
            button.IsCancel = i == cancelButton;
            button.Click += (_, _) =>
            {
                b.OnClick?.Invoke();
                if (!b.Closes) return;
                _result = index;
                Close();
            };
            row.Children.Add(button);
        }
        Content = new StackPanel { Margin = new Thickness(20, 18, 20, 16), Children = { top, row } };
    }

    // Returns the index of the clicked button, or cancelButton if the dialog was closed some other way.
    public static async Task<int> Show(Window owner, string caption, string heading, string text, IReadOnlyList<DialogButton> buttons,
                                       int defaultButton = 0, int cancelButton = -1)
    {
        var dialog = new MessageDialog(caption, heading, text, buttons, defaultButton, cancelButton);
        await dialog.ShowDialog(owner);
        return dialog._result >= 0 ? dialog._result : cancelButton;
    }
}

// A list with checkboxes to pick videos from a playlist or chapters from a video.
sealed class PickDialog : Window
{
    public sealed record Column(string Header, int Width = 0);
    public sealed record Item(string[] Cells, double? Seconds, bool Checked);

    sealed class Row(PickDialog owner, Item item) : INotifyPropertyChanged
    {
        public Item Item { get; } = item;
        public bool IsChecked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                owner.UpdateSummary();
            }
        }
        bool _checked = item.Checked;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    readonly List<Row> _rows;
    readonly ListBox _list;
    readonly TextBlock _summary = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x6D, 0x6D, 0x6D)), VerticalAlignment = VerticalAlignment.Center };
    readonly Button _ok = Ui.Button("Download"), _cancel = Ui.Button("Cancel");
    readonly string _one, _many;
    bool _accepted, _bulk;

    PickDialog(string caption, string heading, Column[] columns, IReadOnlyList<Item> items, string one, string many)
    {
        (_one, _many) = (one, many);
        _rows = items.Select(i => new Row(this, i)).ToList();
        Title = caption;
        Width = 680;
        Height = 480;
        MinWidth = 440;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = Ui.FontSize;

        string widths = "28," + string.Join(",", columns.Select(c => c.Width > 0 ? c.Width.ToString() : "*"));
        Grid Cells(IEnumerable<Control> cells)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(widths) };
            int col = 0;
            foreach (var c in cells)
            {
                Grid.SetColumn(c, col++);
                g.Children.Add(c);
            }
            return g;
        }
        TextBlock Cell(string text, bool header = false) => new()
        {
            Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0),
            FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
        };

        _list = new ListBox
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<Row>((row, _) =>
            {
                var check = new CheckBox { [!CheckBox.IsCheckedProperty] = new Binding(nameof(Row.IsChecked)) { Mode = BindingMode.TwoWay }, MinWidth = 0 };
                return Cells([check, .. row.Item.Cells.Take(columns.Length).Select(t => Cell(t))]);
            }),
        };
        // Space ticks or unticks the highlighted row, like a list with checkboxes on Windows.
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || _list.SelectedItem is not Row row) return;
            row.IsChecked = !row.IsChecked;
            e.Handled = true;
        };

        var header = Cells([new Control(), .. columns.Select(c => Cell(c.Header, header: true))]);
        header.Margin = new Thickness(12, 0, 12, 4);
        var all = Ui.Button("Select all");
        var none = Ui.Button("Select none");
        all.Click += (_, _) => CheckAll(true);
        none.Click += (_, _) => CheckAll(false);
        _ok.IsDefault = true;
        _cancel.IsCancel = true;
        _ok.Click += (_, _) => { _accepted = true; Close(); };
        _cancel.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var left = Ui.Row(all, none);
        DockPanel.SetDock(left, Dock.Left);
        var right = Ui.Row(_cancel, _ok);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.AddRange([left, right]);

        var listFrame = new Border
        {
            Child = _list, BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), ClipToBounds = true,
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(14) };
        var title = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        _summary.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(header, 1);
        Grid.SetRow(listFrame, 2);
        Grid.SetRow(_summary, 3);
        Grid.SetRow(buttons, 4);
        layout.Children.AddRange([title, header, listFrame, _summary, buttons]);
        Content = layout;

        UpdateSummary();
        Opened += (_, _) =>
        {
            var first = _rows.FindIndex(r => r.IsChecked);
            _list.SelectedIndex = Math.Max(0, first);
            if (first >= 0) _list.ScrollIntoView(first);
            _list.Focus();
        };
    }

    public static async Task<List<int>?> Show(Window owner, string caption, string heading, Column[] columns, IReadOnlyList<Item> items,
                                              string one, string many)
    {
        var dialog = new PickDialog(caption, heading, columns, items, one, many);
        await dialog.ShowDialog(owner);
        return dialog._accepted ? dialog.Picked : null;
    }

    List<int> Picked => _rows.Select((r, i) => (r, i)).Where(p => p.r.IsChecked).Select(p => p.i).ToList();

    void CheckAll(bool on)
    {
        _bulk = true;
        foreach (var r in _rows) r.IsChecked = on;
        _bulk = false;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        if (_bulk) return;
        var picked = Picked;
        var seconds = picked.Sum(i => _rows[i].Item.Seconds ?? 0);
        _summary.Text = $"{picked.Count} of {_rows.Count} selected" + (seconds > 0 ? ", " + TimeText.Format(seconds) + " in total" : "");
        _ok.IsEnabled = picked.Count > 0;
        _ok.Content = picked.Count == 0 ? "Download" : $"Download {picked.Count} {(picked.Count == 1 ? _one : _many)}";
    }
}

// Background settings (Cmd+,): which GIF and how the glass looks. Every change is applied at once.
sealed class LookDialog : Window
{
    public event Action<BackgroundLook>? Changed;

    readonly BackgroundLook _look;
    readonly ComboBox _gif = Ui.ComboBox(160);
    readonly List<(Slider Bar, TextBlock Value, Func<BackgroundLook, int> Get, Action<BackgroundLook, int> Set, string Unit)> _sliders = [];
    bool _loading;

    public LookDialog(BackgroundLook look, int gifCount)
    {
        _look = look.Clone();
        Title = "Background";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        FontSize = Ui.FontSize;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), Margin = new Thickness(16, 12) };
        int row = 0;
        void Add(Control c, int col, int span = 1)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            Grid.SetColumnSpan(c, span);
            grid.Children.Add(c);
        }
        TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(Caption("GIF:"), 0);
        _gif.ItemsSource = new[] { "Random" }.Concat(Enumerable.Range(1, gifCount).Select(i => i.ToString())).ToList();
        _gif.Margin = new Thickness(0, 4);
        Add(_gif, 1, 2);

        void Slider(string caption, Func<BackgroundLook, int> get, Action<BackgroundLook, int> set, string unit, int min = 0, int max = 100)
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var bar = new Slider { Minimum = min, Maximum = max, Width = 260, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true };
            var value = new TextBlock { MinWidth = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Add(Caption(caption), 0);
            Add(bar, 1);
            Add(value, 2);
            bar.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) OnChanged(); };
            _sliders.Add((bar, value, get, set, unit));
        }
        Slider("Visibility:", l => l.Visibility, (l, v) => l.Visibility = v, "%");
        Slider("Blur:", l => l.Blur, (l, v) => l.Blur = v, "");
        Slider("Scale:", l => l.Scale, (l, v) => l.Scale = v, "%", BackgroundLook.MinScale, BackgroundLook.MaxScale);
        Slider("Liquid glass:", l => l.Glass, (l, v) => l.Glass = v, "");
        Slider("Glass highlight:", l => l.Highlight, (l, v) => l.Highlight = v, "%");
        Slider("Glass frost:", l => l.Frost, (l, v) => l.Frost = v, "%");
        Slider("Ice:", l => l.Ice, (l, v) => l.Ice = v, "");
        Slider("Text backing:", l => l.Backing, (l, v) => l.Backing = v, "%");

        row++;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var reset = Ui.Button("Reset");
        var close = Ui.Button("Close");
        close.IsCancel = true;
        var buttons = Ui.Row(reset, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        Add(buttons, 0, 3);
        Content = grid;

        _gif.SelectionChanged += (_, _) => OnChanged();
        reset.Click += (_, _) => { Fill(new BackgroundLook()); OnChanged(); };
        close.Click += (_, _) => Close();
        Fill(_look);
    }

    void Fill(BackgroundLook look)
    {
        _loading = true;
        _gif.SelectedIndex = Math.Clamp(look.Gif, 0, _gif.ItemCount - 1);
        foreach (var s in _sliders) s.Bar.Value = Math.Clamp(s.Get(look), s.Bar.Minimum, s.Bar.Maximum);
        _loading = false;
        ShowValues();
    }

    void ShowValues()
    {
        foreach (var s in _sliders) s.Value.Text = (int)s.Bar.Value + s.Unit;
    }

    void OnChanged()
    {
        if (_loading) return;
        _look.Gif = Math.Max(0, _gif.SelectedIndex);
        foreach (var s in _sliders) s.Set(_look, (int)Math.Round(s.Bar.Value));
        ShowValues();
        Changed?.Invoke(_look.Clone());
    }
}
