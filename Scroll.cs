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

/// <summary>Thin, rounded, purple custom vertical scrollbar (overlay).</summary>
internal sealed class SlimScroll : Control
{
    private static readonly Dictionary<Control, SlimScroll> Map = new();

    private readonly Func<(int total, int page, int pos)> _model;
    private readonly Action<int> _set;
    private readonly Control _host;
    private readonly bool _childMode;   // true = sb is a child of the host; false = sibling overlay
    private bool _drag, _hover;
    private int _dy0, _p0;

    private SlimScroll(Control host, bool childMode, Func<(int, int, int)> model, Action<int> set)
    {
        _host = host; _childMode = childMode; _model = model; _set = set;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Width = 11;
        TabStop = false;
    }

    /// <summary>Re-sync (and re-hide native bars for) the custom scrollbar bound to a host.</summary>
    public static void Refresh(Control host)
    {
        if (!Map.TryGetValue(host, out var sb)) return;
        if (host is ListBox) HideNative(host);
        sb.Sync();
    }

    private (int total, int page, int pos) M()
    {
        try { return _model(); } catch { return (0, 1, 0); }
    }

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
            b = new Rectangle(_host.ClientSize.Width - Width - 1, 1, Width, _host.ClientSize.Height - 2);
        }
        else
        {
            if (_host.Parent is null) { Visible = false; return; }
            b = new Rectangle(_host.Right - Width - 2, _host.Top + 2, Width, _host.Height - 4);
        }
        if (Bounds != b) Bounds = b;
        BringToFront();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (total, page, pos) = M();
        if (total <= page || page <= 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int maxPos = Math.Max(1, total - page);
        int th = Math.Max(28, (int)(Height * (float)page / total));
        int y = (int)((Height - th) * (Math.Min(pos, maxPos) / (float)maxPos));
        y = Math.Max(0, Math.Min(Height - th, y));
        var r = new Rectangle(2, y, Width - 4, th);
        using var path = Theme.RoundRect(r, (Width - 4) / 2);
        using var thumb = new SolidBrush(_drag || _hover ? Theme.Brand : Theme.BrandDim);
        g.FillPath(thumb, path);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_drag) { _hover = false; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var (total, page, pos) = M();
        if (total <= page) return;
        int maxPos = Math.Max(1, total - page);
        int th = Math.Max(28, (int)(Height * (float)page / total));
        int ty = (int)((Height - th) * (Math.Min(pos, maxPos) / (float)maxPos));
        if (e.Y >= ty && e.Y <= ty + th) { _drag = true; _dy0 = e.Y; _p0 = pos; }
        else { _set((int)(e.Y / (float)Height * total) - page / 2); Sync(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag) return;
        var (total, page, _) = M();
        int maxPos = Math.Max(1, total - page);
        int th = Math.Max(28, (int)(Height * (float)page / total));
        int span = Math.Max(1, Height - th);
        _set(_p0 + (int)Math.Round((e.Y - _dy0) / (float)span * maxPos));
        Sync();
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; _hover = ClientRectangle.Contains(e.Location); Invalidate(); }

    // ---------------------------------------------------------------- factories

    /// <summary>Custom scrollbar for a DataGridView (native bars removed).</summary>
    public static void ForGrid(DataGridView g)
    {
        g.ScrollBars = ScrollBars.None;
        var sb = new SlimScroll(g, false,
            () => (g.RowCount, Math.Max(1, g.DisplayedRowCount(false)), Math.Max(0, g.FirstDisplayedScrollingRowIndex)),
            p => { int max = Math.Max(0, g.RowCount - 1); p = Math.Max(0, Math.Min(max, p)); try { if (g.RowCount > 0) g.FirstDisplayedScrollingRowIndex = p; } catch { } });
        Map[g] = sb;

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
        var sb = new SlimScroll(list, false,
            () => (list.Items.Count, Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight)), list.TopIndex),
            p => { int max = Math.Max(0, list.Items.Count - 1); list.TopIndex = Math.Max(0, Math.Min(max, p)); });
        Map[list] = sb;

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

        var sb = new SlimScroll(root, true,
            () => (content.Height, root.ClientSize.Height, -content.Top),
            p => { int max = Math.Max(0, content.Height - root.ClientSize.Height); content.Top = -Math.Max(0, Math.Min(max, p)); });
        Map[root] = sb;

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

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

    private static void HideNative(Control c)
    {
        try { if (c.IsHandleCreated) ShowScrollBar(c.Handle, 1 /*SB_VERT*/, false); } catch { }
    }
}
