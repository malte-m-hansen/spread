using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Spread;

/// <summary>
/// `--search-test`: ranking, learning, settings list and the Everything wire format with synthetic data. With `--live` it also asks your
/// running Everything (read-only) and prints counts and timing, never file names.
/// </summary>
internal static class SearchTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    static byte[] BuildList2(uint req, (string name, string dir, long size, DateTime mod, bool folder)[] items, int total)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Unicode);
        int dataStart = 20 + items.Length * 8;
        var data = new List<byte[]>();
        int pos = dataStart;
        var offsets = new List<int>();
        foreach (var it in items)
        {
            using var dm = new MemoryStream(); using var dw = new BinaryWriter(dm);
            void S(string s) { dw.Write((uint)s.Length); dw.Write(Encoding.Unicode.GetBytes(s + "\0")); }
            S(it.name); S(it.dir); dw.Write(it.size); dw.Write(it.mod.ToUniversalTime().ToFileTimeUtc());
            var b = dm.ToArray(); offsets.Add(pos); pos += b.Length; data.Add(b);
        }
        w.Write((uint)total); w.Write((uint)items.Length); w.Write(0u); w.Write(req); w.Write(14u);
        for (int i = 0; i < items.Length; i++) { w.Write(items[i].folder ? 1u : 0u); w.Write((uint)offsets[i]); }
        foreach (var b in data) w.Write(b);
        return ms.ToArray();
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("Search checks (synthetic data):");
        var cfg = new Config();
        string tmp = Path.Combine(Path.GetTempPath(), "spread-usage-test-" + Guid.NewGuid().ToString("N") + ".json");

        // ---- tiers ----
        Check("tier: exact", SearchEngine.Tier("Outlook", Array.Empty<string>(), "outlook") == 0);
        Check("tier: prefix", SearchEngine.Tier("Outlook (classic)", Array.Empty<string>(), "outl") == 1);
        Check("tier: word prefix", SearchEngine.Tier("Microsoft Edge", Array.Empty<string>(), "edg") == 2);
        Check("tier: contains", SearchEngine.Tier("Notepad++", Array.Empty<string>(), "epad") == 3);
        Check("tier: keywords", SearchEngine.Tier("Windows Update", new[] { "upgrade", "patches" }, "upgrade") == 4);
        Check("tier: no match", SearchEngine.Tier("Calculator", Array.Empty<string>(), "zzz") == -1);
        Check("tier: all words must match", SearchEngine.Tier("Check for updates", new[] { "windows update" }, "check updates") == 2 && SearchEngine.Tier("Check for updates", Array.Empty<string>(), "check printers") == -1);

        // ---- frecency with a fake clock ----
        var clock = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        var us = new UsageStore(cfg, tmp, () => clock);
        us.Record("outlook", 1); us.Record("outlook", 1); us.Record("obsidian", 1);
        Check("frecency: more use = higher score", us.ScoreOf("outlook") > us.ScoreOf("obsidian"));
        double before = us.ScoreOf("outlook");
        clock = clock.AddDays(14);
        Check("frecency: halves after the half-life (14 days)", Math.Abs(us.ScoreOf("outlook") - before / 2) < 0.01);
        clock = clock.AddDays(100);
        Check("frecency: old use is almost forgotten", us.ScoreOf("outlook") < 0.02);
        // activations: only after 5 s in front, at most once per 2 minutes
        us = new UsageStore(cfg, tmp + ".2", () => clock);
        us.OnForeground("firefox"); clock = clock.AddSeconds(2); us.FlushPending();
        Check("activation: 2 s in front is not counted", us.ScoreOf("firefox") == 0);
        clock = clock.AddSeconds(4); us.FlushPending();
        Check("activation: 6 s in front counts a little", us.ScoreOf("firefox") > 0.1 && us.ScoreOf("firefox") < 0.2);
        double one = us.ScoreOf("firefox");
        us.OnForeground("code"); clock = clock.AddSeconds(10); us.OnForeground("firefox"); clock = clock.AddSeconds(10); us.FlushPending();
        Check("activation: not counted again within 2 minutes", Math.Abs(us.ScoreOf("firefox") - one) < 0.001);
        // persistence
        us.Save(true);
        var again = new UsageStore(cfg, tmp + ".2", () => clock);
        Check("usage is saved and loaded (keys and numbers only)", again.ScoreOf("firefox") > 0.1 && !File.ReadAllText(tmp + ".2").Contains("\\"));
        var offCfg = new Config(); offCfg.Search.Frecency = false;
        var off = new UsageStore(offCfg, tmp + ".3", () => clock); off.Record("outlook", 1);
        Check("Frecency=false: nothing is learned", off.ScoreOf("outlook") == 0);

        // ---- app key matching ----
        var ol = new AppEntry { Name = "Outlook (classic)", AppId = @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Microsoft Office\root\Office16\OUTLOOK.EXE" };
        var teams = new AppEntry { Name = "Microsoft Teams (work or school)", AppId = "MSTeams_8wekyb3d8bbwe!MSTeams" };
        Check("key matches by exe name", UsageStore.KeyMatchesApp(ol, "outlook") && !UsageStore.KeyMatchesApp(ol, "teams"));
        Check("key matches packaged app by name", UsageStore.KeyMatchesApp(teams, UsageStore.Norm("Microsoft Teams (work or school)")));
        {
            var us5 = new UsageStore(cfg, tmp + ".5", () => clock);
            var edge = new AppEntry { Name = "Microsoft Edge", AppId = "MSEdge" };
            var terminal = new AppEntry { Name = "Terminal", AppId = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" };
            var snip = new AppEntry { Name = "Snipping Tool", AppId = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App" };
            var notepad = new AppEntry { Name = "Notepad", AppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe" };
            foreach (var k in new[] { "msedge", "windowsterminal", "msteams", "snippingtool" }) us5.Record(k, 1);
            Check("frecency: time in msedge / WindowsTerminal / ms-teams counts for Edge / Terminal / Teams", us5.ScoreOfApp(edge) > 0.9 && us5.ScoreOfApp(terminal) > 0.9 && us5.ScoreOfApp(teams) > 0.9 && us5.ScoreOfApp(notepad) == 0,
                $"{us5.ScoreOfApp(edge):0.00} {us5.ScoreOfApp(terminal):0.00} {us5.ScoreOfApp(teams):0.00} {us5.ScoreOfApp(notepad):0.00}");
            Check("frecency: matching by name still counts (SnippingTool.exe -> Snipping Tool)", us5.ScoreOfApp(snip) > 0.9);
            var us6 = new UsageStore(cfg, tmp + ".6", () => clock);
            us6.Record("app:" + UsageStore.Norm("Outlook (classic)"), 1); us6.Record("setting:bluetooth", 1);
            var appApp = new AppEntry { Name = "App", AppId = "Vendor.App_x!Main" }; var setApp = new AppEntry { Name = "Set", AppId = "set" };
            Check("frecency: an app named like the key prefix ('App', 'Set') does not collect every choice", us6.ScoreOfApp(appApp) == 0 && us6.ScoreOfApp(setApp) == 0, $"{us6.ScoreOfApp(appApp)} {us6.ScoreOfApp(setApp)}");
            // Spread's own window in front (the overview): the program before it stops counting
            us6.OnForeground("firefox"); clock = clock.AddSeconds(2); us6.OnForeground(null); clock = clock.AddSeconds(20); us6.FlushPending();
            Check("activation: seconds spent in Spread's overview do not count for the program before it", us6.ScoreOf("firefox") == 0);
            us6.Save(true);
            Check("usage.json is replaced through a temporary file (none left behind)", File.Exists(tmp + ".6") && !File.Exists(tmp + ".6.tmp") && new UsageStore(cfg, tmp + ".6", () => clock).ScoreOf("setting:bluetooth") > 0.9);
        }

        // ---- app catalog: Get-StartApps | ConvertTo-Json ----
        var single = AppCatalog.ParseStartApps("{\"Name\":\"3D Viewer\",\"AppID\":\"Microsoft.Microsoft3DViewer_8wekyb3d8bbwe!Microsoft.Microsoft3DViewer\"}");
        var many = AppCatalog.ParseStartApps("[{\"Name\":\"Calculator\",\"AppID\":\"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App\"},{\"Name\":\"Edge\",\"AppID\":\"MSEdge\"},{\"Name\":null,\"AppID\":\"x\"},{\"AppID\":\"y\"},3]");
        Check("catalog: a single app (ConvertTo-Json writes an object, not an array)", single.Count == 1 && single[0].Name == "3D Viewer");
        Check("catalog: many apps; entries without a name or id are skipped; no output = no apps", many.Count == 2 && many[1].AppId == "MSEdge" && AppCatalog.ParseStartApps("").Count == 0 && AppCatalog.ParseStartApps("\r\n").Count == 0);

        // ---- engine: used-often-first, apps before settings ----
        var catalog = new AppCatalog();
        catalog.SetAll(new[]
        {
            new AppEntry { Name = "Obsidian", AppId = "obsidian" }, ol, new AppEntry { Name = "Notepad", AppId = "notepad" },
            new AppEntry { Name = "Update Helper", AppId = "uh" }
        });
        var clock2 = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
        var usage = new UsageStore(cfg, tmp + ".4", () => clock2);
        var engine = new SearchEngine(catalog, SettingsProvider.Load(cfg.Search), usage, cfg.Search);
        var r1 = engine.Query("o");
        Check("engine: without history shorter name first", r1.Count > 1 && r1[0].Title == "Obsidian");
        usage.Record("app:" + UsageStore.Norm("Outlook (classic)"), 1); usage.Record("app:" + UsageStore.Norm("Outlook (classic)"), 1);
        var r2 = engine.Query("o");
        Check("engine: the app you use more comes first", r2[0].Title == "Outlook (classic)", string.Join(",", r2.Take(3).Select(x => x.Title)));
        var r3 = engine.Query("check for updates");
        Check("engine: 'check for updates' finds the Windows Update page first", r3.Count > 0 && r3[0].Setting?.Id == "update-check", string.Join(",", r3.Take(3).Select(x => x.Title)));
        var r4 = engine.Query("update");
        Check("engine: an app named like the query comes before settings of the same tier", r4.Count > 1 && r4[0].Kind == SearchKind.App && r4.Any(x => x.Kind == SearchKind.Setting), string.Join(",", r4.Take(3).Select(x => x.Title)));
        Check("engine: 'bluetooth' -> Bluetooth & devices", engine.Query("bluetooth").FirstOrDefault()?.Setting?.Id == "bluetooth");
        Check("engine: 'device man' -> Device Manager (if present on this Windows)", engine.Query("device man").FirstOrDefault()?.Setting?.Id == "tool-devmgmt");
        Check("engine: 'env' -> Environment Variables", engine.Query("environment var").FirstOrDefault()?.Setting?.Id == "tool-env");
        Check("engine: empty query finds nothing", engine.Query("  ").Count == 0);
        var noSettings = new Config(); noSettings.Search.Settings = false;
        Check("config: Settings=false hides settings", new SearchEngine(catalog, SettingsProvider.Load(noSettings.Search), usage, noSettings.Search).Query("bluetooth").All(x => x.Kind == SearchKind.App));
        var extra = new Config(); extra.Search.ExtraSettings.Add(new ExtraSetting { Name = "Opdater Windows", Uri = "ms-settings:windowsupdate-action", Keywords = "søg efter opdateringer" });
        var extraEngine = new SearchEngine(catalog, SettingsProvider.Load(extra.Search), usage, extra.Search);
        Check("config: ExtraSettings are searchable (own words)", extraEngine.Query("opdater").FirstOrDefault()?.Setting?.Uri == "ms-settings:windowsupdate-action" && extraEngine.Query("opdateringer").Count > 0);
        var list = SettingsProvider.Builtin();
        Check("settings list: unique ids", list.Select(x => x.Id).Distinct().Count() == list.Count);
        Check("settings list: pages are ms-settings addresses", list.Where(x => !x.Tool).All(x => x.Uri.StartsWith("ms-settings:")), string.Join(",", list.Where(x => !x.Tool && !x.Uri.StartsWith("ms-settings:")).Select(x => x.Id)));
        Check("settings list: tools have a program", list.Where(x => x.Tool).All(x => x.Exe.Length > 0));
        Console.WriteLine($"  info: {list.Count(x => !x.Tool)} settings pages, {list.Count(x => x.Tool)} tools in the list, {SettingsProvider.Load(cfg.Search).Count(x => x.Tool)} tools exist on this PC");

        // ---- Everything wire format ----
        var q = EverythingProtocol.EncodeQuery2(0x1234, 0x5350, EverythingProtocol.MATCHPATH, 0, 8, 0x53, 14, "quarterly pptx");
        Check("ipc: query header and text", BitConverter.ToUInt32(q, 0) == 0x1234 && BitConverter.ToUInt32(q, 16) == 8 && BitConverter.ToUInt32(q, 24) == 14 && Encoding.Unicode.GetString(q, 28, q.Length - 28) == "quarterly pptx\0");
        Check("ipc: sort names", EverythingProtocol.SortOf("name") == 1 && EverythingProtocol.SortOf("date-modified-desc") == 14 && EverythingProtocol.SortOf("size-desc") == 6 && EverythingProtocol.SortOf("nonsense") == 14);
        var when = new DateTime(2026, 11, 4, 12, 30, 0, DateTimeKind.Local);
        var buf = BuildList2(0x53, new[] { ("Quarterly Report.pptx", @"C:\Users\x\OneDrive - Contoso\Projects", 2_345_678L, when, false), ("Building", @"C:\Docs", -1L, when, true) }, 128);
        var hits = EverythingProtocol.DecodeList2(buf, out int total);
        Check("ipc: reply decoded (names, folder, size, date, total)", total == 128 && hits.Count == 2 && hits[0].Name == "Quarterly Report.pptx" && hits[0].Dir.EndsWith("Projects") && hits[0].Size == 2_345_678 && Math.Abs((hits[0].Modified - when).TotalSeconds) < 2 && hits[1].IsFolder && hits[1].Size == -1);
        Check("ipc: full path", hits[0].Full == @"C:\Users\x\OneDrive - Contoso\Projects\Quarterly Report.pptx");
        Check("ipc: truncated reply does not throw", EverythingProtocol.DecodeList2(buf.Take(60).ToArray(), out _).Count <= 2 && EverythingProtocol.DecodeList2(new byte[3], out _).Count == 0);

        foreach (var f in Directory.GetFiles(Path.GetTempPath(), "spread-usage-test-*")) { try { File.Delete(f); } catch { } }

        // modifier keys on Enter / click
        {
            var k = new SearchKeysConfig();
            var M = System.Windows.Input.ModifierKeys.None;
            Check("keys: nothing held = the normal action", SearchKeys.Resolve(KeyTarget.App, M, k) == SearchAction.Default && SearchKeys.Resolve(KeyTarget.File, M, k) == SearchAction.Default);
            Check("keys: Ctrl and Shift on an app = new window", SearchKeys.Resolve(KeyTarget.App, System.Windows.Input.ModifierKeys.Control, k) == SearchAction.NewWindow && SearchKeys.Resolve(KeyTarget.App, System.Windows.Input.ModifierKeys.Shift, k) == SearchAction.NewWindow);
            Check("keys: file Ctrl = show in Explorer, Shift = open with, Alt = copy path", SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Control, k) == SearchAction.Reveal && SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Shift, k) == SearchAction.OpenWith && SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Alt, k) == SearchAction.CopyPath);
            Check("keys: Ctrl+Shift counts as Ctrl, Alt wins over both", SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, k) == SearchAction.Reveal && SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Control, k) == SearchAction.CopyPath);
            var odd = new SearchKeysConfig { FileCtrl = "nonsense", AppShift = "default", FileAlt = "openFolder" };
            Check("keys: unknown text falls back to the default mapping, config can change one", SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Control, odd) == SearchAction.Reveal && SearchKeys.Resolve(KeyTarget.App, System.Windows.Input.ModifierKeys.Shift, odd) == SearchAction.Default && SearchKeys.Resolve(KeyTarget.File, System.Windows.Input.ModifierKeys.Alt, odd) == SearchAction.OpenFolder);
            string h0 = SearchKeys.Hint(KeyTarget.File, M, k), h1 = SearchKeys.Hint(KeyTarget.App, System.Windows.Input.ModifierKeys.Control, k);
            Check("keys: hint lists every meaning, or only the held key's", h0.Contains("Ctrl: show in Explorer") && h0.Contains("Shift: open with") && h0.Contains("Alt: copy path") && h1 == "Ctrl + Enter / click: new window", h0 + " | " + h1);
        }

        if (args.Contains("--live")) _fail += Live(cfg).GetAwaiter().GetResult();
        Console.WriteLine(_fail == 0 ? "SEARCH TEST PASSED" : $"SEARCH TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    static async System.Threading.Tasks.Task<int> Live(Config cfg)
    {
        Console.WriteLine("Live check against your Everything (counts and timing only):");
        var ev = new EverythingClient(cfg.Search.Everything);
        int bad = 0;
        foreach (var text in new[] { "quarterly pptx", "ext:pptx", "zzzzqqqq-nothing" })
        {
            var r = await ev.QueryAsync(text);
            Console.WriteLine($"  '{(text == "quarterly pptx" ? text : text)}': state={r.State} total={r.Total} returned={r.Hits.Count} in {r.Ms} ms" +
                (text == "quarterly pptx" ? $" | 'Quarterly Report.pptx' among them: {r.Hits.Any(h => h.Name.Equals("Quarterly Report.pptx", StringComparison.OrdinalIgnoreCase))}" : ""));
            if (r.State != EverythingState.Ready) { Console.WriteLine("  Everything did not answer: " + r.State); bad++; break; }
        }
        // sorted newest first: dates must be non-increasing
        var n = await ev.QueryAsync("ext:pptx");
        bool sorted = n.Hits.Zip(n.Hits.Skip(1), (a, b) => a.Modified >= b.Modified).All(x => x);
        Console.WriteLine($"  newest-first order holds: {sorted}");
        if (!sorted) bad++;
        Console.WriteLine($"  Everything client found at: {(ev.FindExe() != null ? "yes" : "no")}");
        return bad;
    }
}
