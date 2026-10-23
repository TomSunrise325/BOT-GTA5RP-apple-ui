// Auto-walk for the port job: find the red job marker on screen, turn the camera towards it with
// relative mouse moves and run (W) until we are standing in it. F6 toggles it.
// Works only while the "RAGE Multiplayer" window is in focus. Every decision is written to launcher.log.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Walker
{
    const int VkF6 = 0x75;
    const ushort ScanW = 0x11, ScanA = 0x1E, ScanD = 0x20, ScanSpace = 0x39;

    static volatile bool on;
    static volatile bool stop;
    static Thread th;
    public static event Action<bool> Changed;
    public static bool Enabled { get { return on; } }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    public struct Hit { public bool Found; public double Cx, Cy; public int Area, Bottom; }

    public static void Start()
    {
        if (th != null) return;
        th = new Thread(Loop);
        th.IsBackground = true;
        th.Name = "walker";
        th.Start();
    }
    public static void Stop() { stop = true; }
    public static void Toggle() { SetOn(!on); }

    static void SetOn(bool v)
    {
        on = v;
        Program.Log("Walker " + (v ? "ON" : "OFF"));
        Action<bool> h = Changed;
        if (h != null) { try { h(v); } catch { } }
    }

    // ---------- input ----------
    static void Key(ushort scan, bool down)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = 1;
        a[0].u.ki.wScan = scan;
        a[0].u.ki.dwFlags = (uint)(0x0008 | (down ? 0 : 0x0002));
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }
    static void MouseMove(int dx, int dy)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = 0;
        a[0].u.mi.dx = dx; a[0].u.mi.dy = dy;
        a[0].u.mi.dwFlags = 0x0001;           // MOUSEEVENTF_MOVE (relative)
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }

    static bool GameFocused()
    {
        try
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(GetForegroundWindow(), sb, 256);
            string t = sb.ToString();
            return t.IndexOf("RAGE", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("GTA", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    // ---------- vision ----------
    // The marker is a soft pink-red (about 240,52,57). Cars, chat text and crates are either pure red (G,B < 30)
    // or orange (B ~ 20), so a narrow band on G and B separates it.
    static bool IsMarker(int r, int g, int b)
    {
        return r >= 222 && g >= 34 && g <= 82 && b >= 38 && b <= 78 && r - g >= 140 && Math.Abs(g - b) <= 22;
    }

    // Looks at the whole screen (every 2nd pixel), ignoring the chat corner and the HUD bars.
    // The mask is split into connected blobs; the biggest solid blob wins. Sparse red-and-white stripes
    // (trailer reflectors) fill only ~10% of their box, the marker fills 40-90%, so stripes are rejected.
    public static Hit Detect(byte[] px, int stride, int w, int h, double s)
    {
        Hit res = new Hit();
        int y0 = (int)(0.12 * h), y1 = (int)(0.93 * h);
        int gw = w / 2, gh = (y1 - y0) / 2;
        bool[] m = new bool[gw * gh];
        int total = 0;
        for (int gy = 0; gy < gh; gy++)
        {
            int y = y0 + gy * 2;
            bool chatRow = y < 0.40 * h;
            int row = y * stride;
            for (int gx = 0; gx < gw; gx++)
            {
                int x = gx * 2;
                if (chatRow && x < 0.42 * w) continue;
                int o = row + x * 4;
                if (IsMarker(px[o + 2], px[o + 1], px[o])) { m[gy * gw + gx] = true; total++; }
            }
        }
        if (total * 4 < 100 * s * s) return res;

        int[] stack = new int[total + 16];
        double bestScore = 0;
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i]) continue;
            int sp = 0, n = 0, minx = gw, maxx = 0, miny = gh, maxy = 0;
            long sx = 0, sy = 0;
            stack[sp++] = i; m[i] = false;
            while (sp > 0)
            {
                int c = stack[--sp];
                int cy = c / gw, cx = c - cy * gw;
                n++; sx += cx; sy += cy;
                if (cx < minx) minx = cx; if (cx > maxx) maxx = cx; if (cy < miny) miny = cy; if (cy > maxy) maxy = cy;
                // neighbours within 2 cells (4 px) so the glow's texture holes do not split the blob
                for (int dy = -2; dy <= 2; dy++)
                {
                    int ny = cy + dy; if (ny < 0 || ny >= gh) continue;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = cx + dx; if (nx < 0 || nx >= gw) continue;
                        int ni = ny * gw + nx;
                        if (m[ni]) { m[ni] = false; if (sp < stack.Length) stack[sp++] = ni; }
                    }
                }
            }
            double area = n * 4.0;
            double box = (double)(maxx - minx + 1) * 2 * (maxy - miny + 1) * 2;     // cells are 2x2 px
            double fill = area / Math.Max(1.0, box);
            if (area < 100 * s * s || fill < 0.33) continue;
            if (area > bestScore)
            {
                bestScore = area;
                res.Found = true; res.Area = (int)area;
                res.Cx = (double)sx / n * 2; res.Cy = y0 + (double)sy / n * 2; res.Bottom = y0 + maxy * 2;
            }
        }
        return res;
    }

    // tiny grayscale thumbnail used to notice that we are not moving (stuck on a crate)
    static void Thumb(byte[] px, int stride, int w, int h, byte[] dst)
    {
        for (int ty = 0; ty < 18; ty++)
            for (int tx = 0; tx < 32; tx++)
            {
                int x = tx * w / 32 + w / 64, y = ty * h / 18 + h / 36;
                int o = y * stride + x * 4;
                dst[ty * 32 + tx] = (byte)((px[o] + px[o + 1] * 2 + px[o + 2]) >> 2);
            }
    }

    // ---------- behaviour ----------
    static void Release()
    {
        Key(ScanW, false); Key(ScanA, false); Key(ScanD, false);
    }

    static void Loop()
    {
        Rectangle scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        int w = scr.Width, h = scr.Height;
        double s = h / 1080.0;
        byte[] buf = new byte[w * h * 4];
        byte[] thumb = new byte[32 * 18], prevThumb = new byte[32 * 18];
        bool f6Prev = false, wDown = false, haveThumb = false;
        int lastSeen = Environment.TickCount, arrivedUntil = 0, lastThumb = Environment.TickCount, stuck = 0, lastLog = 0, evade = 0;
        Program.Log("Walker thread: screen " + w + "x" + h);

        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            while (!stop)
            {
                bool f6 = (GetAsyncKeyState(VkF6) & 0x8000) != 0;
                if (f6 && !f6Prev) { SetOn(!on); if (!on) { Release(); wDown = false; } }
                f6Prev = f6;
                if (!on) { Thread.Sleep(20); continue; }

                int now = Environment.TickCount;
                if (!GameFocused()) { if (wDown) { Release(); wDown = false; } Thread.Sleep(150); continue; }
                if (now - AutoE.BarTick < 900) { if (wDown) { Release(); wDown = false; } Thread.Sleep(30); continue; }   // E mini-game is on screen
                if (now < arrivedUntil) { Thread.Sleep(30); continue; }

                try
                {
                    g.CopyFromScreen(scr.Left, scr.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    int stride = d.Stride;
                    Marshal.Copy(d.Scan0, buf, 0, Math.Min(buf.Length, Math.Abs(stride) * h));
                    bmp.UnlockBits(d);

                    Hit hit = Detect(buf, stride, w, h, s);
                    if (hit.Found)
                    {
                        lastSeen = now;
                        double err = hit.Cx - w / 2.0;

                        if (hit.Area >= 10000 * s * s)
                        {
                            if (wDown) { Release(); wDown = false; }
                            arrivedUntil = now + 3000;
                            Program.Log("Walker: reached the marker (area " + hit.Area + "), pause 3 s");
                            continue;
                        }

                        int dx = (int)Math.Round(Math.Max(-70, Math.Min(70, err * 0.22)));
                        if (Math.Abs(err) > 24 * s && dx != 0) MouseMove(dx, 0);

                        bool aligned = Math.Abs(err) < 0.30 * w;
                        if (evade > 0) { evade--; }
                        else if (aligned && !wDown) { Key(ScanW, true); wDown = true; }
                        else if (!aligned && wDown) { Key(ScanW, false); wDown = false; }

                        if (now - lastLog > 700) { lastLog = now; Program.Log("Walker: marker x=" + (int)hit.Cx + " y=" + (int)hit.Cy + " area=" + hit.Area + " err=" + (int)err + " dx=" + dx + (wDown ? " W" : "")); }
                    }
                    else
                    {
                        // marker not visible: stand still and turn the camera until it shows up
                        if (now - lastSeen > 350)
                        {
                            if (wDown) { Key(ScanW, false); wDown = false; }
                            MouseMove((int)(38 * Math.Max(1.0, s)), 0);
                            if (now - lastLog > 1500) { lastLog = now; Program.Log("Walker: no marker, turning the camera"); }
                        }
                    }

                    // stuck detection: while we run, the picture must change
                    if (now - lastThumb >= 400)
                    {
                        lastThumb = now;
                        Thumb(buf, stride, w, h, thumb);
                        if (haveThumb && wDown)
                        {
                            int diff = 0;
                            for (int i = 0; i < thumb.Length; i++) diff += Math.Abs(thumb[i] - prevThumb[i]);
                            if (diff / thumb.Length < 3) stuck++; else stuck = 0;
                            if (stuck >= 3)
                            {
                                stuck = 0;
                                Program.Log("Walker: stuck, jumping around the obstacle");
                                ushort side = (now & 1) == 0 ? ScanA : ScanD;
                                Key(ScanSpace, true); Key(side, true); Thread.Sleep(450); Key(side, false); Key(ScanSpace, false);
                                evade = 3;
                            }
                        }
                        else stuck = 0;
                        Array.Copy(thumb, prevThumb, thumb.Length);
                        haveThumb = true;
                    }
                }
                catch (Exception ex) { Program.Log("Walker error: " + ex.Message); Thread.Sleep(200); }
                Thread.Sleep(25);
            }
        }
        Release();
    }
}
