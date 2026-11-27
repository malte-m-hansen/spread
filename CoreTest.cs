using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace Spread;

/// <summary>
/// `--core-test`: config loading/saving (in a temp folder: your real config.json is not touched), the taskbar restore decision,
/// the restart hand-over of the single-instance lock, and the wallpaper cache eviction.
/// </summary>
internal static class CoreTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    public static int Run()
    {
        TaskbarChecks();
        TaskbarModeChecks();
        HotkeyChecks();
        FullscreenChecks();
        StillFullscreenChecks();
        ConfigChecks();
        ConfigMergeChecks();
        RestartChecks();
        WallpaperChecks();
        TickRecoveryChecks();
        DpiRectChecks();
        MinimizedPlacementChecks();
        VersionChecks();
        AppBarChecks();
        DpiChecks();
        ResultsLayoutChecks();
        AltMenuChecks();
        Console.WriteLine(_fail == 0 ? "CORE TEST PASSED" : $"CORE TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    static void ConfigMergeChecks()
    {
        Console.WriteLine("Config merge, nulls, autostart:");
        string dir = Path.Combine(Path.GetTempPath(), "spread-core-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "config.json");
        string oldLog = Log.FilePath;
        try
        {
            Log.FilePath = Path.Combine(dir, "spread.log"); Log.ResetSize();

            File.WriteAllText(file, "{ \"Version\": 17, \"HotEdge\": \"left\", \"FutureThing\": { \"A\": 1 }, \"Dock\": { \"IconSize\": 40, \"NewDockKey\": true } }");
            var c = Config.LoadFrom(file);
            c.Layout = "gnome"; c.Save();
            string saved = File.ReadAllText(file);
            Check("save keeps keys this build does not know (top level and nested)", saved.Contains("FutureThing") && saved.Contains("NewDockKey") && saved.Contains("\"A\": 1"), saved);
            Check("save still writes the change next to them", Config.LoadFrom(file) is { LoadFailed: false, Layout: "gnome", HotEdge: "left" });

            File.WriteAllText(file, "{ \"Version\": 17, \"HotEdge\": \"left\" }");
            c = Config.LoadFrom(file);
            File.WriteAllText(file, "{ \"Version\": 17, \"HotEdge\": \"right\", \"Dock\": { \"IconSize\": 55 } }");   // hand edit while Spread runs
            c.Dock.Enabled = !c.Dock.Enabled; bool dockNow = c.Dock.Enabled; c.Save();
            var back = Config.LoadFrom(file);
            Check("a hand edit made after load survives a Save of another key", back.HotEdge == "right" && back.Dock.IconSize == 55, back.HotEdge + "/" + back.Dock.IconSize);
            Check("the value the app changed is written", back.Dock.Enabled == dockNow);
            c.Dock.Enabled = !dockNow; c.Save();
            Check("a second Save keeps merging (snapshot refreshed after a save)", Config.LoadFrom(file) is { HotEdge: "right" } b2 && b2.Dock.Enabled == !dockNow);

            File.WriteAllText(file, "{ \"Version\":17, \"HotEdge\":\"left\", \"hotedge\":\"right\" }");   // duplicate key: Load accepts it, the merge parse does not
            c = Config.LoadFrom(file);
            c.Layout = "gnome"; c.Save();
            Check("duplicate keys in config.json: a Save still succeeds and the change is in the file", !c.LoadFailed && Config.LoadFrom(file) is { LoadFailed: false, Layout: "gnome" }, File.ReadAllText(file));

            File.WriteAllText(file, "{ \"Version\": 17, \"HotEdge\": \"left\" }");
            c = Config.LoadFrom(file);
            c.Layout = "gnome";
            var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);   // like a virus scanner: no delete/replace while open
            var release = System.Threading.Tasks.Task.Run(() => { Thread.Sleep(150); hold.Dispose(); });
            c.Save(); release.Wait();
            Check("a config.json held open for a moment: the Save retries the swap and lands", Config.LoadFrom(file) is { LoadFailed: false, Layout: "gnome" }, File.ReadAllText(file));

            File.WriteAllText(file, "{ \"Version\": 17, \"Search\": null, \"HideTaskbar\": null }");
            c = Config.LoadFrom(file);
            Check("a null settings section gets its defaults", !c.LoadFailed && c.Search != null && c.Search.Keys != null && c.HideTaskbar == new Config().HideTaskbar, $"failed={c.LoadFailed}");
            File.WriteAllText(file, "{ \"Version\": 15, \"Search\": { \"Keys\": null } }");
            c = Config.LoadFrom(file);
            Check("a null nested section gets its defaults (and migrations still run)", !c.LoadFailed && c.Search.Keys != null && c.Version == 17, $"failed={c.LoadFailed}");

            Check("autostart: a value pointing elsewhere is refreshed", Program.ShouldRefreshAutostart("old command", "new command"));
            Check("autostart: the same value, or autostart off, is left alone", !Program.ShouldRefreshAutostart("same", "same") && !Program.ShouldRefreshAutostart(null, "new command"));
        }
        finally
        {
            Log.FilePath = oldLog; Log.ResetSize();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void AltMenuChecks()
    {
        Console.WriteLine("Alt key and the window menu:");
        const int WM_SYSCOMMAND = 0x0112, WM_KEYDOWN = 0x0100;
        Check("Alt (alone, or with Space or a letter) would open the window menu: that command is blocked",
            OverlayWindow.IsKeyMenuCommand(WM_SYSCOMMAND, (IntPtr)0xF100) && OverlayWindow.IsKeyMenuCommand(WM_SYSCOMMAND, (IntPtr)0xF102));
        Check("other window commands (minimize, close, move) and other messages are left alone",
            !OverlayWindow.IsKeyMenuCommand(WM_SYSCOMMAND, (IntPtr)0xF020) && !OverlayWindow.IsKeyMenuCommand(WM_SYSCOMMAND, (IntPtr)0xF060)
            && !OverlayWindow.IsKeyMenuCommand(WM_SYSCOMMAND, (IntPtr)0xF010) && !OverlayWindow.IsKeyMenuCommand(WM_KEYDOWN, (IntPtr)0xF100));
    }

    static void ResultsLayoutChecks()
    {
        Console.WriteLine("Search results layout:");
        var v = new ResultsView();
        for (int i = 0; i < 3; i++) v.StripPanel.Children.Add(new System.Windows.Controls.Border { Width = 124, Height = 110, Margin = new System.Windows.Thickness(3) });
        v.KeyHint.Text = "Enter: open / go to it   Ctrl: new window   Shift: new window   Ctrl+Shift: run as administrator   Alt: open file location";
        v.Host.Visibility = System.Windows.Visibility.Visible;
        var area = new System.Windows.Controls.Grid { Width = 2000, Height = 900 };
        area.Children.Add(v.Host);
        void Layout() { area.Measure(new System.Windows.Size(2000, 900)); area.Arrange(new System.Windows.Rect(0, 0, 2000, 900)); area.UpdateLayout(); }
        System.Windows.Point At(System.Windows.FrameworkElement e) => e.TranslatePoint(new System.Windows.Point(0, 0), area);
        Layout();
        var first = At((System.Windows.FrameworkElement)v.StripPanel.Children[0]);
        // where the text really is: a stretched TextBlock reports the whole panel width, so use its own width
        double TextCentre() => At(v.KeyHint).X + (v.KeyHint.DesiredSize.Width - v.KeyHint.Margin.Left - v.KeyHint.Margin.Right) / 2;
        double hintCentre = TextCentre();

        // the Everything list arrives: up to 980 wide, much taller than the icon line
        v.FilesBox.Visibility = System.Windows.Visibility.Visible; v.FilesBox.Width = 980;
        v.FilesPanel.Children.Add(new System.Windows.Controls.Border { Height = 320 });
        Layout();
        var after = At((System.Windows.FrameworkElement)v.StripPanel.Children[0]);
        double hintCentreAfter = TextCentre();
        Check("the icons do not move sideways when the file list arrives", Math.Abs(after.X - first.X) < 0.5, $"x {first.X:0.#} -> {after.X:0.#}");
        Check("the icons do not move up or down when the file list arrives", Math.Abs(after.Y - first.Y) < 0.5, $"y {first.Y:0.#} -> {after.Y:0.#}");
        Check("the hint stays centred under the icons", Math.Abs(hintCentreAfter - hintCentre) < 0.5, $"{hintCentre:0.#} -> {hintCentreAfter:0.#}");
        Check("the icons are centred in the results block", Math.Abs((after.X - 3 + 3 * 130 / 2.0) - 1000) < 1, $"centre {after.X - 3 + 3 * 130 / 2.0:0.#}");
    }

    static void DpiChecks()
    {
        Console.WriteLine("Screen scale (DPI):");
        // Spread runs as "dotnet Spread.dll": Windows reads dotnet.exe's manifest, not app.manifest, so the mode must be set in code.
        // In "system aware" mode (what WPF picks otherwise) the bar's reserved space was off by the ratio of two scales after a dock.
        Check("the process is per-monitor DPI aware (v2), not system aware", Dpi.IsPerMonitorV2, Dpi.Describe() + " " + (Dpi.StartupError ?? ""));
        Check("popover x stays on its monitor, in that monitor's own scale", Popover.ClampX(5000, 300, 0, 5120, 1.25) == 4096 - 300 - 8 && Popover.ClampX(-50, 300, 0, 5120, 1.25) == 4);
        Check("popover x on a monitor left of the primary", Popover.ClampX(-3000, 300, -2560, 0, 1.0) == -2560 + 4 && Popover.ClampX(-100, 300, -2560, 0, 1.0) == -308);
    }

    static void AppBarChecks()
    {
        Console.WriteLine("Top bar (AppBar) after a display change:");
        Check("bar height follows the monitor scale (32 DIP)", AppBarMath.HeightPx(32, 96) == 32 && AppBarMath.HeightPx(32, 120) == 40 && AppBarMath.HeightPx(32, 144) == 48 && AppBarMath.HeightPx(32, 192) == 64);
        var mon = new RECT { Left = 0, Top = 0, Right = 5120, Bottom = 2160 };
        RECT Work(int top) => new() { Left = 0, Top = top, Right = 5120, Bottom = 2160 };
        Check("a reservation taller than the bar is a gap (the laptop's 48 px left behind on a 125% monitor = 8 px of wallpaper)", AppBarMath.ReservedGap(mon, Work(48), 40) == 8);
        Check("a matching reservation has no gap; a missing one is negative", AppBarMath.ReservedGap(mon, Work(40), 40) == 0 && AppBarMath.ReservedGap(mon, Work(0), 40) == -40);
        Check("the gap is measured from the monitor's own top (a monitor not at 0,0)", AppBarMath.ReservedGap(new RECT { Left = -2880, Top = -300, Right = 0, Bottom = 1500 }, new RECT { Left = -2880, Top = -252, Right = 0, Bottom = 1500 }, 48) == 0);
        Check("checking again is bounded: 400 ms, 1 s, 2.5 s, then give up", AppBarMath.SettleDelayMs(0) == 400 && AppBarMath.SettleDelayMs(1) == 1000 && AppBarMath.SettleDelayMs(2) == 2500 && AppBarMath.SettleDelayMs(3) < 0 && AppBarMath.SettleDelayMs(99) < 0);
    }

    static void VersionChecks()
    {
        Console.WriteLine("Version:");
        Check("version strips the commit suffix", AppInfo.Parse("0.1.0+abc123") == "0.1.0" && AppInfo.Parse("1.2.3-beta.1+x") == "1.2.3-beta.1");
        Check("version falls back to dev", AppInfo.Parse(null) == "dev" && AppInfo.Parse("  ") == "dev" && AppInfo.Parse("+abc") == "dev");
        Check("this build has a real version", AppInfo.Version != "dev" && char.IsDigit(AppInfo.Version[0]), AppInfo.Version);
    }

    static void TaskbarModeChecks()
    {
        Console.WriteLine("Taskbar mode memory and restore latch:");
        var m = new TaskbarMode();
        Check("hides are allowed before any restore", m.HideAllowed);
        m.Remember(false);   // autohide start-up
        Check("autohide start-up: a later re-hide keeps the window visible (hideWindow false)", !m.HideWindow);
        m.Remember(true);
        Check("hidden start-up: a later re-hide hides the window again", m.HideWindow);
        m.Latch();
        Check("after a forced restore no hide is allowed", !m.HideAllowed);
    }

    static void HotkeyChecks()
    {
        Console.WriteLine("Hotkey parsing:");
        bool Is(string s, uint mods, uint vk) => HotkeyParser.TryParse(s, out uint m, out uint v) && m == mods && v == vk;
        Check("Ctrl+Alt+Space", Is("Ctrl+Alt+Space", 3, 0x20));
        Check("Ctrl+Alt+1 is the digit 1, not Cancel", Is("Ctrl+Alt+1", 3, 0x31));
        Check("Ctrl+Shift+9", Is("Ctrl+Shift+9", 6, 0x39));
        Check("Ctrl+Alt+12 is rejected", !HotkeyParser.TryParse("Ctrl+Alt+12", out _, out _));
        Check("F5", Is("F5", 0, 0x74));
        Check("Ctrl+Oem3 still works", HotkeyParser.TryParse("Ctrl+Oem3", out _, out uint v3) && v3 == 0xC0);
        Check("garbage is rejected", !HotkeyParser.TryParse("Ctrl+Banana", out _, out _) && !HotkeyParser.TryParse("", out _, out _) && !HotkeyParser.TryParse("Ctrl+", out _, out _));
    }

    static void FullscreenChecks()
    {
        Console.WriteLine("Top bar z-order:");
        Check("no fullscreen app: the bar is topmost", TopBarWindow.ZOrderFor(false) == Native.HWND_TOPMOST);
        Check("fullscreen app open: the bar stays behind", TopBarWindow.ZOrderFor(true) != Native.HWND_TOPMOST && TopBarWindow.ZOrderFor(true) == (IntPtr)1);
    }

    static void StillFullscreenChecks()
    {
        Console.WriteLine("Top bar fullscreen flag that never got cleared:");
        var mon = new RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };
        var full = mon;
        var normal = new RECT { Left = 100, Top = 100, Right = 900, Bottom = 700 };
        var other = new RECT { Left = 2560, Top = 0, Right = 5120, Bottom = 1440 };
        Check("flag set and a window covers the monitor: still fullscreen", TopBarWindow.StillFullscreen(true, full, true, mon));
        Check("flag set but the foreground window is an ordinary one: cleared", !TopBarWindow.StillFullscreen(true, normal, true, mon));
        Check("flag set but no foreground window (app killed): cleared", !TopBarWindow.StillFullscreen(true, default, false, mon));
        Check("flag set but the fullscreen window is on another monitor: cleared", !TopBarWindow.StillFullscreen(true, other, true, mon));
        Check("flag not set stays not set", !TopBarWindow.StillFullscreen(false, full, true, mon));
        Check("a window larger than the monitor (borderless overscan) counts", TopBarWindow.StillFullscreen(true, new RECT { Left = -8, Top = -8, Right = 2568, Bottom = 1448 }, true, mon));
    }

    static void TaskbarChecks()
    {
        Console.WriteLine("Taskbar restore:");
        Check("forced restore always clears auto-hide", TaskbarControl.ShouldClearAutohide(true, false, null) && TaskbarControl.ShouldClearAutohide(true, true, 1));
        Check("no marker: Spread did not switch auto-hide on, it stays", !TaskbarControl.ShouldClearAutohide(false, false, null));
        Check("marker says auto-hide was off before: cleared", TaskbarControl.ShouldClearAutohide(false, true, 0) && TaskbarControl.ShouldClearAutohide(false, true, 2 /*always on top*/));
        Check("marker says auto-hide was ON before: the user keeps it", !TaskbarControl.ShouldClearAutohide(false, true, 1) && !TaskbarControl.ShouldClearAutohide(false, true, 3));
        Check("unreadable marker: cleared (old behaviour)", TaskbarControl.ShouldClearAutohide(false, true, null));
    }

    static void ConfigChecks()
    {
        Console.WriteLine("Config:");
        string dir = Path.Combine(Path.GetTempPath(), "spread-core-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "config.json");
        string oldLog = Log.FilePath;
        DateTime realStamp = File.Exists(Config.FilePath) ? File.GetLastWriteTimeUtc(Config.FilePath) : DateTime.MinValue;
        try
        {
            Log.FilePath = Path.Combine(dir, "spread.log"); Log.ResetSize();

            File.WriteAllText(file, "{\n  // my settings\n  \"Version\": 15,\n  \"HotEdge\": \"top\",\n  \"Dock\": { \"IconSize\": 40, },\n}\n");
            var c = Config.LoadFrom(file);
            Check("comments and trailing commas are accepted", !c.LoadFailed && c.HotEdge == "top" && c.Dock.IconSize == 40, $"failed={c.LoadFailed} edge={c.HotEdge}");

            const string broken = "{ \"Version\": 15, \"HotEdge\": \"top\" \"Layout\": \"gnome\" }";
            File.WriteAllText(file, broken);
            c = Config.LoadFrom(file);
            Check("broken file: defaults, marked as not loaded", c.LoadFailed && c.HotEdge == new Config().HotEdge);
            c.Dock.Pinned.Add("id:Test"); c.Save();
            Check("broken file is never overwritten by Save", File.ReadAllText(file) == broken);

            File.WriteAllText(file, "null");
            c = Config.LoadFrom(file); c.Save();
            Check("a file holding 'null' counts as unreadable and is kept", c.LoadFailed && File.ReadAllText(file) == "null");

            File.WriteAllText(file, "{ \"Version\": 15, \"HotEdgeDwellMs\": \"fast\" }");
            c = Config.LoadFrom(file);
            Check("a wrongly typed value counts as unreadable", c.LoadFailed);

            File.WriteAllText(file, "{ \"Version\": 15, \"HotEdge\": \"left\", \"HideTaskbar\": \"off\" }");
            c = Config.LoadFrom(file);
            c.Layout = "gnome"; c.Save();
            string saved = File.ReadAllText(file);
            Check("save writes the change", Config.LoadFrom(file) is { LoadFailed: false, Layout: "gnome", HotEdge: "left" });
            Check("save leaves no temporary file behind", !File.Exists(file + ".tmp"));
            Check("derived/internal properties are not written", !saved.Contains("TaskbarManaged") && !saved.Contains("TaskbarWindowHidden") && !saved.Contains("LoadFailed"));

            File.WriteAllText(file, "{ \"Version\": 1, \"HotEdgeDwellMs\": 150 }");
            c = Config.LoadFrom(file);
            Check("old file is migrated and written back", c.Version == 17 && c.HotEdgeDwellMs == 40 && File.ReadAllText(file).Contains("\"Version\": 17"));

            File.Delete(file);
            c = Config.LoadFrom(file);
            Check("missing file is created with defaults", !c.LoadFailed && File.Exists(file) && c.Version == 17);

            File.WriteAllText(file, "{ \"Version\": 15, \"Search\": { \"Keys\": { \"AppAlt\": \"default\" } } }");
            c = Config.LoadFrom(file);
            Check("v16: the old Alt+Enter default on apps becomes open file location, and is written back", c.Version == 17 && c.Search.Keys.AppAlt == "reveal" && File.ReadAllText(file).Contains("\"AppAlt\": \"reveal\""), c.Search.Keys.AppAlt);
            c.Search.Keys.AppAlt = "default"; c.Save();
            Check("v16: a later deliberate choice of default is kept", Config.LoadFrom(file).Search.Keys.AppAlt == "default");
            File.WriteAllText(file, "{ \"Version\": 15, \"Search\": { \"Keys\": { \"AppAlt\": \"copyPath\" } } }");
            Check("v16: another saved Alt action is left alone", Config.LoadFrom(file).Search.Keys.AppAlt == "copyPath");

            File.WriteAllText(file, "{ \"Version\": 16, \"Search\": { \"Keys\": { \"FileCtrl\": \"reveal\", \"FileAlt\": \"copyPath\" } } }");
            c = Config.LoadFrom(file);
            Check("v17: the old file keys (Ctrl = show in Explorer, Alt = copy path) follow the app keys, and are written back", c.Version == 17 && c.Search.Keys.FileCtrl == "openWith" && c.Search.Keys.FileAlt == "reveal" && File.ReadAllText(file).Contains("\"FileAlt\": \"reveal\""), c.Search.Keys.FileCtrl + "/" + c.Search.Keys.FileAlt);
            File.WriteAllText(file, "{ \"Version\": 16, \"Search\": { \"Keys\": { \"FileCtrl\": \"openFolder\", \"FileAlt\": \"openWith\" } } }");
            c = Config.LoadFrom(file);
            Check("v17: file keys that were changed on purpose are left alone", c.Search.Keys.FileCtrl == "openFolder" && c.Search.Keys.FileAlt == "openWith");

            DateTime after = File.Exists(Config.FilePath) ? File.GetLastWriteTimeUtc(Config.FilePath) : DateTime.MinValue;
            Check("the real config.json was not touched", after == realStamp);
        }
        finally
        {
            Log.FilePath = oldLog; Log.ResetSize();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void RestartChecks()
    {
        Console.WriteLine("Restart hand-over:");

        // "old" plays the running instance (owns the lock), then ends in the given way; "mine" is the instance started by Restart
        bool Scenario(Action<Mutex> oldInstanceEnds, int waitMs, out long tookMs)
        {
            string name = @"Local\Spread.CoreTest." + Guid.NewGuid().ToString("N");   // fresh per scenario: an existing mutex is not owned by its opener
            using var held = new ManualResetEventSlim();
            var old = new Thread(() =>
            {
                var m = new Mutex(true, name, out _);
                held.Set();
                oldInstanceEnds(m);
            }) { IsBackground = true };
            old.Start();
            held.Wait();
            using var mine = new Mutex(true, name, out bool first);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool got = !first && Program.AcquireAfterRestart(mine, TimeSpan.FromMilliseconds(waitMs));
            tookMs = sw.ElapsedMilliseconds;
            old.Join(5000);
            if (got) try { mine.ReleaseMutex(); } catch { }
            return got;
        }

        Check("lock released by the quitting instance: acquired", Scenario(m => { Thread.Sleep(300); m.ReleaseMutex(); m.Dispose(); }, 5000, out long t1) && t1 >= 200, $"took {t1} ms");
        Check("instance ended by force (abandoned lock): acquired", Scenario(m => Thread.Sleep(300) /* thread ends while owning it */, 5000, out long t2), $"took {t2} ms");
        Check("instance that never quits: gives up after the wait", !Scenario(m => { Thread.Sleep(1500); m.ReleaseMutex(); }, 300, out long t3) && t3 < 1000, $"took {t3} ms");
    }

    static void WallpaperChecks()
    {
        Console.WriteLine("Wallpaper cache:");
        var m1 = new RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };
        var m2 = new RECT { Left = 2560, Top = 0, Right = 5120, Bottom = 1440 };   // same size as m1
        string K(string path, string? mon, RECT r, int pos = 4) => Wallpaper.CacheKey(path, mon, r, pos);
        const string idA = @"\\?\DISPLAY#A#1", idB = @"\\?\DISPLAY#B#2";

        var d = new ConcurrentDictionary<string, int>();
        foreach (var k in new[] { K(@"C:\w\a.jpg", idA, m1), K(@"C:\w\b.jpg", idB, m2) }) d[k] = 1;
        Wallpaper.Evict(d, K(@"C:\w\b.jpg", idB, m2), 3);
        Check("two same-size monitors with different pictures both stay cached", d.Count == 2 && d.ContainsKey(K(@"C:\w\a.jpg", idA, m1)), string.Join(", ", d.Keys));

        d[K(@"C:\w\c.jpg", idA, m1)] = 1;
        Wallpaper.Evict(d, K(@"C:\w\c.jpg", idA, m1), 3);
        Check("a new picture on one monitor evicts only that monitor's old picture",
            !d.ContainsKey(K(@"C:\w\a.jpg", idA, m1)) && d.ContainsKey(K(@"C:\w\c.jpg", idA, m1)) && d.ContainsKey(K(@"C:\w\b.jpg", idB, m2)), string.Join(", ", d.Keys));

        var e = new ConcurrentDictionary<string, int>();
        e[K(@"C:\w\a.jpg", idA, m1, 4)] = 1; e[K(@"C:\w\a.jpg", idA, m1, 2)] = 1;
        Wallpaper.Evict(e, K(@"C:\w\a.jpg", idA, m1, 4), 3);
        Check("a different position mode is a different entry", e.Count == 2);

        var f = new ConcurrentDictionary<string, int>();
        for (int i = 0; i < 5; i++) f[K($@"C:\w\{i}.jpg", "mon" + i, m1)] = 1;
        Wallpaper.Evict(f, K(@"C:\w\4.jpg", "mon4", m1), 3);
        Check("the cache stays capped and keeps the newest", f.Count == 3 && f.ContainsKey(K(@"C:\w\4.jpg", "mon4", m1)), string.Join(", ", f.Keys));

        Check("no monitor id (single monitor): the key still tells monitors apart by their rect",
            K(@"C:\w\a.jpg", null, m1) != K(@"C:\w\a.jpg", null, m2));
    }

    static void TickRecoveryChecks()
    {
        Console.WriteLine("Tick failure recovery:");
        Check("a failed tick while opening or closing hides the overview", OverlayWindow.NeedsHideAfterTickFailure(OverlayWindow.State.Opening) && OverlayWindow.NeedsHideAfterTickFailure(OverlayWindow.State.Closing));
        Check("open (still usable) and hidden (nothing to hide) are left alone", !OverlayWindow.NeedsHideAfterTickFailure(OverlayWindow.State.Open) && !OverlayWindow.NeedsHideAfterTickFailure(OverlayWindow.State.Hidden));
    }

    static void DpiRectChecks()
    {
        Console.WriteLine("DPI change rect:");
        var a = new RECT { Left = 10, Top = 20, Right = 1930, Bottom = 1100 };
        var b = a; b.Right += 1;
        Check("equal rects are the same, any difference is not", WantedRect.SameRect(a, a) && !WantedRect.SameRect(a, b));
        Check("a DPI suggestion is overwritten when a rect was asked for, left alone when not", WantedRect.ShouldRewrite(a) && !WantedRect.ShouldRewrite(null));
        Check("re-apply when the window is not where it was asked to be", WantedRect.NeedsReapply(a, b));
        Check("no re-apply when it is, or when no rect was asked for", !WantedRect.NeedsReapply(a, a) && !WantedRect.NeedsReapply(null, b));
    }

    static void MinimizedPlacementChecks()
    {
        Console.WriteLine("Minimized window position:");
        RECT R(int l, int t, int r, int b) => new RECT { Left = l, Top = t, Right = r, Bottom = b };
        bool Eq(RECT x, RECT y) => x.Left == y.Left && x.Top == y.Top && x.Right == y.Right && x.Bottom == y.Bottom;
        var mon = R(0, 0, 1920, 1080);
        var n = R(100, 200, 900, 700);
        Check("top bar of 48 px: moved down by 48", Eq(WindowEnumerator.PlacementToScreen(n, mon, R(0, 48, 1920, 1080), false), R(100, 248, 900, 748)));
        Check("taskbar on the left (60 px): moved right by 60", Eq(WindowEnumerator.PlacementToScreen(n, mon, R(60, 0, 1920, 1080), false), R(160, 200, 960, 700)));
        var mon2 = R(1920, 0, 3840, 1080);
        Check("second monitor: only the work-area inset counts, not the monitor position", Eq(WindowEnumerator.PlacementToScreen(R(100, 200, 900, 700), mon2, R(1920, 48, 3840, 1080), false), R(100, 248, 900, 748)));
        Check("tool window: already in screen coordinates, unchanged", Eq(WindowEnumerator.PlacementToScreen(n, mon, R(0, 48, 1920, 1080), true), n));
    }
}
