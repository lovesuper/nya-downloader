using System.ComponentModel;
using System.Drawing.Drawing2D;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_inner.ContainsFocus ? FocusLine : NormalLine);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
