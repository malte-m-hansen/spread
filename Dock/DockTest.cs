using System;
using System.Collections.Generic;
using System.Linq;

namespace Spread;

/// <summary>
/// `--dock-test`: pin specs, window-to-app mapping, ordering, import parsing with synthetic data and the layout reserve. With `--live` it also looks at this PC
/// (counts only): which pins would be imported and how the running windows map.
/// </summary>
internal static class DockTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    static AppEntry App(string name, string id) => new() { Name = name, AppId = id };
    static DockWindow W(int h, string exe, string? aumid = null, string title = "t", bool min = false, string? proc = null) =>
        new(new IntPtr(h), title, exe, aumid, min, null, proc ?? System.IO.Path.GetFileNameWithoutExtension(exe));

    public static int Run(string[] args)
    {
        Console.WriteLine("Dock checks (synthetic data):");
        var explorer = App("File Explorer", "Microsoft.Windows.Explorer");
        var firefox = App("Firefox", "308046B0AF4A39CB");
        var outlookClassic = App("Outlook (classic)", "Microsoft.Office.OUTLOOK.EXE.15");
        var outlookNew = App("Outlook (new)", "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows");
        var teams = App("Microsoft Teams", "MSTeams_8wekyb3d8bbwe!MSTeams");
        var terminal = App("Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
        var notepad = App("Notepad", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe");
        var apps = new List<AppEntry> { explorer, firefox, outlookClassic, outlookNew, teams, terminal, notepad };

        Check("exe hint from an id like ...OUTLOOK.EXE.15", DockBuilder.ExeHint(outlookClassic) == "outlook" && DockBuilder.ExeHint(notepad) == "notepad" && DockBuilder.ExeHint(teams) == null && DockBuilder.ExeHint(firefox) == null);
        Check("pin by name / id / exe", DockBuilder.ResolvePin("Firefox", apps) == firefox && DockBuilder.ResolvePin("id:Microsoft.Windows.Explorer", apps) == explorer && DockBuilder.ResolvePin(@"exe:C:\Windows\System32\notepad.exe", apps) == notepad && DockBuilder.ResolvePin("exe:C:\\x\\unknown.exe", apps) == null);
        var noAumid = new List<DockWindow> { W(30, @"C:\Program Files\WindowsApps\x\ms-teams.exe", null, "Chat"), W(31, @"C:\Program Files\WindowsApps\y\WindowsTerminal.exe", null, "pwsh") };
        var viaHints = DockBuilder.Build(new List<string> { "id:MSTeams_8wekyb3d8bbwe!MSTeams" }, apps, noAumid, new DockConfig());
        Check("packaged apps match by package/app name even when the window has no AppUserModelID", viaHints.Count == 2 && viaHints[0].Windows.Count == 1 && viaHints[1].App == terminal && viaHints[1].Windows.Count == 1, string.Join(",", viaHints.Select(x => x.Name + x.Windows.Count)));
        Check("pin by name: unknown name finds nothing", DockBuilder.ResolvePin("Nope", apps) == null);

        var cfg = new DockConfig();
        var windows = new List<DockWindow>
        {
            W(1, @"C:\Windows\explorer.exe", "Microsoft.Windows.Explorer", "Docs"),
            W(2, @"C:\Program Files\Mozilla Firefox\firefox.exe", null, "Mail"),
            W(3, @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", null, "Inbox"),
            W(4, @"C:\Program Files\WindowsApps\x\ms-teams.exe", "MSTeams_8wekyb3d8bbwe!MSTeams", "Chat"),
            W(5, @"C:\Windows\explorer.exe", "Microsoft.Windows.Explorer", "Downloads"),
            W(6, @"C:\Program Files\WindowsApps\y\WindowsTerminal.exe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "pwsh"),
            W(7, @"C:\Users\x\Apps\RDCMan.exe", null, "Servers", true),
            W(8, @"C:\Windows\System32\notepad.exe", null, "notes.txt")
        };
        var pins = new List<string> { "id:Microsoft.Windows.Explorer", "Firefox", "id:Microsoft.Office.OUTLOOK.EXE.15", "id:MSTeams_8wekyb3d8bbwe!MSTeams", @"exe:C:\Users\x\Apps\RDCMan.exe" };
        var e = DockBuilder.Build(pins, apps, windows, cfg);
        Check("pinned apps first, in the configured order", e.Take(5).Select(x => x.Name).SequenceEqual(new[] { "File Explorer", "Firefox", "Outlook (classic)", "Microsoft Teams", "RDCMan" }), string.Join(",", e.Select(x => x.Name)));
        Check("explorer windows grouped via AppUserModelID", e[0].Windows.Count == 2 && e[0].Running);
        Check("Firefox found by exe name only", e[1].Windows.SequenceEqual(new[] { new IntPtr(2) }));
        Check("Outlook classic (not 'new') gets the OUTLOOK.EXE window", e[2].Windows.Count == 1 && e[2].App == outlookClassic && !e.Any(x => x.App == outlookNew));
        Check("Teams matched by AppUserModelID although the exe is called ms-teams", e[3].Windows.Count == 1);
        Check("an exe: pin gets its program's window and shows minimized", e[4].Windows.Count == 1 && e[4].AllMinimized);
        Check("running but not pinned: Terminal (AUMID) and Notepad (exe) follow after the pinned ones", e.Skip(5).Select(x => x.Name).OrderBy(x => x).SequenceEqual(new[] { "Notepad", "Terminal" }) && e.Skip(5).All(x => !x.Pinned));
        Check("every window is on exactly one entry", e.SelectMany(x => x.Windows).Distinct().Count() == 8 && e.Sum(x => x.Windows.Count) == 8);
        Check("keys are unique", e.Select(x => x.Key).Distinct().Count() == e.Count);

        var unknown = DockBuilder.Build(new List<string>(), apps, new List<DockWindow> { W(20, @"C:\tools\weird.exe", null, "w") }, cfg);
        Check("a program without a Start-menu entry still gets an entry (its own name, exe known)", unknown.Count == 1 && unknown[0].Name == "weird" && unknown[0].ExePath == @"C:\tools\weird.exe" && unknown[0].Spec == @"exe:C:\tools\weird.exe");
        Check("spec of an app entry is its id", e[1].Spec == "id:308046B0AF4A39CB");
        var noRun = DockBuilder.Build(pins, apps, windows, new DockConfig { ShowRunning = false });
        Check("ShowRunning=false: only the pinned ones", noRun.Count == 5 && noRun.All(x => x.Pinned));
        var capped = DockBuilder.Build(pins, apps, windows, new DockConfig { MaxItems = 3 });
        Check("MaxItems caps the dock", capped.Count == 3);
        var freq = DockBuilder.Build(new List<string> { "Firefox" }, apps, new List<DockWindow>(), new DockConfig { ShowFrequent = 2 }, a => a == teams ? 5 : a == notepad ? 2 : a == terminal ? 0.1 : 0);
        Check("ShowFrequent adds the most used apps that are not pinned or running", freq.Select(x => x.Name).SequenceEqual(new[] { "Firefox", "Microsoft Teams", "Notepad" }), string.Join(",", freq.Select(x => x.Name)));
        Check("a pin that no longer exists is skipped", DockBuilder.Build(new List<string> { "Gone App", "Firefox" }, apps, new List<DockWindow>(), cfg).Count == 1);

        // search: go to a running app instead of starting it again (same rules as the dock)
        var edge = App("Microsoft Edge", "MSEdge");
        var chrome = App("Google Chrome", "Chrome");
        var calc = App("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        var webApp = App("Some Web App", "MSEdge._crx_abcdef");
        var all = new List<AppEntry>(apps) { edge, chrome, calc, webApp };
        var running = new List<DockWindow>
        {
            W(40, @"C:\Windows\System32\notepad.exe", null, "n"),
            W(41, @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "MSEdge", "e"),
            W(42, @"C:\Program Files\WindowsApps\y\WindowsTerminal.exe", null, "pwsh"),
            W(43, @"C:\Program Files\WindowsApps\x\ms-teams.exe", "MSTeams_8wekyb3d8bbwe!MSTeams", "Chat"),
            W(44, @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", null, "Inbox"),
            W(45, @"C:\Program Files\WindowsApps\c\CalculatorApp.exe", null, "Calculator"),
            W(46, @"C:\Program Files\WindowsApps\y\WindowsTerminal.exe", null, "cmd"),
        };
        IntPtr Find(AppEntry a, List<DockWindow>? ws = null) => DockBuilder.FindWindow(a, ws ?? running, all);
        Check("search finds a running Edge (exe msedge, name Microsoft Edge)", Find(edge) == new IntPtr(41));
        Check("search finds Edge's window without its AppUserModelID (another profile) by the AppID = exe name", Find(edge, new() { W(47, @"C:\x\msedge.exe", "MSEdge.UserData.Profile2") }) == new IntPtr(47) && Find(chrome, new() { W(48, @"C:\x\chrome.exe") }) == new IntPtr(48));
        Check("search finds Terminal (WindowsTerminal.exe, no AppUserModelID), the frontmost of its windows", Find(terminal) == new IntPtr(42));
        Check("search finds Teams by AppUserModelID (exe ms-teams)", Find(teams) == new IntPtr(43));
        Check("search: OUTLOOK.EXE is Outlook (classic)'s, not 'Outlook (new)''s", Find(outlookClassic) == new IntPtr(44) && Find(outlookNew) == IntPtr.Zero);
        Check("search: an app that is not running finds nothing (it gets started)", Find(firefox) == IntPtr.Zero && Find(explorer) == IntPtr.Zero && Find(edge, new()) == IntPtr.Zero);
        Check("search: last resort by name for a window no app claims (CalculatorApp.exe -> Calculator)", Find(calc) == new IntPtr(45));
        Check("search: a web app's window (its own AppUserModelID) is not the browser's", Find(edge, new() { W(49, @"C:\x\msedge.exe", "MSEdge._crx_abcdef") }) == IntPtr.Zero && Find(webApp, new() { W(49, @"C:\x\msedge.exe", "MSEdge._crx_abcdef") }) == new IntPtr(49));
        Check("pin: exe:...msedge.exe is the Edge Start-menu app", DockBuilder.ResolvePin(@"exe:C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", all) == edge);
        var edgePin = DockBuilder.Build(new List<string> { "id:MSEdge" }, all, new List<DockWindow> { W(50, @"C:\x\msedge.exe", "MSEdge.UserData.Profile2", "front"), W(51, @"C:\x\msedge.exe", "MSEdge", "back") }, new DockConfig());
        Check("dock: an entry's windows are front to back whichever kind of match found them", edgePin.Count == 1 && edgePin[0].Windows.SequenceEqual(new[] { new IntPtr(50), new IntPtr(51) }) && edgePin[0].Titles.SequenceEqual(new[] { "front", "back" }),
            string.Join(",", edgePin.SelectMany(x => x.Windows)));

        // clicking: latest window, or the next one when the app is already in front
        var live = new List<IntPtr> { new(10), new(11), new(12) };
        Check("click: latest window when the app is not in front", DockBuilder.NextWindow(live, new IntPtr(99)) == new IntPtr(10));
        Check("click again: the next window", DockBuilder.NextWindow(live, new IntPtr(10)) == new IntPtr(11) && DockBuilder.NextWindow(live, new IntPtr(12)) == new IntPtr(10));
        Check("nothing to go to", DockBuilder.NextWindow(new List<IntPtr>(), IntPtr.Zero) == IntPtr.Zero);

        // import from the taskbar (synthetic blob text + .lnk targets)
        string blob = "xx\0Microsoft.Windows.Explorer\0yy\0MSTeams_8wekyb3d8bbwe!MSTeams\0zz C:\\Users\\x\\Apps\\RDCMan.exe\0 not.an.id!thing";
        var imported = DockImport.ExtractPins(blob, new[] { @"C:\Program Files\Mozilla Firefox\firefox.exe", @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", @"C:\Users\x\Apps\RDCMan.exe" }, apps);
        Check("import: AUMIDs that are Start-menu apps", imported.Contains("id:Microsoft.Windows.Explorer") && imported.Contains("id:MSTeams_8wekyb3d8bbwe!MSTeams") && !imported.Any(x => x.Contains("not.an.id")));
        Check("import: a classic program becomes its Start-menu app, an unknown one stays an exe: pin", imported.Contains("id:Microsoft.Office.OUTLOOK.EXE.15") && imported.Count(x => x.EndsWith("RDCMan.exe", StringComparison.OrdinalIgnoreCase)) == 1);
        Check("import: a program whose Start-menu id is opaque is found by its name (firefox.exe -> Firefox)", imported.Contains("id:308046B0AF4A39CB"), string.Join(",", imported));
        Check("import: no duplicates", imported.Distinct(StringComparer.OrdinalIgnoreCase).Count() == imported.Count);

        // layout: the dock's strip is taken from the bottom of the layout area
        var rnd = new Random(5);
        const double W_ = 1707, H_ = 1035, S = 1.0;
        double reserve = (DockBar.HeightFor(48) + 24) * S;
        var area = new LArea(40 * S, 100 * S, W_ - 80 * S, H_ - 124 * S - reserve);
        var layout = new NaturalLayout(W_, H_, 19 * S, 51 * S, 5 * S, 20 * S, 0.95, 220 * S);
        int outside = 0;
        for (int c = 0; c < 300; c++)
        {
            int n = 1 + c % 20;
            var boxes = Enumerable.Range(0, n).Select(_ => { double w = rnd.Next(300, 1500), h = rnd.Next(200, 900); return new LBox(rnd.Next(0, (int)(W_ - w)), rnd.Next(0, (int)(H_ - h)), w, h); }).ToList();
            foreach (var s in layout.Compute(boxes, area)) if (s.Y + s.Height > area.Y + area.Height + 2) outside++;
        }
        Check("layout with the dock reserve keeps every preview above the dock", outside == 0, outside + " slots below the area");

        // the bar itself: icon sizes from the config (IconSize below 32 made Math.Clamp throw while the overview opened)
        {
            string? err = null; var sizes = new List<double>();
            foreach (var (size, avail) in new[] { (48, 2000.0), (24, 2000.0), (0, 2000.0), (48, 300.0), (24, 300.0) })
                try { var bar = new DockBar(); bar.Rebuild(e, new DockConfig { IconSize = size }, avail); sizes.Add(bar.IconDip); } catch (Exception ex) { err = ex.GetType().Name; }
            Check("dock bar: any IconSize works (48 stays, 24 is kept, 0 becomes 16; narrow screens shrink icons no further than 32)", err == null && sizes.SequenceEqual(new[] { 48.0, 24, 16, 32, 24 }), err ?? string.Join(",", sizes));
        }

        if (args.Contains("--live")) Live();
        Console.WriteLine(_fail == 0 ? "DOCK TEST PASSED" : $"DOCK TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    static void Live()
    {
        Console.WriteLine("Live check on this PC (counts and app names only):");
        var catalog = new AppCatalog();
        catalog.LoadAsync().GetAwaiter().GetResult();
        var swImport = System.Diagnostics.Stopwatch.StartNew();
        var specs = DockImport.ReadTaskbar(catalog.All);
        swImport.Stop();
        Console.WriteLine($"  taskbar pins imported: {specs.Count} -> {string.Join(", ", specs.Select(s => s.StartsWith("id:") ? (catalog.All.FirstOrDefault(a => "id:" + a.AppId == s)?.Name ?? s) : System.IO.Path.GetFileName(s[4..])))}");
        var cfg = Config.Load();
        var wins = WindowEnumerator.Enumerate(IntPtr.Zero, cfg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dw = wins.Select(w => new DockWindow(w.Hwnd, w.Title, w.ExePath ?? "", WindowAumid.Get(w.Hwnd), w.Minimized, w.Icon, w.ProcName)).ToList();
        Console.WriteLine($"  windows: {dw.Count}, with an AppUserModelID: {dw.Count(x => x.Aumid != null)}");
        var entries = DockBuilder.Build(specs, catalog.All, dw, new DockConfig());
        sw.Stop();
        Console.WriteLine($"  dock entries: {entries.Count} ({entries.Count(x => x.Pinned)} pinned, {entries.Count(x => x.Running)} running), windows placed: {entries.Sum(x => x.Windows.Count)}/{dw.Count}, without a Start-menu app: {entries.Count(x => x.App == null)}; built in {sw.ElapsedMilliseconds} ms (the one-time taskbar import took {swImport.ElapsedMilliseconds} ms)");
        sw.Restart(); var again = DockBuilder.Build(specs, catalog.All, wins.Select(w => new DockWindow(w.Hwnd, w.Title, w.ExePath ?? "", WindowAumid.Get(w.Hwnd), w.Minimized, w.Icon, w.ProcName)).ToList(), new DockConfig()); sw.Stop();
        Console.WriteLine($"  second build (JIT warm): {sw.Elapsed.TotalMilliseconds:0.0} ms");
        Console.WriteLine("  entries: " + string.Join(" | ", entries.Select(x => x.Name + (x.Pinned ? "*" : "") + (x.Windows.Count > 0 ? $"({x.Windows.Count})" : ""))));
    }
}
