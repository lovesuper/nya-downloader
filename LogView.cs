using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VideoDownloader;

sealed class LogView : Control
{
    readonly record struct Row(int Line, int Start, int Length);

    readonly List<string> _lines = [];
    readonly List<Row> _rows = [];
    int _lastLineRow, _top, _columns = 80, _lineHeight = 14;
    float _charWidth = 7;
    (int Line, int Col) _anchor, _caret;
    bool _selecting, _dragging, _barHot, _follow = true;
    int _dragOffset;

    public LogView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        TabStop = true;
        Cursor = Cursors.IBeam;
        AccessibleRole = AccessibleRole.Text;
        var menu = new ContextMenuStrip();
        var copy = new ToolStripMenuItem("Copy", null, (_, _) => Copy()) { ShortcutKeyDisplayString = "Ctrl+C" };
        var all = new ToolStripMenuItem("Select all", null, (_, _) => SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" };
        menu.Items.AddRange([copy, all]);
        menu.Opening += (_, _) => copy.Enabled = HasSelection;
        ContextMenuStrip = menu;
        Measure();
    }

    public string LogText => _lines.Count == 0 ? "" : string.Join(Environment.NewLine, _lines) + Environment.NewLine;
    public int LineCount => _lines.Count;
    public int FirstVisibleRow => _top;
    public int RowCount => _rows.Count;
    public int Columns => _columns;
    internal List<string> RowTexts() => _rows.Select(r => _lines[r.Line].Substring(r.Start, r.Length)).ToList();
    public int VisibleRows => Math.Max(1, (ClientSize.Height - Padding.Vertical) / _lineHeight);
    int MaxTop => Math.Max(0, _rows.Count - VisibleRows);
    public bool AtBottom => _top >= MaxTop;
    public bool HasSelection => _anchor != _caret;

    public string SelectedText
    {
        get
        {
            var (a, b) = Ordered();
            if (a == b || _lines.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int line = a.Line; line <= b.Line && line < _lines.Count; line++)
            {
                var s = _lines[line];
                int from = line == a.Line ? Math.Min(a.Col, s.Length) : 0, to = line == b.Line ? Math.Min(b.Col, s.Length) : s.Length;
                if (line > a.Line) sb.Append(Environment.NewLine);
                sb.Append(s, from, Math.Max(0, to - from));
            }
            return sb.ToString();
        }
    }

    public void AppendLine(string text)
    {
        foreach (var part in Clean(text).Split('\n'))
        {
            _lines.Add(part);
            _lastLineRow = _rows.Count;
            Wrap(_lines.Count - 1);
        }
        if (_follow) _top = MaxTop;
        Invalidate();
    }

    public void ReplaceLastLine(string text)
    {
        if (_lines.Count == 0) { AppendLine(text); return; }
        _lines[^1] = Clean(text).Replace('\n', ' ');
        _rows.RemoveRange(_lastLineRow, _rows.Count - _lastLineRow);
        Wrap(_lines.Count - 1);
        _top = _follow ? MaxTop : Math.Clamp(_top, 0, MaxTop);
        Invalidate();
    }

    public void Clear()
    {
        _lines.Clear();
        _rows.Clear();
        _lastLineRow = _top = 0;
        _anchor = _caret = (0, 0);
        _follow = true;
        Invalidate();
    }

    public void SelectAll()
    {
        if (_lines.Count == 0) return;
        _anchor = (0, 0);
        _caret = (_lines.Count - 1, _lines[^1].Length);
        Invalidate();
    }

    public void Copy()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }
    }

    public void ScrollTo(int row)
    {
        int top = Math.Clamp(row, 0, MaxTop);
        _follow = top >= MaxTop;
        if (top == _top) return;
        _top = top;
        Invalidate();
    }

    static string Clean(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").TrimEnd('\n');

    float Dpi => DeviceDpi / 96f;
    int BarZone => (int)Math.Round(12 * Dpi);
    int ColumnsFor(int width) => Math.Max(8, (int)((width - Padding.Horizontal - BarZone) / _charWidth));

    void Measure()
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var size = TextRenderer.MeasureText(new string('x', 100), Font, new Size(int.MaxValue, int.MaxValue), flags);
        _charWidth = Math.Max(1f, size.Width / 100f);
        _lineHeight = Math.Max(1, size.Height);
        _columns = ColumnsFor(ClientSize.Width);
    }

    void Rewrap()
    {
        int keepLine = _top < _rows.Count ? _rows[_top].Line : 0;
        Measure();
        _rows.Clear();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i == _lines.Count - 1) _lastLineRow = _rows.Count;
            Wrap(i);
        }
        _top = _follow ? MaxTop : Math.Clamp(Math.Max(0, _rows.FindIndex(r => r.Line == keepLine)), 0, MaxTop);
        Invalidate();
    }

    void Wrap(int line)
    {
        var s = _lines[line];
        if (s.Length <= _columns) { _rows.Add(new Row(line, 0, s.Length)); return; }
        int start = 0;
        while (start < s.Length)
        {
            int end = Math.Min(s.Length, start + _columns);
            if (end < s.Length)
            {
                int space = s.LastIndexOf(' ', end, end - start);
                if (space > start) end = space + 1;
            }
            _rows.Add(new Row(line, start, end - start));
            start = end;
        }
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Rewrap(); }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Rewrap(); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ColumnsFor(ClientSize.Width) != _columns) Rewrap();
        else _top = _follow ? MaxTop : Math.Clamp(_top, 0, MaxTop);
    }

    (int Line, int Col) HitTest(Point p)
    {
        if (_rows.Count == 0) return (0, 0);
        int r = _top + (int)Math.Floor((p.Y - Padding.Top) / (double)_lineHeight);
        if (r < 0) return (_rows[0].Line, 0);
        if (r >= _rows.Count) return (_lines.Count - 1, _lines[^1].Length);
        var row = _rows[r];
        int col = (int)Math.Round((p.X - Padding.Left) / _charWidth);
        return (row.Line, row.Start + Math.Clamp(col, 0, row.Length));
    }

    ((int Line, int Col) From, (int Line, int Col) To) Ordered() =>
        _anchor.CompareTo(_caret) <= 0 ? (_anchor, _caret) : (_caret, _anchor);

    Rectangle Track
    {
        get
        {
            int w = (int)Math.Round((_barHot || _dragging ? 8 : 5) * Dpi), m = (int)Math.Round(3 * Dpi);
            return new Rectangle(ClientSize.Width - w - m, m, w, Math.Max(1, ClientSize.Height - 2 * m));
        }
    }

    Rectangle Thumb
    {
        get
        {
            if (_rows.Count <= VisibleRows) return Rectangle.Empty;
            var t = Track;
            int h = Math.Min(t.Height, Math.Max((int)Math.Round(24 * Dpi), t.Height * VisibleRows / _rows.Count));
            int y = t.Top + (int)Math.Round((t.Height - h) * (double)_top / Math.Max(1, MaxTop));
            return new Rectangle(t.Left, y, t.Width, h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
        var (from, to) = Ordered();
        bool any = from != to;
        int x0 = Padding.Left, y = Padding.Top;
        using var highlight = new SolidBrush(SystemColors.Highlight);
        for (int r = _top; r < _rows.Count && y < ClientSize.Height; r++, y += _lineHeight)
        {
            var row = _rows[r];
            var s = _lines[row.Line];
            int a = row.Start, b = row.Start + row.Length, selA = a, selB = a;
            bool inside = any && (row.Line, b).CompareTo(from) >= 0 && (row.Line, a).CompareTo(to) <= 0;
            if (inside)
            {
                selA = row.Line == from.Line ? Math.Clamp(from.Col, a, b) : a;
                selB = row.Line == to.Line ? Math.Clamp(to.Col, a, b) : b;
            }
            bool tail = inside && row.Line < to.Line && b == s.Length;
            if (selB > selA || tail)
            {
                int left = x0 + (int)Math.Round((selA - a) * _charWidth);
                int right = x0 + (int)Math.Round((selB - a + (tail ? 1 : 0)) * _charWidth);
                g.FillRectangle(highlight, left, y, right - left, _lineHeight);
            }
            void Part(int start, int end, Color color)
            {
                if (end <= start) return;
                TextRenderer.DrawText(g, s.Substring(start, end - start), Font, new Point(x0 + (int)Math.Round((start - a) * _charWidth), y), color, flags);
            }
            Part(a, selA, ForeColor);
            Part(selA, selB, SystemColors.HighlightText);
            Part(selB, b, ForeColor);
        }
        var thumb = Thumb;
        if (thumb.IsEmpty) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Backdrop.RoundedRect(thumb, thumb.Width / 2f);
        using var brush = new SolidBrush(Color.FromArgb(_barHot || _dragging ? 150 : 95, 0, 0, 0));
        g.FillPath(brush, path);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool handled = true;
        switch (e.KeyCode)
        {
            case Keys.C or Keys.Insert when e.Control: Copy(); break;
            case Keys.A when e.Control: SelectAll(); break;
            case Keys.Up: ScrollTo(_top - 1); break;
            case Keys.Down: ScrollTo(_top + 1); break;
            case Keys.PageUp: ScrollTo(_top - VisibleRows); break;
            case Keys.PageDown: ScrollTo(_top + VisibleRows); break;
            case Keys.Home: ScrollTo(0); break;
            case Keys.End: ScrollTo(MaxTop); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = SystemInformation.MouseWheelScrollLines;
        int step = lines <= 0 ? VisibleRows : lines;
        ScrollTo(_top - (int)Math.Round(e.Delta / 120.0 * step));
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused) Focus();
        if (e.Button != MouseButtons.Left) return;
        var thumb = Thumb;
        if (!thumb.IsEmpty && e.X >= ClientSize.Width - BarZone)
        {
            if (e.Y >= thumb.Top && e.Y < thumb.Bottom) { _dragging = true; _dragOffset = e.Y - thumb.Top; Capture = true; }
            else ScrollTo(_top + (e.Y < thumb.Top ? -VisibleRows : VisibleRows));
            Invalidate();
            return;
        }
        var hit = HitTest(e.Location);
        if ((ModifierKeys & Keys.Shift) == 0) _anchor = hit;
        _caret = hit;
        _selecting = Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var t = Track;
            int room = Math.Max(1, t.Height - Thumb.Height);
            ScrollTo((int)Math.Round((e.Y - _dragOffset - t.Top) / (double)room * MaxTop));
            return;
        }
        if (_selecting)
        {
            if (e.Y < 0) ScrollTo(_top - 1);
            else if (e.Y >= ClientSize.Height) ScrollTo(_top + 1);
            var hit = HitTest(e.Location);
            if (hit != _caret) { _caret = hit; Invalidate(); }
            return;
        }
        bool hot = !Thumb.IsEmpty && e.X >= ClientSize.Width - BarZone;
        if (hot == _barHot) return;
        _barHot = hot;
        Cursor = hot ? Cursors.Default : Cursors.IBeam;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_selecting && !_dragging) return;
        _selecting = _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_barHot || _dragging) return;
        _barHot = false;
        Cursor = Cursors.IBeam;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || _lines.Count == 0 || e.X >= ClientSize.Width - BarZone) return;
        var (line, col) = HitTest(e.Location);
        var s = _lines[line];
        int a = Math.Min(col, s.Length), b = a;
        while (a > 0 && !char.IsWhiteSpace(s[a - 1])) a--;
        while (b < s.Length && !char.IsWhiteSpace(s[b])) b++;
        (_anchor, _caret) = ((line, a), (line, b));
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new LogAccessible(this);

    sealed class LogAccessible(LogView owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Text;
        public override AccessibleStates State => base.State | AccessibleStates.ReadOnly;
        public override string? Value { get => owner.LogText; set { } }
    }
}
