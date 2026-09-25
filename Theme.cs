using System.Runtime.InteropServices;

namespace BurpManager;

/// <summary>Burp-AI style dark palette: neutral dark base, periwinkle-purple accent.</summary>
internal static class Theme
{
    public static readonly Color Bg       = Color.FromArgb(30, 30, 36);   // main (cool dark)
    public static readonly Color Panel    = Color.FromArgb(38, 38, 46);   // content panels
    public static readonly Color Rail     = Color.FromArgb(24, 23, 33);   // left nav (indigo-tinted)
    public static readonly Color Header    = Color.FromArgb(46, 46, 56);   // table header / strips
    public static readonly Color Hover    = Color.FromArgb(52, 51, 66);
    public static readonly Color Select   = Color.FromArgb(56, 54, 102);  // indigo selection (desaturated a touch)
    public static readonly Color Sunken    = Color.FromArgb(24, 24, 30);   // input/code surface (below Panel)
    // In-content scrollbars stay quiet: neutral idle, only brightening under the cursor.
    public static readonly Color Scroll      = Color.FromArgb(62, 62, 74);
    public static readonly Color ScrollHover = Color.FromArgb(104, 104, 122);
    public static readonly Color Border    = Color.FromArgb(58, 58, 70);
    public static readonly Color BorderSoft = Color.FromArgb(44, 44, 54);  // secondary hairline
    public static readonly Color Text      = Color.FromArgb(227, 227, 236);
    public static readonly Color Muted     = Color.FromArgb(152, 152, 168);
    public static readonly Color Accent    = Color.FromArgb(110, 123, 255); // periwinkle blue
    public static readonly Color Brand     = Color.FromArgb(124, 111, 240); // Burp-AI purple
    public static readonly Color BrandDim  = Color.FromArgb(91, 79, 196);
    public static readonly Color Danger    = Color.FromArgb(230, 96, 90);
    public static readonly Color Warn      = Color.FromArgb(224, 168, 66);
    public static readonly Color Ok        = Color.FromArgb(96, 196, 110);

    // Syntax colours for CodeBox — desaturated so they sit inside the periwinkle theme
    // instead of looking like a generic editor palette dropped on top of it.
    public static readonly Color SynKeyword = Color.FromArgb(178, 148, 255);  // violet
    public static readonly Color SynString  = Color.FromArgb(148, 206, 140);  // green
    public static readonly Color SynComment = Color.FromArgb(118, 118, 138);  // dim
    public static readonly Color SynNumber  = Color.FromArgb(236, 170, 108);  // amber
    public static readonly Color SynFunc    = Color.FromArgb(122, 198, 228);  // cyan
    public static readonly Color SynMarker  = Color.FromArgb(255, 186, 88);   // §payload§
    public static readonly Color SynMuted   = Color.FromArgb(132, 132, 150);

    // Typeface: Cascadia (ships with Win11 · SIL OFL) — Mono for UI, Code for brand/code.
    public const string UIName   = "Cascadia Mono";
    public const string MonoName = "Cascadia Code";

    public static readonly Font UI      = new(UIName, 9f);
    public static readonly Font UISemi  = new(UIName, 9f, FontStyle.Bold);
    public static readonly Font Section = new(UIName, 9.5f, FontStyle.Bold);
    public static readonly Font Brandy  = new(MonoName, 12.5f, FontStyle.Bold);

    /// <summary>Monospace/code face (Cascadia Code) at a given size/style.</summary>
    public static Font Code(float size, FontStyle style = FontStyle.Regular) => new(MonoName, size, style);
    /// <summary>UI face (Cascadia Mono) at a given size/style.</summary>
    public static Font Sans(float size, FontStyle style = FontStyle.Regular) => new(UIName, size, style);

    public static void FlatButton(Button b, Color? bg = null, Color? fg = null)
    {
        var back = bg ?? Header;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
        b.BackColor = back;
        b.ForeColor = fg ?? Text;
        b.Font = UI;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.12f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.10f);
    }

    public static void AccentButton(Button b)
    {
        FlatButton(b, Brand, Color.White);
        b.Font = UISemi;
        b.FlatAppearance.BorderColor = BrandDim;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Brand, 0.10f);
    }

    public static void StyleInput(Control c)
    {
        c.BackColor = Sunken;
        c.ForeColor = Text;
        c.Font = UI;
        if (c is TextBox tb) StyleTextBox(tb);
        if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
    }

    /// <summary>
    /// Borderless edit + a frame drawn by the parent. Painting the frame in the control's own
    /// non-client area left stale rectangles behind whenever it moved or resized.
    /// </summary>
    private static void StyleTextBox(TextBox tb)
    {
        tb.BorderStyle = BorderStyle.None;
        tb.AutoSize = false;
        tb.Multiline = true;      // required for EM_SETRECT (our inset + vertical centring)
        tb.WordWrap = false;

        tb.TextChanged += (_, _) =>
        {
            if (IsTall(tb) || tb.Text.IndexOfAny(new[] { '\r', '\n' }) < 0) return;
            int at = tb.SelectionStart;
            tb.Text = tb.Text.Replace("\r", "").Replace("\n", "");
            tb.SelectionStart = Math.Min(at, tb.TextLength);
        };

        void Frame(object? _, PaintEventArgs e)
        {
            if (!tb.Visible) return;
            var r = tb.Bounds;
            r.Inflate(1, 1);
            using var pen = new Pen(tb.Focused ? Brand : Border);
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        void HookParent()
        {
            if (tb.Parent is null) return;
            tb.Parent.Paint -= Frame;
            tb.Parent.Paint += Frame;
        }

        void Refresh()
        {
            ApplyTextRect(tb);
            tb.Parent?.Invalidate(Rectangle.Inflate(tb.Bounds, 3, 3), false);
        }

        tb.ParentChanged += (_, _) => { HookParent(); Refresh(); };
        tb.HandleCreated += (_, _) => Refresh();
        tb.SizeChanged += (_, _) => Refresh();
        tb.LocationChanged += (_, _) => Refresh();
        tb.FontChanged += (_, _) => Refresh();
        tb.GotFocus += (_, _) => Refresh();
        tb.LostFocus += (_, _) => Refresh();
        HookParent();

        new EditSkin(tb);
    }

    /// <summary>
    /// Height of the line box the edit control actually paints: ascent + descent.
    /// TextRenderer.MeasureText pads its result (16px against a real 14px line for Cascadia Mono
    /// 9pt), which is fine for matching Label centring but wrong for "does another line fit".
    /// </summary>
    private static int LineHeight(Font f)
    {
        try
        {
            var ff = f.FontFamily;
            float span = ff.GetLineSpacing(f.Style);
            if (span > 0)
            {
                float px = f.GetHeight();
                return (int)Math.Round((ff.GetCellAscent(f.Style) + ff.GetCellDescent(f.Style)) * px / span);
            }
        }
        catch { }
        return f.Height;
    }

    private static int LineHeight(Control c) => LineHeight(c.Font);
    private static bool IsTall(Control c) => c.ClientSize.Height > LineHeight(c) * 2;

    /// <summary>
    /// Y offset that puts a single line of text on the same baseline a Label would use in a box
    /// of this height. Labels centre on TextRenderer's padded measurement, so an input centred on
    /// the true glyph height ends up a pixel below its own field label.
    /// </summary>
    private static int CenterTop(int height, Font f) =>
        Math.Max(0, (height - TextRenderer.MeasureText("Ag", f).Height) / 2);

    /// <summary>Inset the text and centre it vertically when the box holds a single line.</summary>
    private static void ApplyTextRect(TextBox tb)
    {
        if (!tb.IsHandleCreated) return;
        int fh = LineHeight(tb), h = tb.ClientSize.Height, w = tb.ClientSize.Width;
        if (h <= 0 || w <= 0) return;

        bool tall = h > fh * 2;
        tb.AcceptsReturn = tall;
        var r = new RECT
        {
            Left = 7,
            Top = tall ? 4 : CenterTop(h, tb.Font),
            Right = Math.Max(8, w - 7),
            Bottom = h,
        };
        SendMessageRect(tb.Handle, 0x00B3 /*EM_SETRECT*/, 0, ref r);
        tb.Invalidate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern int SendMessageRect(IntPtr hWnd, int msg, int wParam, ref RECT lParam);

    private static readonly Dictionary<TextBox, string> Hints = new();

    /// <summary>
    /// Hint text for an empty box. WinForms' own PlaceholderText paints at the client origin and
    /// ignores our text rect, so we draw it ourselves at the same inset as real text.
    /// </summary>
    public static void Placeholder(TextBox tb, string hint)
    {
        tb.PlaceholderText = "";
        Hints[tb] = hint;
        tb.TextChanged += (_, _) => tb.Invalidate();
        tb.GotFocus += (_, _) => tb.Invalidate();
        tb.LostFocus += (_, _) => tb.Invalidate();
    }

    /// <summary>
    /// Rides on the edit control itself: re-asserts our formatting rect and paints the hint.
    /// The rect has to be re-asserted from here because the EDIT recomputes it on its own —
    /// on resize, on a font change, and on WM_THEMECHANGED (which SetWindowTheme triggers).
    /// Re-applying from the managed SizeChanged/LocationChanged events alone lost the race.
    /// </summary>
    private sealed class EditSkin : NativeWindow
    {
        private readonly TextBox _tb;
        private bool _busy;

        public EditSkin(TextBox tb)
        {
            _tb = tb;
            if (tb.IsHandleCreated) AssignHandle(tb.Handle);
            tb.HandleCreated += (_, _) => AssignHandle(tb.Handle);
            tb.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            switch (m.Msg)
            {
                case 0x0005: // WM_SIZE
                case 0x0030: // WM_SETFONT
                case 0x007D: // WM_STYLECHANGED
                case 0x031A: // WM_THEMECHANGED
                    if (!_busy) { _busy = true; try { ApplyTextRect(_tb); } finally { _busy = false; } }
                    return;
            }

            if (m.Msg != 0x000F /*WM_PAINT*/) return;
            if (!Hints.TryGetValue(_tb, out var hint) || hint.Length == 0) return;
            if (_tb.Text.Length > 0 || _tb.Focused) return;
            try
            {
                using var g = Graphics.FromHwnd(_tb.Handle);
                // Same origin the edit uses for real text, so the hint doesn't jump when typing starts.
                var at = new Rectangle(7, IsTall(_tb) ? 4 : CenterTop(_tb.ClientSize.Height, _tb.Font),
                                       _tb.ClientSize.Width - 14, LineHeight(_tb));
                TextRenderer.DrawText(g, hint, _tb.Font, at, Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
            catch { }
        }
    }

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    /// <summary>Give a TextBox internal left/right padding so text isn't flush against the border.</summary>
    public static void InnerPad(TextBox tb, int px)
    {
        void Apply() { if (tb.IsHandleCreated) SendMessage(tb.Handle, 0x00D3 /*EM_SETMARGINS*/, 3 /*L|R*/, px | (px << 16)); }
        tb.HandleCreated += (_, _) => Apply();
        Apply();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void UseDarkTitleBar(IntPtr handle)
    {
        int on = 1;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    /// <summary>Force dark mode app-wide (dark scrollbars, context menus). Call once at startup.</summary>
    public static void InitDarkMode()
    {
        try { SetPreferredAppMode(2); } catch { } // 2 = ForceDark
    }

    /// <summary>Give a control the dark scrollbar theme.</summary>
    public static void DarkScroll(Control c)
    {
        try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
    }

    /// <summary>Rounded-rectangle path helper (shared by custom-drawn controls).</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int rad)
    {
        int d = Math.Max(1, rad) * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        if (d >= r.Width || d >= r.Height) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
