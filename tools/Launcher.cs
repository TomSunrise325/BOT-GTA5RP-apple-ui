// Single-file launcher: the bot bundle is appended to this .exe as a zip payload.
// First run extracts it to %LOCALAPPDATA%\BotGTA5RP\<build id>, later runs start instantly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("BOT [GTA5RP]")]
[assembly: System.Reflection.AssemblyProduct("BOT [GTA5RP]")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

static class Program
{
    const string Magic = "BOTPAY01";
    const string DriverInstaller = "ViGEmBus_1.22.0_x64_x86_arm64.exe";

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main()
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { return Run(); }
        catch (Exception ex)
        {
            try { Log("ERROR " + ex); } catch { }
            MessageBox.Show("Не удалось запустить BOT [GTA5RP]:\n\n" + ex.Message + (logPath != null ? "\n\nЖурнал: " + logPath : ""),
                "BOT [GTA5RP]", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int Run()
    {
        string self = Process.GetCurrentProcess().MainModule.FileName;
        long payloadLen, payloadStart;
        string id;
        using (FileStream fs = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length < 32) throw new InvalidDataException("Файл повреждён (нет данных).");
            fs.Seek(-32, SeekOrigin.End);
            byte[] f = new byte[32];
            int got = 0;
            while (got < 32) { int n = fs.Read(f, got, 32 - got); if (n <= 0) break; got += n; }
            if (Encoding.ASCII.GetString(f, 24, 8) != Magic) throw new InvalidDataException("Файл повреждён (нет данных).");
            payloadLen = BitConverter.ToInt64(f, 0);
            byte[] g = new byte[16];
            Array.Copy(f, 8, g, 0, 16);
            id = new Guid(g).ToString("N").Substring(0, 12);
            payloadStart = fs.Length - 32 - payloadLen;
        }

        // BOTGTA5RP_HOME lets tests run in an isolated folder
        string root = Environment.GetEnvironmentVariable("BOTGTA5RP_HOME");
        bool isolated = !string.IsNullOrEmpty(root);
        if (!isolated)
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotGTA5RP");

        Directory.CreateDirectory(root);
        logPath = Path.Combine(root, "launcher.log");
        Log("start, build " + id + ", self=" + self);
        if (!isolated) KillOld(root);

        // Reuse an intact copy of this build; otherwise pick a folder we can actually (re)create.
        // A running bot locks its files, so a busy folder is skipped instead of failing: id, id-1, id-2 ...
        string dir = null, ready = null, botDir = null;
        bool needExtract = false;
        for (int n = 0; n < 20 && dir == null; n++)
        {
            string d = Path.Combine(root, n == 0 ? id : id + "-" + n);
            string r = Path.Combine(d, ".ready");
            string bd = Path.Combine(d, "bot");
            if (File.Exists(r) && Intact(bd)) { dir = d; ready = r; botDir = bd; break; }
            if (!Directory.Exists(d) || TryDelete(d)) { dir = d; ready = r; botDir = bd; needExtract = true; }
            else Log("folder busy, skipping: " + d);
        }
        if (dir == null)
            throw new IOException("Не удалось подготовить папку для бота. Закройте запущенный бот (main.exe) в Диспетчере задач и запустите снова.");
        string main = Path.Combine(botDir, "main.exe");

        if (needExtract)
        {
            Log("extracting to " + dir);
            Extract(self, payloadStart, payloadLen, root, dir, ready);
            if (!Intact(botDir))
                throw new FileNotFoundException(
                    "После распаковки не хватает файлов бота. Скорее всего их удалил антивирус (Защитник Windows).\n" +
                    "Добавьте папку в исключения и запустите снова:\n" + root, main);
        }

        EnsureDriver(dir);

        ProcessStartInfo psi = new ProcessStartInfo(main);
        psi.WorkingDirectory = botDir;
        psi.UseShellExecute = true;
        Process p = Process.Start(psi);
        Log("started main.exe pid=" + (p == null ? -1 : p.Id));

        // if the bot dies right away, say why instead of silently doing nothing
        if (p != null && p.WaitForExit(7000))
        {
            int code = p.ExitCode;
            Log("main.exe exited early, code=" + code);
            if (code != 0)
                MessageBox.Show(
                    "Бот закрылся сразу после запуска (код 0x" + code.ToString("X8") + ").\n\n" +
                    "Что проверить:\n" +
                    "• антивирус/Защитник Windows не блокирует папку:\n  " + botDir + "\n" +
                    "• установлен Microsoft Visual C++ Redistributable 2015–2022 (x64)\n" +
                    "• запуск от имени администратора подтверждён\n\n" +
                    "Журнал: " + logPath,
                    "BOT [GTA5RP]", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 0;
        }
        if (p == null) return 0;

        RunTray(p);
        return 0;
    }

    // stays in the tray while the bot runs: F5 toggles Auto-E, closing the bot closes this too
    static void RunTray(Process bot)
    {
        NotifyIcon tray = new NotifyIcon();
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem toggle = new ToolStripMenuItem("Авто-E вкл/выкл (F5)");
        toggle.Click += delegate { AutoE.Toggle(); };
        ToolStripMenuItem walk = new ToolStripMenuItem("Автохождение к маркеру вкл/выкл (F6)");
        walk.Click += delegate { Walker.Toggle(); };
        ToolStripMenuItem exit = new ToolStripMenuItem("Выйти из трея");
        menu.Items.Add(toggle);
        menu.Items.Add(walk);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        tray.ContextMenuStrip = menu;
        Action<bool> upd = delegate(bool v)
        {
            tray.Text = "BOT [GTA5RP] — Авто-E: " + (v ? "включено" : "выключено") + " (F5)";
            toggle.Checked = v;
            try { (v ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play(); } catch { }
            if (v) tray.ShowBalloonTip(1500, "Авто-E включено", "Встаньте к порту: E нажмётся, когда зелёная зона дойдёт до красной линии. F5 — выключить.", ToolTipIcon.None);
        };
        System.Threading.SynchronizationContext ui = new WindowsFormsSynchronizationContext();
        AutoE.Changed += delegate(bool v) { ui.Post(delegate { upd(v); }, null); };
        Walker.Changed += delegate(bool v)
        {
            ui.Post(delegate
            {
                walk.Checked = v;
                try { (v ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play(); } catch { }
                if (v) tray.ShowBalloonTip(1500, "Автохождение включено", "Бот ищет красный маркер, поворачивает камеру и бежит к нему. F6 — выключить.", ToolTipIcon.None);
            }, null);
        };
        tray.Text = "BOT [GTA5RP] — Авто-E: выключено (F5)";
        tray.Visible = true;

        ApplicationContext ctx = new ApplicationContext();
        exit.Click += delegate { ctx.ExitThread(); };
        System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
        watch.Interval = 1500;
        watch.Tick += delegate { try { if (bot.HasExited) ctx.ExitThread(); } catch { ctx.ExitThread(); } };
        watch.Start();

        AutoE.Start();
        Walker.Start();
        Application.Run(ctx);
        AutoE.Stop();
        Walker.Stop();
        tray.Visible = false;
        tray.Dispose();
    }

    // a new copy replaces an older copy of the bot, so there is never a locked port or a half-deleted folder
    static void KillOld(string root)
    {
        try
        {
            Process me = Process.GetCurrentProcess();
            foreach (Process pr in Process.GetProcessesByName(me.ProcessName))
            {
                if (pr.Id == me.Id) continue;
                try { Log("closing an older launcher, pid " + pr.Id); pr.Kill(); pr.WaitForExit(4000); } catch (Exception ex) { Log("could not close pid " + pr.Id + ": " + ex.Message); }
            }
            foreach (string name in new string[] { "main", "QtWebEngineProcess" })
                foreach (Process pr in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string f = pr.MainModule.FileName;
                        if (f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Log("closing an older " + name + ", pid " + pr.Id); pr.Kill(); pr.WaitForExit(4000); }
                    }
                    catch { }
                }
        }
        catch (Exception ex) { Log("KillOld: " + ex.Message); }
    }

    static bool TryDelete(string d)
    {
        try { Directory.Delete(d, true); } catch { }
        return !Directory.Exists(d);
    }

    static string logPath;
    internal static void Log(string s)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine); } catch { }
    }

    static bool Intact(string botDir)
    {
        string[] must = { "main.exe", "python314.dll", "qt6core.dll", "QtWebEngineProcess.exe",
                          Path.Combine("templates", "layout.html"), Path.Combine("static", "style.css") };
        foreach (string m in must)
            if (!File.Exists(Path.Combine(botDir, m))) return false;
        return true;
    }

    // ---------- extraction ----------
    static void Extract(string self, long start, long len, string root, string dir, string ready)
    {
        Directory.CreateDirectory(root);
        string tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        SplashForm form = new SplashForm();
        Exception error = null;

        Thread worker = new Thread(delegate()
        {
            try
            {
                using (FileStream fs = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SubStream sub = new SubStream(fs, start, len))
                using (ZipArchive zip = new ZipArchive(sub, ZipArchiveMode.Read))
                {
                    long total = 0, done = 0;
                    foreach (ZipArchiveEntry e in zip.Entries) total += e.Length;
                    if (total <= 0) total = 1;
                    string tmpFull = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
                    int lastPct = -1;
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        string dest = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!dest.StartsWith(tmpFull, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                        if (e.FullName.EndsWith("/") || e.Name.Length == 0) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                        done += e.Length;
                        int pct = (int)(done * 100 / total);
                        if (pct != lastPct) { lastPct = pct; form.SetProgress(pct); }
                    }
                }
                Directory.Move(tmp, dir);
                MigrateConfigs(root, dir);
                File.WriteAllText(ready, DateTime.Now.ToString("o"));
            }
            catch (Exception ex) { error = ex; }
            finally { form.CloseSafe(); }
        });
        worker.IsBackground = true;
        worker.Start();
        Application.Run(form);
        worker.Join();
        if (error != null)
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            throw error;
        }
    }

    // keep user settings when a newer build is extracted next to an older one
    static void MigrateConfigs(string root, string newDir)
    {
        try
        {
            string newName = Path.GetFileName(newDir);
            DirectoryInfo best = null;
            foreach (DirectoryInfo d in new DirectoryInfo(root).GetDirectories())
            {
                if (d.Name == newName || d.Name.EndsWith(".tmp")) continue;
                if (!File.Exists(Path.Combine(d.FullName, ".ready"))) continue;
                if (best == null || d.LastWriteTimeUtc > best.LastWriteTimeUtc) best = d;
            }
            if (best == null) return;
            string oldCfg = Path.Combine(best.FullName, "bot", "configs");
            string newCfg = Path.Combine(newDir, "bot", "configs");
            if (Directory.Exists(oldCfg))
            {
                Directory.CreateDirectory(newCfg);
                foreach (string f in Directory.GetFiles(oldCfg, "*.json"))
                    File.Copy(f, Path.Combine(newCfg, Path.GetFileName(f)), true);
            }
            foreach (DirectoryInfo d in new DirectoryInfo(root).GetDirectories())
            {
                if (d.Name == newName) continue;
                try { d.Delete(true); } catch { }
            }
        }
        catch { }
    }

    // ---------- ViGEmBus (virtual gamepad driver, needed by Anti-AFK) ----------
    static void EnsureDriver(string dir)
    {
        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus"))
                if (k != null) return;
            string asked = Path.Combine(dir, ".vigem-asked");
            string setup = Path.Combine(dir, DriverInstaller);
            if (File.Exists(asked) || !File.Exists(setup)) return;
            File.WriteAllText(asked, "1");
            DialogResult r = MessageBox.Show(
                "Для модуля Anti-AFK нужен драйвер виртуального геймпада ViGEmBus.\n\nУстановить его сейчас? (потребуются права администратора)",
                "BOT [GTA5RP]", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            ProcessStartInfo psi = new ProcessStartInfo(setup);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process p = Process.Start(psi);
            if (p != null) p.WaitForExit();
        }
        catch { }
    }
}

// read-only window over a section of a seekable stream
class SubStream : Stream
{
    readonly Stream b; readonly long start, len; long pos;
    public SubStream(Stream baseStream, long start, long len) { b = baseStream; this.start = start; this.len = len; }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return true; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { return len; } }
    public override long Position { get { return pos; } set { pos = value; } }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        long remain = len - pos;
        if (remain <= 0) return 0;
        if (count > remain) count = (int)remain;
        b.Position = start + pos;
        int n = b.Read(buffer, offset, count);
        pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin == SeekOrigin.Begin) pos = offset;
        else if (origin == SeekOrigin.Current) pos += offset;
        else pos = len + offset;
        return pos;
    }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}

// dark, rounded progress window shown only on the first run
class SplashForm : Form
{
    int pct;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    float shown;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 190);
        BackColor = Color.FromArgb(18, 18, 22);
        DoubleBuffered = true;
        ShowInTaskbar = true;
        Text = "BOT [GTA5RP]";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Region = new Region(Round(new Rectangle(0, 0, Width, Height), 22));
        anim.Interval = 16;
        anim.Tick += delegate { shown += (pct - shown) * 0.12f; Invalidate(new Rectangle(30, 128, 380, 14)); };
        anim.Start();
    }

    public void SetProgress(int p) { pct = p; }
    public void CloseSafe()
    {
        try { if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { anim.Stop(); Close(); }); } catch { }
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        GraphicsPath gp = new GraphicsPath();
        int d = rad * 2;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (LinearGradientBrush bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(30, 34, 52), Color.FromArgb(14, 14, 18), 70f))
            g.FillRectangle(bg, ClientRectangle);

        // app tile
        Rectangle tile = new Rectangle(30, 30, 46, 46);
        using (GraphicsPath tp = Round(tile, 12))
        using (LinearGradientBrush tb = new LinearGradientBrush(tile, Color.FromArgb(58, 160, 255), Color.FromArgb(10, 108, 240), 90f))
            g.FillPath(tb, tp);
        using (Pen p = new Pen(Color.White, 3f))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            g.DrawLines(p, new PointF[] { new PointF(56, 38), new PointF(44, 56), new PointF(53, 56), new PointF(50, 68), new PointF(62, 50), new PointF(53, 50), new PointF(56, 38) });
        }

        using (Font f1 = new Font("Segoe UI Semibold", 15f))
        using (Font f2 = new Font("Segoe UI", 9.5f))
        using (SolidBrush w = new SolidBrush(Color.White))
        using (SolidBrush gr = new SolidBrush(Color.FromArgb(150, 150, 165)))
        {
            g.DrawString("BOT [GTA5RP]", f1, w, 90, 32);
            g.DrawString("Первый запуск — готовлю приложение…", f2, gr, 92, 58);
            g.DrawString("Это происходит один раз", f2, gr, 31, 98);
            string t = ((int)Math.Round(shown)) + "%";
            SizeF sz = g.MeasureString(t, f2);
            g.DrawString(t, f2, w, 410 - sz.Width, 98);
        }

        Rectangle track = new Rectangle(30, 130, 380, 8);
        using (GraphicsPath tp2 = Round(track, 4))
        using (SolidBrush tr = new SolidBrush(Color.FromArgb(48, 255, 255, 255)))
            g.FillPath(tr, tp2);
        int fw = (int)(track.Width * Math.Max(0f, Math.Min(100f, shown)) / 100f);
        if (fw >= 8)
        {
            Rectangle fill = new Rectangle(track.X, track.Y, fw, track.Height);
            using (GraphicsPath fp = Round(fill, 4))
            using (LinearGradientBrush fb = new LinearGradientBrush(fill, Color.FromArgb(100, 210, 255), Color.FromArgb(10, 132, 255), 0f))
                g.FillPath(fb, fp);
        }

        using (Pen edge = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
        using (GraphicsPath ep = Round(new Rectangle(0, 0, Width - 1, Height - 1), 22))
            g.DrawPath(edge, ep);
    }

    // allow dragging the borderless window
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = (IntPtr)2; return; } // HTCAPTION
        base.WndProc(ref m);
    }
}
