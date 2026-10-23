// Auto-E for the "put the box on the port" bar: the red marker stands still in the middle,
// the green zone slides left and right. When the zone's centre crosses the marker, press E.
// F5 toggles it. Screen geometry is the one measured on 1920x1080 and scaled by height.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

static class AutoE
{
    // region on a 1080p screen, relative to the screen centre (marker is at +229.5 - 230 = -0.5)
    const int RegionTop = 480, RegionW = 460, RegionH = 140;   // bar rows 8..47, key box centred at row ~103
    const int RowFrom = 15, RowTo = 35;
    const double MarkerX = 230;      // inside the region
    const double Tol = 8;            // px at 1080p
    const double Cooldown = 0.6;     // s
    const ushort ScanE = 0x12;
    const int VkF5 = 0x74;

    static volatile bool on;
    static volatile bool stop;
    static Thread th;
    public static event Action<bool> Changed;
    public static bool Enabled { get { return on; } }
    public static volatile int BarTick = -100000;   // Environment.TickCount of the last time the bar was on screen (used by Walker)

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    public static void Start()
    {
        if (th != null) return;
        th = new Thread(Loop);
        th.IsBackground = true;
        th.Name = "auto-e";
        th.Start();
    }
    public static void Stop() { stop = true; }
    public static void Toggle() { SetOn(!on); }

    static void SetOn(bool v)
    {
        on = v;
        Program.Log("Auto-E " + (v ? "ON" : "OFF"));
        Action<bool> h = Changed;
        if (h != null) { try { h(v); } catch { } }
    }

    static void PressE() { PressKey(ScanE, 40); }

    static void PressKey(ushort scan, int holdMs)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = 1;
        a[0].u.ki.wScan = scan;
        a[0].u.ki.dwFlags = 0x0008;           // KEYEVENTF_SCANCODE
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(holdMs);
        a[0].u.ki.dwFlags = 0x0008 | 0x0002;  // ... | KEYEVENTF_KEYUP
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }

    // construction mini-game: the letter shown under the bar must be hammered
    static ushort ScanOf(char c)
    {
        switch (c) { case 'F': return 0x21; case 'G': return 0x22; case 'H': return 0x23; case 'Y': return 0x15; default: return ScanE; }
    }

    // the port bar has a fixed red marker line; the construction bar does not
    static bool HasRedLine(byte[] px, int stride, int w, int h, double s)
    {
        int xa = (int)Math.Round((MarkerX - 8) * s), xb = (int)Math.Round((MarkerX + 6) * s);
        int ya = (int)Math.Round(10 * s), yb = (int)Math.Round(46 * s);
        for (int x = xa; x <= xb && x < w; x++)
        {
            int cnt = 0;
            for (int y = ya; y < yb && y < h; y++)
            {
                int o = y * stride + x * 4;
                if (px[o + 2] > 215 && px[o + 1] < 95 && px[o] < 110) cnt++;
            }
            if (cnt >= 22 * s) return true;
        }
        return false;
    }

    // Returns the zone centre inside the region, or -1 when there is no green zone of the right width.
    public static double Detect(byte[] px, int stride, int w, int h, double s)
    {
        int y0 = (int)Math.Round(RowFrom * s), y1 = Math.Min(h, (int)Math.Round(RowTo * s));
        if (y1 <= y0) return -1;
        int rows = y1 - y0, minX = -1, maxX = -1;
        for (int x = 0; x < w; x++)
        {
            int cnt = 0;
            for (int y = y0; y < y1; y++)
            {
                int o = y * stride + x * 4;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                if (g > 130 && r < 150 && b < 120 && g - r > 40) cnt++;
            }
            if (cnt * 2 > rows) { if (minX < 0) minX = x; maxX = x; }
        }
        if (minX < 0) return -1;
        double width = maxX - minX;
        if (width < 40 * s || width > 90 * s) return -1;
        return (minX + maxX) / 2.0;
    }

    static void Loop()
    {
        Rectangle scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        double s = scr.Height / 1080.0;
        int rw = (int)Math.Round(RegionW * s), rh = (int)Math.Round(RegionH * s);
        int rx = scr.Left + scr.Width / 2 - (int)Math.Round(230 * s);
        int ry = scr.Top + (int)Math.Round(RegionTop * s);
        double marker = MarkerX * s - 0.5 * s;     // red line measured at 229.5 in the region
        Program.Log("Auto-E thread: screen " + scr.Width + "x" + scr.Height + ", region " + rx + "," + ry + " " + rw + "x" + rh);

        bool f5Prev = false;
        double lastPress = -10, prevD = double.NaN, lastSpam = -10, lastUnknown = -10;
        char lastLetter = '\0'; int spamCount = 0;
        DateTime t0 = DateTime.UtcNow;
        byte[] buf = new byte[rw * rh * 4];
        using (Bitmap bmp = new Bitmap(rw, rh, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            while (!stop)
            {
                bool f5 = (GetAsyncKeyState(VkF5) & 0x8000) != 0;
                if (f5 && !f5Prev) { SetOn(!on); prevD = double.NaN; }
                f5Prev = f5;

                if (!on) { Thread.Sleep(15); continue; }
                try
                {
                    g.CopyFromScreen(rx, ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
                    BitmapData d = bmp.LockBits(new Rectangle(0, 0, rw, rh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(d.Scan0, buf, 0, Math.Min(buf.Length, Math.Abs(d.Stride) * rh));
                    int stride = d.Stride;
                    bmp.UnlockBits(d);

                    double c = Detect(buf, stride, rw, rh, s);
                    double now = (DateTime.UtcNow - t0).TotalSeconds;
                    bool port = c >= 0 && HasRedLine(buf, stride, rw, rh, s);
                    bool bar = KeyGlyph.BarDark(buf, stride, rw, rh, s);
                    if (!port)
                    {
                        prevD = double.NaN;
                        if (bar)
                        {
                            double sc;
                            char L = KeyGlyph.Read(buf, stride, rw, rh, s, out sc);
                            if (L != '\0')
                            {
                                BarTick = Environment.TickCount;
                                if (L != lastLetter) { lastLetter = L; spamCount = 0; Program.Log("Stroyka: key " + L + " (match " + sc.ToString("0.00") + "), hammering"); }
                                if (now - lastSpam >= 0.07)
                                {
                                    lastSpam = now;
                                    PressKey(ScanOf(L), 22);
                                    if (++spamCount % 50 == 0) Program.Log("Stroyka: " + spamCount + " presses of " + L);
                                }
                            }
                        }
                        else lastLetter = '\0';
                    }
                    else
                    {
                        BarTick = Environment.TickCount;
                        double dist = c - marker;
                        bool cross = !double.IsNaN(prevD) && Math.Sign(prevD) != Math.Sign(dist) && Math.Abs(prevD) < Tol * s * 4;
                        if ((Math.Abs(dist) <= Tol * s || cross) && now - lastPress > Cooldown)
                        {
                            PressE();
                            lastPress = now;
                            Program.Log("Auto-E: E pressed (offset " + dist.ToString("0.0") + " px)");
                        }
                        prevD = dist;
                    }
                }
                catch (Exception ex) { Program.Log("Auto-E error: " + ex.Message); Thread.Sleep(200); }
                Thread.Sleep(3);
            }
        }
    }
}
