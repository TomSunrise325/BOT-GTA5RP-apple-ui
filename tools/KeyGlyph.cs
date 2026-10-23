// Reads the letter (F, H, G, E, Y) shown in the key box under the bar of the construction mini-game.
// The box sits at the same place as the port bar: horizontally centred, 103 px under the region top (1080p).
// The glyph is cut out, scaled to a fixed grid and compared with letters rendered from a bold font.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

static class KeyGlyph
{
    const int GW = 12, GH = 16;
    const string Letters = "FHGEY";
    static float[][] tmpl;

    // window around the key box inside the AutoE region (1080p px)
    const double BoxX = 231, BoxY = 103, Half = 26;

    static float[] Resample(float[,] g, int x0, int y0, int x1, int y1)
    {
        float[] v = new float[GW * GH];
        double bw = (x1 - x0 + 1) / (double)GW, bh = (y1 - y0 + 1) / (double)GH;
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                int xa = x0 + (int)Math.Floor(i * bw), xb = x0 + (int)Math.Max(Math.Floor((i + 1) * bw) - 1, Math.Floor(i * bw));
                int ya = y0 + (int)Math.Floor(j * bh), yb = y0 + (int)Math.Max(Math.Floor((j + 1) * bh) - 1, Math.Floor(j * bh));
                float sum = 0; int n = 0;
                for (int y = ya; y <= yb; y++) for (int x = xa; x <= xb; x++) { sum += g[y, x]; n++; }
                v[j * GW + i] = n > 0 ? sum / n : 0;
            }
        return v;
    }

    static float Cos(float[] a, float[] b)
    {
        float ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { float x = a[i] - ma, y = b[i] - mb; dot += x * y; na += x * x; nb += y * y; }
        return na > 0 && nb > 0 ? dot / (float)Math.Sqrt(na * nb) : 0;
    }

    static float[] Render(char c, string font)
    {
        using (Bitmap bmp = new Bitmap(120, 120, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        using (Font f = new Font(font, 72f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.DrawString(c.ToString(), f, Brushes.White, 10, 5);
            float[,] gr = new float[120, 120];
            int minx = 120, miny = 120, maxx = -1, maxy = -1;
            for (int y = 0; y < 120; y++)
                for (int x = 0; x < 120; x++)
                {
                    float v = bmp.GetPixel(x, y).R / 255f;
                    gr[y, x] = v;
                    if (v > 0.5f) { if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; }
                }
            return Resample(gr, minx, miny, maxx, maxy);
        }
    }

    static void Init()
    {
        if (tmpl != null) return;
        string[] fonts = { "Segoe UI", "Arial" };
        tmpl = new float[Letters.Length * fonts.Length][];
        for (int i = 0; i < Letters.Length; i++)
            for (int k = 0; k < fonts.Length; k++) tmpl[i * fonts.Length + k] = Render(Letters[i], fonts[k]);
    }

    // true when a dark bar of the mini-game is on screen in the bar rows (15..35) of the region
    public static bool BarDark(byte[] px, int stride, int w, int h, double s)
    {
        int dark = 0, n = 0;
        for (int y = (int)(18 * s); y < (int)(34 * s); y += 4)
            for (int x = (int)(30 * s); x < (int)(430 * s) && x < w; x += 6)
            {
                int o = y * stride + x * 4;
                int mx = Math.Max(px[o], Math.Max(px[o + 1], px[o + 2]));
                n++; if (mx < 85) dark++;
            }
        return n > 0 && dark >= 0.55 * n;
    }

    // Returns the letter in the key box, or '\0'. score = cosine similarity of the best match.
    public static char Read(byte[] px, int stride, int w, int h, double s, out double score)
    {
        score = 0;
        Init();
        int cx = (int)Math.Round(BoxX * s), cy = (int)Math.Round(BoxY * s), hf = (int)Math.Round(Half * s);
        int xa = Math.Max(0, cx - hf), xb = Math.Min(w - 1, cx + hf), ya = Math.Max(0, cy - hf), yb = Math.Min(h - 1, cy + hf);
        if (xb - xa < 10 || yb - ya < 10) return '\0';

        int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1, bright = 0, dark = 0, total = 0;
        for (int y = ya; y <= yb; y++)
            for (int x = xa; x <= xb; x++)
            {
                int o = y * stride + x * 4;
                int mn = Math.Min(px[o], Math.Min(px[o + 1], px[o + 2])), mx = Math.Max(px[o], Math.Max(px[o + 1], px[o + 2]));
                total++;
                if (mx < 120) dark++;
                if (mn > 185) { bright++; if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; }
            }
        if (bright < 18 * s * s || bright > 0.30 * total || dark < 0.40 * total) return '\0';
        int gw = maxx - minx + 1, gh = maxy - miny + 1;
        if (gh < 10 * s || gh > 34 * s || gw < 5 * s || gw > 30 * s) return '\0';

        float[,] gr = new float[h, w];
        for (int y = miny; y <= maxy; y++)
            for (int x = minx; x <= maxx; x++)
            {
                int o = y * stride + x * 4;
                int mn = Math.Min(px[o], Math.Min(px[o + 1], px[o + 2]));
                gr[y, x] = Math.Max(0f, Math.Min(1f, (mn - 110) / 145f));
            }
        float[] v = Resample(gr, minx, miny, maxx, maxy);

        float best = -1, second = -1; int bi = -1;
        float[] perLetter = new float[Letters.Length];
        for (int i = 0; i < Letters.Length; i++) perLetter[i] = -1;
        int fonts = tmpl.Length / Letters.Length;
        for (int i = 0; i < Letters.Length; i++)
            for (int k = 0; k < fonts; k++) perLetter[i] = Math.Max(perLetter[i], Cos(v, tmpl[i * fonts + k]));
        for (int i = 0; i < Letters.Length; i++)
        {
            if (perLetter[i] > best) { second = best; best = perLetter[i]; bi = i; }
            else if (perLetter[i] > second) second = perLetter[i];
        }
        score = best;
        if (bi < 0 || best < 0.55f) return '\0';
        // E and F differ only by the bottom bar: decide by the mass of the last rows
        int ei = Letters.IndexOf('E'), fi = Letters.IndexOf('F');
        if ((bi == ei || bi == fi) && Math.Abs(perLetter[ei] - perLetter[fi]) < 0.12f)
        {
            float bottom = 0;
            for (int j = GH - 2; j < GH; j++) for (int i = 1; i < GW - 1; i++) bottom += v[j * GW + i];
            bottom /= 2 * (GW - 2);
            return bottom > 0.40f ? 'E' : 'F';
        }
        if (best - second < 0.04f) return '\0';
        return Letters[bi];
    }
}
