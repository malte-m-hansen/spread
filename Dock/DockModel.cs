using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;

namespace Spread;

// ---------------- config ----------------

internal sealed class DockConfig
{
    /// <summary>A dock at the bottom of the overview (only visible while the overview is open).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Only "bottom" for now.</summary>
    public string Position { get; set; } = "bottom";
    /// <summary>Icon size in DIPs.</summary>
    public int IconSize { get; set; } = 48;
    /// <summary>In this order. "Firefox" (Start-menu name), "id:Microsoft.Windows.Explorer" (AppID / AppUserModelID) or "exe:C:\path\app.exe". Empty = imported once from the taskbar.</summary>
    public List<string> Pinned { get; set; } = new();
    public bool ImportTaskbarPins { get; set; } = true;
    /// <summary>Also show running apps that are not pinned.</summary>
    public bool ShowRunning { get; set; } = true;
    /// <summary>This many of your most-used apps that are neither pinned nor running (0 = none).</summary>
    public int ShowFrequent { get; set; } = 0;
    public int MaxItems { get; set; } = 18;
    public bool Magnify { get; set; } = true;
    public bool Labels { get; set; } = true;
    public bool HideWhenSearching { get; set; } = false;
}

// ---------------- model ----------------

/// <summary>A window as the dock sees it (so the builder can be tested without real windows).</summary>
internal sealed record DockWindow(IntPtr Hwnd, string Title, string ExePath, string? Aumid, bool Minimized, ImageSource? Icon, string ProcName);

internal sealed class DockEntry
{
    public string Key = "";
    public string Name = "";
    public AppEntry? App;
    public ImageSource? Icon;
    public bool Pinned;
    public string? ExePath;
    public List<IntPtr> Windows = new();          // front to back
    public List<string> Titles = new();
    public bool AllMinimized;
    /// <summary>The text that pins this entry in config.json (or null when it cannot be pinned).</summary>
    public string? Spec => App != null ? "id:" + App.AppId : !string.IsNullOrEmpty(ExePath) ? "exe:" + ExePath : null;
    public bool Running => Windows.Count > 0;
}

internal static class DockBuilder
{
    static string Norm(string s) => UsageStore.Norm(s);

    /// <summary>"OUTLOOK" from ...OUTLOOK.EXE.15 or from a path ending in OUTLOOK.EXE; null when the id says nothing about a program.</summary>
    public static string? ExeHint(AppEntry a)
    {
        try
        {
            if (a.AppId.Contains('\\') && a.AppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Norm(Path.GetFileNameWithoutExtension(a.AppId));
            var m = Regex.Match(a.AppId, @"\.(?<e>[A-Za-z0-9_\-]+)\.EXE\b", RegexOptions.IgnoreCase);
            if (m.Success) return Norm(m.Groups["e"].Value);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Names a running program of this app could have: the exe hint, and for packaged apps (`Microsoft.WindowsTerminal_8wekyb3d8bbwe!App`, `MSTeams_8wekyb3d8bbwe!MSTeams`)
    /// the package name and the app part - their exe is named like them (WindowsTerminal.exe, ms-teams.exe).
    /// </summary>
    public static List<string> ExeHints(AppEntry a)
    {
        var res = new List<string>();
        var h = ExeHint(a); if (h != null) res.Add(h);
        int bang = a.AppId.IndexOf('!');
        if (bang > 0)
        {
            string pkg = a.AppId[..bang]; int us = pkg.IndexOf('_'); if (us > 0) pkg = pkg[..us];
            string n = Norm(pkg); if (n.Length >= 4) res.Add(n);
            string part = Norm(a.AppId[(bang + 1)..]); if (part.Length >= 4 && part != "app") res.Add(part);
        }
        return res;
    }

    static bool HintMatches(AppEntry a, string key)
    {
        foreach (var h in ExeHints(a))
            if (h == key || (key.Length >= 6 && h.EndsWith(key, StringComparison.Ordinal))) return true;
        return false;
    }

    /// <summary>Which Start-menu app does a pin text mean? (null for exe: pins of programs without a Start-menu entry)</summary>
    public static AppEntry? ResolvePin(string spec, IReadOnlyList<AppEntry> apps)
    {
        spec = spec.Trim();
        if (spec.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) { string id = spec[3..].Trim(); return apps.FirstOrDefault(a => string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase)); }
        if (spec.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            string key = Norm(Path.GetFileNameWithoutExtension(spec[4..].Trim()));
            return key.Length == 0 ? null : apps.FirstOrDefault(a => HintMatches(a, key));
        }
        return apps.FirstOrDefault(a => string.Equals(a.Name, spec, StringComparison.OrdinalIgnoreCase)) ?? apps.FirstOrDefault(a => a.Name.StartsWith(spec, StringComparison.OrdinalIgnoreCase));
    }

    static string ExeKey(DockWindow w) => string.IsNullOrEmpty(w.ExePath) ? Norm(w.ProcName) : UsageStore.KeyOfExe(w.ExePath);

    /// <summary>Order of attempts: the window's own AppUserModelID, then the program's exe name, then the app name. Run for all entries before the next kind of match is tried.</summary>
    static bool Matches(DockEntry e, DockWindow w, int pass)
    {
        var a = e.App;
        if (pass == 0) return a != null && !string.IsNullOrEmpty(w.Aumid) && string.Equals(a.AppId, w.Aumid, StringComparison.OrdinalIgnoreCase);
        string key = ExeKey(w);
        if (pass == 1)
        {
            if (!string.IsNullOrEmpty(e.ExePath) && Norm(Path.GetFileNameWithoutExtension(e.ExePath)) == key) return true;
            return a != null && HintMatches(a, key);
        }
        return a != null && ExeHints(a).Count == 0 && UsageStore.KeyMatchesApp(a, key);
    }

    public static List<DockEntry> Build(IReadOnlyList<string> pins, IReadOnlyList<AppEntry> apps, IReadOnlyList<DockWindow> windows, DockConfig cfg, Func<AppEntry, double>? frecency = null)
    {
        var entries = new List<DockEntry>();
        foreach (var spec in pins)
        {
            if (string.IsNullOrWhiteSpace(spec)) continue;
            var app = ResolvePin(spec, apps);
            string? exe = spec.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) ? spec[4..].Trim() : null;
            if (app == null && exe == null) continue;    // a name or id that no longer exists
            var e = new DockEntry { App = app, Pinned = true, ExePath = exe, Name = app?.Name ?? Path.GetFileNameWithoutExtension(exe ?? ""), Icon = app?.Icon, Key = app != null ? "id:" + app.AppId : "exe:" + exe };
            if (entries.Any(x => x.Key == e.Key)) continue;
            entries.Add(e);
        }

        if (cfg.ShowRunning || entries.Count > 0)
        {
            var unclaimed = new List<DockWindow>(windows);
            for (int pass = 0; pass < 3; pass++)
                foreach (var w in unclaimed.ToList())
                {
                    var hit = entries.FirstOrDefault(e => Matches(e, w, pass));
                    if (hit == null) continue;
                    hit.Windows.Add(w.Hwnd); hit.Titles.Add(w.Title);
                    hit.Icon ??= w.Icon;
                    unclaimed.Remove(w);
                }
            foreach (var e in entries) e.AllMinimized = e.Windows.Count > 0 && windows.Where(w => e.Windows.Contains(w.Hwnd)).All(w => w.Minimized);

            if (cfg.ShowRunning)
            {
                // running programs that are not pinned: one entry each, in the order of their frontmost window
                var extra = new List<DockEntry>();
                foreach (var w in unclaimed)
                {
                    DockEntry? e = null;
                    foreach (int pass in new[] { 0, 1, 2 })
                    {
                        var app = apps.FirstOrDefault(a => Matches(new DockEntry { App = a }, w, pass));
                        if (app != null) { e = extra.FirstOrDefault(x => x.App == app) ?? new DockEntry { App = app, Name = app.Name, Icon = app.Icon, Key = "id:" + app.AppId }; break; }
                    }
                    e ??= extra.FirstOrDefault(x => x.App == null && x.Key == "exe:" + ExeKey(w)) ?? new DockEntry { Name = string.IsNullOrEmpty(w.ProcName) ? "Program" : w.ProcName, Icon = w.Icon, ExePath = w.ExePath, Key = "exe:" + ExeKey(w) };
                    if (!extra.Contains(e)) extra.Add(e);
                    e.Windows.Add(w.Hwnd); e.Titles.Add(w.Title); e.Icon ??= w.Icon;
                    e.AllMinimized = e.Windows.Count > 0 && windows.Where(x => e.Windows.Contains(x.Hwnd)).All(x => x.Minimized);
                }
                entries.AddRange(extra);
            }
        }

        if (cfg.ShowFrequent > 0 && frecency != null)
        {
            var have = new HashSet<string>(entries.Select(e => e.Key));
            foreach (var a in apps.Select(a => (a, s: frecency(a))).Where(x => x.s > 0.5 && !have.Contains("id:" + x.a.AppId)).OrderByDescending(x => x.s).Take(cfg.ShowFrequent).Select(x => x.a))
                entries.Add(new DockEntry { App = a, Name = a.Name, Icon = a.Icon, Key = "id:" + a.AppId });
        }

        return entries.Take(Math.Max(1, cfg.MaxItems)).ToList();
    }

    /// <summary>The window to go to when the entry is clicked: its latest window, or the next one when the frontmost already is the current one.</summary>
    public static IntPtr NextWindow(IReadOnlyList<IntPtr> windows, IntPtr current)
    {
        if (windows.Count == 0) return IntPtr.Zero;
        int i = -1;
        for (int k = 0; k < windows.Count; k++) if (windows[k] == current) { i = k; break; }
        return i < 0 ? windows[0] : windows[(i + 1) % windows.Count];
    }
}

// ---------------- importing the taskbar's pins (once) ----------------

internal static class DockImport
{
    /// <summary>
    /// Pin specs from the text of the Taskband "Favorites" blob (AppUserModelIDs and program paths found in it) and the targets of the pinned .lnk files.
    /// Only things that match a Start-menu app (or an existing exe path) are kept; duplicates are dropped.
    /// </summary>
    public static List<string> ExtractPins(string blobText, IEnumerable<string> lnkTargets, IReadOnlyList<AppEntry> apps)
    {
        var specs = new List<string>();
        void Add(string s) { if (!specs.Contains(s, StringComparer.OrdinalIgnoreCase)) specs.Add(s); }
        var ids = new HashSet<string>(apps.Select(a => a.AppId), StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(blobText, @"[A-Za-z0-9._\-]{3,}![A-Za-z0-9._\-]{2,}|Microsoft\.Windows\.Explorer"))
            if (ids.Contains(m.Value)) Add("id:" + m.Value);
        var paths = lnkTargets.Concat(Regex.Matches(blobText, @"[A-Za-z]:\\[^\x00-\x1f""<>|]{3,200}?\.exe", RegexOptions.IgnoreCase).Select(m => m.Value)).Where(p => !string.IsNullOrWhiteSpace(p));
        foreach (var p in paths)
        {
            string key = UsageStore.Norm(Path.GetFileNameWithoutExtension(p));
            var app = apps.FirstOrDefault(a => DockBuilder.ExeHints(a).Contains(key)) ?? apps.FirstOrDefault(a => UsageStore.Norm(a.Name) == key && DockBuilder.ExeHints(a).Count == 0);
            Add(app != null ? "id:" + app.AppId : "exe:" + p);
        }
        // a Start-menu app by its program name when the id is opaque (e.g. Firefox)
        return specs;
    }

    /// <summary>Reads the real taskbar pins of this user.</summary>
    public static List<string> ReadTaskbar(IReadOnlyList<AppEntry> apps)
    {
        var targets = new List<string>();
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(dir))
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                foreach (var f in Directory.GetFiles(dir, "*.lnk"))
                {
                    try { string t = sh.CreateShortcut(f).TargetPath; if (!string.IsNullOrEmpty(t)) targets.Add(t); } catch { }
                }
            }
        }
        catch { }
        string blob = "";
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (k?.GetValue("Favorites") is byte[] b) blob = Encoding.Unicode.GetString(b);
        }
        catch { }
        return ExtractPins(blob, targets, apps);
    }
}

/// <summary>The AppUserModelID of a window (packaged apps and Explorer have one that equals their Start-menu AppID).</summary>
internal static class WindowAumid
{
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    public static string? Get(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return null;
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { var s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; return string.IsNullOrEmpty(s) ? null : s; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
    }
}
