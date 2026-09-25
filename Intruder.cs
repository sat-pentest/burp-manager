using System.Text;
using System.Text.RegularExpressions;

namespace BurpManager;

// ============================================================ INTRUDER
// Native port of the intruder.aspx generator: raw request + §markers§ +
// attack type + per-position payload sets → standalone Python `requests` script.
internal sealed class IntruderModule : Panel
{
    private readonly CodeBox _raw = new(CodeBox.Lang.Http);
    private readonly TextBox _url = new() { Text = "http://TARGET" };
    private readonly TextBox _timeout = new() { Text = "10" };
    private readonly TextBox _delay = new() { Text = "0" };
    private readonly TextBox _enc = new();
    private readonly DarkCheckBox _verify = new();
    private readonly DarkCheckBox _redir = new();
    private readonly DarkRadio _sniper = new() { Text = "Sniper", Checked = true };
    private readonly DarkRadio _pitchfork = new() { Text = "Pitchfork" };
    private readonly DarkRadio _cluster = new() { Text = "Cluster bomb" };
    private readonly FlowLayoutPanel _payloads = new();
    private readonly CodeBox _out = new(CodeBox.Lang.Python, readOnly: true);
    private readonly List<PayloadEditor> _editors = new();
    private Control _plHeader = null!;
    private SplitContainer? _right;
    private Control? _outBody;
    private bool _plCollapsed, _genCollapsed;
    private string[] _positions = Array.Empty<string>();

    public IntruderModule(bool embedded = false)
    {
        BackColor = Theme.Bg;
        Padding = new Padding(12);

        // options bar (two rows) — delineated by its surface colour, not a border line
        var opt = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Theme.Panel, Padding = new Padding(12, 10, 12, 10) };
        foreach (var t in new[] { _url, _timeout, _delay, _enc }) Theme.StyleInput(t);
        _verify.Text = "verify SSL"; _verify.AutoSize = false; _verify.Size = new Size(108, 22);
        _redir.Text = "follow redirect"; _redir.AutoSize = false; _redir.Size = new Size(150, 22);
        foreach (var r in new[] { _sniper, _pitchfork, _cluster })
        { r.ForeColor = Theme.Text; r.AutoSize = false; r.Size = new Size(28 + r.Text.Length * 8, 24); r.Font = Theme.UI; r.CheckedChanged += (_, _) => RebuildPayloads(); }

        // Fields are chained off the previous one's right edge: a guessed label width
        // (chars × 8) used to run the box under the next caption.
        const int FrameGap = 3;   // the input frame is drawn 1px outside its bounds
        int F(string cap, Control c, int x, int y, int w)
        {
            var l = Tk.Cap(cap); l.Location = new Point(x, y + 3);
            c.Location = new Point(x + TextRenderer.MeasureText(cap, l.Font).Width + 8, y);
            c.Width = w; c.Height = 24;
            opt.Controls.Add(l); opt.Controls.Add(c);
            return c.Right + FrameGap;
        }
        int at = F("Target", _url, 0, 4, 270);
        at = F("timeout", _timeout, at + 26, 4, 44);
        at = F("delay(ms)", _delay, at + 26, 4, 50);
        _verify.Location = new Point(at + 30, 6);
        _redir.Location = new Point(_verify.Right + 8, 6);
        opt.Controls.Add(_verify); opt.Controls.Add(_redir);
        var atk = new FlowLayoutPanel { Location = new Point(0, 40), Size = new Size(340, 28), BackColor = Theme.Panel };
        atk.Controls.AddRange(new Control[] { _sniper, _pitchfork, _cluster });
        opt.Controls.Add(atk);
        F("encode chars", _enc, 372, 44, 180);

        // action bar
        var act = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        act.Controls.Add(Tk.Btn("Add §", 60, InsertMarker));
        act.Controls.Add(Tk.Btn("Clear §", 66, ClearMarkers));
        act.Controls.Add(Tk.Btn("Sample", 66, LoadSample));
        act.Controls.Add(new Label { Width = 16 });
        act.Controls.Add(Tk.Btn("GENERATE ▸ Python", 150, Generate, accent: true));
        act.Controls.Add(Tk.Btn("COPY", 60, () => Tk.Copy(_out.Text)));
        act.Controls.Add(Tk.Btn("SAVE .py", 74, () => Tk.SaveAs(_out.Text, "Python|*.py|All|*.*", "py")));

        // body: raw (left) | payloads+output (right)
        var main = new DarkSplit { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Theme.Bg, SplitterWidth = 8 };
        main.Panel1.BackColor = main.Panel2.BackColor = Theme.Bg;
        _raw.Dock = DockStyle.Fill;
        _raw.TextChanged += (_, _) => RebuildPayloads();
        var rawWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        rawWrap.Controls.Add(Tk.Inset(_raw, 10));
        rawWrap.Controls.Add(Tk.Section("RAW REQUEST   ·   §마커§로 위치 지정"));
        main.Panel1.Controls.Add(rawWrap);

        var right = new DarkSplit { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Bg, SplitterWidth = 8 };
        right.Panel1.BackColor = right.Panel2.BackColor = Theme.Bg;
        _payloads.Dock = DockStyle.Fill; _payloads.FlowDirection = FlowDirection.TopDown; _payloads.WrapContents = false; _payloads.AutoScroll = true; _payloads.BackColor = Theme.Bg;
        _payloads.ClientSizeChanged += (_, _) => SizeEditors();
        SlimScroll.ForAutoScroll(_payloads);   // purple custom scrollbar
        var plWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        plWrap.Controls.Add(_payloads);
        _plHeader = Tk.Section("PAYLOADS", c => { _plCollapsed = c; LayoutRight(); });
        plWrap.Controls.Add(_plHeader);
        _out.Dock = DockStyle.Fill;
        var outWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        _outBody = Tk.Inset(_out, 10);
        outWrap.Controls.Add(_outBody);
        outWrap.Controls.Add(Tk.Section("GENERATED PYTHON", c => { _genCollapsed = c; LayoutRight(); }));
        right.Panel1.Controls.Add(plWrap);
        right.Panel2.Controls.Add(outWrap);
        main.Panel2.Controls.Add(right);
        _right = right;

        Controls.Add(main);
        Controls.Add(act);
        Controls.Add(opt);
        if (!embedded) Controls.Add(Tk.Head("INTRUDER  ·  Python 공격 스크립트 생성"));

        main.SizeChanged += (_, _) => { try { if (main.Width > 300) main.SplitterDistance = (int)(main.Width * 0.58); } catch { } };
        right.SizeChanged += (_, _) => LayoutRight();
        HandleCreated += (_, _) =>
        {
            try { main.SplitterDistance = (int)(main.Width * 0.58); } catch { }
            // RichTextBox.Text set before the handle exists does not raise TextChanged, so the
            // sample loaded in the constructor never reached RebuildPayloads. Drive it once here.
            RebuildPayloads();
            Generate();
            // BM_COLLAPSE=pl|gen|both — lets the collapse states be captured for verification.
            switch (Environment.GetEnvironmentVariable("BM_COLLAPSE"))
            {
                case "pl": _plCollapsed = true; break;
                case "gen": _genCollapsed = true; break;
                case "both": _plCollapsed = _genCollapsed = true; break;
            }
            LayoutRight();
            SizeEditors();
            // Layout shifts during construction can leave the boxes scrolled — pin them once ready.
            BeginInvoke(() => { Tk.ScrollTop(_raw); Tk.ScrollTop(_out); });
        };
        LoadSample();
        Generate();
    }

    /// <summary>
    /// Size PAYLOADS / GENERATED PYTHON. Collapsing hides the pane's body rather than only
    /// shrinking its half of the splitter: the splitter alone left the payload combo poking out
    /// under the header, and it cannot starve both panes at once, which is why collapsing one
    /// used to force the other back open.
    /// </summary>
    private void LayoutRight()
    {
        if (_right is null) return;
        _payloads.Visible = !_plCollapsed;
        if (_outBody != null) _outBody.Visible = !_genCollapsed;
        if (_right.Height < 120) return;

        const int HeaderOnly = 30;   // just the section strip stays visible
        try
        {
            int free = _right.Height - _right.SplitterWidth;
            int d = _plCollapsed ? HeaderOnly
                  : _genCollapsed ? Math.Max(HeaderOnly, free - HeaderOnly)
                  : free / 2;
            _right.SplitterDistance = Math.Max(_right.Panel1MinSize, Math.Min(d, free - _right.Panel2MinSize));
        }
        catch { }
        SizeEditors();
    }

    private void SizeEditors()
    {
        int w = Math.Max(120, _payloads.ClientSize.Width - 4);
        foreach (var e in _editors) e.Width = w;
        // A single set fills the pane (matches GENERATED PYTHON below); multiple sets stack and scroll.
        if (_editors.Count == 1) _editors[0].Height = Math.Max(120, _payloads.ClientSize.Height - 4);
        else foreach (var e in _editors) e.Height = 150;
    }

    // -------- markers --------
    private void InsertMarker()
    {
        int s = _raw.SelectionStart, len = _raw.SelectionLength;
        string v = _raw.Text;
        string sel = len > 0 ? v.Substring(s, len) : "value";
        _raw.Text = v.Substring(0, s) + "§" + sel + "§" + v.Substring(s + len);
        _raw.SelectionStart = s; _raw.SelectionLength = sel.Length + 2;
        _raw.Focus();
    }
    private void ClearMarkers() => _raw.Text = _raw.Text.Replace("§", "");
    private void LoadSample() => _raw.Text =
        "POST /login HTTP/1.1\r\nHost: target\r\nContent-Type: application/x-www-form-urlencoded\r\n\r\nuser=§user§&pass=§pass§";

    // -------- payload editors --------
    private void RebuildPayloads()
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(_raw.Text, "§(.*?)§"))
        { var n = m.Groups[1].Value; if (!found.Contains(n)) found.Add(n); }
        _positions = found.ToArray();

        // preserve prior values by label
        var prev = _editors.ToDictionary(e => e.Label, e => e);
        _payloads.Controls.Clear();
        _editors.Clear();

        if (_positions.Length == 0)
        {
            _payloads.Controls.Add(new Label { Text = "§마커§ 를 추가하면 페이로드 셋이 여기 표시됩니다.", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(4, 8, 0, 0) });
            return;
        }

        bool sniper = _sniper.Checked;
        var labels = sniper ? new[] { "payloads (모든 위치 순차)" } : _positions;
        foreach (var lab in labels)
        {
            var ed = prev.TryGetValue(lab, out var old) ? old : new PayloadEditor(lab, showLabel: !sniper);
            _editors.Add(ed);
            _payloads.Controls.Add(ed);
        }
        SetPayloadHeader(sniper
            ? $"PAYLOADS   ·   Sniper — {_positions.Length}개 위치에 순차 적용"
            : $"PAYLOADS   ·   위치별 {_positions.Length}개 셋");
        SizeEditors();
    }

    private void SetPayloadHeader(string text)
    {
        foreach (Control c in _plHeader.Controls) if (c is Label l) { l.Text = text; break; }
    }

    // -------- generation --------
    private static string PyStr(string s) =>
        "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n") + "'";
    private static string PyFStr(string s) =>
        "f'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n") + "'";
    private static string RegEsc(string s) => Regex.Escape(s);

    private void Generate()
    {
        if (_positions.Length == 0) { Tk.SetText(_out, "# ← Raw Request에 §마커§로 위치를 지정하세요."); return; }

        string raw = _raw.Text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = raw.Split('\n');
        var reqParts = (lines.Length > 0 ? lines[0] : "GET / HTTP/1.1").Split(' ');
        string method = (reqParts.Length > 0 ? reqParts[0] : "GET").ToUpperInvariant();
        string path = reqParts.Length > 1 ? reqParts[1] : "/";

        var headers = new List<(string k, string v)>();
        var bodyLines = new List<string>(); bool inBody = false;
        for (int i = 1; i < lines.Length; i++)
        {
            if (!inBody && lines[i].Trim() == "") { inBody = true; continue; }
            if (inBody) bodyLines.Add(lines[i]);
            else { int ci = lines[i].IndexOf(':'); if (ci > -1) { var k = lines[i][..ci].Trim(); var v = lines[i][(ci + 1)..].Trim(); if (!k.Equals("content-length", StringComparison.OrdinalIgnoreCase)) headers.Add((k, v)); } }
        }
        string bodyRaw = string.Join("\n", bodyLines).Trim();

        bool sniper = _sniper.Checked, pf = _pitchfork.Checked, cb = _cluster.Checked;
        int timeout = int.TryParse(_timeout.Text, out var to) ? to : 10;
        int delayMs = int.TryParse(_delay.Text, out var dm) ? dm : 0;
        bool verify = _verify.Checked, redir = _redir.Checked;
        string encChars = _enc.Text;
        bool useEnc = encChars.Length > 0;
        bool useDelay = delayMs > 0;

        var L = new List<string> { "import requests", "import warnings" };
        if (useDelay) L.Add("import time");
        L.Add("warnings.filterwarnings('ignore')");
        L.Add("");
        L.Add("TARGET = " + PyStr((_url.Text.Trim().Length > 0 ? _url.Text.Trim() : "http://TARGET")));
        L.Add("");
        if (useEnc)
        {
            L.Add("_ENC_CHARS = set(" + PyStr(encChars) + ")");
            L.Add("def _enc(p):");
            L.Add("    return ''.join('%{:02X}'.format(ord(c)) if c in _ENC_CHARS else c for c in str(p))");
            L.Add("");
        }

        // payload lists
        if (sniper)
        { L.Add("# Sniper: 각 위치에 순차 적용"); L.AddRange(_editors[0].Gen("payloads")); L.Add(""); }
        else
            for (int i = 0; i < _positions.Length; i++)
            { L.Add($"# §{_positions[i]}§"); L.AddRange(EditorFor(i).Gen("payloads_" + (i + 1))); L.Add(""); }

        // headers
        L.Add("headers = {");
        foreach (var (k, v) in headers) L.Add("    " + PyStr(k) + ": " + PyStr(v) + ",");
        L.Add("}");
        L.Add("");

        string pathTmpl = path.Replace("{", "{{").Replace("}", "}}");
        string bodyTmpl = bodyRaw.Replace("{", "{{").Replace("}", "}}");

        string ReqCall(string ind, bool hasBody)
        {
            var sb = new StringBuilder();
            sb.Append(ind).Append("resp = requests.request(").Append(PyStr(method)).Append(", url, headers=headers");
            if (hasBody) sb.Append(", data=data.encode('utf-8')");
            sb.Append($", timeout={timeout}, verify={(verify ? "True" : "False")}, allow_redirects={(redir ? "True" : "False")})");
            return sb.ToString();
        }

        if (sniper)
        {
            var pt = path.Replace("{", "{{").Replace("}", "}}");
            var bt = bodyRaw.Replace("{", "{{").Replace("}", "}}");
            for (int i = 0; i < _positions.Length; i++)
            { pt = Regex.Replace(pt, "§" + RegEsc(_positions[i]) + "§", "{pos_vals[" + i + "]}"); bt = Regex.Replace(bt, "§" + RegEsc(_positions[i]) + "§", "{pos_vals[" + i + "]}"); }
            L.Add("positions = " + PyList(_positions));
            L.Add("pos_count = len(positions)");
            L.Add("for pos_idx in range(pos_count):");
            L.Add("    print(f\"\\n[*] 위치 {pos_idx+1}/{pos_count}: {positions[pos_idx]}\")");
            L.Add("    for i, payload in enumerate(payloads):");
            L.Add("        pos_vals = [''] * pos_count");
            L.Add("        pos_vals[pos_idx] = " + (useEnc ? "_enc(payload)" : "payload"));
            L.Add("        url = TARGET + " + PyFStr(pt));
            if (bt.Length > 0) L.Add("        data = " + PyFStr(bt));
            L.Add("        try:");
            L.Add(ReqCall("            ", bt.Length > 0));
            L.Add("            print(f\"  [{i}] {payload!r:20} -> {resp.status_code}  {len(resp.content)} bytes\")");
            L.Add("        except Exception as ex:");
            L.Add("            print(f\"  [ERR] {payload!r}: {ex}\")");
            if (useDelay) L.Add($"        time.sleep({(delayMs / 1000.0):0.###})");
        }
        else
        {
            for (int i = 0; i < _positions.Length; i++)
            { pathTmpl = Regex.Replace(pathTmpl, "§" + RegEsc(_positions[i]) + "§", "{pl" + (i + 1) + "}"); bodyTmpl = Regex.Replace(bodyTmpl, "§" + RegEsc(_positions[i]) + "§", "{pl" + (i + 1) + "}"); }
            string ind;
            if (pf)
            {
                L.Add("for combo in zip(" + string.Join(", ", _positions.Select((_, i) => "payloads_" + (i + 1))) + "):");
                for (int i = 0; i < _positions.Length; i++)
                    L.Add("    pl" + (i + 1) + " = " + (useEnc ? "_enc(combo[" + i + "])" : "combo[" + i + "]"));
                ind = "    ";
            }
            else // cluster bomb: nested loops
            {
                ind = "";
                for (int i = 0; i < _positions.Length; i++)
                { L.Add(ind + "for _p" + (i + 1) + " in payloads_" + (i + 1) + ":"); ind += "    "; L.Add(ind + "pl" + (i + 1) + " = " + (useEnc ? "_enc(_p" + (i + 1) + ")" : "_p" + (i + 1))); }
            }
            L.Add(ind + "url = TARGET + " + PyFStr(pathTmpl));
            if (bodyTmpl.Length > 0) L.Add(ind + "data = " + PyFStr(bodyTmpl));
            string tag = "(" + string.Join(", ", _positions.Select((_, i) => "pl" + (i + 1))) + ")";
            L.Add(ind + "try:");
            L.Add(ReqCall(ind + "    ", bodyTmpl.Length > 0));
            L.Add(ind + "    print(f\"  {" + tag + "!r} -> {resp.status_code}  {len(resp.content)} bytes\")");
            L.Add(ind + "except Exception as ex:");
            L.Add(ind + "    print(f\"  [ERR] {" + tag + "!r}: {ex}\")");
            if (useDelay) L.Add(ind + $"time.sleep({(delayMs / 1000.0):0.###})");
        }

        _out.Text = string.Join("\r\n", L);
    }

    private PayloadEditor EditorFor(int posIndex)
    {
        // pitchfork/clusterbomb: editors are 1:1 with positions in order
        return posIndex < _editors.Count ? _editors[posIndex] : _editors[^1];
    }

    private static string PyList(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(PyStr)) + "]";

    // ---------------------------------------------------- per-position editor
    private sealed class PayloadEditor : Panel
    {
        public string Label { get; }
        private readonly ThemeCombo _type = new();
        private readonly TextBox _list = Tk.Mono();
        private readonly Panel _numRow = new();
        private readonly TextBox _from = new() { Text = "1" }, _to = new() { Text = "100" }, _step = new() { Text = "1" }, _digits = new() { Text = "0" };

        /// <param name="showLabel">false for the single Sniper set — the PAYLOADS section header already names it.</param>
        public PayloadEditor(string label, bool showLabel = true)
        {
            Label = label;
            Width = 300; Height = 150; Margin = new Padding(0, 0, 0, 10);
            BackColor = Theme.Bg;

            _type.AddItems("List", "Numbers");
            _type.SelectedIndexChanged += (_, _) => Sync();

            _list.Dock = DockStyle.Fill;
            _list.ScrollBars = ScrollBars.Vertical;   // line-based payloads: no horizontal bar
            _list.WordWrap = true;
            _list.Text = "admin\r\nroot\r\ntest";

            _numRow.Dock = DockStyle.Fill; _numRow.BackColor = Theme.Sunken; _numRow.Visible = false;
            foreach (var t in new[] { _from, _to, _step, _digits }) Theme.StyleInput(t);
            void NF(string cap, Control c, int x)
            { var l = Tk.Cap(cap); l.Location = new Point(x + 8, 10); c.Location = new Point(x + 8, 30); c.Width = 62; c.Height = 24; _numRow.Controls.Add(l); _numRow.Controls.Add(c); }
            NF("from", _from, 0); NF("to", _to, 72); NF("step", _step, 144); NF("min-digits", _digits, 216);

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            host.Controls.Add(Tk.Inset(_list));
            host.Controls.Add(_numRow);

            // Control row: type combo on the left (aligned with the field below), optional §label after it.
            var row = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 6) };
            _type.Dock = DockStyle.Left; _type.Width = 118; _type.Height = 26;
            if (showLabel)
                row.Controls.Add(new Label
                {
                    Text = "§ " + label, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.UI,
                    TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), AutoEllipsis = true,
                });
            row.Controls.Add(_type);

            Controls.Add(host);
            Controls.Add(row);
        }

        private void Sync() { bool num = _type.SelectedIndex == 1; _numRow.Visible = num; _list.Visible = !num; }

        public List<string> Gen(string varName)
        {
            var L = new List<string>();
            if (_type.SelectedIndex == 1) // numbers / range
            {
                int s = int.TryParse(_from.Text, out var a) ? a : 0;
                int e = int.TryParse(_to.Text, out var b) ? b : 100;
                int st = int.TryParse(_step.Text, out var c) ? c : 1; if (st == 0) st = 1;
                int md = int.TryParse(_digits.Text, out var d) ? Math.Max(0, d) : 0;
                if (md > 0) L.Add(varName + " = [format(i, '0" + md + "d') for i in range(" + s + ", " + (e + 1) + ", " + st + ")]");
                else L.Add(varName + " = list(range(" + s + ", " + (e + 1) + ", " + st + "))");
            }
            else
            {
                var items = _list.Text.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (items.Count == 0) items = new List<string> { "admin", "root", "test" };
                L.Add(varName + " = [");
                foreach (var it in items) L.Add("    " + PyStr(it) + ",");
                L.Add("]");
            }
            return L;
        }
    }
}

// ============================================================ TOOLS host
// Native module hosting INTRUDER / ENCODE / WEBSHELL with a collapsible top tab bar.
internal sealed class ToolsPanel : Panel
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly Panel _bar = new() { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Bg };
    private readonly Panel _strip = new() { Dock = DockStyle.Top, Height = 16, BackColor = Theme.Rail, Visible = false, Cursor = Cursors.Hand };
    private readonly Dictionary<string, (Button btn, Control panel)> _tabs = new();
    private readonly Dictionary<string, string> _desc = new();
    private readonly Dictionary<string, NavIcon> _tabIcons = new();
    private readonly Label _sub = new();
    private string _active = "";

    public ToolsPanel()
    {
        BackColor = Theme.Bg;
        Padding = new Padding(12);

        var head = Tk.Head("TOOLS  ·  내장 도구");

        // The tab row is the only title for the active tool — no second header/divider inside it.
        var tools = new (string key, string desc, Control panel)[]
        {
            ("INTRUDER", "Python 공격 스크립트 생성", new IntruderModule(embedded: true)),
            ("ENCODE",   "인코딩 / 디코딩 / 해시",     new EncodeModule(embedded: true)),
            ("WEBSHELL", "웹셸 / 리버스 셸 생성",      new WebshellModule(embedded: true)),
        };
        foreach (var (key, desc, _) in tools) _desc[key] = desc;

        int x = 0;
        foreach (var (key, _, panel) in tools)
        {
            panel.Dock = DockStyle.Fill; panel.Visible = false; panel.Padding = new Padding(0);
            _content.Controls.Add(panel);
            var b = new Button
            {
                Text = key, Location = new Point(x, 7), Size = new Size(118, 28),
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0),
            };
            Theme.FlatButton(b);
            b.Click += (_, _) => Show(key);
            // Matching line icon inside the tab, left of the label.
            var ic = new NavIcon { IconName = key, Tint = Theme.Muted, Size = new Size(15, 15), Location = new Point(9, 6) };
            ic.Click += (_, _) => Show(key);
            b.Controls.Add(ic);
            _bar.Controls.Add(b);
            _tabs[key] = (b, panel);
            _tabIcons[key] = ic;
            x += 124;
        }

        var collapse = new Button { Text = "▲", Size = new Size(30, 28), Location = new Point(x + 8, 7) };
        Theme.FlatButton(collapse);
        collapse.Click += (_, _) => SetBar(false);
        _bar.Controls.Add(collapse);

        // Active tool's subtitle lives next to the tabs (replaces the removed inner header).
        _sub.AutoSize = true;
        _sub.ForeColor = Theme.Muted;
        _sub.Font = Theme.UI;
        _sub.Location = new Point(x + 50, 14);
        _bar.Controls.Add(_sub);

        _strip.Controls.Add(new Label { Text = "  ▼  상단 패널 펼치기", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Sans(8f), TextAlign = ContentAlignment.MiddleLeft });
        _strip.Click += (_, _) => SetBar(true);
        foreach (Control c in _strip.Controls) c.Click += (_, _) => SetBar(true);

        Controls.Add(_content);
        Controls.Add(_strip);
        Controls.Add(_bar);
        Controls.Add(head);

        var init = Environment.GetEnvironmentVariable("BM_TOOL");
        Show(!string.IsNullOrEmpty(init) && _tabs.ContainsKey(init) ? init : "INTRUDER");
    }

    private void SetBar(bool open) { _bar.Visible = open; _strip.Visible = !open; }

    private void Show(string key)
    {
        _active = key;
        _sub.Text = _desc.TryGetValue(key, out var d) ? "· " + d : "";
        foreach (var kv in _tabs)
        {
            kv.Value.panel.Visible = kv.Key == key;
            if (kv.Key == key) Theme.AccentButton(kv.Value.btn); else Theme.FlatButton(kv.Value.btn);
        }
        // Re-tint icons: active tab gets white to sit on the accent fill.
        foreach (var kv in _tabIcons)
        {
            var old = kv.Value;
            var parent = old.Parent;
            if (parent is null) continue;
            var tint = kv.Key == key ? Color.White : Theme.Muted;
            var fresh = new NavIcon { IconName = kv.Key, Tint = tint, Size = old.Size, Location = old.Location };
            fresh.Click += (_, _) => Show(kv.Key);
            parent.Controls.Remove(old);
            old.Dispose();
            parent.Controls.Add(fresh);
            _tabIcons[kv.Key] = fresh;
        }
    }
}
