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
    public static readonly Color Select   = Color.FromArgb(62, 58, 120);  // indigo selection
    public static readonly Color Border    = Color.FromArgb(58, 58, 70);
    public static readonly Color Text      = Color.FromArgb(227, 227, 236);
    public static readonly Color Muted     = Color.FromArgb(152, 152, 168);
    public static readonly Color Accent    = Color.FromArgb(110, 123, 255); // periwinkle blue
    public static readonly Color Brand     = Color.FromArgb(124, 111, 240); // Burp-AI purple
    public static readonly Color BrandDim  = Color.FromArgb(91, 79, 196);
    public static readonly Color Danger    = Color.FromArgb(230, 96, 90);
    public static readonly Color Warn      = Color.FromArgb(224, 168, 66);
    public static readonly Color Ok        = Color.FromArgb(96, 196, 110);

    public static readonly Font UI      = new("Segoe UI", 9f);
    public static readonly Font UISemi  = new("Segoe UI Semibold", 9f);
    public static readonly Font Section = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Brandy  = new("Consolas", 13f, FontStyle.Bold);

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
        c.BackColor = Color.FromArgb(28, 28, 34);
        c.ForeColor = Text;
        c.Font = UI;
        if (c is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
        if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
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
}
