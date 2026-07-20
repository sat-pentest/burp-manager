using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BurpManager;

/// <summary>Generates a multi-size .ico: a scope reticle in Burp orange on charcoal.</summary>
internal static class IconGen
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Run(string outPath) => WriteIco(outPath, Sizes.Select(Render).ToList());

    /// <summary>Build the .ico from a source PNG, recolored to the app's purple palette.</summary>
    public static void RunFromImage(string srcPath, string outPath)
    {
        using var src = new Bitmap(srcPath);
        using var recolored = Recolor(src);
        WriteIco(outPath, Sizes.Select(sz => PngResized(recolored, sz)).ToList());
    }

    private static void WriteIco(string outPath, List<byte[]> frames)
    {
        using var fs = File.Create(outPath);
        using var bw = new BinaryWriter(fs);
        bw.Write((short)0);             // reserved
        bw.Write((short)1);             // type: icon
        bw.Write((short)frames.Count);  // count

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = Sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s)); // width
            bw.Write((byte)(s >= 256 ? 0 : s)); // height
            bw.Write((byte)0);   // palette
            bw.Write((byte)0);   // reserved
            bw.Write((short)1);  // planes
            bw.Write((short)32); // bpp
            bw.Write(frames[i].Length);
            bw.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) bw.Write(f);
    }

    private static byte[] PngResized(Bitmap bmp, int sz)
    {
        using var dst = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(bmp, new Rectangle(0, 0, sz, sz));
        }
        using var ms = new MemoryStream();
        dst.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Gradient-map the source's luminance onto the app's purple ramp (alpha preserved).</summary>
    private static unsafe Bitmap Recolor(Bitmap src)
    {
        // dark → light purple ramp
        var stops = new (float p, Color c)[]
        {
            (0.00f, Color.FromArgb(24, 22, 42)),
            (0.30f, Color.FromArgb(46, 40, 96)),
            (0.55f, Color.FromArgb(108, 92, 240)),
            (0.78f, Color.FromArgb(170, 160, 246)),
            (1.00f, Color.FromArgb(238, 236, 252)),
        };
        var lut = new Color[256];
        for (int i = 0; i < 256; i++) lut[i] = RampColor(stops, i / 255f);

        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte* px = row + x * 4;            // BGRA
                    byte a = px[3];
                    if (a == 0) continue;
                    int lum = (px[2] * 299 + px[1] * 587 + px[0] * 114) / 1000; // R,G,B
                    var c = lut[lum];
                    px[0] = c.B; px[1] = c.G; px[2] = c.R; // keep alpha
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private static Color RampColor((float p, Color c)[] stops, float t)
    {
        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].p)
            {
                var a = stops[i - 1]; var b = stops[i];
                float k = (t - a.p) / Math.Max(1e-4f, b.p - a.p);
                return Lerp(a.c, b.c, k);
            }
        }
        return stops[^1].c;
    }

    // Kali-style palette
    private static readonly Color NavyTop = Color.FromArgb(41, 55, 72);
    private static readonly Color NavyBot = Color.FromArgb(20, 27, 37);
    private static readonly Color CreamHi = Color.FromArgb(237, 234, 226);
    private static readonly Color CreamLo = Color.FromArgb(212, 208, 197);
    private static readonly Color SilverHi = Color.FromArgb(214, 218, 224);
    private static readonly Color SilverMid = Color.FromArgb(150, 157, 166);
    private static readonly Color SilverLo = Color.FromArgb(92, 99, 108);

    private static byte[] Render(int s)
    {
        using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float m = s * 0.055f;
        var rect = new RectangleF(m, m, s - 2 * m, s - 2 * m);
        float radius = s * 0.24f;
        float cx = s / 2f, cy = s / 2f;

        using var path = Rounded(rect, radius);

        // 1) Navy background.
        using (var bg = new LinearGradientBrush(rect, NavyTop, NavyBot, 90f))
            g.FillPath(bg, path);

        var savedClip = g.Clip;
        g.SetClip(path);

        // 2) Cream wedge in the top-right corner.
        using (var cream = new GraphicsPath())
        {
            cream.AddPolygon(new[]
            {
                new PointF(s * 0.34f, m), new PointF(s - m, m), new PointF(s - m, s * 0.66f),
            });
            using var cb = new LinearGradientBrush(
                new RectangleF(s * 0.30f, 0, s * 0.72f, s * 0.72f), CreamHi, CreamLo, 40f);
            g.FillPath(cb, cream);
        }

        // 3) Silver dragon coil.
        DrawDragon(g, s, cx, cy);

        g.Clip = savedClip;

        using (var border = new Pen(Color.FromArgb(70, 80, 95), Math.Max(1f, s * 0.010f)))
            g.DrawPath(border, path);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static void DrawDragon(Graphics g, float s, float cx, float cy)
    {
        const int N = 220;
        double a0 = Math.PI * 1.12, turns = 1.52, maxR = s * 0.305;
        var pts = new PointF[N];
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)(N - 1);
            double ang = a0 + turns * 2 * Math.PI * t;
            double r = maxR * (1 - 0.66 * t);
            pts[i] = new PointF(cx + (float)(Math.Cos(ang) * r), cy + (float)(Math.Sin(ang) * r));
        }

        // Spikes along the outer half of the spine.
        for (int i = 6; i < N * 0.58f; i += 8)
        {
            float t = i / (float)(N - 1);
            double ang = a0 + turns * 2 * Math.PI * t;
            float nx = (float)Math.Cos(ang), ny = (float)Math.Sin(ang);
            float tx = -ny, ty = nx;
            float len = s * 0.085f * (1 - 0.55f * t), hw = s * 0.05f * (1 - 0.35f * t);
            var p = pts[i];
            var tip = new PointF(p.X + nx * len, p.Y + ny * len);
            using var tri = new GraphicsPath();
            tri.AddPolygon(new[]
            {
                new PointF(p.X + tx * hw, p.Y + ty * hw), tip, new PointF(p.X - tx * hw, p.Y - ty * hw),
            });
            using var sb = new SolidBrush(Lerp(SilverHi, SilverMid, t));
            g.FillPath(sb, tri);
        }

        // Tapered coil body.
        for (int i = 0; i < N - 1; i++)
        {
            float t = i / (float)(N - 1);
            float w = Math.Max(s * 0.012f, s * 0.10f * (1 - 0.70f * t));
            using var pen = new Pen(Lerp(SilverHi, SilverLo, t), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, pts[i], pts[i + 1]);
        }

        // Head at the outer end.
        double ha = a0;
        float hnx = (float)Math.Cos(ha), hny = (float)Math.Sin(ha);
        float htx = -hny, hty = hnx;
        var head0 = pts[0];
        var headC = new PointF(head0.X + hnx * s * 0.025f, head0.Y + hny * s * 0.025f);
        float hR = s * 0.085f;
        using (var hb = new LinearGradientBrush(
            new RectangleF(headC.X - hR, headC.Y - hR, hR * 2, hR * 2), SilverHi, SilverMid, 60f))
            g.FillEllipse(hb, headC.X - hR, headC.Y - hR, hR * 2, hR * 2);

        // Snout (points outward) + a swept-back horn.
        using (var sb = new SolidBrush(SilverMid))
        {
            using var snout = new GraphicsPath();
            var snoutTip = new PointF(headC.X + hnx * hR * 1.7f, headC.Y + hny * hR * 1.7f);
            snout.AddPolygon(new[]
            {
                new PointF(headC.X + htx * hR * 0.6f, headC.Y + hty * hR * 0.6f),
                snoutTip,
                new PointF(headC.X - htx * hR * 0.6f, headC.Y - hty * hR * 0.6f),
            });
            g.FillPath(sb, snout);
        }
        using (var sb = new SolidBrush(SilverHi))
        {
            using var horn = new GraphicsPath();
            var baseP = new PointF(headC.X - htx * hR * 0.2f, headC.Y - hty * hR * 0.2f);
            var hornTip = new PointF(headC.X - htx * hR * 2.1f + hnx * hR * 0.3f,
                                     headC.Y - hty * hR * 2.1f + hny * hR * 0.3f);
            horn.AddPolygon(new[]
            {
                baseP, hornTip,
                new PointF(headC.X - htx * hR * 0.4f + hnx * hR * 0.5f, headC.Y - hty * hR * 0.4f + hny * hR * 0.5f),
            });
            g.FillPath(sb, horn);
        }

        // Eye.
        float er = Math.Max(1f, s * 0.018f);
        var eye = new PointF(headC.X + hnx * hR * 0.35f - htx * hR * 0.15f,
                             headC.Y + hny * hR * 0.35f - hty * hR * 0.15f);
        using (var eb = new SolidBrush(NavyBot))
            g.FillEllipse(eb, eye.X - er, eye.Y - er, er * 2, er * 2);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
