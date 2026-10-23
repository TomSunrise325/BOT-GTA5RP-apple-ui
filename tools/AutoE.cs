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

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

    // construction hammering runs on its own thread so the screen capture never slows it down
    static volatile char spamLetter;
    static volatile int spamUntil;
    static int spamPresses;
    public static int SpamCount { get { return spamPresses; } }

    public static void Start()
    {
        if (th != null) return;
        try { timeBeginPeriod(1); } catch { }
        th = new Thread(Loop);
        th.IsBackground = true;
        th.Name = "auto-e";
        th.Start();
        Thread sp = new Thread(SpamLoop);
        sp.IsBackground = true;
        sp.Name = "auto-e-spam";
        sp.Priority = ThreadPriority.AboveNormal;
        sp.Start();
        Thread pp = new Thread(PostLoop);
        pp.IsBackground = true;
        pp.Name = "auto-e-post";
        pp.Priority = ThreadPriority.AboveNormal;
        pp.Start();
    }
    public static void Stop() { stop = true; }

    static void SendScan(ushort scan, bool down)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = 1;
        a[0].u.ki.wScan = scan;
        a[0].u.ki.dwFlags = (uint)(0x0008 | (down ? 0 : 0x0002));
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }

    // virtual-key + scan code: some games read one, some the other, so both go out
    static void SendVk(ushort vk, ushort scan, bool down)
    {
        INPUT[] a = new INPUT[2];
        a[0].type = 1;
        a[0].u.ki.wVk = vk; a[0].u.ki.wScan = scan;
        a[0].u.ki.dwFlags = (uint)(down ? 0 : 0x0002);
        a[1].type = 1;
        a[1].u.ki.wScan = scan;
        a[1].u.ki.dwFlags = (uint)(0x0008 | (down ? 0 : 0x0002));
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));   // one call: on some systems every call costs ~16 ms
    }

    static void Spin(System.Diagnostics.Stopwatch sw, double ms)
    {
        double end = sw.Elapsed.TotalMilliseconds + ms;
        while (sw.Elapsed.TotalMilliseconds < end) Thread.SpinWait(40);
    }

    // Keyboard events injected with SendInput are throttled by Windows to ~30 presses per second on many systems.
    // So the same presses are also posted straight to the focused game window (WM_KEYDOWN / WM_KEYUP), which does
    // not go through that queue: about 500 presses per second. Only used while a letter is recognised on screen.
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    public static bool PostToAnyWindow;          // tests only; in the app the foreground window must be the game
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);

    static bool GameWindow(IntPtr h)
    {
        if (PostToAnyWindow) return true;
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, 256);
            string t = sb.ToString();
            return t.IndexOf("RAGE", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("GTA", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    static ushort VkOf(char c) { return (ushort)c; }   // 'F','G','H','E','Y' are their own virtual-key codes

    static void PostLoop()
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        int posted = 0;
        while (!stop)
        {
            char L = spamLetter;
            if (L != '\0' && Environment.TickCount < spamUntil)
            {
                IntPtr h = GetForegroundWindow();
                if (h != IntPtr.Zero && GameWindow(h))
                {
                    uint vk = VkOf(L), scan = MapVirtualKey(vk, 0);
                    IntPtr down = (IntPtr)(1 | (scan << 16));
                    IntPtr up = (IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31));
                    PostMessage(h, 0x0100, (IntPtr)vk, down);
                    PostMessage(h, 0x0101, (IntPtr)vk, up);
                    PostCount = ++posted;
                    Spin(sw, 0.8);
                    continue;
                }
            }
            Thread.Sleep(2);
        }
    }
    public static int PostCount;

    // The SendInput path: real keyboard input, so it also reaches games that ignore posted messages.
    // Windows throttles it to ~30 presses per second on many systems; PostLoop above is the fast one.
    const int Burst = 1;
    static void SpamLoop()
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        INPUT[] batch = new INPUT[Burst * 2];
        ushort cur = 0;
        while (!stop)
        {
            char L = spamLetter;
            if (L != '\0' && Environment.TickCount < spamUntil)
            {
                ushort sc = ScanOf(L);
                if (sc != cur)
                {
                    cur = sc;
                    for (int i = 0; i < Burst; i++)
                    {
                        batch[2 * i].type = 1; batch[2 * i].u.ki.wScan = sc; batch[2 * i].u.ki.dwFlags = 0x0008;
                        batch[2 * i + 1].type = 1; batch[2 * i + 1].u.ki.wScan = sc; batch[2 * i + 1].u.ki.dwFlags = 0x0008 | 0x0002;
                    }
                }
                SendInput((uint)batch.Length, batch, Marshal.SizeOf(typeof(INPUT)));
                spamPresses += Burst;
                if (spamPresses % 1600 == 0) Program.Log("Stroyka: " + spamPresses + " presses of " + L);
                Spin(sw, 3.0);
            }
            else Thread.Sleep(2);
        }
    }

    // while the mini-game is on screen the character must stand still; walking comes back as soon as it is gone
    static bool wasW, wasShift;
    static void WalkPause()
    {
        wasW = (GetAsyncKeyState(0x57) & 0x8000) != 0;
        wasShift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        Program.Log("Stroyka: letter on screen, walking paused" + (wasW ? " (W was held" + (wasShift ? " with Shift)" : ")") : ""));
        ReleaseWalk();
    }
    static void ReleaseWalk()
    {
        if (wasW) SendVk(0x57, 0x11, false);
        if (wasShift) SendVk(0xA0, 0x2A, false);
    }
    static void WalkResume()
    {
        Program.Log("Stroyka: letter gone, walking resumed" + (wasW ? "" : " (nothing to resume)"));
        if (wasShift) SendVk(0xA0, 0x2A, true);
        if (wasW) SendVk(0x57, 0x11, true);
        wasW = wasShift = false;
    }
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
        double lastPress = -10, prevD = double.NaN;
        char lastLetter = '\0'; bool active = false; int lastGuard = 0;
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

                // the letter has been gone for 130 ms (or Auto-E was switched off): stop hammering, resume walking
                if (active && (!on || Environment.TickCount > spamUntil)) { active = false; spamLetter = '\0'; lastLetter = '\0'; WalkResume(); }
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
                                if (!active) { active = true; WalkPause(); }
                                if (L != lastLetter) { lastLetter = L; Program.Log("Stroyka: key " + L + " (match " + sc.ToString("0.00") + "), hammering"); }
                                spamLetter = L; spamUntil = Environment.TickCount + 130;
                                if (Environment.TickCount - lastGuard > 150) { lastGuard = Environment.TickCount; ReleaseWalk(); }   // the bot's own walker may press W again
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
