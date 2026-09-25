using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BurpManager;

/// <summary>
/// Routes WM_MOUSEWHEEL to the innermost registered scroll surface under the
/// cursor, so hover-scroll works without the target needing keyboard focus.
/// </summary>
internal static class WheelHub
{
    private static readonly List<(Control c, Func<bool> can, Action<int> scroll)> Targets = new();
    private static bool _installed;

    public static void Register(Control c, Func<bool> canScroll, Action<int> scroll)
    {
        Targets.Add((c, canScroll, scroll));
        if (_installed) return;
        Application.AddMessageFilter(new Filter());
        _installed = true;
    }

    private sealed class Filter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A) return false; // WM_MOUSEWHEEL
            var p = Cursor.Position;
            (Control c, Func<bool> can, Action<int> s)? best = null;
            long bestArea = long.MaxValue;
            foreach (var t in Targets)
            {
                if (t.c.IsDisposed || !t.c.Visible || !t.c.IsHandleCreated) continue;
                if (!t.c.RectangleToScreen(t.c.ClientRectangle).Contains(p)) continue;
                bool can; try { can = t.can(); } catch { can = false; }
                if (!can) continue;   // skip surfaces with nothing to scroll → bubble to a larger one
                long area = (long)t.c.ClientSize.Width * t.c.ClientSize.Height;
                if (area < bestArea) { bestArea = area; best = t; }
            }
            if (best is null) return false;
            int delta = (short)((long)m.WParam >> 16);
            try { best.Value.s(delta); } catch { }
            return true;
        }
    }
}

/// <summary>Thin, rounded, purple custom scrollbar (overlay) — vertical or horizontal.</summary>
internal sealed class SlimScroll : Control
{
    private const int Thick = 11;

    private static readonly Dictionary<Control, List<SlimScroll>> Map = new();

    private readonly Func<(int total, int page, int pos)> _model;
    private readonly Action<int> _set;
    private readonly Control _host;
    private readonly bool _childMode;   // true = sb is a child of the host; false = sibling overlay
    private readonly bool _horizontal;
    private readonly int _endInset;     // leave room for the perpendicular bar
    private bool _outside;              // sit in a gutter beside the host instead of over it
    private bool _drag, _hover;
    private int _d0, _p0;

    private SlimScroll(Control host, bool childMode, bool horizontal, Func<(int, int, int)> model, Action<int> set, int endInset = 0)
    {
        _host = host; _childMode = childMode; _horizontal = horizontal; _model = model; _set = set; _endInset = endInset;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        if (horizontal) Height = Thick; else Width = Thick;
        TabStop = false;
    }

    private static void Register(Control host, SlimScroll sb)
    {
        if (!Map.TryGetValue(host, out var list)) Map[host] = list = new List<SlimScroll>();
        list.Add(sb);
    }

    /// <summary>Re-sync (and re-hide native bars for) the custom scrollbars bound to a host.</summary>
    public static void Refresh(Control host)
    {
        if (!Map.TryGetValue(host, out var list)) return;
        if (host is ListBox) HideNative(host);
        foreach (var sb in list) sb.Sync();
    }

    private (int total, int page, int pos) M()
    {
        try { return _model(); } catch { return (0, 1, 0); }
    }

    /// <summary>Track length along the scroll axis.</summary>
    private int Span => _horizontal ? Width : Height;

    public void Sync()
    {
        var (total, page, _) = M();
        bool need = total > page && page > 0;
        if (Visible != need) Visible = need;
        if (!need) return;

        Rectangle b;
        if (_childMode)
        {
            if (!_host.IsHandleCreated) return;
            var cs = _host.ClientSize;
            b = _horizontal
                ? new Rectangle(1, cs.Height - Thick - 1, Math.Max(0, cs.Width - 2 - _endInset), Thick)
                : new Rectangle(cs.Width - Thick - 1, 1, Thick, Math.Max(0, cs.Height - 2 - _endInset));
        }
        else
        {
            if (_host.Parent is null) { Visible = false; return; }
            if (_outside)
            {
                // Dedicated gutter next to the host — never covers content.
                b = _horizontal
                    ? new Rectangle(_host.Left, _host.Bottom + 1, Math.Max(0, _host.Width - _endInset), Thick)
                    : new Rectangle(_host.Right + 1, _host.Top, Thick, Math.Max(0, _host.Height - _endInset));
            }
            else
            {
                // Sit inside the host's border box so the overlay never covers a sibling header.
                b = _horizontal
                    ? new Rectangle(_host.Left + 2, _host.Bottom - Thick - 2, Math.Max(0, _host.Width - 4 - _endInset), Thick)
                    : new Rectangle(_host.Right - Thick - 2, _host.Top + 2, Thick, Math.Max(0, _host.Height - 4 - _endInset));
                b.Intersect(new Rectangle(_host.Left, _host.Top, _host.Width, _host.Height));
            }
        }
        if (Bounds != b) Bounds = b;
        BringToFront();
        Invalidate();
    }

    /// <summary>Thumb offset + length along the scroll axis for the current model state.</summary>
    private (int off, int len) Thumb()
    {
        var (total, page, pos) = M();
        int maxPos = Math.Max(1, total - page);
        int len = Math.Max(28, (int)(Span * (float)page / total));
        len = Math.Min(len, Span);
        int off = (int)((Span - len) * (Math.Min(pos, maxPos) / (float)maxPos));
        return (Math.Max(0, Math.Min(Span - len, off)), len);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (total, page, _) = M();
        if (total <= page || page <= 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (off, len) = Thumb();
        var r = _horizontal
            ? new Rectangle(off, 2, len, Height - 4)
            : new Rectangle(2, off, Width - 4, len);
        using var path = Theme.RoundRect(r, (Thick - 4) / 2);
        using var thumb = new SolidBrush(_drag || _hover ? Theme.ScrollHover : Theme.Scroll);
        g.FillPath(thumb, path);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_drag) { _hover = false; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var (total, page, pos) = M();
        if (total <= page) return;
        int at = _horizontal ? e.X : e.Y;
        var (off, len) = Thumb();
        if (at >= off && at <= off + len) { _drag = true; _d0 = at; _p0 = pos; }
        else { _set((int)(at / (float)Span * total) - page / 2); Sync(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag) return;
        var (total, page, _) = M();
        int maxPos = Math.Max(1, total - page);
        var (_, len) = Thumb();
        int track = Math.Max(1, Span - len);
        int at = _horizontal ? e.X : e.Y;
        _set(_p0 + (int)Math.Round((at - _d0) / (float)track * maxPos));
        Sync();
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; _hover = ClientRectangle.Contains(e.Location); Invalidate(); }

    // ---------------------------------------------------------------- factories

    /// <summary>Custom scrollbar for a DataGridView (native bars removed).</summary>
    public static void ForGrid(DataGridView g)
    {
        g.ScrollBars = ScrollBars.None;
        var sb = new SlimScroll(g, false, false,
            () => (g.RowCount, Math.Max(1, g.DisplayedRowCount(false)), Math.Max(0, g.FirstDisplayedScrollingRowIndex)),
            p => { int max = Math.Max(0, g.RowCount - 1); p = Math.Max(0, Math.Min(max, p)); try { if (g.RowCount > 0) g.FirstDisplayedScrollingRowIndex = p; } catch { } });
        Register(g, sb);

        void Attach()
        {
            if (g.Parent != null && sb.Parent != g.Parent) g.Parent.Controls.Add(sb);
            sb.Sync();
        }
        g.HandleCreated += (_, _) => Attach();
        g.ParentChanged += (_, _) => Attach();
        g.SizeChanged += (_, _) => sb.Sync();
        g.Scroll += (_, _) => sb.Sync();
        g.RowsAdded += (_, _) => sb.Sync();
        g.RowsRemoved += (_, _) => sb.Sync();
        g.VisibleChanged += (_, _) => sb.Sync();
        WheelHub.Register(g, () => g.RowCount > g.DisplayedRowCount(false), delta =>
        {
            int max = Math.Max(0, g.RowCount - 1);
            int np = Math.Max(0, Math.Min(max, Math.Max(0, g.FirstDisplayedScrollingRowIndex) + (delta > 0 ? -3 : 3)));
            try { if (g.RowCount > 0) g.FirstDisplayedScrollingRowIndex = np; } catch { }
            sb.Sync();
        });
    }

    /// <summary>Custom scrollbar for a ListBox (native bar hidden).</summary>
    public static void ForList(ListBox list)
    {
        var sb = new SlimScroll(list, false, false,
            () => (list.Items.Count, Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight)), list.TopIndex),
            p => { int max = Math.Max(0, list.Items.Count - 1); list.TopIndex = Math.Max(0, Math.Min(max, p)); });
        Register(list, sb);

        void Attach()
        {
            if (list.Parent != null && sb.Parent != list.Parent) list.Parent.Controls.Add(sb);
            HideNative(list);
            sb.Sync();
        }
        list.HandleCreated += (_, _) => Attach();
        list.ParentChanged += (_, _) => Attach();
        list.SizeChanged += (_, _) => { HideNative(list); sb.Sync(); };
        WheelHub.Register(list, () => list.Items.Count > list.ClientSize.Height / Math.Max(1, list.ItemHeight), delta =>
        {
            int max = Math.Max(0, list.Items.Count - 1);
            list.TopIndex = Math.Max(0, Math.Min(max, list.TopIndex + (delta > 0 ? -3 : 3)));
            HideNative(list);
            sb.Sync();
        });
    }

    /// <summary>Wrap a panel of stacked Dock=Top children with a custom scrollbar (no native AutoScroll bar).</summary>
    public static void WrapTop(Panel root)
    {
        var pad = root.Padding;
        root.SuspendLayout();
        var kids = root.Controls.Cast<Control>().ToArray();
        root.Controls.Clear();
        root.AutoScroll = false;
        root.Padding = new Padding(0);

        var content = new Panel { BackColor = root.BackColor, Location = new Point(0, 0), Padding = pad };
        content.Controls.AddRange(kids);

        int Sum() => pad.Vertical + content.Controls.Cast<Control>()
            .Where(c => c.Dock == DockStyle.Top).Sum(c => c.Height + c.Margin.Vertical);
        void Layout()
        {
            content.Width = Math.Max(0, root.ClientSize.Width - 12);
            content.Height = Math.Max(root.ClientSize.Height, Sum());
        }
        Layout();

        var sb = new SlimScroll(root, true, false,
            () => (content.Height, root.ClientSize.Height, -content.Top),
            p => { int max = Math.Max(0, content.Height - root.ClientSize.Height); content.Top = -Math.Max(0, Math.Min(max, p)); });
        Register(root, sb);

        root.Controls.Add(content);
        root.Controls.Add(sb);
        root.Resize += (_, _) => { Layout(); sb.Sync(); };
        root.HandleCreated += (_, _) => { Layout(); sb.Sync(); };
        content.ControlAdded += (_, _) => { Layout(); sb.Sync(); };

        WheelHub.Register(root, () => content.Height > root.ClientSize.Height, delta =>
        {
            int max = Math.Max(0, content.Height - root.ClientSize.Height);
            content.Top = -Math.Max(0, Math.Min(max, -content.Top + (delta > 0 ? -60 : 60)));
            sb.Sync();
        });
        root.ResumeLayout();
    }

    /// <summary>Custom vertical + horizontal scrollbars for a multiline TextBox (native bars hidden).</summary>
    public static void ForTextBox(TextBoxBase tb)
    {
        // WordWrap lives on the concrete types, not TextBoxBase.
        bool Wrapped() => tb switch { TextBox t => t.WordWrap, RichTextBox r => r.WordWrap, _ => false };
        int LineH() => Math.Max(1, tb.Font.Height);
        int Page() => Math.Max(1, tb.ClientSize.Height / LineH());
        int First() => tb.IsHandleCreated ? SendMessage(tb.Handle, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, 0, 0) : 0;
        void ScrollTo(int line)
        {
            if (!tb.IsHandleCreated) return;
            SendMessage(tb.Handle, 0x00B6 /*EM_LINESCROLL*/, 0, line - First());
        }

        // Horizontal model in character columns (monospace, so char width is uniform).
        int CharW() => Math.Max(1, TextRenderer.MeasureText("0000000000", tb.Font).Width / 10);
        int ColsPage() => Math.Max(1, tb.ClientSize.Width / CharW());
        int ColsTotal() => Wrapped() ? 0 : (tb.Lines.Length == 0 ? 0 : tb.Lines.Max(l => l.Length) + 2);
        // Absolute h-offset read from the control itself, so user scrolling stays in sync.
        int HPos()
        {
            if (!tb.IsHandleCreated) return 0;
            var pt = new POINT();
            SendMessagePt(tb.Handle, 0x00D6 /*EM_GETSCROLLPOS*/, 0, ref pt);
            return Math.Max(0, pt.x / CharW());
        }
        void HScrollTo(int col)
        {
            if (!tb.IsHandleCreated) return;
            int max = Math.Max(0, ColsTotal() - ColsPage());
            col = Math.Max(0, Math.Min(max, col));
            var pt = new POINT();
            SendMessagePt(tb.Handle, 0x00D6 /*EM_GETSCROLLPOS*/, 0, ref pt);
            pt.x = col * CharW();
            SendMessagePt(tb.Handle, 0x00D7 /*EM_SETSCROLLPOS*/, 0, ref pt);
        }

        // Tk.Inset reserves a gutter around the box, so the bars sit beside the text, not on it.
        var vsb = new SlimScroll(tb, false, false,
            () => (tb.Lines.Length, Page(), First()),
            p => ScrollTo(Math.Max(0, p)), endInset: 0) { _outside = true };
        var hsb = new SlimScroll(tb, false, true,
            () => (ColsTotal(), ColsPage(), HPos()),
            p => HScrollTo(p), endInset: 0) { _outside = true };
        Register(tb, vsb);
        Register(tb, hsb);

        // Read-only refresh: never writes a scroll position, so caret/typing is untouched.
        void Sync() { HideBoth(tb); vsb.Sync(); hsb.Sync(); }
        void Attach()
        {
            if (tb.Parent != null)
            {
                if (vsb.Parent != tb.Parent) tb.Parent.Controls.Add(vsb);
                if (hsb.Parent != tb.Parent) tb.Parent.Controls.Add(hsb);
            }
            Sync();
        }
        tb.HandleCreated += (_, _) => Attach();
        tb.ParentChanged += (_, _) => Attach();
        // Repaint on resize: shrinking for the gutters can otherwise leave stale glyphs behind.
        tb.SizeChanged += (_, _) => { Sync(); tb.Invalidate(); };
        tb.TextChanged += (_, _) => Sync();
        tb.Click += (_, _) => Sync();
        tb.KeyUp += (_, _) => Sync();
        WheelHub.Register(tb, () => tb.Lines.Length > Page(), delta =>
        {
            ScrollTo(First() + (delta > 0 ? -3 : 3));
            Sync();
        });
    }

    /// <summary>Custom scrollbar for an AutoScroll ScrollableControl (Panel/FlowLayoutPanel).</summary>
    public static void ForAutoScroll(ScrollableControl p)
    {
        var sb = new SlimScroll(p, false, false,
            () => (p.DisplayRectangle.Height, p.ClientSize.Height, -p.AutoScrollPosition.Y),
            v => { int max = Math.Max(0, p.DisplayRectangle.Height - p.ClientSize.Height); p.AutoScrollPosition = new Point(0, Math.Max(0, Math.Min(max, v))); });
        Register(p, sb);

        void Hide() { try { if (p.IsHandleCreated) ShowScrollBar(p.Handle, 3 /*SB_BOTH*/, false); } catch { } }
        void Attach()
        {
            if (p.Parent != null && sb.Parent != p.Parent) p.Parent.Controls.Add(sb);
            Hide();
            sb.Sync();
        }
        p.HandleCreated += (_, _) => Attach();
        p.ParentChanged += (_, _) => Attach();
        p.ControlAdded += (_, _) => { Hide(); sb.Sync(); };
        p.ClientSizeChanged += (_, _) => { Hide(); sb.Sync(); };
        p.Scroll += (_, _) => { Hide(); sb.Sync(); };
        WheelHub.Register(p, () => p.DisplayRectangle.Height > p.ClientSize.Height, delta =>
        {
            int max = Math.Max(0, p.DisplayRectangle.Height - p.ClientSize.Height);
            int np = Math.Max(0, Math.Min(max, -p.AutoScrollPosition.Y + (delta > 0 ? -48 : 48)));
            p.AutoScrollPosition = new Point(0, np);
            Hide(); sb.Sync();
        });
    }

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern int SendMessagePt(IntPtr hWnd, int msg, int wParam, ref POINT lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

    private static void HideNative(Control c)
    {
        try { if (c.IsHandleCreated) ShowScrollBar(c.Handle, 1 /*SB_VERT*/, false); } catch { }
    }

    private static void HideBoth(Control c)
    {
        try { if (c.IsHandleCreated) ShowScrollBar(c.Handle, 3 /*SB_BOTH*/, false); } catch { }
    }
}
