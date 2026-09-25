using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace BurpManager;

/// <summary>
/// SplitContainer without the dotted focus rectangle Windows paints over the splitter band.
/// The splitter is still draggable; it just never takes keyboard focus.
/// </summary>
internal sealed class DarkSplit : SplitContainer
{
    public DarkSplit()
    {
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Deliberately not calling base: its only job here is the focus rectangle.
        using var b = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(b, SplitterRectangle);
    }
}

/// <summary>
/// Code surface with lightweight syntax colouring. A RichTextBox rather than a TextBox because
/// a plain edit control can only carry a single colour for its whole contents.
/// </summary>
internal sealed class CodeBox : RichTextBox
{
    internal enum Lang { None, Http, Python }

    private static readonly Font Face = Theme.Code(9.5f);
    private static readonly Font FaceBold = Theme.Code(9.5f, FontStyle.Bold);

    private readonly Lang _lang;
    private bool _busy;

    public CodeBox(Lang lang, bool readOnly = false, bool wrap = false)
    {
        _lang = lang;
        WordWrap = wrap;
        ReadOnly = readOnly;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Sunken;
        ForeColor = Theme.Text;
        Font = Face;
        DetectUrls = false;
        // Forced bars keep the scrollable metrics honest; SlimScroll hides the native ones.
        ScrollBars = RichTextBoxScrollBars.ForcedBoth;
        SlimScroll.ForTextBox(this);
        TextChanged += (_, _) => Highlight();
        // Text set before the handle exists is cached and streamed in *after* HandleCreated is
        // raised, so colouring inline here would paint an empty buffer. Queue it instead.
        HandleCreated += (_, _) => BeginInvoke(new Action(Highlight));
    }

    // ------------------------------------------------------------------ token tables

    private static readonly Regex PyRx = new(
        @"(?<cm>\#[^\n]*)" +
        @"|(?<st>'''[\s\S]*?'''|""""""[\s\S]*?""""""|'(?:\\.|[^'\\\n])*'|""(?:\\.|[^""\\\n])*"")" +
        @"|(?<kw>\b(?:and|as|assert|async|await|break|class|continue|def|del|elif|else|except|" +
        @"False|finally|for|from|global|if|import|in|is|lambda|None|nonlocal|not|or|pass|print|" +
        @"raise|return|True|try|while|with|yield)\b)" +
        @"|(?<nu>\b\d+(?:\.\d+)?\b)" +
        @"|(?<fn>\b[A-Za-z_]\w*(?=\())",
        RegexOptions.Compiled);

    private static readonly Regex HttpRx = new(
        @"^(?<mth>[A-Z]{3,10})[ \t]+(?<pth>\S+)[ \t]+(?<ver>HTTP/[\d.]+)[ \t]*$" +
        @"|^(?<hn>[A-Za-z0-9\-]+)(?=:)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>§payload markers§ win over everything else — they are the point of the pane.</summary>
    private static readonly Regex MarkerRx = new(@"§[^§\n]*§", RegexOptions.Compiled);

    private static IEnumerable<(int at, int len, Color color, bool bold)> Tokens(string text, Lang lang)
    {
        if (lang == Lang.Python)
        {
            foreach (Match m in PyRx.Matches(text))
            {
                if (m.Groups["cm"].Success) yield return (m.Index, m.Length, Theme.SynComment, false);
                else if (m.Groups["st"].Success) yield return (m.Index, m.Length, Theme.SynString, false);
                else if (m.Groups["kw"].Success) yield return (m.Index, m.Length, Theme.SynKeyword, false);
                else if (m.Groups["nu"].Success) yield return (m.Index, m.Length, Theme.SynNumber, false);
                else if (m.Groups["fn"].Success) yield return (m.Index, m.Length, Theme.SynFunc, false);
            }
        }
        else if (lang == Lang.Http)
        {
            foreach (Match m in HttpRx.Matches(text))
            {
                var g = m.Groups["mth"];
                if (g.Success)
                {
                    yield return (g.Index, g.Length, Theme.SynKeyword, true);
                    var v = m.Groups["ver"];
                    yield return (v.Index, v.Length, Theme.SynMuted, false);
                }
                else
                {
                    var h = m.Groups["hn"];
                    yield return (h.Index, h.Length, Theme.SynFunc, false);
                }
            }
        }

        foreach (Match m in MarkerRx.Matches(text))
            yield return (m.Index, m.Length, Theme.SynMarker, true);
    }

    // ------------------------------------------------------------------ painting

    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr h, int msg, int w, int l);

    private int FirstVisibleLine() =>
        IsHandleCreated ? SendMessage(Handle, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, 0, 0) : 0;

    private void ScrollToLine(int line)
    {
        if (!IsHandleCreated) return;
        SendMessage(Handle, 0x00B6 /*EM_LINESCROLL*/, 0, line - FirstVisibleLine());
    }

    private void Redraw(bool on)
    {
        if (!IsHandleCreated) return;
        SendMessage(Handle, 0x000B /*WM_SETREDRAW*/, on ? 1 : 0, 0);
    }

    /// <summary>
    /// Recolour the whole buffer. Selection and scroll position are captured up front and put
    /// back afterwards, so this is invisible while typing.
    /// </summary>
    public void Highlight()
    {
        if (_busy || _lang == Lang.None || !IsHandleCreated) return;
        _busy = true;
        int selAt = SelectionStart, selLen = SelectionLength, firstLine = FirstVisibleLine();
        Redraw(false);
        try
        {
            string text = Text;
            Select(0, text.Length);
            SelectionColor = ForeColor;
            SelectionFont = Face;

            foreach (var (at, len, color, bold) in Tokens(text, _lang))
            {
                if (at < 0 || len <= 0 || at + len > text.Length) continue;
                Select(at, len);
                SelectionColor = color;
                if (bold) SelectionFont = FaceBold;
            }

            Select(selAt, selLen);
        }
        catch { }
        finally
        {
            ScrollToLine(firstLine);
            Redraw(true);
            Invalidate();
            _busy = false;
        }
    }
}
