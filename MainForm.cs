using System.Text.Json.Nodes;

namespace BurpManager;

public sealed class MainForm : Form
{
    private MasterDocument _doc = new();
    private string? _masterPath;
    private bool _dirty;
    private ProgramGroup? _current;
    private bool _suppressSync;

    private readonly Dictionary<string, Panel> _modules = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private string _selectedModule = "";

    // rail collapse
    private Panel _rail = null!;
    private FlowLayoutPanel _navFlow = null!;
    private Label _railCaption = null!;
    private Button _railToggle = null!;
    private bool _railCollapsed;

    // chromeless title row (brand + master + window buttons)
    private Panel _titleBar = null!;
    private AppLogo _brandLogo = null!;
    private Label _brandName = null!, _brandVer = null!;
    private CapButton? _maxBtn;
    private readonly List<CapButton> _capBtns = new();
    private readonly Panel _content = new();
    private readonly Label _masterLabel = new();

    // Programs module widgets
    private readonly ListBox _entries = new();
    private readonly TextBox _search = new() { Name = "search" };
    private readonly Label _counter = new();
    private readonly ThemeCombo _masterCombo = new();
    private readonly Label _saveLabel = new();
    private DataGridView _incGrid = null!;
    private DataGridView _excGrid = null!;
    private SplitContainer _progSplit = null!;
    private readonly TextBox _nameBox = new() { Name = "name" };
    private readonly TextBox _rateBox = new() { Name = "rate" };
    private readonly TextBox _roeBox = new() { Name = "roe" };
    private readonly DarkCheckBox _advChk = new();
    private readonly Label _modeBadge = new();

    // Dashboard + validate widgets
    private readonly FlowLayoutPanel _tiles = new();
    private FlowLayoutPanel _healthBody = null!;
    private DashChart _chart = null!;
    private ListBox _activeList = null!;
    private Label _activeHead = null!;
    private Label _masterInfo = null!;
    private DataGridView _lintGrid = null!;
    private readonly Label _statusLabel = new();

    // Proxy module widgets
    private DataGridView _lstGrid = null!, _reqGrid = null!, _respGrid = null!, _mrGrid = null!;
    private readonly DarkCheckBox _reqIntercept = new();
    private readonly DarkCheckBox _respIntercept = new();
    private readonly DarkCheckBox _proxyInExport = new();
    private readonly DarkCheckBox _mrScopeOnly = new();

    private string MasterDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private string ProxySettingsPath => Path.Combine(MasterDir, "proxy.settings.json");

    /// <summary>Overlay the persisted app-level proxy settings onto the current document.</summary>
    private void ApplyPersistedProxy() => ScopeIO.LoadProxySettings(_doc.Proxy, ProxySettingsPath);

    /// <summary>Save the current proxy config as the app-level "last used" settings.</summary>
    private void PersistProxy() { try { ScopeIO.SaveProxySettings(_doc.Proxy, ProxySettingsPath); } catch { } }

    public MainForm(string? initialMaster = null)
    {
        Text = "Burp Manager";
        Width = 1240; Height = 780;
        MinimumSize = new Size(1040, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.UI;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.None;   // chromeless: our own title row
        BackColor = Theme.Border;                 // shows through Padding as a 1px frame
        Padding = new Padding(1);

        BuildRail();
        BuildContent();
        BuildStatus();
        BuildTitleBar();                          // added last → spans the full width on top
        WireDragDrop();

        var iconPath = Path.Combine(MasterDir, "app.ico");
        if (File.Exists(iconPath)) { try { Icon = new Icon(iconPath); } catch { } }
        BuildTray();

        SwitchModule("PROGRAMS");
        RefreshMasterCombo();
        ApplyPersistedProxy();   // start with the last-used common proxy settings

        if (initialMaster != null) TryLoadMaster(initialMaster);
        else SetStatus("새 마스터. JSON을 창에 드래그&드롭하거나 [IMPORT] 로 불러오세요.");

        var mod = Environment.GetEnvironmentVariable("BM_MODULE");
        if (!string.IsNullOrEmpty(mod) && _modules.ContainsKey(mod)) SwitchModule(mod);
    }

    // ============================================================= tray

    private NotifyIcon? _tray;
    private bool _reallyExit;

    /// <summary>
    /// Live in the notification area: [✕] hides the window instead of ending the process, and a
    /// double-click on the tray icon brings it back. Quit is on the tray menu.
    /// </summary>
    private void BuildTray()
    {
        var menu = new ContextMenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.UI, ShowImageMargin = false };
        menu.Renderer = new ToolStripProfessionalRenderer(new TrayColors());
        var open = new ToolStripMenuItem("창 열기", null, (_, _) => RestoreFromTray());
        var quit = new ToolStripMenuItem("종료", null, (_, _) => { _reallyExit = true; Close(); });
        menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), quit });

        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "BURPSUITE MANAGER",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Anything short of a real shutdown just hides. CloseReason is unreliable here: our own
        // ✕ calls Close() and never goes through WM_SYSCOMMAND/SC_CLOSE, so it reports None
        // rather than UserClosing — keying off UserClosing meant the app simply exited.
        bool shutdown = e.CloseReason is CloseReason.WindowsShutDown
                                      or CloseReason.TaskManagerClosing
                                      or CloseReason.ApplicationExitCall;

        // Hiding to the tray is not losing work, so the unsaved-changes prompt must not fire here.
        if (!_reallyExit && !shutdown && _tray != null)
        {
            e.Cancel = true;
            Hide();   // never touch ShowInTaskbar: it recreates the handle, and destroying the
            return;   // main form's handle ends Application.Run — i.e. the process exits
        }
        if (!ConfirmDiscard()) { e.Cancel = true; _reallyExit = false; base.OnFormClosing(e); return; }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        base.OnFormClosing(e);
    }

    /// <summary>Dark palette for the tray context menu (a native ToolStrip, not our own paint).</summary>
    private sealed class TrayColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Select;
        public override Color MenuItemSelectedGradientBegin => Theme.Select;
        public override Color MenuItemSelectedGradientEnd => Theme.Select;
        public override Color MenuItemBorder => Theme.Brand;
        public override Color MenuBorder => Theme.Border;
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Set splitter once the container has its final width.
        try { _progSplit.SplitterDistance = 330; } catch { }
        // Dark scrollbars.
        Theme.DarkScroll(_entries);
        Theme.DarkScroll(_incGrid);
        Theme.DarkScroll(_excGrid);
        Theme.DarkScroll(_lintGrid);
        Theme.DarkScroll(_roeBox);
        Theme.DarkScroll(_activeList);
        if (_modules.TryGetValue(_selectedModule, out var mp)) Theme.DarkScroll(mp);
        UpdateCounts();

        if (Environment.GetEnvironmentVariable("BM_RAILCOLLAPSE") == "1") ToggleRail();

        // Fills the meta fields with a ruler string so text alignment can be measured, not eyeballed.
        if (Environment.GetEnvironmentVariable("BM_SEEDMETA") == "1")
        {
            _nameBox.Text = "HHxx";
            _rateBox.Text = "HHxx";
            _roeBox.Text = "HHxx";
            _search.Text = "HHxx";
        }

        if (Environment.GetEnvironmentVariable("BM_TESTDIALOG") == "1")
            BeginInvoke(() => Dlg.Show(this,
                "저장하지 않은 변경사항이 있습니다. 저장할까요?", "확인",
                MessageBoxButtons.YesNoCancel, Dlg.Kind.Question));

    }

    // ============================================================= title bar

    private const int TitleH = 48;
    private const int GripPx = 6;      // invisible resize border on a chromeless window

    /// <summary>Chromeless window: our own title row (brand · master · window buttons).</summary>
    private void BuildTitleBar()
    {
        _titleBar = new Panel { Dock = DockStyle.Top, Height = TitleH, BackColor = Theme.Rail };

        _brandLogo = new AppLogo { Location = new Point(14, 9), Size = new Size(30, 30) };
        _brandName = new Label
        {
            Text = "BURPSUITE", Font = Theme.Code(12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(236, 234, 252), AutoSize = true, Location = new Point(52, 7),
        };
        _brandVer = new Label
        {
            Text = "MANAGER ▌ v1.0", Font = Theme.Code(7f, FontStyle.Bold),
            ForeColor = Theme.Brand, AutoSize = true, Location = new Point(53, 28),
        };

        // Master indicator, centred like CCM's path readout.
        _masterLabel.Text = "● (새 마스터)";
        _masterLabel.AutoSize = true;
        _masterLabel.ForeColor = Theme.Muted;
        _masterLabel.Font = Theme.Code(8.5f);

        var min = WinBtn("─", () => WindowState = FormWindowState.Minimized);
        var max = WinBtn("□", ToggleMaximize);
        var cls = WinBtn("✕", Close, danger: true);
        _maxBtn = max;
        _capBtns.AddRange(new[] { min, max, cls });

        void LayoutBar()
        {
            int w = _titleBar.Width;
            cls.Location = new Point(w - 46, 0);
            max.Location = new Point(w - 92, 0);
            min.Location = new Point(w - 138, 0);
            _masterLabel.Location = new Point(Math.Max(240, (w - _masterLabel.Width) / 2), (TitleH - _masterLabel.Height) / 2);
        }
        _titleBar.Resize += (_, _) => LayoutBar();
        _masterLabel.TextChanged += (_, _) => LayoutBar();

        _titleBar.Controls.AddRange(new Control[] { _brandLogo, _brandName, _brandVer, _masterLabel, min, max, cls });

        // Drag to move / double-click to maximize — on the bar and its passive children.
        foreach (var c in new Control[] { _titleBar, _brandLogo, _brandName, _brandVer, _masterLabel })
            c.MouseDown += OnCaptionMouseDown;

        Controls.Add(_titleBar);
        LayoutBar();
    }

    private CapButton WinBtn(string glyph, Action onClick, bool danger = false) =>
        new(glyph, new Size(46, TitleH), danger, onClick);

    /// <summary>
    /// FormBorderStyle.None strips WS_THICKFRAME / WS_MAXIMIZEBOX, and the shell needs those to
    /// offer Snap — without them Win+Arrow is silently ignored. WM_NCCALCSIZE below cancels the
    /// non-client frame they would otherwise draw.
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

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        // Keep a Snap/Win+Up maximize inside the work area of whichever screen we are on.
        if (WindowState == FormWindowState.Normal && IsHandleCreated)
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
    }

    /// <summary>Snap and Win+Arrow resize without touching our buttons — re-derive their state.</summary>
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
    private long _lastCapClickMs;
    private Point _lastCapClickPos;

    private void OnCaptionMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        long now = Environment.TickCount64;
        var pos = Cursor.Position;
        bool isDouble =
            now - _lastCapClickMs <= SystemInformation.DoubleClickTime &&
            Math.Abs(pos.X - _lastCapClickPos.X) <= SystemInformation.DoubleClickSize.Width &&
            Math.Abs(pos.Y - _lastCapClickPos.Y) <= SystemInformation.DoubleClickSize.Height;

        _lastCapClickMs = now;
        _lastCapClickPos = pos;

        if (isDouble) { _lastCapClickMs = 0; ToggleMaximize(); return; }
        DragWindow();
    }

    private void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;   // don't cover the taskbar
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        if (_maxBtn != null) _maxBtn.SetGlyph(WindowState == FormWindowState.Maximized ? "❐" : "□");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    /// <summary>Hand the drag to Windows so snap/aero gestures still work.</summary>
    private void DragWindow()
    {
        ReleaseCapture();
        SendMessage(Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
    }

    /// <summary>Give the chromeless window native resize edges.</summary>
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

    // ================================================================= rail

    private void BuildRail()
    {
        var rail = new Panel { Dock = DockStyle.Left, Width = 160, BackColor = Theme.Rail };

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Rail,
            Padding = new Padding(0, 4, 0, 0),
        };

        // Explicit top-to-bottom order.
        AddNav(nav, "DASHBOARD", "▦");
        AddNav(nav, "PROGRAMS", "▤");
        AddNav(nav, "PROXY", "⇄");
        AddNav(nav, "TOOLS", "⚒");
        AddNav(nav, "WEB TOOLS", "◫");
        AddNav(nav, "VALIDATE", "✓");
        AddNav(nav, "IMPORT / EXPORT", "⇅");
        AddNav(nav, "SETTINGS", "⚙");

        var caption = new Label
        {
            Text = "MODULES",
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Theme.Muted,
            Font = Theme.Sans(7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(16, 0, 0, 6),
        };

        // collapse toggle strip at the very top of the rail
        var toggleWrap = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.Rail };
        _railToggle = new NavButton
        {
            Text = "‹", Dock = DockStyle.Right, Width = 34,
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Rail, ForeColor = Theme.Muted,
            Font = Theme.Sans(12f, FontStyle.Bold), Cursor = Cursors.Hand,
        };
        _railToggle.FlatAppearance.BorderSize = 0;
        _railToggle.FlatAppearance.MouseOverBackColor = Theme.Hover;
        _railToggle.Click += (_, _) => ToggleRail();
        toggleWrap.Controls.Add(_railToggle);

        _rail = rail;
        _navFlow = nav;
        _railCaption = caption;

        rail.Controls.Add(nav);
        rail.Controls.Add(caption);
        rail.Controls.Add(toggleWrap);
        Controls.Add(rail);
        Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Border });
    }

    private void ToggleRail()
    {
        _railCollapsed = !_railCollapsed;
        _rail.SuspendLayout();
        _rail.Width = _railCollapsed ? 54 : 160;
        _railCaption.Visible = !_railCollapsed;
        _railToggle.Text = _railCollapsed ? "›" : "‹";
        foreach (var b in _navButtons.Values) { b.Width = _railCollapsed ? 48 : 156; b.Invalidate(); }
        _rail.ResumeLayout();
    }

    private void AddNav(FlowLayoutPanel nav, string name, string glyph)
    {
        var accent = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Theme.Rail };
        var b = new NavButton
        {
            Text = "",
            Width = 156, Height = 40, Margin = new Padding(0),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Rail,
            Cursor = Cursors.Hand, Tag = accent,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.Paint += (_, e) => PaintNav(e.Graphics, b, name);
        b.Click += (_, _) => SwitchModule(name);
        b.MouseEnter += (_, _) => { if (_selectedModule != name) accent.BackColor = Theme.BrandDim; };
        b.MouseLeave += (_, _) => { if (_selectedModule != name) accent.BackColor = Theme.Rail; };
        b.Controls.Add(accent);
        _navButtons[name] = b;
        nav.Controls.Add(b);
    }

    private void PaintNav(Graphics g, Button b, string name)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        bool sel = _selectedModule == name;
        var fg = sel ? Color.White : Theme.Muted;
        const int isz = 16;
        if (_railCollapsed)
            DrawNavIcon(g, name, new Rectangle((b.Width - isz) / 2 + 1, (b.Height - isz) / 2, isz, isz), fg);
        else
        {
            DrawNavIcon(g, name, new Rectangle(16, (b.Height - isz) / 2, isz, isz), fg);
            TextRenderer.DrawText(g, name, Theme.Sans(8.5f, FontStyle.Bold),
                new Rectangle(42, 0, b.Width - 46, b.Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// <summary>Shared line-icon set: used by the nav rail and by module titles.</summary>
    internal static void DrawNavIcon(Graphics g, string name, Rectangle r, Color c)
    {
        using var pen = new Pen(c, 1.5f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        using var br = new SolidBrush(c);
        int x = r.X, y = r.Y, w = r.Width, h = r.Height, cx = x + w / 2, cy = y + h / 2;
        switch (name)
        {
            case "DASHBOARD":   // 2×2 grid
                int cw = (w - 3) / 2, ch = (h - 3) / 2;
                g.DrawRectangle(pen, x, y, cw, ch);
                g.DrawRectangle(pen, x + cw + 3, y, cw, ch);
                g.DrawRectangle(pen, x, y + ch + 3, cw, ch);
                g.DrawRectangle(pen, x + cw + 3, y + ch + 3, cw, ch);
                break;
            case "PROGRAMS":    // list rows with bullets
                foreach (var ly in new[] { y + 2, cy, y + h - 2 })
                { g.FillEllipse(br, x, ly - 1, 3, 3); g.DrawLine(pen, x + 6, ly, x + w, ly); }
                break;
            case "PROXY":       // opposing arrows
                int a1 = y + 4, a2 = y + h - 4;
                g.DrawLine(pen, x + 1, a1, x + w, a1);
                g.DrawLines(pen, new[] { new Point(x + w - 4, a1 - 3), new Point(x + w, a1), new Point(x + w - 4, a1 + 3) });
                g.DrawLine(pen, x, a2, x + w - 1, a2);
                g.DrawLines(pen, new[] { new Point(x + 4, a2 - 3), new Point(x, a2), new Point(x + 4, a2 + 3) });
                break;
            case "TOOLS":       // sliders
                int s1 = y + 4, s2 = y + h - 4;
                g.DrawLine(pen, x, s1, x + w, s1); using (var kb = new SolidBrush(c)) g.FillEllipse(kb, x + w - 9, s1 - 3, 6, 6);
                g.DrawLine(pen, x, s2, x + w, s2); using (var kb = new SolidBrush(c)) g.FillEllipse(kb, x + 3, s2 - 3, 6, 6);
                break;
            case "WEB TOOLS":   // globe
                g.DrawEllipse(pen, x, y, w - 1, h - 1);
                g.DrawEllipse(pen, x + w / 3, y, w - 1 - 2 * w / 3, h - 1);
                g.DrawLine(pen, x, cy, x + w, cy);
                break;
            case "VALIDATE":    // check in circle
                g.DrawEllipse(pen, x, y, w - 1, h - 1);
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.28f, cy), new PointF(x + w * 0.44f, cy + h * 0.17f), new PointF(x + w * 0.72f, y + h * 0.30f),
                });
                break;
            case "IMPORT / EXPORT":   // up + down arrows
                int mx1 = x + w / 3, mx2 = x + 2 * w / 3;
                g.DrawLine(pen, mx1, y + 1, mx1, y + h);
                g.DrawLines(pen, new[] { new Point(mx1 - 3, y + 5), new Point(mx1, y + 1), new Point(mx1 + 3, y + 5) });
                g.DrawLine(pen, mx2, y, mx2, y + h - 1);
                g.DrawLines(pen, new[] { new Point(mx2 - 3, y + h - 5), new Point(mx2, y + h - 1), new Point(mx2 + 3, y + h - 5) });
                break;
            case "SETTINGS":    // gear
                float inner = w * 0.30f;
                for (int i = 0; i < 8; i++)
                {
                    double an = i * Math.PI / 4;
                    float ox = (float)Math.Cos(an), oy = (float)Math.Sin(an);
                    g.DrawLine(pen, cx + ox * inner, cy + oy * inner, cx + ox * (w / 2f), cy + oy * (h / 2f));
                }
                g.DrawEllipse(pen, cx - inner, cy - inner, inner * 2, inner * 2);
                break;
            case "INTRUDER":    // crosshair
                g.DrawEllipse(pen, x + 2, y + 2, w - 5, h - 5);
                g.DrawLine(pen, cx, y, cx, y + 3);
                g.DrawLine(pen, cx, y + h - 3, cx, y + h);
                g.DrawLine(pen, x, cy, x + 3, cy);
                g.DrawLine(pen, x + w - 3, cy, x + w, cy);
                g.FillEllipse(br, cx - 1.5f, cy - 1.5f, 3, 3);
                break;
            case "ENCODE":      // angle brackets
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.38f, y + 1), new PointF(x + 1, cy), new PointF(x + w * 0.38f, y + h - 1),
                });
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.62f, y + 1), new PointF(x + w - 1, cy), new PointF(x + w * 0.62f, y + h - 1),
                });
                break;
            case "WEBSHELL":    // terminal prompt
                g.DrawRectangle(pen, x, y + 1, w - 1, h - 3);
                g.DrawLines(pen, new[]
                {
                    new PointF(x + 3.5f, cy - 2.5f), new PointF(x + 6.5f, cy), new PointF(x + 3.5f, cy + 2.5f),
                });
                g.DrawLine(pen, x + 8, cy + 2.5f, x + w - 4, cy + 2.5f);
                break;
            default:
                g.DrawRectangle(pen, x, y, w - 1, h - 1);
                break;
        }
    }

    private void SwitchModule(string name)
    {
        _selectedModule = name;
        foreach (var kv in _modules) kv.Value.Visible = kv.Key == name;
        foreach (var kv in _navButtons)
        {
            bool sel = kv.Key == name;
            kv.Value.BackColor = sel ? Theme.Header : Theme.Rail;
            if (kv.Value.Tag is Panel accent) accent.BackColor = sel ? Theme.Brand : Theme.Rail;
            kv.Value.Invalidate();
        }
        if (_modules.TryGetValue(name, out var panel)) Theme.DarkScroll(panel);  // dark AutoScroll bar
        if (name == "DASHBOARD") RefreshDashboard();
        if (name == "VALIDATE") RefreshLint();
        if (name == "PROXY") RefreshProxy();
        if (name == "WEB TOOLS" && panel is WebToolsPanel wt) wt.EnsureInit();
    }

    // ============================================================== content

    private void BuildContent()
    {
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;

        _modules["PROGRAMS"] = BuildProgramsModule();
        _modules["DASHBOARD"] = BuildDashboardModule();
        _modules["PROXY"] = BuildProxyModule();
        _modules["TOOLS"] = new ToolsPanel();
        _modules["WEB TOOLS"] = new WebToolsPanel();
        _modules["VALIDATE"] = BuildValidateModule();
        _modules["IMPORT / EXPORT"] = BuildIoModule();
        _modules["SETTINGS"] = BuildSettingsModule();

        foreach (var p in _modules.Values) { p.Dock = DockStyle.Fill; p.Visible = false; _content.Controls.Add(p); }
        Controls.Add(_content);
        _content.BringToFront();
    }

    // ------------------------------------------------------ PROGRAMS module

    private Panel BuildProgramsModule()
    {
        var root = new Panel { BackColor = Theme.Bg, Padding = new Padding(12) };

        // top strip: MASTER selector + reload + counter
        var strip = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var mlabel = new Label { Text = "MASTER", ForeColor = Theme.Muted, Font = Theme.Sans(8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(2, 12) };
        _masterCombo.Location = new Point(90, 7);
        _masterCombo.Width = 360;
        _masterCombo.SelectedIndexChanged += (_, _) => OnMasterComboPick();
        var reload = new Button { Text = "RELOAD", Location = new Point(460, 7), Size = new Size(80, 25) };
        Theme.FlatButton(reload);
        reload.Click += (_, _) => { if (_masterPath != null) TryLoadMaster(_masterPath); else RefreshMasterCombo(); };
        _counter.Text = "0 programs";
        _counter.ForeColor = Theme.Warn;
        _counter.Font = Theme.Code(8.5f);
        _counter.AutoSize = true;
        _counter.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        strip.Resize += (_, _) => _counter.Location = new Point(strip.Width - _counter.PreferredWidth - 6, 12);
        strip.Controls.AddRange(new Control[] { mlabel, _masterCombo, reload, _counter });

        var split = new DarkSplit
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Theme.Border,
            SplitterWidth = 2,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1MinSize = 200;
        split.Panel1.BackColor = Theme.Panel;
        split.Panel2.BackColor = Theme.Bg;
        _progSplit = split;

        // ----- left: entries -----
        var entHeader = SectionBar("ENTRIES");
        _search.Location = new Point(0, 0);
        _search.Dock = DockStyle.Top;
        Theme.StyleInput(_search);
        Theme.Placeholder(_search, "search…");
        _search.TextChanged += (_, _) => RefreshEntries();
        var searchWrap = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 4, 8, 4), BackColor = Theme.Panel };
        searchWrap.Controls.Add(_search);

        _entries.Dock = DockStyle.Fill;
        _entries.BorderStyle = BorderStyle.None;
        _entries.BackColor = Theme.Panel;
        _entries.ForeColor = Theme.Text;
        _entries.Font = Theme.UI;
        _entries.IntegralHeight = false;
        _entries.DrawMode = DrawMode.OwnerDrawFixed;
        _entries.ItemHeight = 26;
        _entries.DrawItem += DrawEntry;
        _entries.SelectedIndexChanged += (_, _) => OnEntrySelected();
        _entries.MouseDown += (_, me) =>
        {
            int idx = _entries.IndexFromPoint(me.Location);
            if (idx < 0 || me.X < 6 || me.X > 30) return;
            var g = (ProgramGroup)_entries.Items[idx];
            g.Enabled = !g.Enabled;
            MarkDirty();
            UpdateCounts();
            _entries.Invalidate(_entries.GetItemRectangle(idx));
        };
        SlimScroll.ForList(_entries);   // purple custom scrollbar

        var entHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(8, 0, 8, 8) };
        entHost.Controls.Add(_entries);
        split.Panel1.Controls.Add(entHost);
        split.Panel1.Controls.Add(searchWrap);
        split.Panel1.Controls.Add(entHeader);

        // ----- right: detail -----
        split.Panel2.Controls.Add(BuildDetail());

        root.Controls.Add(split);
        root.Controls.Add(strip);
        return root;
    }

    private Control BuildDetail()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 0, 12, 0) };

        // save bar
        var saveBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var save = new Button { Text = "SAVE MASTER", Location = new Point(0, 6), Size = new Size(130, 27) };
        Theme.AccentButton(save);
        save.Click += (_, _) => SaveMaster();
        var expBtn = new Button { Text = "EXPORT ▸ Burp", Location = new Point(138, 6), Size = new Size(120, 27) };
        Theme.FlatButton(expBtn);
        expBtn.Click += (_, _) => ExportCurrent();
        _saveLabel.Text = "";
        _saveLabel.ForeColor = Theme.Muted;
        _saveLabel.Font = Theme.Code(8f);
        _saveLabel.AutoSize = true;
        _saveLabel.Location = new Point(268, 13);
        _modeBadge.Dock = DockStyle.Right;
        _modeBadge.Width = 190;
        _modeBadge.TextAlign = ContentAlignment.MiddleRight;
        _modeBadge.Font = Theme.Code(8.5f, FontStyle.Bold);
        _modeBadge.ForeColor = Theme.Muted;
        saveBar.Controls.AddRange(new Control[] { save, expBtn, _saveLabel, _modeBadge });

        // meta card
        var meta = new Panel { Dock = DockStyle.Top, Height = 132, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 8) };
        var metaHead = new Label { Text = "FRONTMATTER", Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Sans(8.5f, FontStyle.Bold) };

        var mt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, BackColor = Theme.Panel };
        mt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));   // left label gutter (name/mode/ROE)
        mt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        mt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // inline label: hugs its text
        mt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        Theme.StyleInput(_nameBox); _nameBox.Dock = DockStyle.Fill;
        Theme.StyleInput(_rateBox); _rateBox.Dock = DockStyle.Fill;
        Theme.StyleInput(_roeBox);  _roeBox.Dock = DockStyle.Fill; _roeBox.Multiline = true;
        _advChk.Text = "advanced";
        _advChk.ForeColor = Theme.Text; _advChk.AutoSize = false; _advChk.Size = new Size(130, 24);
        _nameBox.Leave += (_, _) => ApplyMeta(true);
        _rateBox.Leave += (_, _) => ApplyMeta(false);
        _roeBox.Leave += (_, _) => ApplyMeta(false);
        _advChk.CheckedChanged += (_, _) => OnAdvancedToggled();

        mt.Controls.Add(MetaLabel("name"), 0, 0);
        mt.Controls.Add(_nameBox, 1, 0);
        mt.Controls.Add(MetaLabel("rate", inline: true), 2, 0);
        mt.Controls.Add(_rateBox, 3, 0);
        mt.Controls.Add(MetaLabel("mode"), 0, 1);
        mt.Controls.Add(_advChk, 1, 1);
        mt.Controls.Add(MetaLabel("ROE"), 0, 2);
        mt.Controls.Add(_roeBox, 1, 2);
        mt.SetColumnSpan(_roeBox, 3);
        mt.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        mt.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        mt.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        meta.Controls.Add(mt);
        meta.Controls.Add(metaHead);

        // include / exclude grids
        _incGrid = NewGrid();
        _excGrid = NewGrid();
        WireGrid(_incGrid, true);
        WireGrid(_excGrid, false);

        var grids = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Bg };
        grids.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grids.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grids.Controls.Add(GridSection("INCLUDE   ·   in-scope", Theme.Ok, _incGrid, true), 0, 0);
        grids.Controls.Add(GridSection("EXCLUDE   ·   out-of-scope", Theme.Warn, _excGrid, false), 0, 1);

        host.Controls.Add(grids);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        host.Controls.Add(meta);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        host.Controls.Add(saveBar);
        return host;
    }

    private Control GridSection(string title, Color accent, DataGridView grid, bool isInclude)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 8) };

        var head = Tk.Section(title);   // unified neutral header

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var btns = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 66, FlowDirection = FlowDirection.TopDown, BackColor = Theme.Bg, Padding = new Padding(0, 0, 6, 0) };
        btns.Controls.Add(MiniBtn("Add", () => AddRule(isInclude)));
        btns.Controls.Add(MiniBtn("Edit", () => EditSelectedRow(grid)));
        btns.Controls.Add(MiniBtn("Remove", () => RemoveRule(isInclude)));
        btns.Controls.Add(MiniBtn("Up", () => MoveRule(isInclude, -1)));
        btns.Controls.Add(MiniBtn("Down", () => MoveRule(isInclude, +1)));

        body.Controls.Add(Framed(grid));
        body.Controls.Add(btns);
        panel.Controls.Add(body);
        panel.Controls.Add(head);
        return panel;
    }

    /// <summary>
    /// Hairline frame around a grid. Drawn as a 1px padding show-through rather than
    /// BorderStyle.FixedSingle, which the system paints in its own colour.
    /// Uses Theme.Border, not BorderSoft: BorderSoft sits within 2 levels of the grid's own
    /// header strip and vanishes along the top edge.
    /// </summary>
    private static Control Framed(Control inner)
    {
        var f = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        inner.Dock = DockStyle.Fill;
        f.Controls.Add(inner);
        return f;
    }

    private Button MiniBtn(string text, Action onClick)
    {
        var b = new Button { Text = text, Width = 64, Height = 26, Margin = new Padding(0, 0, 0, 4) };
        Theme.FlatButton(b);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Field label. Gutter labels fill their fixed column; an <paramref name="inline"/> label sits
    /// between two fields, so it auto-sizes to its text instead of trailing dead space.
    /// Anchoring Left only keeps it vertically centred in the row.
    /// </summary>
    private static Label MetaLabel(string t, bool inline = false) => inline
        ? new()
        {
            Text = t, ForeColor = Theme.Muted, Font = Theme.UI,
            AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(18, 0, 10, 0),
        }
        : new()
        {
            Text = t, ForeColor = Theme.Muted, Font = Theme.UI,
            TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill,
        };

    // ------------------------------------------------------ grid plumbing

    private DataGridView NewGrid()
    {
        var g = StyleGrid(new DataGridView());
        AttachEmptyHint(g, "규칙 없음 — [Add] 로 추가하세요");

        var en = CheckCol("On", "en", 10);
        var proto = EnumCol("Protocol", "proto", 16, "any", "http", "https");
        var host = new DataGridViewTextBoxColumn { HeaderText = "Host", Name = "host", FillWeight = 42 };
        var port = new DataGridViewTextBoxColumn { HeaderText = "Port", Name = "port", FillWeight = 12 };
        var file = new DataGridViewTextBoxColumn { HeaderText = "File", Name = "file", FillWeight = 20 };
        g.Columns.AddRange(en, proto, host, port, file);
        AttachCheckboxPaint(g, "en");
        return g;
    }

    /// <summary>Apply the shared dark grid styling.</summary>
    private DataGridView StyleGrid(DataGridView g)
    {
        g.Dock = DockStyle.Fill;
        g.BackgroundColor = Theme.Panel;
        g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToResizeRows = false;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = true;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        g.Font = Theme.UI;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 28;
        g.GridColor = Theme.BorderSoft;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.RowTemplate.Height = 25;
        g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Header;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Header;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
        g.ColumnHeadersDefaultCellStyle.Font = Theme.UISemi;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        g.DefaultCellStyle.BackColor = Theme.Panel;
        g.DefaultCellStyle.ForeColor = Theme.Text;
        g.DefaultCellStyle.SelectionBackColor = Theme.Select;
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        g.RowsDefaultCellStyle.BackColor = Theme.Panel;
        g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(44, 44, 54);
        g.DataError += (_, e) => e.ThrowException = false;
        AttachEnumEditor(g);
        SlimScroll.ForGrid(g);   // purple custom scrollbar
        return g;
    }

    /// <summary>Checkbox column whose header is centred over the centred box.</summary>
    private static DataGridViewCheckBoxColumn CheckCol(string header, string name, int fw)
    {
        var c = new DataGridViewCheckBoxColumn { HeaderText = header, Name = name, FillWeight = fw };
        c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        c.HeaderCell.Style.Padding = new Padding(0);
        return c;
    }

    /// <summary>A read-only text column that opens a fully-themed dropdown (Tag = options).</summary>
    private static DataGridViewTextBoxColumn EnumCol(string header, string name, int fw, params string[] options)
        => new() { HeaderText = header, Name = name, FillWeight = fw, ReadOnly = true, Tag = options };

    /// <summary>Render enum cells with a chevron and open a custom dark dropdown on click.</summary>
    private void AttachEnumEditor(DataGridView g)
    {
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (g.Columns[e.ColumnIndex].Tag is not string[]) return;
            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            e.PaintBackground(e.CellBounds, sel);
            var txt = e.FormattedValue?.ToString() ?? "";
            var tr = new Rectangle(e.CellBounds.X + 5, e.CellBounds.Y, e.CellBounds.Width - 22, e.CellBounds.Height);
            TextRenderer.DrawText(e.Graphics!, txt, g.Font, tr, sel ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            int ax = e.CellBounds.Right - 13, ay = e.CellBounds.Y + e.CellBounds.Height / 2;
            e.Graphics!.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // Quiet chevron on idle rows, clear one on the selected row — keeps the grid calm.
            // Tied to the visible selection only: keying off CurrentCellAddress lit up a row
            // that carried no highlight, so the bright chevron looked arbitrary.
            bool active = sel;
            using (var pen = new Pen(active ? Theme.Muted : Theme.Scroll, 1.4f))
                e.Graphics.DrawLines(pen, new[] { new Point(ax - 4, ay - 2), new Point(ax, ay + 2), new Point(ax + 4, ay - 2) });
            e.Handled = true;
        };
        g.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (g.Columns[e.ColumnIndex].Tag is not string[] opts) return;
            var cell = g.Rows[e.RowIndex].Cells[e.ColumnIndex];
            var rect = g.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            var screen = g.PointToScreen(new Point(rect.Left, rect.Bottom));
            ShowEnumPopup(cell, opts, Math.Max(rect.Width, 140), screen);
        };
    }

    /// <summary>Non-modal themed dropdown: sets the cell value on pick, closes on click-away.</summary>
    private void ShowEnumPopup(DataGridViewCell cell, string[] options, int width, Point loc)
    {
        var field = Theme.Sunken;
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Theme.Brand,           // 1px accent frame via padding
            Padding = new Padding(1),
            Size = new Size(width, options.Length * 24 + 2),
            Location = loc,
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = field, ForeColor = Theme.Text,
            Font = Theme.UI, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24,
        };
        list.Items.AddRange(options);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Select : field)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.X += 7;
            TextRenderer.DrawText(e.Graphics, (string)list.Items[e.Index]!, list.Font, r,
                sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        f.Controls.Add(list);

        void Choose()
        {
            if (list.SelectedItem is string s && s != (cell.Value?.ToString() ?? ""))
                cell.Value = s;   // fires CellValueChanged → sync
            f.Close();
        }
        list.MouseClick += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Choose(); else if (e.KeyCode == Keys.Escape) f.Close(); };
        f.Deactivate += (_, _) => f.Close();
        f.FormClosed += (_, _) => f.Dispose();

        int idx = Array.IndexOf(options, cell.Value?.ToString() ?? "");
        if (idx >= 0) list.SelectedIndex = idx;

        f.Show(this);      // non-modal — the rest of the app stays interactive
        f.BringToFront();
        list.Focus();      // so clicking elsewhere triggers Deactivate → close
    }

    /// <summary>Owner-draw a checkbox column so the unchecked state matches the theme.</summary>
    private static void AttachCheckboxPaint(DataGridView g, string colName)
    {
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (g.Columns[e.ColumnIndex].Name != colName) return;

            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            e.PaintBackground(e.CellBounds, selected);

            bool chk = e.FormattedValue is bool b && b;
            int box = 15;
            var r = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - box) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - box) / 2, box, box);

            var gg = e.Graphics!;
            gg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(chk ? Theme.Brand : Theme.Sunken))
                gg.FillRectangle(bg, r);
            using (var pen = new Pen(chk ? Theme.Brand : Theme.Border, 1.2f))
                gg.DrawRectangle(pen, r);
            if (chk)
                using (var cp = new Pen(Color.White, 1.8f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                })
                    gg.DrawLines(cp, new[]
                    {
                        new Point(r.X + 3, r.Y + 8), new Point(r.X + 6, r.Y + 11), new Point(r.X + 12, r.Y + 4),
                    });
            e.Handled = true;
        };
    }

    private static void AttachEmptyHint(DataGridView g, string hint)
    {
        g.Paint += (_, e) =>
        {
            if (g.RowCount != 0) return;
            var area = new Rectangle(0, g.ColumnHeadersHeight, g.Width, g.Height - g.ColumnHeadersHeight);
            TextRenderer.DrawText(e.Graphics, hint, Theme.UI, area, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
    }

    private void WireGrid(DataGridView g, bool isInclude)
    {
        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        g.CellValueChanged += (_, e) =>
        {
            if (_suppressSync || e.RowIndex < 0) return;
            RowToRule(g.Rows[e.RowIndex]);
            MarkDirty();
            UpdateCounts();
            RefreshEntries();
        };
    }

    private static void RowToRule(DataGridViewRow row)
    {
        if (row.Tag is not ScopeRule r) return;
        r.Enabled = row.Cells["en"].Value is bool b && b;
        r.Protocol = row.Cells["proto"].Value?.ToString() ?? "any";
        r.Host = row.Cells["host"].Value?.ToString() ?? "";
        r.Port = row.Cells["port"].Value?.ToString() ?? "";
        r.File = row.Cells["file"].Value?.ToString() ?? "";
    }

    private void LoadGrid(DataGridView g, List<ScopeRule> rules)
    {
        _suppressSync = true;
        g.Rows.Clear();
        foreach (var r in rules)
        {
            int i = g.Rows.Add();
            var row = g.Rows[i];
            row.Tag = r;
            row.Cells["en"].Value = r.Enabled;
            row.Cells["proto"].Value = string.IsNullOrEmpty(r.Protocol) ? "any" : r.Protocol;
            row.Cells["host"].Value = r.Host;
            row.Cells["port"].Value = r.Port;
            row.Cells["file"].Value = r.File;
        }
        g.ClearSelection();
        _suppressSync = false;
    }

    private void AddRule(bool isInclude)
    {
        if (_current is null) return;
        var rule = new ScopeRule(new JsonObject
        {
            ["enabled"] = true, ["protocol"] = "any", ["host"] = "", ["port"] = "", ["file"] = ""
        });
        (isInclude ? _current.Include : _current.Exclude).Add(rule);
        LoadGrid(isInclude ? _incGrid : _excGrid, isInclude ? _current.Include : _current.Exclude);
        MarkDirty(); UpdateCounts(); RefreshEntries();
    }

    private void RemoveRule(bool isInclude)
    {
        if (_current is null) return;
        var g = isInclude ? _incGrid : _excGrid;
        var list = isInclude ? _current.Include : _current.Exclude;
        var selRows = g.SelectedRows.Cast<DataGridViewRow>().ToList();
        var toRemove = selRows.Select(r => r.Tag as ScopeRule).Where(x => x != null).ToList();
        if (toRemove.Count == 0) { SetStatus("삭제할 행을 선택하세요."); return; }
        int anchor = selRows.Min(r => r.Index);
        int first = Math.Max(0, g.FirstDisplayedScrollingRowIndex);
        foreach (var r in toRemove) list.Remove(r!);
        LoadGrid(g, list);
        MarkDirty(); UpdateCounts(); RefreshEntries();
        RestoreView(g, first, anchor);   // keep the scroll position instead of jumping to top
    }

    /// <summary>Restore a grid's scroll offset and a sensible selection after a reload.</summary>
    private static void RestoreView(DataGridView g, int firstRow, int anchorRow)
    {
        if (g.RowCount == 0) return;
        int sel = Math.Max(0, Math.Min(anchorRow, g.RowCount - 1));
        g.ClearSelection();
        g.Rows[sel].Selected = true;
        int first = Math.Max(0, Math.Min(firstRow, g.RowCount - 1));
        try { g.FirstDisplayedScrollingRowIndex = first; } catch { }
    }

    private void MoveRule(bool isInclude, int dir)
    {
        if (_current is null) return;
        var g = isInclude ? _incGrid : _excGrid;
        var list = isInclude ? _current.Include : _current.Exclude;
        if (g.CurrentRow?.Tag is not ScopeRule r) return;
        int idx = list.IndexOf(r);
        int ni = idx + dir;
        if (ni < 0 || ni >= list.Count) return;
        (list[idx], list[ni]) = (list[ni], list[idx]);
        LoadGrid(g, list);
        g.ClearSelection();
        g.Rows[ni].Selected = true;
        g.CurrentCell = g.Rows[ni].Cells["host"];
        MarkDirty();
    }

    // ------------------------------------------------------ entries list

    private void RefreshEntries()
    {
        var filter = _search.Text.Trim();
        var prev = _current;
        _entries.BeginUpdate();
        _entries.Items.Clear();
        foreach (var g in _doc.Programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _entries.Items.Add(g);
        }
        _entries.EndUpdate();

        if (prev != null)
        {
            int i = _entries.Items.IndexOf(prev);
            if (i >= 0) _entries.SelectedIndex = i;
        }
        else if (_entries.Items.Count > 0) _entries.SelectedIndex = 0;
        SlimScroll.Refresh(_entries);
        UpdateCounts();
    }

    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = (ProgramGroup)_entries.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var gg = e.Graphics;
        gg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using (var b = new SolidBrush(sel ? Theme.Select : Theme.Panel)) gg.FillRectangle(b, e.Bounds);
        if (sel)
            using (var bar = new SolidBrush(Theme.Brand))
                gg.FillRectangle(bar, new Rectangle(e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height));

        // enable checkbox
        int box = 14;
        var r = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + (e.Bounds.Height - box) / 2, box, box);
        using (var cbg = new SolidBrush(g.Enabled ? Theme.Brand : Theme.Sunken))
            gg.FillRectangle(cbg, r);
        using (var pen = new Pen(g.Enabled ? Theme.Brand : Theme.Border, 1.1f))
            gg.DrawRectangle(pen, r);
        if (g.Enabled)
            using (var cp = new Pen(Color.White, 1.7f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            })
                gg.DrawLines(cp, new[]
                {
                    new Point(r.X + 3, r.Y + 7), new Point(r.X + 5, r.Y + 10), new Point(r.X + 11, r.Y + 3),
                });

        var nameColor = !g.Enabled ? Color.FromArgb(112, 112, 124)
                                   : sel ? Color.White : Theme.Text;
        var nameRect = new Rectangle(e.Bounds.X + 32, e.Bounds.Y, e.Bounds.Width - 32 - 100, e.Bounds.Height);
        TextRenderer.DrawText(gg, g.Name, Theme.UISemi, nameRect, nameColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // mode badge
        var badge = g.AdvancedMode ? "ADV" : "SMP";
        var bcol = !g.Enabled ? Color.FromArgb(52, 52, 62)
                              : g.AdvancedMode ? Theme.BrandDim : Color.FromArgb(66, 66, 80);
        var brect = new Rectangle(e.Bounds.Right - 92, e.Bounds.Y + (e.Bounds.Height - 15) / 2, 36, 15);
        using (var bp = RoundRect(brect, 3)) using (var bb = new SolidBrush(bcol)) gg.FillPath(bb, bp);
        TextRenderer.DrawText(gg, badge, Theme.Code(6.75f, FontStyle.Bold), brect,
            !g.Enabled ? Theme.Muted : g.AdvancedMode ? Color.White : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var count = $"{g.Include.Count}/{g.Exclude.Count}";
        var cntRect = new Rectangle(e.Bounds.Right - 48, e.Bounds.Y, 42, e.Bounds.Height);
        TextRenderer.DrawText(gg, count, Theme.Code(8f), cntRect,
            !g.Enabled ? Color.FromArgb(100, 100, 112) : sel ? Color.White : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int rad)
    {
        int d = rad * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void OnEntrySelected()
    {
        _current = _entries.SelectedItem as ProgramGroup;
        LoadDetail();
    }

    private void LoadDetail()
    {
        _suppressSync = true;
        if (_current is null)
        {
            _nameBox.Text = _rateBox.Text = _roeBox.Text = "";
            _advChk.Checked = false;
            _incGrid.Rows.Clear(); _excGrid.Rows.Clear();
            _saveLabel.Text = "";
            _modeBadge.Text = "";
            _suppressSync = false;
            return;
        }
        _nameBox.Text = _current.Name;
        _rateBox.Text = _current.Meta.RateLimit;
        _roeBox.Text = _current.Meta.Roe;
        _advChk.Checked = _current.AdvancedMode;
        _saveLabel.Text = _current.Meta.SourceFile.Length > 0 ? "src: " + _current.Meta.SourceFile : "";
        UpdateModeBadge();
        _suppressSync = false;
        LoadGrid(_incGrid, _current.Include);
        LoadGrid(_excGrid, _current.Exclude);
    }

    private void UpdateModeBadge()
    {
        bool adv = _current?.AdvancedMode ?? false;
        _modeBadge.Text = _current is null ? "" : adv ? "◤ ADVANCED · regex" : "◤ SIMPLE · prefix";
        _modeBadge.ForeColor = adv ? Theme.Brand : Theme.Muted;
    }

    private void OnAdvancedToggled()
    {
        if (_current is null || _suppressSync) return;
        bool toAdv = _advChk.Checked;
        if (_current.AdvancedMode == toAdv) return;

        int ruleCount = _current.Include.Count + _current.Exclude.Count;
        if (ruleCount > 0)
        {
            var r = Dlg.Show(this,
                $"scope 모드를 {(toAdv ? "ADVANCED (정규식)" : "SIMPLE (접두사)")}로 바꿉니다.\n" +
                $"기존 {ruleCount}개 규칙의 host/file 문법을 자동 변환할까요?\n" +
                "(best-effort 변환입니다 — 변환 후 규칙을 검토하세요.)",
                "모드 변경", MessageBoxButtons.YesNoCancel, Dlg.Kind.Question);
            if (r == DialogResult.Cancel)
            {
                _suppressSync = true; _advChk.Checked = _current.AdvancedMode; _suppressSync = false;
                return;
            }
            if (r == DialogResult.Yes) ConvertProgramMode(_current, toAdv);
        }

        _current.AdvancedMode = toAdv;
        MarkDirty();
        UpdateModeBadge();
        RefreshEntries();
        LoadGrid(_incGrid, _current.Include);
        LoadGrid(_excGrid, _current.Exclude);
        SetStatus(toAdv ? "ADVANCED(정규식) 모드로 전환했습니다." : "SIMPLE(접두사) 모드로 전환했습니다.");
    }

    /// <summary>Best-effort conversion of rule host/file syntax between regex and literal-prefix.</summary>
    private static void ConvertProgramMode(ProgramGroup g, bool toAdvanced)
    {
        foreach (var r in g.Include.Concat(g.Exclude))
        {
            r.Host = toAdvanced ? LiteralToRegex(r.Host, anchorEnd: true) : RegexToLiteral(r.Host);
            r.File = toAdvanced ? LiteralToRegex(r.File, anchorEnd: false) : RegexToLiteral(r.File);
        }
    }

    private static string LiteralToRegex(string s, bool anchorEnd)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return "^" + System.Text.RegularExpressions.Regex.Escape(s) + (anchorEnd ? "$" : "");
    }

    private static string RegexToLiteral(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        if (s.StartsWith("^")) s = s[1..];
        if (s.EndsWith("$")) s = s[..^1];
        // drop common "any subdomain" and wildcard fragments
        s = s.Replace("(.*\\.)?", "").Replace("(.*.)?", "").Replace(".*", "");
        try { s = System.Text.RegularExpressions.Regex.Unescape(s); } catch { }
        return s;
    }

    private void ApplyMeta(bool nameMayChange)
    {
        if (_current is null || _suppressSync) return;
        _current.Meta.RateLimit = _rateBox.Text.Trim();
        _current.Meta.Roe = _roeBox.Text;
        if (nameMayChange)
        {
            var nn = _nameBox.Text.Trim();
            if (nn.Length > 0 && nn != _current.Name &&
                !_doc.Programs.Any(p => p != _current && p.Name.Equals(nn, StringComparison.OrdinalIgnoreCase)))
            {
                _current.Name = nn;
                RefreshEntries();
            }
        }
        MarkDirty();
    }

    private void UpdateCounts()
    {
        int inc = _doc.Programs.Sum(p => p.Include.Count);
        int exc = _doc.Programs.Sum(p => p.Exclude.Count);
        int on = _doc.Programs.Count(p => p.Enabled);
        _counter.Text = $"{_doc.Programs.Count} programs · {on} on · {inc} inc · {exc} exc";
        if (_counter.Parent is Control cp)
            _counter.Location = new Point(cp.Width - _counter.PreferredWidth - 6, 12);
    }

    // ----------------------------------------------------- master combo

    private void RefreshMasterCombo()
    {
        _suppressSync = true;
        _masterCombo.Clear();
        try
        {
            foreach (var f in Directory.EnumerateFiles(MasterDir, "*.json"))
                _masterCombo.AddItems(Path.GetFileName(f));
        }
        catch { }
        if (_masterPath != null)
        {
            var n = Path.GetFileName(_masterPath);
            if (!_masterCombo.Contains(n)) _masterCombo.AddItems(n);
            _masterCombo.SelectSilently(n);
        }
        _suppressSync = false;
    }

    private void OnMasterComboPick()
    {
        if (_suppressSync || _masterCombo.SelectedItem is not string name) return;
        var path = Path.Combine(MasterDir, name);
        if (File.Exists(path) && path != _masterPath) { if (ConfirmDiscard()) TryLoadMaster(path); }
    }

    // ------------------------------------------------------ DASHBOARD

    private Panel BuildDashboardModule()
    {
        var root = new Panel { BackColor = Theme.Bg, Padding = new Padding(20), AutoScroll = true };

        // Two-column card area (fills space below the tiles).
        var cols = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg,
        };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));

        // Left column: Health (top) + Distribution chart (fill).
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var healthCard = DashCard("SCOPE HEALTH  ·  클릭 → VALIDATE", out var healthBody);
        healthCard.Cursor = Cursors.Hand;
        _healthBody = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Panel };
        healthBody.Controls.Add(_healthBody);
        void goValidate(object? s, EventArgs e) => SwitchModule("VALIDATE");
        healthCard.Click += goValidate; healthBody.Click += goValidate; _healthBody.Click += goValidate;

        var chartCard = DashCard("DISTRIBUTION", out var chartBody);
        _chart = new DashChart { Dock = DockStyle.Fill };
        chartBody.Controls.Add(_chart);

        left.Controls.Add(healthCard, 0, 0);
        left.Controls.Add(chartCard, 0, 1);

        // Right column: Active programs (fill) + Master info (bottom).
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));

        var activeCard = DashCard("ACTIVE PROGRAMS", out var activeBody, out _activeHead);
        _activeList = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Panel,
            ForeColor = Theme.Text, Font = Theme.UI, IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24,
        };
        _activeList.DrawItem += DrawActiveItem;
        SlimScroll.ForList(_activeList);   // purple custom scrollbar
        _activeList.DoubleClick += (_, _) =>
        {
            if (_activeList.SelectedItem is ProgramGroup g) { _current = g; SwitchModule("PROGRAMS"); SelectEntry(g); }
        };
        var exportActive = new Button { Text = "EXPORT ACTIVE ▸ Burp", Dock = DockStyle.Bottom, Height = 32 };
        Theme.AccentButton(exportActive);
        exportActive.Click += (_, _) => ExportAll();
        activeBody.Controls.Add(_activeList);
        activeBody.Controls.Add(exportActive);

        var masterCard = DashCard("MASTER", out var masterBody);
        _masterInfo = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Code(8.5f), TextAlign = ContentAlignment.TopLeft };
        masterBody.Controls.Add(_masterInfo);

        right.Controls.Add(activeCard, 0, 0);
        right.Controls.Add(masterCard, 0, 1);

        cols.Controls.Add(left, 0, 0);
        cols.Controls.Add(right, 1, 0);

        // Tiles row (2 rows of 4, wrapping).
        _tiles.Dock = DockStyle.Top;
        _tiles.Height = 200;
        _tiles.BackColor = Theme.Bg;
        _tiles.FlowDirection = FlowDirection.LeftToRight;
        _tiles.Padding = new Padding(0, 0, 0, 8);

        var head = Tk.Head("DASHBOARD");

        root.Controls.Add(cols);
        root.Controls.Add(_tiles);
        root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        root.Controls.Add(head);
        return root;
    }

    private void RefreshDashboard()
    {
        var progs = _doc.Programs;
        int inc = progs.Sum(p => p.Include.Count);
        int exc = progs.Sum(p => p.Exclude.Count);
        int on = progs.Count(p => p.Enabled);
        int off = progs.Count - on;
        int uniqueHosts = progs.SelectMany(p => p.Include.Concat(p.Exclude))
            .Select(r => r.Host?.Trim() ?? "").Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        double avg = progs.Count > 0 ? (double)(inc + exc) / progs.Count : 0;

        var lint = ScopeLinter.Run(_doc);
        int high = lint.Count(f => f.Severity == "HIGH");
        int med = lint.Count(f => f.Severity == "MEDIUM");
        int info = lint.Count(f => f.Severity == "INFO");
        int emptyScope = progs.Count(p => p.Include.Count(r => r.Enabled) == 0);
        int noExclude = progs.Count(p => p.Exclude.Count == 0);

        // --- tiles (2 rows of 4) ---
        _tiles.Controls.Clear();
        _tiles.Controls.Add(Tile("PROGRAMS", progs.Count.ToString(), Theme.Accent));
        _tiles.Controls.Add(Tile("ACTIVE", on.ToString(), Theme.Brand));
        _tiles.Controls.Add(Tile("INCLUDE RULES", inc.ToString(), Theme.Ok));
        _tiles.Controls.Add(Tile("EXCLUDE RULES", exc.ToString(), Theme.Warn));
        _tiles.Controls.Add(Tile("DISABLED", off.ToString(), Theme.Muted));
        _tiles.Controls.Add(Tile("UNIQUE HOSTS", uniqueHosts.ToString(), Theme.Accent));
        _tiles.Controls.Add(Tile("AVG RULES", avg.ToString("0.0"), Theme.Ok));
        _tiles.Controls.Add(Tile("LINT · HIGH", high.ToString(), high > 0 ? Theme.Danger : Theme.Ok));

        // --- health rows ---
        _healthBody.Controls.Clear();
        _healthBody.Controls.Add(HealthRow("LINT · HIGH", high, Theme.Danger));
        _healthBody.Controls.Add(HealthRow("LINT · MEDIUM", med, Theme.Warn));
        _healthBody.Controls.Add(HealthRow("LINT · INFO", info, Theme.Muted));
        _healthBody.Controls.Add(HealthRow("빈 scope (활성 include 0)", emptyScope, emptyScope > 0 ? Theme.Danger : Theme.Ok));
        _healthBody.Controls.Add(HealthRow("exclude 미설정 프로그램", noExclude, noExclude > 0 ? Theme.Warn : Theme.Ok));

        // --- chart ---
        _chart.Active = on; _chart.Disabled = off;
        _chart.PAny = CountProto(progs, "any"); _chart.PHttp = CountProto(progs, "http"); _chart.PHttps = CountProto(progs, "https");
        _chart.Invalidate();

        // --- active list ---
        _activeHead.Text = $"ACTIVE PROGRAMS ({on})";
        _activeList.Items.Clear();
        foreach (var g in progs.Where(p => p.Enabled).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            _activeList.Items.Add(g);
        SlimScroll.Refresh(_activeList);

        // --- master info ---
        _masterInfo.Text =
            $"file    : {(_masterPath is null ? "(미저장 새 문서)" : Path.GetFileName(_masterPath))}\n" +
            $"state   : {(_dirty ? "● 미저장 변경 있음" : "저장됨")}\n" +
            $"schema  : v{_doc.SchemaVersion}\n" +
            $"hosts   : {uniqueHosts} unique\n" +
            $"rules   : {inc} inc / {exc} exc  (avg {avg:0.0})";
    }

    private static int CountProto(IEnumerable<ProgramGroup> progs, string proto)
    {
        return progs.SelectMany(p => p.Include).Count(r =>
        {
            var pr = string.IsNullOrEmpty(r.Protocol) ? "any" : r.Protocol;
            return pr.Equals(proto, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void SelectEntry(ProgramGroup g)
    {
        int i = _entries.Items.IndexOf(g);
        if (i >= 0) _entries.SelectedIndex = i;
    }

    private Control HealthRow(string label, int count, Color color)
    {
        var row = new Panel { Width = 340, Height = 26, BackColor = Theme.Panel, Margin = new Padding(0, 0, 0, 1), Cursor = Cursors.Hand };
        var dot = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = color };
        var lbl = new Label { Text = label, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.UI, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
        var val = new Label { Text = count.ToString(), Dock = DockStyle.Right, Width = 48, ForeColor = color, Font = Theme.Code(10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight };
        void go(object? s, EventArgs e) => SwitchModule("VALIDATE");
        row.Click += go; lbl.Click += go; val.Click += go;
        row.Controls.Add(lbl); row.Controls.Add(val); row.Controls.Add(dot);
        return row;
    }

    private void DrawActiveItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = (ProgramGroup)_activeList.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? Theme.Select : Theme.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
        using (var dot = new SolidBrush(Theme.Brand))
            e.Graphics.FillEllipse(dot, e.Bounds.X + 10, e.Bounds.Y + e.Bounds.Height / 2 - 3, 6, 6);
        TextRenderer.DrawText(e.Graphics, g.Name, Theme.UI,
            new Rectangle(e.Bounds.X + 24, e.Bounds.Y, e.Bounds.Width - 76, e.Bounds.Height),
            sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, $"{g.Include.Count}/{g.Exclude.Count}", Theme.Code(8f),
            new Rectangle(e.Bounds.Right - 50, e.Bounds.Y, 44, e.Bounds.Height),
            sel ? Color.White : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private static Panel Tile(string caption, string value, Color accent)
    {
        var p = new Panel { Width = 176, Height = 88, BackColor = Theme.Panel, Margin = new Padding(0, 0, 12, 12) };
        p.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accent });
        p.Controls.Add(new Label { Text = value, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Sans(25f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
        p.Controls.Add(new Label { Text = caption, Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Muted, Font = Theme.Sans(7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
        return p;
    }

    private Panel DashCard(string title, out Panel body) => DashCard(title, out body, out _);

    private Panel DashCard(string title, out Panel body, out Label header)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(14, 10, 14, 12) };
        header = new Label { Text = title, Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Muted, Font = Theme.Sans(8.5f, FontStyle.Bold) };
        body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        var hdr = header;
        card.Controls.Add(body);
        card.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Panel });
        card.Controls.Add(hdr);
        return card;
    }

    // ------------------------------------------------------ PROXY

    private Panel BuildProxyModule()
    {
        var root = new Panel { BackColor = Theme.Bg, Padding = new Padding(20, 12, 20, 12), AutoScroll = true };

        _lstGrid = NewListenerGrid();
        _reqGrid = NewInterceptGrid();
        _respGrid = NewInterceptGrid();
        _mrGrid = NewMrGrid();
        WireProxyGrid(_lstGrid, SyncListener);
        WireProxyGrid(_reqGrid, r => SyncIntercept(r));
        WireProxyGrid(_respGrid, r => SyncIntercept(r));
        WireProxyGrid(_mrGrid, SyncMatchReplace);

        _reqIntercept.Text = "가로채기 ON";  _reqIntercept.AutoSize = false; _reqIntercept.Size = new Size(120, 22);
        _respIntercept.Text = "가로채기 ON"; _respIntercept.AutoSize = false; _respIntercept.Size = new Size(120, 22);
        _reqIntercept.CheckedChanged += (_, _) => { if (!_suppressSync) { _doc.Proxy.InterceptRequests = _reqIntercept.Checked; MarkDirty(); PersistProxy(); } };
        _respIntercept.CheckedChanged += (_, _) => { if (!_suppressSync) { _doc.Proxy.InterceptResponses = _respIntercept.Checked; MarkDirty(); PersistProxy(); } };
        _mrScopeOnly.Text = "SCOPE ONLY"; _mrScopeOnly.AutoSize = false; _mrScopeOnly.Size = new Size(120, 22);
        _mrScopeOnly.CheckedChanged += (_, _) => { if (!_suppressSync) { _doc.Proxy.MatchReplaceScopeOnly = _mrScopeOnly.Checked; MarkDirty(); PersistProxy(); } };

        // Sections stacked top→bottom (add in reverse for Dock=Top).
        var secMr = ProxySection("MATCH / REPLACE", Theme.Accent, _mrGrid, AddMr,
            () => GridRemove(_mrGrid, _doc.Proxy.MatchReplace, LoadMr),
            () => GridMove(_mrGrid, _doc.Proxy.MatchReplace, -1, LoadMr),
            () => GridMove(_mrGrid, _doc.Proxy.MatchReplace, +1, LoadMr), _mrScopeOnly, 190);
        var secResp = ProxySection("RESPONSE INTERCEPT", Theme.Warn, _respGrid, AddResp,
            () => GridRemove(_respGrid, _doc.Proxy.ResponseRules, () => LoadIntercept(_respGrid, _doc.Proxy.ResponseRules)),
            () => GridMove(_respGrid, _doc.Proxy.ResponseRules, -1, () => LoadIntercept(_respGrid, _doc.Proxy.ResponseRules)),
            () => GridMove(_respGrid, _doc.Proxy.ResponseRules, +1, () => LoadIntercept(_respGrid, _doc.Proxy.ResponseRules)), _respIntercept, 190);
        var secReq = ProxySection("REQUEST INTERCEPT", Theme.Ok, _reqGrid, AddReq,
            () => GridRemove(_reqGrid, _doc.Proxy.RequestRules, () => LoadIntercept(_reqGrid, _doc.Proxy.RequestRules)),
            () => GridMove(_reqGrid, _doc.Proxy.RequestRules, -1, () => LoadIntercept(_reqGrid, _doc.Proxy.RequestRules)),
            () => GridMove(_reqGrid, _doc.Proxy.RequestRules, +1, () => LoadIntercept(_reqGrid, _doc.Proxy.RequestRules)), _reqIntercept, 190);
        var secLst = ProxySection("PROXY LISTENERS", Theme.Brand, _lstGrid, AddListener,
            () => GridRemove(_lstGrid, _doc.Proxy.Listeners, LoadListeners),
            () => GridMove(_lstGrid, _doc.Proxy.Listeners, -1, LoadListeners),
            () => GridMove(_lstGrid, _doc.Proxy.Listeners, +1, LoadListeners), null, 190);

        // toolbar
        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var imp = new Button { Text = "IMPORT PROXY", Location = new Point(0, 6), Size = new Size(112, 27) };
        Theme.FlatButton(imp); imp.Click += (_, _) => ImportProxyFromFile();
        var exp = new Button { Text = "EXPORT PROXY ▸ Burp", Location = new Point(120, 6), Size = new Size(158, 27) };
        Theme.AccentButton(exp); exp.Click += (_, _) => ExportProxyToFile();
        var ext = new Button { Text = "EXPORT EXT (.java)", Location = new Point(286, 6), Size = new Size(150, 27) };
        Theme.FlatButton(ext); ext.Click += (_, _) => ExportExtensionToFile();
        _proxyInExport.Text = "프로그램 Export에 공통 프록시 포함";
        _proxyInExport.AutoSize = false; _proxyInExport.Size = new Size(280, 24); _proxyInExport.Location = new Point(448, 8);
        _proxyInExport.CheckedChanged += (_, _) => { if (!_suppressSync) { _doc.Proxy.IncludeInExport = _proxyInExport.Checked; MarkDirty(); PersistProxy(); } };
        bar.Controls.AddRange(new Control[] { imp, exp, ext, _proxyInExport });

        var head = Tk.Head("PROXY  ·  공통 설정");

        root.Controls.Add(secMr);
        root.Controls.Add(secResp);
        root.Controls.Add(secReq);
        root.Controls.Add(secLst);
        root.Controls.Add(bar);
        root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
        root.Controls.Add(head);
        SlimScroll.WrapTop(root);   // purple custom scrollbar instead of native AutoScroll bar
        return root;
    }

    private Control ProxySection(string title, Color accent, DataGridView grid,
        Action add, Action remove, Action up, Action down, Control? headerExtra, int height)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 8) };
        var head = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 5) };
        // No rule under the title: the grid header strip already separates the section.
        head.Controls.Add(new Label { Text = title, Dock = DockStyle.Left, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Sans(8.5f, FontStyle.Bold) });
        if (headerExtra != null) { headerExtra.Dock = DockStyle.Right; head.Controls.Add(headerExtra); }

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var btns = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 66, FlowDirection = FlowDirection.TopDown, BackColor = Theme.Bg, Padding = new Padding(0, 0, 6, 0) };
        btns.Controls.Add(MiniBtn("Add", add));
        btns.Controls.Add(MiniBtn("Edit", () => EditSelectedRow(grid)));
        btns.Controls.Add(MiniBtn("Remove", remove));
        btns.Controls.Add(MiniBtn("Up", up));
        btns.Controls.Add(MiniBtn("Down", down));
        body.Controls.Add(Framed(grid));
        body.Controls.Add(btns);
        panel.Controls.Add(body);
        panel.Controls.Add(head);
        return panel;
    }

    private DataGridView NewListenerGrid()
    {
        var g = StyleGrid(new DataGridView());
        AttachEmptyHint(g, "리스너 없음 — [Add] 로 추가하세요");
        g.Columns.Add(CheckCol("On", "run", 12));
        g.Columns.Add(EnumCol("Bind", "bind", 52, "loopback_only", "all_interfaces", "specific_address"));
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Port", Name = "port", FillWeight = 30 });
        AttachCheckboxPaint(g, "run");
        return g;
    }

    private DataGridView NewInterceptGrid()
    {
        var g = StyleGrid(new DataGridView());
        AttachEmptyHint(g, "규칙 없음 — [Add] 로 추가하세요");
        g.Columns.Add(CheckCol("On", "en", 8));
        g.Columns.Add(EnumCol("Operator", "op", 12, "and", "or"));
        g.Columns.Add(EnumCol("Match type", "mt", 20, "file_extension", "url", "http_method", "request", "content_type_header", "status_code", "header", "body", "cookie", "host", "listener_port"));
        g.Columns.Add(EnumCol("Relationship", "rel", 22, "matches", "does_not_match", "contains_parameters", "does_not_contain_parameters", "is_in_target_scope", "is_not_in_target_scope", "was_modified", "was_intercepted"));
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Condition", Name = "cond", FillWeight = 38 });
        AttachCheckboxPaint(g, "en");
        return g;
    }

    private DataGridView NewMrGrid()
    {
        var g = StyleGrid(new DataGridView());
        AttachEmptyHint(g, "규칙 없음 — [Add] 로 추가하세요");
        g.Columns.Add(CheckCol("On", "en", 6));
        g.Columns.Add(CheckCol("Regex", "rx", 9));
        g.Columns.Add(EnumCol("Type", "type", 20, "request_header", "request_body", "request_first_line", "request_param_name", "request_param_value", "response_header", "response_body", "response_first_line"));
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Match", Name = "match", FillWeight = 23 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Replace", Name = "repl", FillWeight = 23 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Comment", Name = "cmt", FillWeight = 19 });
        AttachCheckboxPaint(g, "en");
        AttachCheckboxPaint(g, "rx");
        return g;
    }

    private void WireProxyGrid(DataGridView g, Action<DataGridViewRow> sync)
    {
        g.CurrentCellDirtyStateChanged += (_, _) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.CellValueChanged += (_, e) => { if (!_suppressSync && e.RowIndex >= 0) { sync(g.Rows[e.RowIndex]); MarkDirty(); PersistProxy(); } };
    }

    private void RefreshProxy()
    {
        LoadListeners();
        LoadIntercept(_reqGrid, _doc.Proxy.RequestRules);
        LoadIntercept(_respGrid, _doc.Proxy.ResponseRules);
        LoadMr();
        _suppressSync = true;
        _reqIntercept.Checked = _doc.Proxy.InterceptRequests;
        _respIntercept.Checked = _doc.Proxy.InterceptResponses;
        _proxyInExport.Checked = _doc.Proxy.IncludeInExport;
        _mrScopeOnly.Checked = _doc.Proxy.MatchReplaceScopeOnly;
        _suppressSync = false;
        Theme.DarkScroll(_lstGrid); Theme.DarkScroll(_reqGrid); Theme.DarkScroll(_respGrid); Theme.DarkScroll(_mrGrid);
    }

    private void LoadListeners()
    {
        _suppressSync = true;
        _lstGrid.Rows.Clear();
        foreach (var l in _doc.Proxy.Listeners)
        {
            var row = _lstGrid.Rows[_lstGrid.Rows.Add()];
            row.Tag = l;
            row.Cells["run"].Value = l.Running;
            row.Cells["bind"].Value = string.IsNullOrEmpty(l.Bind) ? "loopback_only" : l.Bind;
            row.Cells["port"].Value = l.Port;
        }
        _lstGrid.ClearSelection();
        _suppressSync = false;
    }

    private void LoadIntercept(DataGridView g, List<InterceptRule> rules)
    {
        _suppressSync = true;
        g.Rows.Clear();
        foreach (var r in rules)
        {
            var row = g.Rows[g.Rows.Add()];
            row.Tag = r;
            row.Cells["en"].Value = r.Enabled;
            row.Cells["op"].Value = string.IsNullOrEmpty(r.Operator) ? "or" : r.Operator;
            row.Cells["mt"].Value = string.IsNullOrEmpty(r.MatchType) ? "url" : r.MatchType;
            row.Cells["rel"].Value = string.IsNullOrEmpty(r.Relationship) ? "matches" : r.Relationship;
            row.Cells["cond"].Value = r.Condition;
        }
        g.ClearSelection();
        _suppressSync = false;
    }

    private void LoadMr()
    {
        _suppressSync = true;
        _mrGrid.Rows.Clear();
        foreach (var r in _doc.Proxy.MatchReplace)
        {
            var row = _mrGrid.Rows[_mrGrid.Rows.Add()];
            row.Tag = r;
            row.Cells["en"].Value = r.Enabled;
            row.Cells["rx"].Value = r.Regex;
            row.Cells["type"].Value = string.IsNullOrEmpty(r.RuleType) ? "request_header" : r.RuleType;
            row.Cells["match"].Value = r.Match;
            row.Cells["repl"].Value = r.Replace;
            row.Cells["cmt"].Value = r.Comment;
        }
        _mrGrid.ClearSelection();
        _suppressSync = false;
    }

    private static void SyncListener(DataGridViewRow row)
    {
        if (row.Tag is not ProxyListener l) return;
        l.Running = row.Cells["run"].Value is bool b && b;
        l.Bind = row.Cells["bind"].Value?.ToString() ?? "loopback_only";
        l.Port = row.Cells["port"].Value?.ToString() ?? "";
    }

    private static void SyncIntercept(DataGridViewRow row)
    {
        if (row.Tag is not InterceptRule r) return;
        r.Enabled = row.Cells["en"].Value is bool b && b;
        r.Operator = row.Cells["op"].Value?.ToString() ?? "or";
        r.MatchType = row.Cells["mt"].Value?.ToString() ?? "";
        r.Relationship = row.Cells["rel"].Value?.ToString() ?? "";
        r.Condition = row.Cells["cond"].Value?.ToString() ?? "";
    }

    private static void SyncMatchReplace(DataGridViewRow row)
    {
        if (row.Tag is not MatchReplaceRule r) return;
        r.Enabled = row.Cells["en"].Value is bool b && b;
        r.Regex = row.Cells["rx"].Value is bool rx && rx;
        r.RuleType = row.Cells["type"].Value?.ToString() ?? "request_header";
        r.Match = row.Cells["match"].Value?.ToString() ?? "";
        r.Replace = row.Cells["repl"].Value?.ToString() ?? "";
        r.Comment = row.Cells["cmt"].Value?.ToString() ?? "";
    }

    private void AddListener()
    {
        _doc.Proxy.Listeners.Add(new ProxyListener(new JsonObject
        { ["running"] = true, ["listen_mode"] = "loopback_only", ["listener_port"] = 8080 }));
        LoadListeners(); MarkDirty(); PersistProxy();
    }

    private static InterceptRule NewInterceptRule() => new(new JsonObject
    { ["enabled"] = true, ["boolean_operator"] = "or", ["match_type"] = "url", ["match_relationship"] = "matches", ["match_condition"] = "" });

    private void AddReq() { _doc.Proxy.RequestRules.Add(NewInterceptRule()); LoadIntercept(_reqGrid, _doc.Proxy.RequestRules); MarkDirty(); PersistProxy(); }
    private void AddResp() { _doc.Proxy.ResponseRules.Add(NewInterceptRule()); LoadIntercept(_respGrid, _doc.Proxy.ResponseRules); MarkDirty(); PersistProxy(); }

    private void AddMr()
    {
        _doc.Proxy.MatchReplace.Add(new MatchReplaceRule(new JsonObject
        { ["enabled"] = true, ["category"] = "regex", ["rule_type"] = "request_header", ["comment"] = "" }));
        LoadMr(); MarkDirty(); PersistProxy();
    }

    private void GridRemove<T>(DataGridView g, List<T> list, Action reload) where T : JsonRow
    {
        var selRows = g.SelectedRows.Cast<DataGridViewRow>().ToList();
        var sel = selRows.Select(r => r.Tag as T).Where(x => x != null).ToList();
        if (sel.Count == 0) { SetStatus("삭제할 행을 선택하세요."); return; }
        int anchor = selRows.Min(r => r.Index);
        int first = Math.Max(0, g.FirstDisplayedScrollingRowIndex);
        foreach (var x in sel) list.Remove(x!);
        reload(); MarkDirty(); PersistProxy();
        RestoreView(g, first, anchor);   // keep the scroll position instead of jumping to top
    }

    private void GridMove<T>(DataGridView g, List<T> list, int dir, Action reload) where T : JsonRow
    {
        if (g.CurrentRow?.Tag is not T r) return;
        int i = list.IndexOf(r), ni = i + dir;
        if (ni < 0 || ni >= list.Count) return;
        (list[i], list[ni]) = (list[ni], list[i]);
        reload();
        if (ni < g.Rows.Count) { g.ClearSelection(); g.Rows[ni].Selected = true; }
        MarkDirty(); PersistProxy();
    }

    private void ImportProxyFromFile()
    {
        using var d = new OpenFileDialog { Filter = "Burp JSON (*.json)|*.json|All files|*.*" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            if (ScopeIO.ImportProxy(d.FileName, _doc.Proxy)) { RefreshProxy(); MarkDirty(); PersistProxy(); SetStatus("프록시 설정을 가져왔습니다. (앱 전역 설정으로 저장됨)"); }
            else Dlg.Show(this, "proxy 블록을 찾지 못했습니다.", "가져오기", MessageBoxButtons.OK, Dlg.Kind.Warn);
        }
        catch (Exception ex) { Error("프록시 가져오기 실패", ex); }
    }

    private void ExportProxyToFile()
    {
        using var d = new SaveFileDialog { Filter = "Burp proxy config (*.json)|*.json", FileName = "proxy.burp.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try { ScopeIO.ExportProxy(_doc.Proxy, d.FileName); SetStatus($"프록시 Export: {d.FileName}"); }
        catch (Exception ex) { Error("프록시 Export 실패", ex); }
    }

    private void ExportExtensionToFile()
    {
        if (_doc.Proxy.MatchReplace.Count(r => r.Enabled) == 0)
        {
            Dlg.Show(this, "활성화된 Match/Replace 규칙이 없습니다.", "확장 Export", MessageBoxButtons.OK, Dlg.Kind.Info);
            return;
        }
        using var d = new SaveFileDialog { Filter = "Java source (*.java)|*.java", FileName = "BurpManagerMatchReplace.java" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            ScopeIO.ExportMatchReplaceExtension(_doc.Proxy, d.FileName);
            SetStatus($"확장 생성: {d.FileName}  (montoya-api로 컴파일 후 Burp에 로드" +
                      (_doc.Proxy.MatchReplaceScopeOnly ? " · in-scope 전용)" : ")"));
        }
        catch (Exception ex) { Error("확장 Export 실패", ex); }
    }

    /// <summary>Generic themed editor for the selected row of any rule grid.</summary>
    private void EditSelectedRow(DataGridView g)
    {
        if (g.CurrentRow is not { } row) { SetStatus("편집할 행을 선택하세요."); return; }

        using var f = new Form
        {
            Text = "규칙 편집",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowIcon = false, ShowInTaskbar = false,
            BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.UI, ClientSize = new Size(460, 10),
        };
        f.HandleCreated += (_, _) => Theme.UseDarkTitleBar(f.Handle);

        int y = 16;
        var editors = new List<(string name, Func<object?> get)>();
        foreach (DataGridViewColumn col in g.Columns)
        {
            var lbl = new Label { Text = col.HeaderText, Location = new Point(16, y + 3), Size = new Size(96, 20), ForeColor = Theme.Muted };
            var cur = row.Cells[col.Index].Value;
            Control input;
            if (col is DataGridViewCheckBoxColumn)
            {
                var cb = new DarkCheckBox { Text = "", Location = new Point(116, y), Size = new Size(40, 24), Checked = cur is bool b && b };
                input = cb; editors.Add((col.Name, () => cb.Checked));
            }
            else if (col.Tag is string[] opts)
            {
                var combo = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(116, y), Size = new Size(320, 24) };
                combo.Items.AddRange(opts);
                combo.SelectedItem = cur?.ToString();
                if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
                input = combo; editors.Add((col.Name, () => combo.SelectedItem?.ToString() ?? ""));
            }
            else
            {
                var tb = new TextBox { Location = new Point(116, y), Size = new Size(320, 24), Text = cur?.ToString() ?? "" };
                Theme.StyleInput(tb);
                input = tb; editors.Add((col.Name, () => tb.Text));
            }
            f.Controls.Add(lbl);
            f.Controls.Add(input);
            y += 32;
        }

        var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Size = new Size(84, 30), Location = new Point(272, y + 8) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Size = new Size(84, 30), Location = new Point(362, y + 8) };
        Theme.AccentButton(ok); Theme.FlatButton(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.ClientSize = new Size(460, y + 50);

        if (f.ShowDialog(this) != DialogResult.OK) return;
        foreach (var (name, get) in editors)
            row.Cells[name].Value = get();   // each set fires CellValueChanged → sync/persist
    }

    // ------------------------------------------------------ VALIDATE

    private Panel BuildValidateModule()
    {
        var root = new Panel { BackColor = Theme.Bg, Padding = new Padding(16) };
        _lintGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Panel,
            BorderStyle = BorderStyle.None,
            GridColor = Theme.Border,
            EnableHeadersVisualStyles = false,
            AllowUserToAddRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Font = Theme.UI,
        };
        _lintGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _lintGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _lintGrid.GridColor = Theme.BorderSoft;
        _lintGrid.ColumnHeadersHeight = 28;
        _lintGrid.RowTemplate.Height = 25;
        _lintGrid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Header;
        _lintGrid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        _lintGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Header;
        _lintGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
        _lintGrid.ColumnHeadersDefaultCellStyle.Font = Theme.UISemi;
        _lintGrid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        _lintGrid.DefaultCellStyle.BackColor = Theme.Panel;
        _lintGrid.DefaultCellStyle.ForeColor = Theme.Text;
        _lintGrid.DefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        _lintGrid.DefaultCellStyle.SelectionBackColor = Theme.Select;
        _lintGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        _lintGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Severity", FillWeight = 12 });
        _lintGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Program", FillWeight = 20 });
        _lintGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Finding", FillWeight = 68 });
        SlimScroll.ForGrid(_lintGrid);   // purple custom scrollbar

        var head = Tk.Head("VALIDATE  ·  ROE lint");
        root.Controls.Add(_lintGrid);
        root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        root.Controls.Add(head);
        return root;
    }

    private void RefreshLint()
    {
        _lintGrid.Rows.Clear();
        var findings = ScopeLinter.Run(_doc).OrderBy(f => Rank(f.Severity)).ToList();
        foreach (var f in findings)
        {
            int i = _lintGrid.Rows.Add(f.Severity, f.Program, f.Message);
            _lintGrid.Rows[i].DefaultCellStyle.ForeColor = f.Severity switch
            {
                "HIGH" => Theme.Danger,
                "MEDIUM" => Theme.Warn,
                _ => Theme.Muted,
            };
        }
        if (findings.Count == 0)
        {
            int i = _lintGrid.Rows.Add("OK", "-", "지적사항 없음. scope 구성이 깨끗합니다.");
            _lintGrid.Rows[i].DefaultCellStyle.ForeColor = Theme.Ok;
        }
        _lintGrid.ClearSelection();
    }

    private static int Rank(string s) => s switch { "HIGH" => 0, "MEDIUM" => 1, "INFO" => 2, _ => 3 };

    // ------------------------------------------------------ IMPORT/EXPORT

    private Panel BuildIoModule()
    {
        var root = new Panel { BackColor = Theme.Bg, Padding = new Padding(20) };
        var head = Tk.Head("IMPORT / EXPORT");

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 260, FlowDirection = FlowDirection.TopDown, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
        flow.Controls.Add(BigBtn("Burp JSON 파일 가져오기", "여러 원본 project-settings JSON 을 선택해 프로그램으로 추가", ImportFiles));
        flow.Controls.Add(BigBtn("폴더 스캔하여 가져오기", "하위 폴더까지 재귀 스캔하여 scope 가 있는 JSON 일괄 추가", ImportFolder));
        flow.Controls.Add(BigBtn("선택 프로그램 → Burp scope export", "현재 선택된 프로그램만 target.scope 형식으로 저장", ExportCurrent));
        flow.Controls.Add(BigBtn("전체 프로그램 → 폴더로 export", "각 프로그램을 <name>.burpscope.json 으로 일괄 저장", ExportAll));

        root.Controls.Add(flow);
        root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        root.Controls.Add(head);
        return root;
    }

    private Button BigBtn(string title, string sub, Action onClick)
    {
        var b = new Button { Width = 640, Height = 52, Margin = new Padding(0, 0, 0, 10), TextAlign = ContentAlignment.MiddleLeft, Text = "  " + title + "\n  " + sub };
        Theme.FlatButton(b);
        b.Font = Theme.UI;
        b.Click += (_, _) => onClick();
        return b;
    }

    // ------------------------------------------------------ SETTINGS

    private Panel BuildSettingsModule()
    {
        var root = new Panel { BackColor = Theme.Bg, Padding = new Padding(20) };
        var head = Tk.Head("SETTINGS");

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 220, FlowDirection = FlowDirection.TopDown, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
        flow.Controls.Add(BigBtn("새 마스터 만들기", "빈 마스터 문서로 초기화", NewMaster));
        flow.Controls.Add(BigBtn("마스터 열기…", "기존 통합 JSON(master) 불러오기", OpenMaster));
        flow.Controls.Add(BigBtn("다른 이름으로 저장…", "현재 마스터를 새 경로로 저장", () => SaveMasterAs()));
        var note = new Label
        {
            Text = "· 저장 시 기존 파일은 *.json.bak 로 자동 백업됩니다.\n· 원본 흩어진 JSON 은 읽기 전용 — Export 는 항상 새 파일로 생성됩니다.\n· 마스터 기본 위치: " + MasterDir,
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.UI, Margin = new Padding(2, 12, 0, 0),
        };
        flow.Controls.Add(note);

        root.Controls.Add(flow);
        root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg });
        root.Controls.Add(head);
        return root;
    }

    // ------------------------------------------------------ status bar

    private void BuildStatus()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = Theme.Rail, Padding = new Padding(12, 0, 12, 0) };
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Theme.Muted;
        _statusLabel.Font = Theme.Code(8.5f);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        bar.Controls.Add(_statusLabel);
        Controls.Add(bar);
    }

    private static Label SectionBar(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 28, ForeColor = Theme.Muted,
        Font = Theme.Sans(8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0), BackColor = Theme.Panel,
    };

    // ============================================================== actions

    private void TryLoadMaster(string path)
    {
        try
        {
            _doc = MasterDocument.Load(path);
            ApplyPersistedProxy();   // last-used proxy overrides whatever the master carried
            _masterPath = path;
            _dirty = false;
            _current = null;
            RefreshEntries();
            RefreshMasterCombo();
            UpdateTitle();
            SetStatus($"마스터 로드: {path}  (프로그램 {_doc.Programs.Count}개)");
        }
        catch (Exception ex) { Error("마스터 로드 실패", ex); }
    }

    private void NewMaster()
    {
        if (!ConfirmDiscard()) return;
        _doc = new MasterDocument(); _masterPath = null; _dirty = false; _current = null;
        ApplyPersistedProxy();
        RefreshEntries(); UpdateTitle(); SetStatus("새 마스터 문서.");
        SwitchModule("PROGRAMS");
    }

    private void OpenMaster()
    {
        if (!ConfirmDiscard()) return;
        using var d = new OpenFileDialog { Filter = "Master JSON (*.json)|*.json|All files|*.*", InitialDirectory = MasterDir };
        if (d.ShowDialog() != DialogResult.OK) return;
        TryLoadMaster(d.FileName);
        SwitchModule("PROGRAMS");
    }

    private bool SaveMaster()
    {
        if (_masterPath is null) return SaveMasterAs();
        try
        {
            _doc.Save(_masterPath); _dirty = false; UpdateTitle();
            RefreshMasterCombo();
            SetStatus($"저장 완료: {_masterPath}  (백업 *.json.bak)");
            return true;
        }
        catch (Exception ex) { Error("저장 실패", ex); return false; }
    }

    private bool SaveMasterAs()
    {
        using var d = new SaveFileDialog { Filter = "Master JSON (*.json)|*.json", FileName = "burp-scope-master.json", InitialDirectory = MasterDir };
        if (d.ShowDialog() != DialogResult.OK) return false;
        _masterPath = d.FileName;
        return SaveMaster();
    }

    private void ImportFiles()
    {
        using var d = new OpenFileDialog { Filter = "Burp JSON (*.json)|*.json|All files|*.*", Multiselect = true };
        if (d.ShowDialog() != DialogResult.OK) return;
        ImportPaths(d.FileNames);
    }

    private void ImportFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Burp JSON 이 있는 폴더 (하위 재귀 스캔)" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try { ImportPaths(Directory.EnumerateFiles(d.SelectedPath, "*.json", SearchOption.AllDirectories)); }
        catch (Exception ex) { Error("폴더 스캔 실패", ex); }
    }

    private void WireDragDrop()
    {
        void Walk(Control c) { EnableDrop(c); foreach (Control ch in c.Controls) Walk(ch); }
        Walk(this);
    }

    private void EnableDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        c.DragDrop += (_, e) => OnFilesDropped(e);
    }

    private void OnFilesDropped(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var jsons = paths
            .Where(p => File.Exists(p) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Concat(paths.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.json", SearchOption.AllDirectories)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (jsons.Length == 0) { SetStatus("드롭한 항목에 JSON 파일이 없습니다."); return; }
        ImportPaths(jsons);
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        int ok = 0, skip = 0;
        foreach (var p in paths)
        {
            try
            {
                var g = ScopeIO.ImportFile(p, UniqueName(Path.GetFileNameWithoutExtension(p)));
                if (g is null) { skip++; continue; }
                g.Enabled = false;   // imported disabled — user enables what they want
                _doc.Programs.Add(g); ok++;
            }
            catch { skip++; }
        }
        if (ok > 0) MarkDirty();
        RefreshEntries();
        SwitchModule("PROGRAMS");
        SetStatus($"가져오기: {ok}개 추가(비활성 상태), {skip}개 건너뜀(scope 없음/실패). ENTRIES 체크박스로 활성화하세요.");
        if (ok == 0 && skip > 0)
            Dlg.Show(this, "scope 를 찾지 못했습니다. target.scope 가 포함된 JSON 인지 확인하세요.", "가져오기", MessageBoxButtons.OK, Dlg.Kind.Warn);
    }

    private string UniqueName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "program";
        var name = baseName; int i = 2;
        while (_doc.Programs.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName}_{i++}";
        return name;
    }

    private void ExportCurrent()
    {
        if (_current is null) { Dlg.Show(this, "먼저 프로그램을 선택하세요.", "Export", MessageBoxButtons.OK, Dlg.Kind.Info); return; }
        using var d = new SaveFileDialog { Filter = "Burp scope (*.json)|*.json", FileName = $"{_current.Name}.burpscope.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        try { ScopeIO.ExportProgram(_current, d.FileName, _doc.Proxy.IncludeInExport ? _doc.Proxy : null); SetStatus($"Export: {d.FileName}  (Burp: Project settings → Load)"); }
        catch (Exception ex) { Error("Export 실패", ex); }
    }

    private void ExportAll()
    {
        var active = _doc.Programs.Where(p => p.Enabled).ToList();
        if (active.Count == 0)
        {
            Dlg.Show(this, "활성화(체크)된 프로그램이 없습니다. ENTRIES 에서 내보낼 프로그램을 활성화하세요.",
                "Export", MessageBoxButtons.OK, Dlg.Kind.Info);
            return;
        }
        using var d = new FolderBrowserDialog { Description = "활성 프로그램 scope 를 저장할 폴더" };
        if (d.ShowDialog() != DialogResult.OK) return;
        int n = 0;
        foreach (var g in active)
        {
            try { ScopeIO.ExportProgram(g, Path.Combine(d.SelectedPath, $"{g.Name}.burpscope.json"), _doc.Proxy.IncludeInExport ? _doc.Proxy : null); n++; } catch { }
        }
        int skipped = _doc.Programs.Count - active.Count;
        SetStatus($"전체 Export 완료: {n}개(활성) → {d.SelectedPath}" + (skipped > 0 ? $"  ·  비활성 {skipped}개 제외" : ""));
    }

    // ------------------------------------------------------ helpers

    private void MarkDirty() { _dirty = true; UpdateTitle(); }

    private void UpdateTitle()
    {
        var name = _masterPath is null ? "(새 문서)" : Path.GetFileName(_masterPath);
        Text = $"Burp Manager — {name}{(_dirty ? " ●" : "")}";
        _masterLabel.Text = "● " + (_masterPath is null ? "(새 마스터)" : Path.GetFileName(_masterPath));
        _masterLabel.ForeColor = _masterPath is null ? Theme.Muted : Theme.Ok;
    }

    private void SetStatus(string msg) => _statusLabel.Text = "  " + msg;

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = Dlg.Show(this, "저장하지 않은 변경사항이 있습니다. 저장할까요?", "확인", MessageBoxButtons.YesNoCancel, Dlg.Kind.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return SaveMaster();
        return true;
    }

    private void Error(string title, Exception ex) =>
        Dlg.Show(this, ex.Message, title, MessageBoxButtons.OK, Dlg.Kind.Error);

}

/// <summary>Small orange scope-reticle drawn in the header.</summary>
internal sealed class ReticleLogo : Control
{
    public ReticleLogo()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = Math.Min(Width, Height);
        float cx = Width / 2f;

        // Shield.
        float sw = s * 0.74f, sh = s * 0.84f, top = (Height - sh) / 2f - s * 0.02f;
        float left = cx - sw / 2, right = cx + sw / 2, r = sw * 0.16f, shoulder = top + sh * 0.58f;
        using var shield = new System.Drawing.Drawing2D.GraphicsPath();
        shield.AddArc(left, top, r * 2, r * 2, 180, 90);
        shield.AddArc(right - r * 2, top, r * 2, r * 2, 270, 90);
        shield.AddLine(right, top + r, right, shoulder);
        shield.AddBezier(right, shoulder, right, top + sh * 0.86f, cx + sw * 0.20f, top + sh * 0.97f, cx, top + sh);
        shield.AddBezier(cx, top + sh, cx - sw * 0.20f, top + sh * 0.97f, left, top + sh * 0.86f, left, shoulder);
        shield.AddLine(left, shoulder, left, top + r);
        shield.CloseFigure();

        // Line-art style, matching the nav icons: stroked outline, no fill.
        using (var edge = new Pen(Theme.Brand, Math.Max(1.4f, s * 0.055f))
        { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            g.DrawPath(edge, shield);

        // Crosshair.
        float ccy = top + sh * 0.44f, ring = s * 0.16f, reach = s * 0.26f, gap = s * 0.07f;
        using (var pen = new Pen(Theme.Brand, Math.Max(1.2f, s * 0.045f)))
            g.DrawEllipse(pen, cx - ring, ccy - ring, ring * 2, ring * 2);
        using (var pen = new Pen(Theme.Brand, Math.Max(1.2f, s * 0.05f))
        { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
        {
            g.DrawLine(pen, cx, ccy - reach, cx, ccy - gap);
            g.DrawLine(pen, cx, ccy + gap, cx, ccy + reach);
            g.DrawLine(pen, cx - reach, ccy, cx - gap, ccy);
            g.DrawLine(pen, cx + gap, ccy, cx + reach, ccy);
        }
        float dot = s * 0.038f;
        using (var b = new SolidBrush(Theme.Brand))
            g.FillEllipse(b, cx - dot, ccy - dot, dot * 2, dot * 2);
    }
}

/// <summary>Thin horizontal divider that fades from the brand colour on the left.</summary>
internal sealed class GradientDivider : Control
{
    private const int GlowW = 220;      // width of the travelling highlight
    private const int StepPx = 6;       // px advanced per tick (≈30fps → ~180px/s)
    private const int FadeW = 200;      // px over which the glow eases in / out

    /// <summary>true: purple across the whole width; false: fades out to the right.</summary>
    public bool FullColor { get; init; }

    /// <summary>Fraction of the width where the travelling glow finishes (1 = far right edge).</summary>
    public float SweepEnd { get; init; } = 1f;

    private readonly System.Windows.Forms.Timer _timer;
    private int _pos;

    /// <summary>Set false for a static line (e.g. inside module headers).</summary>
    public bool Animate
    {
        get => _timer.Enabled;
        set { if (value != _timer.Enabled) { _timer.Enabled = value; Invalidate(); } }
    }

    public GradientDivider()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Bg;
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) =>
        {
            if (!Visible || Width <= 0 || FindForm() is { WindowState: FormWindowState.Minimized }) return;
            _pos += StepPx;
            // Restart once the glow has fully faded out (its centre passed the finish line).
            if (_pos > EndX + GlowW / 2) _pos = 0;
            Invalidate();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        if (Width <= 0) return;

        // Base rail: either purple edge-to-edge, or brand → dim → border fading out to the right.
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(0, 0, Width, Height), Theme.Brand, Theme.Bg, 0f))
        {
            brush.InterpolationColors = FullColor
                ? new System.Drawing.Drawing2D.ColorBlend
                {
                    Colors = new[] { Theme.Brand, Theme.BrandDim, Theme.BrandDim },
                    Positions = new[] { 0f, 0.35f, 1f },
                }
                : new System.Drawing.Drawing2D.ColorBlend
                {
                    Colors = new[] { Theme.Brand, Theme.BrandDim, Theme.Border, Theme.Bg },
                    Positions = new[] { 0f, 0.22f, 0.5f, 1f },
                };
            g.FillRectangle(brush, 0, 0, Width, Height);
        }

        if (!_timer.Enabled) return;

        // Ease the whole glow in at the start and out toward the finish line, so it never cuts off hard.
        float f = FadeFactor();
        if (f <= 0.01f) return;

        var clip = g.Clip;
        g.SetClip(new Rectangle(0, 0, Width, Height));

        // Wide, low-alpha halo first → reads as bloom around the core.
        Sweep(g, _pos - GlowW * 2, GlowW * 2, new[]
        {
            Fade(Color.FromArgb(0, Theme.Brand), f),
            Fade(Color.FromArgb(70, Theme.Brand), f),
            Fade(Color.FromArgb(0, Theme.Brand), f),
        }, new[] { 0f, 0.5f, 1f });

        // Travelling core: transparent → white-hot → transparent.
        Sweep(g, _pos - GlowW, GlowW, new[]
        {
            Fade(Color.FromArgb(0, Theme.Brand), f),
            Fade(Color.FromArgb(150, Theme.Brand), f),
            Fade(Color.FromArgb(255, 246, 245, 255), f),
            Fade(Color.FromArgb(150, Theme.Brand), f),
            Fade(Color.FromArgb(0, Theme.Brand), f),
        }, new[] { 0f, 0.34f, 0.5f, 0.66f, 1f });

        g.Clip = clip;
    }

    /// <summary>X where the travelling glow finishes.</summary>
    private int EndX => (int)(Width * Math.Clamp(SweepEnd, 0.05f, 1f));

    private static Color Fade(Color c, float f) => Color.FromArgb((int)Math.Round(c.A * f), c);

    /// <summary>0 → 1 → 0 envelope: ramps up after entering, ramps down as it nears EndX.</summary>
    private float FadeFactor()
    {
        float center = _pos - GlowW / 2f;
        float inF = Math.Clamp((center + GlowW / 2f) / FadeW, 0f, 1f);
        float outF = Math.Clamp((EndX - center) / (float)FadeW, 0f, 1f);
        return Math.Min(inF, outF);
    }

    private void Sweep(Graphics g, int x, int w, Color[] colors, float[] stops)
    {
        var band = new Rectangle(x, 0, w, Height);
        if (band.Right <= 0 || band.Left >= Width || w <= 0) return;
        using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
            band, Color.Transparent, Color.Transparent, 0f);
        br.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend { Colors = colors, Positions = stops };
        g.FillRectangle(br, band);
    }
}

/// <summary>ComboBox themed to the dark palette: custom arrow, border, dark item list.</summary>
internal sealed class DarkComboBox : ComboBox
{
    private static readonly Color Field = Theme.Sunken;

    public DarkComboBox()
    {
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = Field;
        ForeColor = Theme.Text;
        ItemHeight = 20;
        Font = Theme.UI;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) { e.DrawBackground(); return; }
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var bg = sel ? Theme.Select : Field;
        using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
        var rect = e.Bounds; rect.X += 5;
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, rect,
            sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F || m.Msg == 0x0133) // WM_PAINT / WM_CTLCOLOREDIT
        {
            using var g = Graphics.FromHwnd(Handle);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int bw = 20;
            var btn = new Rectangle(Width - bw, 1, bw - 1, Height - 2);
            using (var b = new SolidBrush(Field)) g.FillRectangle(b, btn);

            int ax = Width - bw / 2 - 2, ay = Height / 2;
            using (var pen = new Pen(Theme.Muted, 1.5f))
                g.DrawLines(pen, new[]
                {
                    new Point(ax - 4, ay - 2), new Point(ax, ay + 2), new Point(ax + 4, ay - 2)
                });

            using var bp = new Pen(Focused ? Theme.Brand : Theme.Border);
            g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);
        }
    }
}

/// <summary>Circular-arrow reload glyph — drawn, so it reads clearly at small sizes.</summary>
internal sealed class ReloadIcon : Control
{
    public ReloadIcon()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e) =>
        Paint(e.Graphics, ClientRectangle, Theme.Text);

    /// <summary>
    /// Shared drawing so the embedded toolbar and the pop-out window's caption show the
    /// identical glyph. <paramref name="box"/> is the square the icon is fitted into.
    /// </summary>
    public static void Paint(Graphics g, Rectangle box, Color color)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int s = Math.Min(box.Width, box.Height);
        if (s < 6) return;

        // Open circle with a wide gap on the right; the arrow head fills that gap.
        const float StartDeg = 48f, SweepDeg = 264f;
        float pad = s * 0.16f;
        var r = new RectangleF(box.X + pad, box.Y + pad, s - pad * 2, s - pad * 2);

        using var pen = new Pen(color, Math.Max(1.2f, s * 0.080f))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };
        g.DrawArc(pen, r, StartDeg, SweepDeg);

        // Head sits on the arc's start point and points along the *tangent*. Pointing it radially
        // outward (an earlier attempt) merged it into the stroke and the glyph read as a bare "G".
        double th = StartDeg * Math.PI / 180;
        float px = r.X + r.Width / 2f + (float)Math.Cos(th) * r.Width / 2f;
        float py = r.Y + r.Height / 2f + (float)Math.Sin(th) * r.Height / 2f;
        float dx = (float)Math.Sin(th), dy = -(float)Math.Cos(th);   // tangent, against the sweep
        float ex = -dy, ey = dx;                                     // perpendicular

        float len = s * 0.36f, halfW = s * 0.115f;
        // 0.85 puts almost the whole head ahead of the arc's end so the two read as separate
        // shapes; at 0.70 the base overlapped the stroke and thickened the junction.
        const float TipRatio = 0.85f;
        using var head = new System.Drawing.Drawing2D.GraphicsPath();
        head.AddPolygon(new[]
        {
            new PointF(px + dx * len * TipRatio, py + dy * len * TipRatio),
            new PointF(px - dx * len * (1 - TipRatio) + ex * halfW, py - dy * len * (1 - TipRatio) + ey * halfW),
            new PointF(px - dx * len * (1 - TipRatio) - ex * halfW, py - dy * len * (1 - TipRatio) - ey * halfW),
        });
        using var br = new SolidBrush(color);
        g.FillPath(br, head);
    }
}

/// <summary>Line icon for a module/tool name — same set the nav rail draws.</summary>
internal sealed class NavIcon : Control
{
    public string IconName { get; init; } = "";
    public Color Tint { get; init; } = Theme.Brand;

    public NavIcon()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int s = Math.Min(Width, Height);
        MainForm.DrawNavIcon(e.Graphics, IconName, new Rectangle((Width - s) / 2, (Height - s) / 2, s, s), Tint);
    }
}

/// <summary>Brand mark that renders the app icon itself (purple Kali dragon), so both match exactly.</summary>
internal sealed class AppLogo : Control
{
    private const int Period = 108;      // ticks per cycle (~3.6s at 33ms) → an occasional glint
    private const int SweepTicks = 30;   // ticks the glint takes to cross the mark

    private static Image? _img;          // the icon itself
    private static Image? _sil;          // white silhouette (same alpha) used as the shine layer
    private static bool _tried;

    private readonly System.Windows.Forms.Timer _timer;
    private int _tick;

    public AppLogo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Load();

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) =>
        {
            if (!Visible || FindForm() is { WindowState: FormWindowState.Minimized }) return;
            _tick = (_tick + 1) % Period;
            if (_tick <= SweepTicks) Invalidate();   // only repaint while the glint is on screen
        };
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _timer.Start(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }

    private static void Load()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var path = Path.Combine(dir, "app.ico");
            if (!File.Exists(path)) return;
            using var ic = new Icon(path, 128, 128);
            _img = ic.ToBitmap();

            // White version of the mark, alpha preserved → shine is confined to the icon's shape.
            var sil = new Bitmap(_img.Width, _img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sil))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { 1f, 1f, 1f, 0f, 1f },   // force RGB to white, keep alpha
                }));
                g.DrawImage(_img, new Rectangle(0, 0, sil.Width, sil.Height),
                    0, 0, _img.Width, _img.Height, GraphicsUnit.Pixel, ia);
            }
            _sil = sil;
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_img is null) return;
        var g = e.Graphics;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(_img, new Rectangle(0, 0, Width, Height));

        if (_sil is null || _tick > SweepTicks) return;

        // Diagonal shine travelling across the mark, softened by three stacked bands.
        float t = _tick / (float)SweepTicks;
        float env = (float)Math.Sin(Math.PI * t);                 // fade in → out, no hard start/stop
        float travel = Width + Height * 1.4f;
        float center = -Height * 0.7f + t * travel;
        Band(g, center, Width * 1.10f, (int)(46 * env));
        Band(g, center, Width * 0.60f, (int)(92 * env));
        Band(g, center, Width * 0.26f, (int)(165 * env));
    }

    private void Band(Graphics g, float center, float w, int alpha)
    {
        if (alpha <= 1 || _sil is null) return;
        float skew = Height * 0.55f;   // slant of the shine
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(center - w / 2 + skew, 0),
            new PointF(center + w / 2 + skew, 0),
            new PointF(center + w / 2 - skew, Height),
            new PointF(center - w / 2 - skew, Height),
        });

        var saved = g.Clip;
        g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Replace);
        using (var ia = new System.Drawing.Imaging.ImageAttributes())
        {
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha / 255f });
            g.DrawImage(_sil, new Rectangle(0, 0, Width, Height),
                0, 0, _sil.Width, _sil.Height, GraphicsUnit.Pixel, ia);
        }
        g.Clip = saved;
    }
}

/// <summary>Fully custom combo: themed field + themed borderless dropdown popup.</summary>
internal sealed class ThemeCombo : Control
{
    private static readonly Color Field = Theme.Sunken;
    private readonly List<string> _items = new();
    private int _index = -1;
    private bool _open;

    public event EventHandler? SelectedIndexChanged;

    public ThemeCombo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 28; Cursor = Cursors.Hand; Font = Theme.UI; ForeColor = Theme.Text;
    }

    public void AddItems(params string[] items)
    {
        _items.AddRange(items);
        if (_index < 0 && _items.Count > 0) _index = 0;
        Invalidate();
    }

    public void Clear() { _items.Clear(); _index = -1; Invalidate(); }
    public bool Contains(string item) => _items.Contains(item);
    public int IndexOf(string item) => _items.IndexOf(item);
    public int Count => _items.Count;

    /// <summary>Select by text without raising SelectedIndexChanged (for programmatic sync).</summary>
    public void SelectSilently(string item)
    {
        int i = _items.IndexOf(item);
        if (i >= 0) { _index = i; Invalidate(); }
    }

    public int SelectedIndex
    {
        get => _index;
        set { if (value != _index && value >= -1 && value < _items.Count) { _index = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public string? SelectedItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Field)) g.FillRectangle(bg, ClientRectangle);
        TextRenderer.DrawText(g, SelectedItem ?? "", Font, new Rectangle(8, 0, Width - 26, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        int ax = Width - 14, ay = Height / 2;
        using (var pen = new Pen(Theme.Muted, 1.5f))
            g.DrawLines(pen, new[] { new Point(ax - 4, ay - 2), new Point(ax, ay + 2), new Point(ax + 4, ay - 2) });
        using (var bp = new Pen(_open ? Theme.Brand : Theme.Border)) g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e) => OpenPopup();

    private void OpenPopup()
    {
        if (_items.Count == 0 || _open) return;
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false, TopMost = true, BackColor = Theme.Brand, Padding = new Padding(1),
            Size = new Size(Width, Math.Min(_items.Count, 12) * 24 + 2),
            Location = PointToScreen(new Point(0, Height)),
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Field, ForeColor = Theme.Text,
            Font = Font, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24,
        };
        list.Items.AddRange(_items.ToArray());
        list.DrawItem += (_, ev) =>
        {
            if (ev.Index < 0) return;
            bool s = (ev.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(s ? Theme.Brand : Field)) ev.Graphics.FillRectangle(b, ev.Bounds);
            var rr = ev.Bounds; rr.X += 8;
            TextRenderer.DrawText(ev.Graphics, (string)list.Items[ev.Index]!, Font, rr, s ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        f.Controls.Add(list);
        if (_index >= 0) list.SelectedIndex = _index;
        void Choose() { if (list.SelectedIndex >= 0) SelectedIndex = list.SelectedIndex; f.Close(); }
        list.MouseClick += (_, _) => Choose();
        list.KeyDown += (_, ev) => { if (ev.KeyCode == Keys.Enter) Choose(); else if (ev.KeyCode == Keys.Escape) f.Close(); };
        f.Deactivate += (_, _) => f.Close();
        f.FormClosed += (_, _) => { _open = false; Invalidate(); f.Dispose(); };
        _open = true; Invalidate();
        f.Show(FindForm()); f.BringToFront(); list.Focus();
    }
}

/// <summary>CheckBox owner-drawn with a themed box and purple check.</summary>
internal sealed class DarkCheckBox : CheckBox
{
    private static readonly Color Field = Theme.Sunken;

    public DarkCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        Font = Theme.UI;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        const int box = 16;
        int by = (Height - box) / 2;
        var rect = new Rectangle(0, by, box, box);

        using (var bg = new SolidBrush(Checked ? Theme.Brand : Field))
            g.FillRectangle(bg, rect);
        using (var pen = new Pen(Checked ? Theme.Brand : Theme.Border, 1.2f))
            g.DrawRectangle(pen, rect);

        if (Checked)
            using (var cp = new Pen(Color.White, 1.9f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            })
                g.DrawLines(cp, new[]
                {
                    new Point(rect.X + 3, rect.Y + 8),
                    new Point(rect.X + 6, rect.Y + 11),
                    new Point(rect.X + 12, rect.Y + 4),
                });

        var textRect = new Rectangle(box + 7, 0, Width - box - 7, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

/// <summary>RadioButton owner-drawn with a themed ring and purple dot.</summary>
internal sealed class DarkRadio : RadioButton
{
    private static readonly Color Field = Theme.Sunken;

    public DarkRadio()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        Font = Theme.UI;
        AutoSize = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        const int d = 15;
        var rect = new Rectangle(0, (Height - d) / 2, d, d);
        using (var bg = new SolidBrush(Field)) g.FillEllipse(bg, rect);
        using (var pen = new Pen(Checked ? Theme.Brand : Theme.Border, 1.4f)) g.DrawEllipse(pen, rect);
        if (Checked)
            using (var dot = new SolidBrush(Theme.Brand))
                g.FillEllipse(dot, rect.X + 4, rect.Y + 4, d - 8, d - 8);

        var tr = new Rectangle(d + 7, 0, Width - d - 7, Height);
        TextRenderer.DrawText(g, Text, Font, tr, Checked ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
}

/// <summary>Dashboard chart: active/disabled donut + protocol distribution bars.</summary>
internal sealed class DashChart : Control
{
    public int Active, Disabled, PAny, PHttp, PHttps;

    public DashChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Panel;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Donut — active vs disabled.
        int total = Active + Disabled;
        float d = Math.Min(Height - 24, 132);
        if (d < 40) return;
        float dx = 6, dy = (Height - d) / 2f;
        var rc = new RectangleF(dx, dy, d, d);
        using (var track = new SolidBrush(Color.FromArgb(60, 58, 76))) g.FillEllipse(track, rc);
        if (total > 0 && Active > 0)
            using (var arc = new SolidBrush(Theme.Brand))
                g.FillPie(arc, rc.X, rc.Y, rc.Width, rc.Height, -90, 360f * Active / total);
        float hole = d * 0.60f;
        using (var hb = new SolidBrush(Theme.Panel))
            g.FillEllipse(hb, dx + (d - hole) / 2, dy + (d - hole) / 2, hole, hole);
        TextRenderer.DrawText(g, Active.ToString(), Theme.Sans(16f, FontStyle.Bold),
            new Rectangle((int)dx, (int)dy - 8, (int)d, (int)d), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"/ {total} active", Theme.Sans(7.5f),
            new Rectangle((int)dx, (int)dy + 16, (int)d, (int)d), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Protocol bars.
        int bx = (int)(dx + d + 26);
        int bw = Width - bx - 12;
        if (bw < 60) return;
        var items = new (string name, int val, Color col)[]
        {
            ("any", PAny, Theme.Accent), ("http", PHttp, Theme.Warn), ("https", PHttps, Theme.Ok),
        };
        int max = Math.Max(1, items.Max(i => i.val));
        int rowH = 34;
        int by = (Height - items.Length * rowH) / 2 + 4;
        foreach (var (name, val, col) in items)
        {
            TextRenderer.DrawText(g, name, Theme.UI, new Rectangle(bx, by, 60, 16), Theme.Muted, TextFormatFlags.Left);
            TextRenderer.DrawText(g, val.ToString(), Theme.Code(8.5f), new Rectangle(bx, by, bw, 16), Theme.Text, TextFormatFlags.Right);
            using (var tb = new SolidBrush(Color.FromArgb(55, 53, 70))) g.FillRectangle(tb, bx, by + 18, bw, 9);
            int fw = (int)(bw * (val / (float)max));
            if (val > 0) fw = Math.Max(fw, 4);
            using (var fb = new SolidBrush(col)) g.FillRectangle(fb, bx, by + 18, fw, 9);
            by += rowH;
        }
    }
}

/// <summary>Dark-themed replacement for MessageBox.</summary>
internal static class Dlg
{
    public enum Kind { Info, Warn, Error, Question }

    public static DialogResult Show(IWin32Window? owner, string message, string title,
        MessageBoxButtons buttons = MessageBoxButtons.OK, Kind kind = Kind.Info)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = Theme.UI,
        };
        f.HandleCreated += (_, _) => Theme.UseDarkTitleBar(f.Handle);

        var (glyph, col) = kind switch
        {
            Kind.Warn => ("!", Theme.Warn),
            Kind.Error => ("×", Theme.Danger),
            Kind.Question => ("?", Theme.Brand),
            _ => ("i", Theme.Accent),
        };

        const int W = 470;
        var icon = new IconBadge { Glyph = glyph, Accent = col, Location = new Point(26, 26), Size = new Size(46, 46) };
        var titleLbl = new Label
        {
            Text = title, Font = Theme.Sans(11f, FontStyle.Bold), ForeColor = Color.White,
            Location = new Point(90, 25), AutoSize = false, Size = new Size(W - 108, 22),
        };
        var msgLbl = new Label { Text = message, Font = Theme.UI, ForeColor = Theme.Text, Location = new Point(90, 51), AutoSize = false };
        var sz = TextRenderer.MeasureText(message, Theme.UI, new Size(W - 108, 0), TextFormatFlags.WordBreak);
        msgLbl.Size = new Size(W - 108, Math.Max(36, sz.Height + 4));

        int contentBottom = Math.Max(icon.Bottom, msgLbl.Bottom) + 20;

        var list = BuildButtons(buttons, out var accept, out var cancel);
        int totalW = list.Sum(b => b.Width) + (list.Count - 1) * 8;
        int x = W - 18 - totalW;
        foreach (var b in list) { b.Location = new Point(x, contentBottom); x += b.Width + 8; }

        f.ClientSize = new Size(W, contentBottom + 40);
        f.Controls.Add(icon);
        f.Controls.Add(titleLbl);
        f.Controls.Add(msgLbl);
        foreach (var b in list) f.Controls.Add(b);
        if (accept != null) f.AcceptButton = accept;
        if (cancel != null) f.CancelButton = cancel;

        return f.ShowDialog(owner);
    }

    private static List<Button> BuildButtons(MessageBoxButtons kind, out Button? accept, out Button? cancel)
    {
        accept = null; cancel = null;
        var list = new List<Button>();

        Button Make(string text, DialogResult dr, bool primary)
        {
            var b = new Button { Text = text, DialogResult = dr, Height = 32 };
            b.Width = Math.Max(84, TextRenderer.MeasureText(text, Theme.UI).Width + 34);
            if (primary) Theme.AccentButton(b); else Theme.FlatButton(b);
            return b;
        }

        switch (kind)
        {
            case MessageBoxButtons.OKCancel:
                var ok = Make("확인", DialogResult.OK, true);
                var c = Make("취소", DialogResult.Cancel, false);
                list.Add(ok); list.Add(c); accept = ok; cancel = c; break;
            case MessageBoxButtons.YesNo:
                var y = Make("예", DialogResult.Yes, true);
                var n = Make("아니요", DialogResult.No, false);
                list.Add(y); list.Add(n); accept = y; cancel = n; break;
            case MessageBoxButtons.YesNoCancel:
                var y2 = Make("예", DialogResult.Yes, true);
                var n2 = Make("아니요", DialogResult.No, false);
                var c2 = Make("취소", DialogResult.Cancel, false);
                list.Add(y2); list.Add(n2); list.Add(c2); accept = y2; cancel = c2; break;
            default:
                var okb = Make("확인", DialogResult.OK, true);
                list.Add(okb); accept = okb; cancel = okb; break;
        }
        return list;
    }
}

/// <summary>Circular severity badge with a glyph, used by <see cref="Dlg"/>.</summary>
internal sealed class IconBadge : Control
{
    public string Glyph = "";
    public Color Accent = Color.Gray;

    public IconBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var fill = new SolidBrush(Color.FromArgb(42, Accent))) g.FillEllipse(fill, r);
        using (var pen = new Pen(Accent, 2f)) g.DrawEllipse(pen, r);
        TextRenderer.DrawText(g, Glyph, Theme.Sans(Height * 0.42f, FontStyle.Bold),
            ClientRectangle, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
