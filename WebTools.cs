using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BurpManager;

/// <summary>
/// Embeds the local pentest web-toolkit pages (intruder / encode / webshell)
/// inside a themed module via WebView2. Session cookies persist in a private
/// user-data folder, so the one-time login carries across restarts.
/// </summary>
internal sealed class WebToolsPanel : Panel
{
    // The suite is reachable two ways: the loopback binding and the IIS host-header binding.
    // Switching between them is a host swap on the current URL — path and query are preserved.
    // Loopback stays http: the Let's Encrypt cert is issued for the domain name, so https on an
    // IP literal would fail validation and WebView2 would show an interstitial.
    private const string IpOrigin = "http://127.0.0.1";
    private const string DefaultDomainOrigin = "https://suni.onthewifi.com";
    private const string LegacyDomainOrigin = "http://suni.onthewifi.com";
    private const string StartPath = "/pentest/intruder.aspx";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _addr = new();
    private readonly Label _hint = new();
    private readonly Panel _bar = new() { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Bg };
    private readonly Panel _strip = new() { Dock = DockStyle.Top, Height = 16, BackColor = Theme.Rail, Visible = false, Cursor = Cursors.Hand };
    private string _current = IpOrigin + StartPath;
    private bool _initStarted, _ready;

    // host mode
    private string _domainOrigin = DefaultDomainOrigin;
    private bool _useDomain;
    private Button? _modeBtn;
    private readonly Label _tls = new();
    private readonly ToolTip _modeTip = new() { InitialDelay = 350 };
    private string? _modeFile;
    private string ActiveOrigin => _useDomain ? _domainOrigin : IpOrigin;

    // pop-out state
    private Panel _webHost = null!;
    private ThemedWindow? _popWin;
    private Button? _popBtn;
    private CapButton? _popModeBtn;

    // Viewport width toggle for the detached window. 390px is a typical phone breakpoint —
    // wide enough to be realistic, narrow enough to trip every responsive layout.
    private const int MobileWidth = 390;
    private bool _popMobile;
    private int _popDesktopWidth = 1180;

    // Cookie persistence (keeps the pentest-suite login across restarts). Session cookies are
    // scoped per host, so each origin needs its own file or switching mode logs you out.
    private string? _dataDir;
    private string? CookieFile =>
        _dataDir is null ? null
        : Path.Combine(_dataDir, "cookies_" + new Uri(ActiveOrigin).Host.Replace(':', '_') + ".json");

    public WebToolsPanel()
    {
        BackColor = Theme.Bg;
        Padding = new Padding(12);

        // Tool switching happens inside the embedded web UI's own sidebar — no duplicate tabs here.
        var reload = new Button { Text = "  새로고침", Location = new Point(0, 7), Size = new Size(96, 28), TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0) };
        Theme.FlatButton(reload);
        reload.Click += (_, _) => { if (_ready) _web.CoreWebView2?.Reload(); };
        var rIcon = new ReloadIcon { Size = new Size(18, 18), Location = new Point(9, 5) };
        rIcon.Click += (_, _) => { if (_ready) _web.CoreWebView2?.Reload(); };
        reload.Controls.Add(rIcon);

        var open = new Button { Text = "⇱ 브라우저", Location = new Point(104, 7), Size = new Size(96, 28) };
        Theme.FlatButton(open);
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(_current) { UseShellExecute = true }); } catch { } };

        var pop = new Button { Text = "⧉ 창 분리", Location = new Point(208, 7), Size = new Size(92, 28) };
        Theme.FlatButton(pop);
        pop.Click += (_, _) => TogglePopOut();
        _popBtn = pop;

        var mode = new Button { Text = "⇄ 도메인", Location = new Point(308, 7), Size = new Size(96, 28) };
        Theme.FlatButton(mode);
        mode.Click += (_, _) => ToggleHostMode();
        _modeBtn = mode;

        var collapse = new Button { Text = "▲", Location = new Point(412, 7), Size = new Size(30, 28) };
        Theme.FlatButton(collapse);
        collapse.Click += (_, _) => SetBar(false);

        _addr.AutoSize = true;
        _addr.ForeColor = Theme.Muted;
        _addr.Font = Theme.Code(8.5f);
        _addr.Location = new Point(452, 13);
        _addr.Text = _current;

        _tls.AutoSize = true;
        _tls.Font = Theme.Code(8.5f, FontStyle.Bold);
        _tls.ForeColor = Theme.Muted;
        _tls.Visible = false;
        _modeTip.SetToolTip(_tls, "");
        void PlaceTls() => _tls.Location = new Point(Math.Max(8, _bar.Width - _tls.Width - 10), 14);
        _bar.Resize += (_, _) => PlaceTls();
        _tls.TextChanged += (_, _) => PlaceTls();

        _bar.Controls.Add(_tls);
        _bar.Controls.Add(reload);
        _bar.Controls.Add(open);
        _bar.Controls.Add(pop);
        _bar.Controls.Add(mode);
        _bar.Controls.Add(collapse);
        _bar.Controls.Add(_addr);

        _strip.Controls.Add(new Label { Text = "  ▼  상단 패널 펼치기", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Sans(8f), TextAlign = ContentAlignment.MiddleLeft });
        _strip.Click += (_, _) => SetBar(true);
        foreach (Control c in _strip.Controls) c.Click += (_, _) => SetBar(true);

        var head = Tk.Head("WEB TOOLS  ·  로컬 pentest 툴킷");   // module icon + title, same as other modules

        // Host that holds the WebView + a centered hint shown until it is ready.
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sunken };
        _webHost = host;
        _hint.Text = "WebView2 초기화 중…";
        _hint.Dock = DockStyle.Fill;
        _hint.TextAlign = ContentAlignment.MiddleCenter;
        _hint.ForeColor = Theme.Muted;
        _hint.Font = Theme.Sans(10f);
        host.Controls.Add(_web);
        host.Controls.Add(_hint);
        _web.Visible = false;

        Controls.Add(host);
        Controls.Add(_strip);
        Controls.Add(_bar);
        Controls.Add(head);

    }

    private void SetBar(bool open) { _bar.Visible = open; _strip.Visible = !open; }

    // ------------------------------------------------------------ pop-out window

    /// <summary>Move the live WebView into its own window (and back), so the session is never re-created.</summary>
    private void TogglePopOut()
    {
        if (_popWin != null) { _popWin.Activate(); return; }
        if (!_ready) { _hint.Text = "웹뷰 초기화 후 분리할 수 있습니다."; return; }

        var win = new ThemedWindow("WEB TOOLS", new Size(1180, 820));
        _popWin = win;

        // Detached, the module's toolbar is out of reach — reload lives on the caption instead.
        // Same glyph as the embedded toolbar via ReloadIcon.Paint.
        win.AddCaptionButton("", () => { if (_ready) _web.CoreWebView2?.Reload(); },
            painter: (g, r, c) =>
            {
                const int sz = 15;   // matched to the ─ □ ✕ glyph weight beside it
                ReloadIcon.Paint(g, new Rectangle(r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz), c);
            },
            tip: "새로고침");

        // Host-mode swap. No room for a label at 40px, but the caption already shows the full URL,
        // so the active mode is visible there — the button only has to mean "switch".
        // Reuses the shared nav icon set's PROXY glyph (opposing arrows) for consistency.
        _popModeBtn = win.AddCaptionButton("", ToggleHostMode,
            painter: (g, r, c) =>
            {
                const int sz = 14;
                MainForm.DrawNavIcon(g, "PROXY",
                    new Rectangle(r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz), c);
            });

        // Mobile / desktop viewport width. Draws the device you will get, matching the mode
        // button's "label the destination" rule.
        _popMobile = false;
        win.AddCaptionButton("", () => ToggleViewport(win),
            painter: (g, r, c) => DeviceIcon(g, r, c, phone: !_popMobile),
            tip: "모바일 / PC 폭 전환");

        // WebView fills the whole window under the title bar — no extra toolbar.
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sunken };

        // Hand the WebView over to the new window.
        _web.Parent?.Controls.Remove(_web);
        host.Controls.Add(_web);
        _web.Dock = DockStyle.Fill;

        win.Controls.Add(host);

        // Placeholder in the module while detached.
        _hint.Text = "웹뷰가 별도 창에서 열려 있습니다.\n[⧉ 창 복귀] 를 누르거나 창을 닫으면 이 자리로 돌아옵니다.";
        _hint.Visible = true;
        if (_popBtn != null) _popBtn.Text = "⧉ 창 복귀";

        win.FormClosed += (_, _) =>
        {
            try
            {
                _web.Parent?.Controls.Remove(_web);
                _webHost.Controls.Add(_web);
                _web.Dock = DockStyle.Fill;
                _web.BringToFront();
                _hint.Visible = false;
                if (_popBtn != null) _popBtn.Text = "⧉ 창 분리";
            }
            catch { }
            _popWin = null;
            _popModeBtn = null;
        };

        // No owner: an owned window would minimise/restore together with the main shell.
        win.Show();
        win.SetTitle("WEB TOOLS  ·  " + _current);
        UpdateModeButton();   // gives the new caption button its tooltip
    }

    /// <summary>Kick off async WebView2 init the first time the module is shown.</summary>
    public async void EnsureInit()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BurpManager", "WebView2");
            Directory.CreateDirectory(dataDir);
            _dataDir = dataDir;
            LoadMode();   // needs _dataDir; may rewrite _current before the first navigation
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.SourceChanged += (_, _) =>
            {
                _current = _web.Source?.ToString() ?? _current;
                _addr.Text = _current;
                _popWin?.SetTitle("WEB TOOLS  ·  " + _current);
            };

            // The suite authenticates with a server session (ASP.NET_SessionId, a *session*
            // cookie), which WebView2 drops when it shuts down. Persist it ourselves so the
            // login survives restarts for as long as the server session lives.
            await RestoreCookiesAsync();
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                await SaveCookiesAsync();
                if (!e.IsSuccess) OnNavigationFailed(e.WebErrorStatus);
            };
            if (FindForm() is { } f) f.FormClosing += async (_, _) => await SaveCookiesAsync();

            _ready = true;
            _hint.Visible = false;
            _web.Visible = true;
            _web.Source = new Uri(_current);
            RefreshTlsBadge();

            if (Environment.GetEnvironmentVariable("BM_POPOUT") == "1")
                BeginInvoke(() => TogglePopOut());
        }
        catch (Exception ex)
        {
            _hint.Text = "WebView2를 초기화할 수 없습니다.\n" +
                         "WebView2 런타임 설치 및 로컬 IIS(127.0.0.1) 구동을 확인하세요.\n\n" + ex.Message;
        }
    }

    // -------------------------------------------------------- cookie persistence

    private sealed class SavedCookie
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "/";
        public bool IsHttpOnly { get; set; }
        public bool IsSecure { get; set; }
    }

    /// <summary>Write the current cookies to disk (session cookies included).</summary>
    private async Task SaveCookiesAsync()
    {
        var file = CookieFile;
        if (file is null || _web.CoreWebView2 is null) return;
        try
        {
            var list = await _web.CoreWebView2.CookieManager.GetCookiesAsync(ActiveOrigin);
            var save = list
                .Select(c => new SavedCookie
                {
                    Name = c.Name, Value = c.Value, Domain = c.Domain,
                    Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    IsHttpOnly = c.IsHttpOnly, IsSecure = c.IsSecure,
                })
                .ToList();
            if (save.Count == 0) return;
            File.WriteAllText(file,
                System.Text.Json.JsonSerializer.Serialize(save), new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// Re-inject saved cookies with a real expiry so they outlive the browser process.
    /// Uses CDP Network.setCookie with a url (not a Domain attribute): the host here is an
    /// IP literal, for which domain-cookies are invalid and get rejected.
    /// </summary>
    private async Task RestoreCookiesAsync()
    {
        var file = CookieFile;
        if (file is null || !File.Exists(file) || _web.CoreWebView2 is null) return;
        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<SavedCookie>>(
                File.ReadAllText(file));
            if (saved is null) return;

            double expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            foreach (var s in saved)
            {
                var p = new Dictionary<string, object>
                {
                    ["name"] = s.Name,
                    ["value"] = s.Value,
                    ["url"] = ActiveOrigin + (string.IsNullOrEmpty(s.Path) ? "/" : s.Path),
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["httpOnly"] = s.IsHttpOnly,
                    ["secure"] = s.IsSecure,
                    ["expires"] = expires,   // session cookie → persistent
                };
                await _web.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    "Network.setCookie", System.Text.Json.JsonSerializer.Serialize(p));
            }
        }
        catch { }
    }

    private void Navigate(string url)
    {
        _current = url;
        _addr.Text = url;
        if (_ready) _web.Source = new Uri(url);
    }

    // ------------------------------------------------------------ viewport width

    /// <summary>
    /// Swap the detached window between a phone-width and the desktop width it had before.
    /// MinimumSize has to move with it — the window's 560px floor would otherwise clamp the
    /// mobile width and the layout would never cross its breakpoint.
    /// </summary>
    private void ToggleViewport(ThemedWindow win)
    {
        try
        {
            if (win.WindowState != FormWindowState.Normal) win.WindowState = FormWindowState.Normal;

            // Width, not ClientSize: WM_NCCALCSIZE makes client == window, but WinForms still adds
            // the 14px frame it thinks WS_THICKFRAME needs — via ClientSize the window grew by
            // 14px on every round trip, and the height drifted too.
            if (!_popMobile)
            {
                _popDesktopWidth = win.Width;
                _popMobile = true;
                win.MinimumSize = new Size(MobileWidth, win.MinimumSize.Height);
                win.Width = MobileWidth;
            }
            else
            {
                _popMobile = false;
                win.MinimumSize = new Size(560, win.MinimumSize.Height);
                win.Width = Math.Max(560, _popDesktopWidth);
            }
        }
        catch { }
    }

    /// <summary>Phone or monitor outline, drawn to fit <paramref name="box"/>.</summary>
    private static void DeviceIcon(Graphics g, Rectangle box, Color color, bool phone)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f);
        int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;

        if (phone)
        {
            var r = new Rectangle(cx - 5, cy - 8, 10, 16);
            using var path = Theme.RoundRect(r, 2);
            g.DrawPath(pen, path);
            g.DrawLine(pen, cx - 2, r.Bottom - 3, cx + 2, r.Bottom - 3);   // home bar
        }
        else
        {
            var r = new Rectangle(cx - 8, cy - 7, 16, 11);
            g.DrawRectangle(pen, r);
            g.DrawLine(pen, cx, r.Bottom, cx, r.Bottom + 3);               // stand
            g.DrawLine(pen, cx - 4, r.Bottom + 4, cx + 4, r.Bottom + 4);   // base
        }
    }

    // ------------------------------------------------------------ host mode (IP ⇄ domain)

    private sealed class ModeState
    {
        public bool UseDomain { get; set; }
        public string Domain { get; set; } = DefaultDomainOrigin;
    }

    /// <summary>
    /// The domain lives in the state file rather than only in code, so it can be repointed
    /// (or switched to https) without a rebuild.
    /// </summary>
    private void LoadMode()
    {
        if (_dataDir is null) return;
        _modeFile = Path.Combine(_dataDir, "hostmode.json");
        try
        {
            if (File.Exists(_modeFile))
            {
                var st = System.Text.Json.JsonSerializer.Deserialize<ModeState>(File.ReadAllText(_modeFile));
                if (st != null)
                {
                    _useDomain = st.UseDomain;
                    if (!string.IsNullOrWhiteSpace(st.Domain)) _domainOrigin = st.Domain.TrimEnd('/');

                    // Upgrade the old http default in place. A domain the user changed themselves
                    // is left alone — only the value we used to ship gets rewritten.
                    if (string.Equals(_domainOrigin, LegacyDomainOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        _domainOrigin = DefaultDomainOrigin;
                        SaveMode();
                    }
                }
            }
            else SaveMode();
        }
        catch { }

        // BM_HOSTMODE=ip|domain — forces the mode so the switch can be captured for verification.
        var force = Environment.GetEnvironmentVariable("BM_HOSTMODE");
        if (force == "domain") _useDomain = true;
        else if (force == "ip") _useDomain = false;

        _current = Rehost(_current, ActiveOrigin);
        _addr.Text = _current;
        UpdateModeButton();
    }

    private void SaveMode()
    {
        if (_modeFile is null) return;
        try
        {
            File.WriteAllText(_modeFile, System.Text.Json.JsonSerializer.Serialize(
                new ModeState { UseDomain = _useDomain, Domain = _domainOrigin }),
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>Swap the origin of <paramref name="url"/>, keeping path and query.</summary>
    private static string Rehost(string url, string origin)
    {
        try
        {
            var u = new Uri(url);
            return origin.TrimEnd('/') + u.PathAndQuery;
        }
        catch { return origin.TrimEnd('/') + StartPath; }
    }

    private void UpdateModeButton()
    {
        var from = new Uri(ActiveOrigin).Host;
        var to = new Uri(_useDomain ? IpOrigin : _domainOrigin).Host;
        var tip = $"현재: {from}   →   클릭 시: {to}";

        if (_modeBtn != null)
        {
            // Label the destination, not the current state — a toggle that names where it takes you.
            _modeBtn.Text = _useDomain ? "⇄ 내부 IP" : "⇄ 도메인";
            _modeTip.SetToolTip(_modeBtn, tip);
        }
        if (_popModeBtn != null) _modeTip.SetToolTip(_popModeBtn, tip);
    }

    /// <summary>
    /// Flip between the loopback and the domain binding. Cookies are flushed for the origin we
    /// are leaving and restored for the one we arrive at, since each host holds its own session.
    /// </summary>
    private async void ToggleHostMode()
    {
        await SaveCookiesAsync();          // still on the old origin
        _useDomain = !_useDomain;
        SaveMode();
        UpdateModeButton();

        // Cookies first: the very first request to the new origin should already carry its session.
        await RestoreCookiesAsync();

        // Then navigate — and nothing after it may touch the WebView. Calling Reload() here
        // cancelled the pending navigation, the old document came back, and SourceChanged put the
        // old URL back in the label: state flipped but the page never moved.
        Navigate(Rehost(_current, ActiveOrigin));
        _popWin?.SetTitle("WEB TOOLS  ·  " + _current);
        RefreshTlsBadge();
    }

    // ------------------------------------------------------------ certificate expiry watch

    /// <summary>
    /// Show how long the active origin's certificate has left. Let's Encrypt issues 90-day certs,
    /// so an unattended renewal failure otherwise only shows up as a broken page months later.
    /// </summary>
    private async void RefreshTlsBadge()
    {
        if (!_useDomain)
        {
            _tls.Visible = false;   // loopback is plain http — nothing to report
            return;
        }

        _tls.Visible = true;
        _tls.ForeColor = Theme.Muted;
        _tls.Text = "TLS  확인중…";

        var info = await TlsProbe.ForAsync(_domainOrigin);
        if (info is null) { _tls.Visible = false; return; }

        if (info.Error != null && info.NotAfter == default)
        {
            _tls.ForeColor = Theme.Danger;
            _tls.Text = "TLS  오류";
            _modeTip.SetToolTip(_tls, info.Error);
            return;
        }

        int d = info.DaysLeft;
        _tls.Text = d < 0 ? "TLS  만료됨" : $"TLS  D-{d}";
        _tls.ForeColor =
            d < 0 || !info.ChainOk ? Theme.Danger :
            d <= 7 ? Theme.Danger :
            d <= 30 ? Theme.Warn : Theme.Muted;
        var nl = Environment.NewLine;
        _modeTip.SetToolTip(_tls,
            info.Subject + nl +
            "발급자: " + info.Issuer + nl +
            "만료: " + info.NotAfter.ToString("yyyy-MM-dd HH:mm") +
            (info.ChainOk ? "" : nl + "⚠ " + info.Error));
    }

    /// <summary>
    /// Domain mode is the fragile one — its certificate expires and its DNS can fail. When the
    /// navigation dies for one of those reasons, drop back to loopback rather than leaving a
    /// blank pane, and say why.
    /// </summary>
    private void OnNavigationFailed(CoreWebView2WebErrorStatus status)
    {
        if (!_useDomain) return;

        bool tlsOrDns = status is CoreWebView2WebErrorStatus.CertificateExpired
                              or CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
                              or CoreWebView2WebErrorStatus.CertificateRevoked
                              or CoreWebView2WebErrorStatus.CertificateIsInvalid
                              or CoreWebView2WebErrorStatus.ServerUnreachable
                              or CoreWebView2WebErrorStatus.HostNameNotResolved
                              or CoreWebView2WebErrorStatus.Timeout;
        if (!tlsOrDns) return;

        _useDomain = false;
        SaveMode();
        UpdateModeButton();
        RefreshTlsBadge();
        Navigate(Rehost(_current, ActiveOrigin));
        _popWin?.SetTitle("WEB TOOLS  ·  " + _current);
        SetStatusHint($"도메인 접속 실패({status}) — 내부 IP로 전환했습니다.");
    }

    private void SetStatusHint(string msg)
    {
        _addr.ForeColor = Theme.Warn;
        _addr.Text = msg;
        // Put the address back once the fallback navigation reports in.
        BeginInvoke(new Action(async () =>
        {
            await Task.Delay(4000);
            _addr.ForeColor = Theme.Muted;
            _addr.Text = _current;
        }));
    }

}

/// <summary>
/// Reads the TLS certificate an origin actually presents. Used to surface expiry in the UI:
/// the suite's Let's Encrypt cert lives 90 days, so a silent lapse breaks the domain mode.
/// The validation callback always returns true — this probes and reports, it never gates.
/// </summary>
internal static class TlsProbe
{
    internal sealed record Info(bool ChainOk, string Subject, string Issuer, DateTime NotAfter, string? Error)
    {
        public int DaysLeft => (int)Math.Floor((NotAfter - DateTime.Now).TotalDays);
    }

    public static async Task<Info?> ForAsync(string origin, int timeoutMs = 4000)
    {
        Uri u;
        try { u = new Uri(origin); } catch { return null; }
        if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(u.Host, u.IsDefaultPort ? 443 : u.Port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
                return new Info(false, "", "", default, "연결 시간 초과");
            await connect;

            System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
            bool chainOk = false;
            using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, c, _, errors) =>
            {
                if (c != null) cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(c);
                chainOk = errors == System.Net.Security.SslPolicyErrors.None;
                return true;
            });
            await ssl.AuthenticateAsClientAsync(u.Host);

            if (cert is null) return new Info(false, "", "", default, "인증서를 받지 못했습니다");
            var t = System.Security.Cryptography.X509Certificates.X509NameType.SimpleName;
            return new Info(chainOk,
                cert.GetNameInfo(t, false), cert.GetNameInfo(t, true),
                cert.NotAfter, chainOk ? null : "체인 검증 실패");
        }
        catch (Exception ex) { return new Info(false, "", "", default, ex.Message); }
    }
}
