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
        ConfigChecks();
        RestartChecks();
        WallpaperChecks();
        Console.WriteLine(_fail == 0 ? "CORE TEST PASSED" : $"CORE TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
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
            Check("old file is migrated and written back", c.Version == 15 && c.HotEdgeDwellMs == 40 && File.ReadAllText(file).Contains("\"Version\": 15"));

            File.Delete(file);
            c = Config.LoadFrom(file);
            Check("missing file is created with defaults", !c.LoadFailed && File.Exists(file) && c.Version == 15);

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
        var d = new ConcurrentDictionary<string, int>();
        foreach (var k in new[] { @"C:\w\a.jpg|5120x2160|4", @"C:\w\b.jpg|5120x2160|4", @"C:\w\a.jpg|2560x1600|4", @"C:\w\a.jpg|1920x1080|4", @"C:\w\a.jpg|3840x2160|4" }) d[k] = 1;
        Wallpaper.Evict(d, @"C:\w\b.jpg|5120x2160|4", 3);
        Check("an older picture for the same monitor geometry is dropped", !d.ContainsKey(@"C:\w\a.jpg|5120x2160|4"));
        Check("the new one is kept and the cache is capped", d.ContainsKey(@"C:\w\b.jpg|5120x2160|4") && d.Count == 3, string.Join(", ", d.Keys));
        var e = new ConcurrentDictionary<string, int>(); e[@"C:\w\a.jpg|5120x2160|4"] = 1; e[@"C:\w\a.jpg|5120x2160|2"] = 1;
        Wallpaper.Evict(e, @"C:\w\a.jpg|5120x2160|4", 3);
        Check("a different position mode is a different entry", e.Count == 2);
    }
}
