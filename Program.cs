using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace Spread;

internal static class Program
{
    delegate bool ConsoleCtrl(uint type);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(ConsoleCtrl handler, bool add);
    static ConsoleCtrl? _ctrl;   // kept alive

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--layout-test")) return LayoutTest();
        if (args.Contains("--list-windows")) return ListWindows();
        if (args.Contains("--widget-test")) return WidgetTest.Run();
        if (args.Contains("--atlassian-test")) return AtlassianTest.Run(args);
        if (args.Contains("--toast-test")) return ToastTest.Run(args);
        if (args.Contains("--search-test")) return SearchTest.Run(args);
        if (args.Contains("--github-test")) return GitHubTest.Run(args);
        if (args.Contains("--log-test")) return LogTest.Run();
        if (args.Contains("--core-test")) return CoreTest.Run();
        if (args.Contains("--bar-test")) { int f = WidgetTest.Offline(); Console.WriteLine(f == 0 ? "BAR TEST PASSED" : $"BAR TEST FAILED ({f})"); return f == 0 ? 0 : 1; }
        if (args.Contains("--dock-test")) return DockTest.Run(args);
        if (args.Contains("--quicksettings-test")) return QuickSettingsTest.Run(args);
        if (args.Contains("--capture-test")) return SnapshotCache.ProbeMinimized();
        if (args.Contains("--dwm-order-test")) return DwmOrderTest.Run();
        if (args.Contains("--restore-taskbar"))
        {
            bool was = TaskbarControl.IsHiddenBySpread;
            TaskbarControl.Restore(force: true);
            Console.WriteLine($"Windows taskbar is set to always visible (state now {TaskbarControl.GetState()}); Spread had {(was ? "" : "not ")}hidden it.");
            return 0;
        }
        if (args.Contains("--list-tray"))
        {
            var list = TrayRegistry.ReadRunning(Config.Load().TrayExclude);
            Console.WriteLine($"{list.Count} tray icons would be shown (running programs with a registered notification icon):");
            foreach (var e in list) Console.WriteLine($"  {(e.Promoted ? "[corner] " : "         ")}{System.IO.Path.GetFileName(e.ExePath),-34} icon={(e.Icon != null ? "yes" : "no ")}  {e.Tooltip}");
            return 0;
        }
        if (args.Contains("--tray-spike")) return TraySpike.Run(false);
        if (args.Contains("--tray-spike-register")) return TraySpike.Run(true);
        int dumpIdx = Array.IndexOf(args, "--layout-dump");
        if (dumpIdx >= 0 && dumpIdx + 1 < args.Length) return LayoutDump(args[dumpIdx + 1]);

        using var mutex = new Mutex(true, @"Local\Spread.Overview.Mutex", out bool first);
        if (!first && args.Contains("--restarted"))
        {
            first = AcquireAfterRestart(mutex, TimeSpan.FromSeconds(20));
            if (!first) Log.Write("restart: the previous instance did not exit within 20 s; this one ends");
        }
        var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Spread.Overview.Toggle");
        if (!first)
        {
            if (args.Contains("--toggle")) toggle.Set();
            return 0;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log.Write("unhandled: " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Write("fatal: " + e.ExceptionObject); TaskbarControl.Restore(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { TaskbarControl.Restore(); Log.Write("shutdown: process exit handlers"); };

        var cfg = Config.Load();
        Log.Configure(cfg);
        var apps = new AppCatalog();
        _ = apps.LoadAsync().ContinueWith(_ => apps.PreloadIcons());

        using var events = new WinEvents();
        var cache = new WindowCache(cfg, events);
        var snaps = new SnapshotCache(cfg, events);

        OverlayWindow? overlayRef = null;
        TopBarWindow? bar = null;
        if (TaskbarControl.IsHiddenBySpread && !cfg.TaskbarManaged) TaskbarControl.Restore();   // repair after a crash / config change
        if (cfg.TopBar)
        {
            bar = new TopBarWindow(cfg, events, cache, () => overlayRef?.Toggle());
            bar.Init();
        }
        if (cfg.TaskbarManaged) TaskbarControl.Hide(cfg.TaskbarWindowHidden);
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        guard.Tick += (_, _) => TaskbarControl.Guard();   // Explorer shows its taskbar window again now and then
        if (cfg.TaskbarWindowHidden) guard.Start();

        var usage = new UsageStore(cfg);
        usage.Attach(events);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { usage.FlushPending(); usage.Save(true); };
        var engine = new SearchEngine(apps, SettingsProvider.Load(cfg.Search), usage, cfg.Search);
        var overlay = new OverlayWindow(cfg, apps, cache, snaps, engine);
        overlay.Init();
        overlayRef = overlay;
        ToastManager.Init(cfg, () => overlay.IsOverlayVisible);
        if (bar != null) bar.WorkAreaChanged += overlay.RefreshPark;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => app.Dispatcher.BeginInvoke(new Action(overlay.RefreshPark));
        overlay.Prewarm();   // decode the wallpaper and warm the window list up front

        var edge = new HotEdge(cfg, ts => overlay.ShowOverlay(ts), overlay.Prewarm, () => overlay.IsOverlayVisible || TaskbarControl.Revealed);
        edge.Start();
        using WinKeyHook? hook = cfg.WinTap ? new WinKeyHook(() => overlay.Toggle(keyboard: true)) : null;

        ThreadPool.RegisterWaitForSingleObject(toggle, (_, _) => app.Dispatcher.BeginInvoke(overlay.Toggle), null, -1, false);

        // Ctrl+C in the console, closing the console window, logoff: restore the taskbar and release the reserved space
        _ctrl = type =>
        {
            Log.Write($"shutdown requested from the console (type {type})");
            try
            {
                using var done = new ManualResetEventSlim();
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        hook?.Dispose();                       // first: a hook left behind by a stuck process slows every key press system-wide
                        Log.Write("shutdown: keyboard hook removed");
                        overlay.HideOverlay(); Log.Write("shutdown: overlay hidden");
                        bar?.Unregister(); Log.Write("shutdown: bar unregistered");
                        TaskbarControl.Restore();
                    }
                    catch (Exception e) { Log.Write("shutdown step failed: " + e.Message); }
                    finally { done.Set(); Log.Write("shutdown: asking the app to exit"); app.Shutdown(); }
                }));
                done.Wait(3000);
                TaskbarControl.Restore();   // in case the UI thread was busy
            }
            catch { }
            // Cleanup is done. If a normal exit hangs for any reason, end the process so nothing stays behind.
            var killer = new Thread(() => { Thread.Sleep(800); Log.Write("shutdown: normal exit took too long, ending the process"); Process.GetCurrentProcess().Kill(); }) { IsBackground = true };
            killer.Start();
            return type == 0 || type == 1;   // Ctrl+C / Ctrl+Break: handled, we shut down normally; close/logoff: let Windows finish
        };
        SetConsoleCtrlHandler(_ctrl, true);

        using var tray = BuildTray(overlay, app);
        Log.Write($"started (hotEdge={cfg.HotEdge} dwell={cfg.HotEdgeDwellMs}ms, layout={cfg.Layout}, hotkey={cfg.Hotkey}, winTap={cfg.WinTap})");
        if (args.Contains("--show")) app.Dispatcher.BeginInvoke(new Action(() => overlay.ShowOverlay()));
        try { app.Run(); Log.Write("shutdown: message loop ended"); }
        finally { bar?.Unregister(); TaskbarControl.Restore(); Log.Write("shutdown: done"); }
        return 0;
    }

    static System.Windows.Forms.NotifyIcon BuildTray(OverlayWindow overlay, System.Windows.Application app)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show overview", null, (_, _) => overlay.ShowOverlay(0, false, "tray"));

        var autostart = new System.Windows.Forms.ToolStripMenuItem("Start with Windows") { Checked = IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add("Restore Windows taskbar", null, (_, _) => TaskbarControl.Restore(force: true));
        menu.Items.Add("Open settings folder", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", Config.Dir) { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { overlay.HideOverlay(); app.Shutdown(); });

        var ni = new System.Windows.Forms.NotifyIcon { Icon = MakeIcon(), Text = "Spread", Visible = true, ContextMenuStrip = menu };
        ni.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) overlay.Toggle(); };
        return ni;
    }

    /// <summary>
    /// Command line that starts this app again. Spread has no exe of its own (it runs as "dotnet Spread.dll"), so when the host
    /// is dotnet the command must name the dll too. Note: dotnet.exe is a console program, so a console window is shown
    /// (minimized by "start /min"); a signed exe from IT would remove that.
    /// </summary>
    internal static string LaunchCommand()
    {
        string host = Environment.ProcessPath ?? "";
        if (System.IO.Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return $"cmd.exe /c start \"\" /min \"{host}\" \"{typeof(Program).Assembly.Location}\"";
        return "\"" + host + "\"";
    }

    internal static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Spread") != null;
    }

    internal static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on) k.SetValue("Spread", LaunchCommand()); else k.DeleteValue("Spread", false);
    }

    /// <summary>
    /// Started by <see cref="Restart"/>: the old instance is still shutting down. Wait for it to let go of the single-instance lock
    /// (an abandoned lock - the old process was ended by force - is ours just the same).
    /// </summary>
    internal static bool AcquireAfterRestart(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Quits Spread the normal way (the bar gives its space back and the Windows taskbar returns).</summary>
    internal static void Quit() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()));

    /// <summary>
    /// Starts a new Spread with "--restarted" (it waits until this one has let go of its single-instance lock) and quits this one.
    /// It used to delay the start with "timeout /t 3", but timeout needs a console and fails at once in a hidden cmd: the new
    /// instance then found the lock still taken and exited, and Restart only quit Spread.
    /// </summary>
    internal static void Restart()
    {
        try
        {
            string host = Environment.ProcessPath ?? "";
            string start = System.IO.Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"start \"\" /min \"{host}\" \"{typeof(Program).Assembly.Location}\" --restarted"
                : $"start \"\" \"{host}\" --restarted";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {start}") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e) { Log.Write("restart failed: " + e.GetType().Name); return; }
        Quit();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(0x35, 0x84, 0xE4));
            foreach (var r in new[] { new Rectangle(2, 3, 14, 11), new Rectangle(18, 3, 12, 11), new Rectangle(2, 17, 10, 12), new Rectangle(14, 17, 16, 12) })
                g.FillRectangle(b, r);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---------- self tests (print nothing private) ----------

    /// <summary>Invariants only. Exact GNOME parity is checked by tools/parity.js via --layout-dump.</summary>
    static int LayoutTest()
    {
        var rnd = new Random(42);
        int fail = 0;
        var area = new LArea(80, 110, 3253, 1274);
        var layout = new GnomeLayout(1440, 20 + 24.2, 20 + 24.2);
        for (int c = 0; c < 500; c++)
        {
            int n = rnd.Next(1, 25);
            var boxes = Enumerable.Range(0, n).Select(_ => new LBox(rnd.Next(0, 2000), rnd.Next(0, 900), rnd.Next(200, 3400), rnd.Next(150, 1440))).ToList();
            var slots = layout.Compute(boxes, area);
            for (int i = 0; i < n; i++)
            {
                var s = slots[i];
                bool aspectOk = Math.Abs(s.Width / s.Height - boxes[i].Width / boxes[i].Height) < 0.01;
                bool inside = s.X >= area.X - 1 && s.Y >= area.Y - 1 && s.X + s.Width <= area.X + area.Width + 1 && s.Y + s.Height <= area.Y + area.Height + 1;
                bool capped = s.Width <= boxes[i].Width * GnomeLayout.WindowPreviewMaximumScale + 1e-6;
                if (!aspectOk || !inside || !capped) { fail++; if (fail < 5) Console.WriteLine($"FAIL case {c} item {i}: aspect={aspectOk} inside={inside} capped={capped} n={n}"); }
            }
        }
        var demo = layout.Compute(new[] { new LBox(0, 0, 3400, 1400), new LBox(200, 100, 1400, 900), new LBox(1700, 200, 900, 700), new LBox(900, 500, 1200, 800) }, area);
        Console.WriteLine("demo layout (4 windows on 3413x1440):");
        foreach (var r in demo) Console.WriteLine($"  x={r.X:0} y={r.Y:0} w={r.Width:0} h={r.Height:0}");
        Console.WriteLine(fail == 0 ? "LAYOUT TEST PASSED (500 random cases)" : $"LAYOUT TEST FAILED ({fail} violations)");
        return (fail == 0 ? 0 : 1) | NaturalLayoutTest() | StackLayoutTest();
    }

    /// <summary>Natural layout on the user's real monitor geometry (5120x2160, scale 1.5), 1..30 windows.</summary>
    static int NaturalLayoutTest()
    {
        const double W = 5120, H = 2160, S = 1.5;
        var area = new LArea(40 * S, 100 * S, W - 80 * S, H - 124 * S);
        double top = 19 * S, bottom = 51 * S, side = 5 * S, gap = 20 * S;
        var layout = new NaturalLayout(W, H, top, bottom, side, gap, 0.95, 220 * S);
        var rnd = new Random(7);
        int overlapFail = 0, boundsFail = 0, aspectFail = 0, cases = 0;
        double corrSum = 0; int corrN = 0, worstMs = 0; double totalMs = 0;
        layout.Compute(new[] { new LBox(0, 0, 800, 600) }, area);   // JIT warm-up
        for (int c = 0; c < 600; c++)
        {
            int n = 1 + c % 30;
            var boxes = new List<LBox>();
            bool stacked = c % 3 == 0;   // many users keep several maximized windows on top of each other
            for (int i = 0; i < n; i++)
            {
                double w = stacked ? rnd.Next(1800, 5100) : rnd.Next(500, 2800), h = stacked ? rnd.Next(1000, 2100) : rnd.Next(350, 1600);
                boxes.Add(new LBox(rnd.Next(0, (int)(W - w) + 1), rnd.Next(0, (int)(H - h) + 1), w, h));
            }
            var sw = Stopwatch.StartNew();
            var slots = layout.Compute(boxes, area);
            sw.Stop(); totalMs += sw.Elapsed.TotalMilliseconds; worstMs = Math.Max(worstMs, (int)Math.Ceiling(sw.Elapsed.TotalMilliseconds));
            cases++;

            bool overlap = false, outside = false, aspect = false;
            for (int i = 0; i < n; i++)
            {
                var a = slots[i];
                if (Math.Abs(a.Width / a.Height - boxes[i].Width / boxes[i].Height) > 0.005 * boxes[i].Width / boxes[i].Height) aspect = true;
                double li = a.X - side - gap / 2, ri = a.X + a.Width + side + gap / 2, ti = a.Y - top - gap / 2, bi = a.Y + a.Height + bottom + gap / 2;
                if (li < area.X - 2 || ri > area.X + area.Width + 2 || ti < area.Y - 2 || bi > area.Y + area.Height + 2) outside = true;
                for (int j = i + 1; j < n; j++)
                {
                    var b = slots[j];
                    double lj = b.X - side - gap / 2, rj = b.X + b.Width + side + gap / 2, tj = b.Y - top - gap / 2, bj = b.Y + b.Height + bottom + gap / 2;
                    if (Math.Min(ri, rj) - Math.Max(li, lj) > 2 && Math.Min(bi, bj) - Math.Max(ti, tj) > 2) overlap = true;
                }
            }
            if (overlap && overlapFail < 8) Console.WriteLine($"  overlap case {c}: n={n} stacked={stacked} scale={layout.LastScale:0.000} feasible={layout.LastFeasible}");
            if (overlap) overlapFail++; if (outside) boundsFail++; if (aspect) aspectFail++;
            if (n >= 4 && !stacked)
            {
                corrSum += (Corr(boxes.Select(b => b.CenterX).ToArray(), slots.Select(s => s.X + s.Width / 2).ToArray()) +
                            Corr(boxes.Select(b => b.CenterY).ToArray(), slots.Select(s => s.Y + s.Height / 2).ToArray())) / 2;
                corrN++;
            }
        }
        double avgCorr = corrN > 0 ? corrSum / corrN : 1;
        Console.WriteLine($"natural layout: {cases} cases | overlap {overlapFail}, outside {boundsFail}, aspect {aspectFail} | " +
                          $"position correlation {avgCorr:0.00} | avg {totalMs / cases:0.00} ms, worst {worstMs} ms");
        var demo = layout.Compute(new[] { new LBox(0, 0, 5100, 2100), new LBox(300, 200, 2400, 1500), new LBox(2600, 500, 2000, 1200), new LBox(1500, 900, 1600, 1000) }, area);
        foreach (var d in demo) Console.WriteLine($"  x={d.X:0} y={d.Y:0} w={d.Width:0} h={d.Height:0}");
        bool ok = overlapFail <= cases * 0.02 && boundsFail <= cases * 0.01 && aspectFail == 0 && avgCorr >= 0.75 && worstMs < 25;
        Console.WriteLine(ok ? "NATURAL LAYOUT TEST PASSED" : "NATURAL LAYOUT TEST FAILED");
        return ok ? 0 : 1;
    }

    /// <summary>Photo-stack geometry, alone and together with ordinary windows in the natural layout.</summary>
    static int StackLayoutTest()
    {
        var rnd = new Random(11);
        int bad = 0;
        // 1. geometry invariants
        for (int c = 0; c < 400; c++)
        {
            int n = rnd.Next(1, 25), visible = rnd.Next(1, 8);
            var sizes = Enumerable.Range(0, n).Select(_ => ((double)rnd.Next(300, 2500), (double)rnd.Next(200, 1500))).ToList();
            double pct = 2 + rnd.NextDouble() * 6;
            double off = StackLayout.Offset(sizes, pct);
            int v = StackLayout.Visible(n, visible);
            var firstBox = StackLayout.Cards(sizes, 0, visible, pct, out double bw0, out double bh0);
            for (int front = 0; front < n; front++)
            {
                var r = StackLayout.Cards(sizes, front, visible, pct, out double bw, out double bh);
                if (Math.Abs(bw - bw0) > 1e-6 || Math.Abs(bh - bh0) > 1e-6) { bad++; Console.WriteLine($"  stack box depends on front (case {c})"); }
                var depths = Enumerable.Range(0, n).Select(j => StackLayout.Depth(j, front, n)).OrderBy(d => d).ToList();
                if (!depths.SequenceEqual(Enumerable.Range(0, n))) { bad++; Console.WriteLine($"  depths are not a permutation (case {c})"); }
                for (int j = 0; j < n; j++)
                {
                    if (r[j].X < -1e-6 || r[j].Y < -1e-6 || r[j].X + r[j].Width > bw + 1e-6 || r[j].Y + r[j].Height > bh + 1e-6) { bad++; Console.WriteLine($"  card outside the box (case {c})"); }
                    int d = StackLayout.Depth(j, front, n);
                    if (d == 0 && (Math.Abs(r[j].X - (v - 1) * off) > 1e-6)) { bad++; Console.WriteLine($"  front card not at the front position (case {c})"); }
                    if (d >= 1 && d < v)
                    {
                        // the card one step closer to the front sits `off` further down-right: a strip of this card is uncovered
                        int above = Enumerable.Range(0, n).First(k => StackLayout.Depth(k, front, n) == d - 1);
                        if (Math.Abs((r[above].X - r[j].X) - off) > 1e-6 || Math.Abs((r[above].Y - r[j].Y) - off) > 1e-6) { bad++; Console.WriteLine($"  no strip between depth {d - 1} and {d} (case {c})"); }
                    }
                }
            }
        }
        // 2. stacks as single boxes next to ordinary windows
        const double W = 5120, H = 2160, S = 1.5;
        var area = new LArea(40 * S, 100 * S, W - 80 * S, H - 124 * S);
        var layout = new NaturalLayout(W, H, 19 * S, 51 * S, 5 * S, 20 * S, 0.95, 220 * S);
        int overlap = 0, cases = 0;
        for (int c = 0; c < 300; c++)
        {
            var boxes = new List<LBox>();
            int singles = rnd.Next(0, 10), stacks = rnd.Next(1, 4);
            for (int i = 0; i < singles; i++) { double w = rnd.Next(500, 2800), h = rnd.Next(350, 1500); boxes.Add(new LBox(rnd.Next(0, (int)(W - w)), rnd.Next(0, (int)(H - h)), w, h)); }
            for (int k = 0; k < stacks; k++)
            {
                int n = rnd.Next(3, 15);
                var sizes = Enumerable.Range(0, n).Select(_ => ((double)rnd.Next(500, 2200), (double)rnd.Next(350, 1400))).ToList();
                StackLayout.Cards(sizes, 0, 5, 4, out double bw, out double bh);
                boxes.Add(new LBox(rnd.Next(0, (int)Math.Max(1, W - bw)), rnd.Next(0, (int)Math.Max(1, H - bh)), bw, bh));
            }
            var slots = layout.Compute(boxes, area);
            cases++;
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = slots[i]; var b = slots[j];
                    if (Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X) > 2 && Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) > 2) { overlap++; break; }
                }
        }
        Console.WriteLine($"stack layout: geometry problems {bad}, stack+window layouts with overlapping slots {overlap}/{cases}");
        bool ok = bad == 0 && overlap <= cases * 0.02;
        Console.WriteLine(ok ? "STACK LAYOUT TEST PASSED" : "STACK LAYOUT TEST FAILED");
        return ok ? 0 : 1;
    }

    static double Corr(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
        return saa < 1e-9 || sbb < 1e-9 ? 1 : sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>--layout-dump cases.json : prints slots for each case as JSON (used by tools/parity.js).</summary>
    static int LayoutDump(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var results = new List<object>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            double D(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).GetDouble();
            var a = c.GetProperty("area");
            var layout = new GnomeLayout(D(c.GetProperty("monitor"), "h"), D(c, "rowSpacing"), D(c, "colSpacing"));
            var boxes = c.GetProperty("windows").EnumerateArray().Select(w => new LBox(D(w, "x"), D(w, "y"), D(w, "w"), D(w, "h"))).ToList();
            var slots = layout.Compute(boxes, new LArea(D(a, "x"), D(a, "y"), D(a, "w"), D(a, "h")));
            results.Add(slots.Select(s => new { x = s.X, y = s.Y, w = s.Width, h = s.Height }).ToArray());
        }
        Console.Out.Write(System.Text.Json.JsonSerializer.Serialize(new { results }));
        return 0;
    }

    static int ListWindows()
    {
        var wins = WindowEnumerator.Enumerate(IntPtr.Zero, new Config());
        Console.WriteLine($"{wins.Count} windows would appear:");
        foreach (var g in wins.GroupBy(w => w.ProcName).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-28} x{g.Count()}  minimized={g.Count(w => w.Minimized)}");
        return 0;
    }
}
