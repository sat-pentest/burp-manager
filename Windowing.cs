using System.Runtime.InteropServices;

namespace BurpManager;

/// <summary>
/// Caption button (─ □ ✕) that owns its hover state.
/// A FlatStyle Button keeps its hover fill via an internal flag cleared only by MouseLeave, and
/// maximizing moves the button out from under the cursor without ever delivering one — so the
/// highlight stayed stuck on. Here the state is ours, and <see cref="SyncHover"/> can re-derive
/// it from the real cursor position after anything that moves the window.
/// </summary>
internal sealed class CapButton : Control
{
    private readonly bool _danger;
    private readonly Action<Graphics, Rectangle, Color>? _painter;
    private bool _over, _down;

    /// <param name="painter">
    /// Draws the button face instead of <paramref name="glyph"/>: receives the client rectangle
    /// and the current foreground colour, so an icon picks up the same hover brightening as text.
    /// </param>
    public CapButton(string glyph, Size size, bool danger, Action onClick,
                     Action<Graphics, Rectangle, Color>? painter = null)
    {
        _painter = painter;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        Text = glyph;
        Size = size;
        _danger = danger;
        BackColor = Theme.Rail;
        ForeColor = Theme.Muted;
        Font = Theme.Sans(9f);
        Cursor = Cursors.Hand;
        TabStop = false;

        MouseEnter += (_, _) => { _over = true; Invalidate(); };
        MouseLeave += (_, _) => { _over = false; _down = false; Invalidate(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } };
        MouseUp += (_, e) =>
        {
            bool hit = _down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            _down = false;
            Invalidate();
            if (hit) onClick();
        };
    }

    /// <summary>Re-derive hover from the actual cursor position.</summary>
    public void SyncHover()
    {
        bool over = Visible && ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (over == _over) return;
        _over = over;
        _down = false;
        Invalidate();
    }

    public void SetGlyph(string g) { Text = g; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var hot = _danger ? Theme.Danger : Theme.Hover;
        e.Graphics.Clear(_down ? ControlPaint.Dark(hot, 0.12f) : _over ? hot : Theme.Rail);
        var fg = _over ? Color.White : Theme.Muted;

        if (_painter != null) { _painter(e.Graphics, ClientRectangle, fg); return; }

        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// Rail / chrome button that never shows a focus rectangle. WinForms draws a white outline on a
/// focused FlatStyle button, and the first rail entry takes focus at startup — so DASHBOARD came
/// up outlined on every launch. The rail is a menu, not a form field: it should not take focus.
/// </summary>
internal sealed class NavButton : Button
{
    public NavButton() => TabStop = false;
    protected override bool ShowFocusCues => false;
}

/// <summary>
/// Patient-monitor ECG trace. A sweep head runs left to right and the tail behind it decays,
/// which is what makes it read as "live" rather than as a static squiggle.
/// </summary>
internal sealed class VitalPulse : Control
{
    private const int Beats = 2;          // complexes across the full width
    private const float Step = 0.011f;    // phase per tick → ~1.7s sweep ≈ 70 bpm

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    private float _phase;

    public VitalPulse()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        _tick.Tick += (_, _) => { _phase = (_phase + Step) % 1f; Invalidate(); };
        // Don't burn a repaint every 33ms while the window is hidden or minimised.
        VisibleChanged += (_, _) => Pump();
        HandleCreated += (_, _) => Pump();
    }

    private void Pump()
    {
        if (Visible && IsHandleCreated) _tick.Start();
        else _tick.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Stop(); _tick.Dispose(); }
        base.Dispose(disposing);
    }

    private static float Bump(float t, float centre, float width)
    {
        float z = (t - centre) / width;
        return (float)Math.Exp(-z * z);
    }

    /// <summary>One PQRST complex over t ∈ [0,1), normalised so the R peak is 1.0.</summary>
    private static float Ecg(float t) =>
          0.16f * Bump(t, 0.160f, 0.030f)   // P
        - 0.12f * Bump(t, 0.275f, 0.012f)   // Q
        + 1.00f * Bump(t, 0.310f, 0.011f)   // R
        - 0.38f * Bump(t, 0.345f, 0.014f)   // S
        + 0.30f * Bump(t, 0.520f, 0.045f);  // T

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int w = Width, h = Height;
        if (w < 8 || h < 6) return;
        float mid = h * 0.62f, amp = h * 0.46f;

        float Y(float t) => mid - Ecg((t * Beats) % 1f) * amp;

        // Resting baseline, so the swept-out part still reads as a monitor lead.
        using (var flat = new Pen(Color.FromArgb(46, Theme.Brand), 1f))
            g.DrawLine(flat, 0, mid, w, mid);

        for (int x = 0; x < w - 1; x++)
        {
            float t = x / (float)w;
            float age = (_phase - t + 1f) % 1f;          // 0 at the head, →1 at the tail
            int a = (int)(255 * Math.Pow(1f - age, 2.4));
            if (a < 12) continue;
            using var pen = new Pen(Color.FromArgb(Math.Min(255, a), Theme.Brand), 1.5f);
            g.DrawLine(pen, x, Y(t), x + 1, Y((x + 1) / (float)w));
        }

        // Leading dot: the bright point the eye tracks.
        float hx = _phase * w, hy = Y(_phase);
        using var glow = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
        g.FillEllipse(glow, hx - 2.6f, hy - 2.6f, 5.2f, 5.2f);
        using var dot = new SolidBrush(Color.FromArgb(235, 240, 240, 255));
        g.FillEllipse(dot, hx - 1.3f, hy - 1.3f, 2.6f, 2.6f);
    }
}

/// <summary>
/// Chromeless secondary window styled like the main shell: 1px frame, custom title row
/// with drag / maximize / close, and native resize edges.
/// </summary>
internal class ThemedWindow : Form
{
    private const int GripPx = 6;
    private const int BarH = 36;

    private readonly Label _title = new();
    private readonly VitalPulse _pulse = new() { Size = new Size(132, 16) };
    private CapButton? _maxBtn;
    private readonly List<CapButton> _capBtns = new();
    private readonly List<CapButton> _extraBtns = new();
    private Action? _layout;

    /// <summary>Title row — put extra controls here (docked Right) if needed.</summary>
    protected Panel TitleBar { get; }

    public ThemedWindow(string title, Size size)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;   // shown without an owner
        MinimumSize = new Size(560, 360);
        ClientSize = size;
        Font = Theme.UI;
        ForeColor = Theme.Text;
        BackColor = Theme.Border;          // shows through Padding as the 1px frame
        Padding = new Padding(1);
        ShowInTaskbar = true;

        TitleBar = new Panel { Dock = DockStyle.Top, Height = BarH, BackColor = Theme.Rail };

        var logo = new AppLogo { Location = new Point(10, 8), Size = new Size(20, 20) };
        _title.Text = title;
        // Not AutoSize: a long URL grew straight through the caption buttons and overlapped them
        // when the window was narrowed. Width is clamped in Layout(); overflow becomes an ellipsis.
        _title.AutoSize = false;
        _title.AutoEllipsis = true;
        _title.ForeColor = Theme.Text;
        _title.Font = Theme.Code(9.5f, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        // One line tall on purpose: a Label wraps before it ellipsizes, and at narrow widths the
        // title broke onto a second row inside the caption. With no room to wrap it just truncates.
        _title.Height = TextRenderer.MeasureText("Ag", _title.Font).Height;
        _title.Location = new Point(TitleLeft, (BarH - _title.Height) / 2);

        var cls = WinBtn("✕", Close, danger: true);
        var max = WinBtn("□", ToggleMaximize);
        var min = WinBtn("─", () => WindowState = FormWindowState.Minimized);
        _maxBtn = max;
        _capBtns.AddRange(new[] { min, max, cls });

        _layout = () =>
        {
            cls.Location = new Point(TitleBar.Width - 40, 0);
            max.Location = new Point(TitleBar.Width - 80, 0);
            min.Location = new Point(TitleBar.Width - 120, 0);
            // Extras fill leftwards from the window-command group.
            for (int i = 0; i < _extraBtns.Count; i++)
                _extraBtns[i].Location = new Point(TitleBar.Width - 160 - 40 * i, 0);

            // Title gets whatever is left before the buttons, never more than its text needs.
            int free = ButtonsLeft - 12 - TitleLeft;
            int natural = TextRenderer.MeasureText(_title.Text, _title.Font).Width + 4;
            _title.Width = Math.Max(0, Math.Min(natural, free));
            _title.Visible = free > 24;
            PlacePulse();
        };
        TitleBar.Resize += (_, _) => _layout();

        TitleBar.Controls.AddRange(new Control[] { logo, _title, _pulse, min, max, cls });
        foreach (var c in new Control[] { TitleBar, logo, _title })
            c.MouseDown += OnCaptionMouseDown;

        Controls.Add(TitleBar);
        _layout();
    }

    /// <summary>
    /// Add a caption command to the left of ─ □ ✕. Shares CapButton's hover handling, so it also
    /// gets the cursor re-sync that keeps the highlight from sticking after a resize.
    /// </summary>
    public CapButton AddCaptionButton(string glyph, Action onClick,
                                      Action<Graphics, Rectangle, Color>? painter = null,
                                      string? tip = null)
    {
        var b = new CapButton(glyph, new Size(40, BarH), false, onClick, painter);
        if (tip != null) new ToolTip { InitialDelay = 400 }.SetToolTip(b, tip);
        _extraBtns.Add(b);
        _capBtns.Add(b);
        TitleBar.Controls.Add(b);
        b.BringToFront();
        _layout?.Invoke();
        return b;
    }

    /// <summary>
    /// Park the trace just past the title, which is AutoSize and grows with the URL. Hidden when
    /// there is no room left before the caption buttons, rather than sliding under them.
    /// </summary>
    private void PlacePulse()
    {
        int x = _title.Right + 16;
        _pulse.Visible = _title.Visible && x + _pulse.Width <= ButtonsLeft - 12;
        _pulse.Location = new Point(x, (BarH - _pulse.Height) / 2);
    }

    private const int TitleLeft = 38;   // clears the logo
    /// <summary>Left edge of the caption button strip (window commands + any extras).</summary>
    private int ButtonsLeft => TitleBar.Width - 120 - 40 * _extraBtns.Count;

    /// <summary>
    /// WinForms docks the highest-index child first, so any control added after the title bar
    /// would claim the whole client area and end up underneath it. Keep the bar outermost.
    /// </summary>
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control != TitleBar) TitleBar.SendToBack();
    }

    public void SetTitle(string t) { _title.Text = t; _layout?.Invoke(); }

    private CapButton WinBtn(string glyph, Action onClick, bool danger = false) =>
        new(glyph, new Size(40, BarH), danger, onClick);

    /// <summary>
    /// FormBorderStyle.None strips WS_THICKFRAME / WS_MAXIMIZEBOX, and the shell needs those to
    /// offer Snap — without them Win+Arrow is silently ignored. Put them back and cancel the
    /// non-client frame they would otherwise draw (see WM_NCCALCSIZE below).
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            Snap.AddStyles(cp);
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ClampMaximize();
    }

    /// <summary>Keep a Snap/Win+Up maximize inside the work area instead of over the taskbar.</summary>
    private void ClampMaximize()
    {
        if (IsHandleCreated) MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (WindowState == FormWindowState.Normal) ClampMaximize();
    }

    /// <summary>
    /// Snap resizes the window without going through our own buttons, so the maximize glyph and
    /// the hover states have to be re-derived from the resulting state rather than set at click time.
    /// </summary>
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        _maxBtn?.SetGlyph(WindowState == FormWindowState.Maximized ? "❐" : "□");
        foreach (var b in _capBtns) b.SyncHover();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        foreach (var b in _capBtns) b.SyncHover();
    }

    // Handing the drag to Windows on mouse-down eats the second click, so detect the
    // double-click here instead of relying on MouseDoubleClick.
    private long _lastClickMs;
    private Point _lastClickPos;

    private void OnCaptionMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        long now = Environment.TickCount64;
        var pos = Cursor.Position;
        bool isDouble =
            now - _lastClickMs <= SystemInformation.DoubleClickTime &&
            Math.Abs(pos.X - _lastClickPos.X) <= SystemInformation.DoubleClickSize.Width &&
            Math.Abs(pos.Y - _lastClickPos.Y) <= SystemInformation.DoubleClickSize.Height;

        _lastClickMs = now;
        _lastClickPos = pos;

        if (isDouble) { _lastClickMs = 0; ToggleMaximize(); return; }
        DragWindow();
    }

    private void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private void DragWindow()
    {
        ReleaseCapture();
        SendMessage(Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (Snap.EatNcCalcSize(ref m)) return;
        if (Snap.EatNcPaint(ref m)) return;

        if (m.Msg == 0x0084 /*WM_NCHITTEST*/ && WindowState == FormWindowState.Normal)
        {
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            bool l = p.X <= GripPx, r = p.X >= ClientSize.Width - GripPx;
            bool t = p.Y <= GripPx, b = p.Y >= ClientSize.Height - GripPx;
            int hit =
                l && t ? 13 : r && t ? 14 : l && b ? 16 : r && b ? 17 :
                l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
            if (hit != 0) { m.Result = (IntPtr)hit; return; }
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Snap support for chromeless (FormBorderStyle.None) windows. That border style strips the
/// styles the shell looks for, so Win+Arrow is silently ignored; they have to be put back
/// without letting Windows draw a caption or a sizing border.
/// </summary>
internal static class Snap
{
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_THICKFRAME  = 0x00040000;

    /// <summary>
    /// WS_THICKFRAME is what makes the shell offer Snap at all; the box styles let Win+Up
    /// maximize and Win+Down restore.
    /// NEVER add WS_SYSMENU here — Windows pairs it with WS_CAPTION and the native title bar
    /// comes back, which is exactly the regression this replaced.
    /// </summary>
    public static void AddStyles(CreateParams cp) =>
        cp.Style |= WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;

    /// <summary>
    /// WS_THICKFRAME would otherwise reserve a sizing border and shrink the client area.
    /// Claiming the whole proposed rect as client keeps the window chromeless.
    /// Returns true when the message was consumed.
    /// </summary>
    public static bool EatNcCalcSize(ref Message m)
    {
        if (m.Msg != 0x0083 /*WM_NCCALCSIZE*/ || m.WParam == IntPtr.Zero) return false;
        m.Result = IntPtr.Zero;
        return true;
    }

    /// <summary>
    /// Stop DefWindowProc from painting a standard frame over our client area.
    /// WS_THICKFRAME leaves the window with a non-client area of zero size, but the default
    /// handlers still draw into it whenever activation changes — so clicking back to the other
    /// window stamped a native caption across the top of a chromeless one. Nothing about the
    /// window styles or the client rect changes when this happens, which is why it is only
    /// visible in a screenshot taken after an activation switch.
    /// Returns true when the message was consumed.
    /// </summary>
    public static bool EatNcPaint(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0085: // WM_NCPAINT
                m.Result = IntPtr.Zero;
                return true;
            case 0x0086: // WM_NCACTIVATE — TRUE keeps the activation, skips the repaint
                m.Result = (IntPtr)1;
                return true;
            default:
                return false;
        }
    }
}
