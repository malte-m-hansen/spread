using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// Learns which apps you use. Per app key (lower-case letters/digits of the exe or app name) a decayed "frecency" score:
/// score = score * 0.5^(days / halfLife) + weight. Launching/choosing an app in Spread weighs 1; an app that simply stays in front for a while weighs a little.
/// Stored in usage.json: app keys and numbers only (no files, no window titles).
/// </summary>
internal sealed class UsageStore
{
    sealed class Entry { public double Score { get; set; } public DateTime Updated { get; set; } }

    public const double ActivationWeight = 0.15, MinStaySeconds = 5, MinGapMinutes = 2;

    readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    readonly Func<DateTime> _now;
    readonly double _halfLifeDays;
    readonly string _path;
    readonly bool _enabled;
    readonly HashSet<string> _exclude;
    readonly uint _selfPid = (uint)Environment.ProcessId;
    DateTime _lastSave = DateTime.MinValue;
    bool _dirty;

    // activation tracking
    string? _fgKey; DateTime _fgSince; bool _fgCounted;
    readonly Dictionary<string, DateTime> _lastCounted = new();
    DispatcherTimer? _timer;
    Native.WinEventProc? _ownForeground;   // kept alive for the hook

    public UsageStore(Config cfg, string? path = null, Func<DateTime>? now = null)
    {
        _enabled = cfg.Search.Frecency;
        _halfLifeDays = Math.Max(1, cfg.Search.FrecencyHalfLifeDays);
        _path = path ?? Path.Combine(Config.Dir, "usage.json");
        _now = now ?? (() => DateTime.UtcNow);
        _exclude = new HashSet<string>(cfg.ExcludeProcesses.Select(Norm), StringComparer.Ordinal);
        Load();
    }

    public bool Enabled => _enabled;

    public static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Key of a running program from its exe path.</summary>
    public static string KeyOfExe(string exePath) => Norm(Path.GetFileNameWithoutExtension(exePath));

    double Decayed(Entry e, DateTime now) => e.Score * Math.Pow(0.5, Math.Max(0, (now - e.Updated).TotalDays) / _halfLifeDays);

    public void Record(string key, double weight)
    {
        if (!_enabled || string.IsNullOrEmpty(key)) return;
        var now = _now();
        if (_map.TryGetValue(key, out var e)) { e.Score = Decayed(e, now) + weight; e.Updated = now; }
        else _map[key] = new Entry { Score = weight, Updated = now };
        _dirty = true;
    }

    public double ScoreOf(string key)
    {
        if (!_enabled || !_map.TryGetValue(key, out var e)) return 0;
        return Decayed(e, _now());
    }

    /// <summary>Same matching idea as finding a running window of a Start-menu entry: exe name from the app id, else the (normalized) name.</summary>
    public static bool KeyMatchesApp(AppEntry a, string key) => KeyMatches(IdExe(a), Norm(a.Name), key);

    static string? IdExe(AppEntry a)
    {
        try { if (a.AppId.Contains('\\') && a.AppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Norm(Path.GetFileNameWithoutExtension(a.AppId)); } catch { }
        return null;
    }

    static bool KeyMatches(string? idExe, string n, string key)
    {
        if (key.Length < 3) return false;
        if (idExe != null) return key == idExe;
        return n.Length >= 3 && (n == key || n.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(n, StringComparison.Ordinal));
    }

    /// <summary>
    /// Program keys count for the app by name (as before) or by the dock's exe hints: by name alone the time spent in msedge, WindowsTerminal or
    /// ms-teams never counted for Edge, Terminal or Teams. "app:" / "setting:" keys are choices, not programs: an app named "App" matched every one.
    /// </summary>
    public double ScoreOfApp(AppEntry a)
    {
        if (!_enabled) return 0;
        var now = _now();
        string? idExe = IdExe(a); string n = Norm(a.Name);
        var keys = DockBuilder.KeysOf(a);
        double s = 0;
        foreach (var kv in _map)
            if (kv.Key.IndexOf(':') < 0 && (KeyMatches(idExe, n, kv.Key) || DockBuilder.HintMatches(keys, kv.Key))) s += Decayed(kv.Value, now);
        s += ScoreOf("app:" + n);   // choices made in Spread are recorded under the app's own name as well
        return s;
    }

    // ---------------- activations ----------------

    /// <summary>The program that is in front changed (call with its key; null for shell/own windows).</summary>
    public void OnForeground(string? key)
    {
        FlushPending();
        _fgKey = key; _fgSince = _now(); _fgCounted = false;
    }

    /// <summary>Counts the current foreground program once it stayed in front long enough (also called by the timer).</summary>
    public void FlushPending()
    {
        if (!_enabled || _fgKey == null || _fgCounted) return;
        var now = _now();
        if ((now - _fgSince).TotalSeconds < MinStaySeconds) return;
        _fgCounted = true;
        if (_exclude.Contains(_fgKey)) return;
        if (_lastCounted.TryGetValue(_fgKey, out var last) && (now - last).TotalMinutes < MinGapMinutes) return;
        _lastCounted[_fgKey] = now;
        Record(_fgKey, ActivationWeight);
    }

    /// <summary>
    /// WinEvents skips Spread's own windows (WINEVENT_SKIPOWNPROCESS), so the overview taking the foreground was never seen: the seconds spent in it
    /// counted for the program that was in front before, which reached the 5 s even after a glance. This hook gets only this process's foreground changes.
    /// </summary>
    void HookOwnForeground()
    {
        _ownForeground = (_, _, _, idObject, idChild, _, _) => { if (idObject == 0 && idChild == 0) { try { OnForeground(null); } catch { } } };
        // EVENT_SYSTEM_FOREGROUND, this process only, WINEVENT_OUTOFCONTEXT: delivered on this (the UI) thread
        if (Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _ownForeground, _selfPid, 0, 0) == IntPtr.Zero) Log.Write("usage: own foreground hook failed");
    }

    public void Attach(WinEvents events)
    {
        if (!_enabled) return;
        HookOwnForeground();
        events.Foreground += h =>
        {
            try
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0 || pid == _selfPid) { OnForeground(null); return; }
                var cls = new StringBuilder(64); Native.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") { OnForeground(null); return; }
                if (cls.ToString() == "ApplicationFrameWindow")
                {
                    // a UWP app (Calculator, Photos ...) is hosted by ApplicationFrameHost: count the app in the CoreWindow child, as the window list does
                    var core = Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                    if (core != IntPtr.Zero) { Native.GetWindowThreadProcessId(core, out uint p2); if (p2 != 0) pid = p2; }
                }
                var hp = Native.OpenProcess(0x1000, false, pid);
                if (hp == IntPtr.Zero) { OnForeground(null); return; }
                try
                {
                    var sb = new StringBuilder(1024); int n = sb.Capacity;
                    OnForeground(Native.QueryFullProcessImageName(hp, 0, sb, ref n) ? KeyOfExe(sb.ToString()) : null);
                }
                finally { Native.CloseHandle(hp); }
            }
            catch { OnForeground(null); }
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => { FlushPending(); Save(false); };
        _timer.Start();
    }

    // ---------------- persistence ----------------

    void Load()
    {
        try
        {
            if (!_enabled || !File.Exists(_path)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
            if (d != null) foreach (var kv in d) if (kv.Value != null) _map[kv.Key] = kv.Value;
        }
        catch { }
    }

    /// <summary>
    /// Written to a temporary file that then replaces usage.json: a write cut short (the process is ended 0.8 s after a console shutdown request, power loss)
    /// left a truncated file that Load could not read, and the next save replaced all history with what was left.
    /// </summary>
    public void Save(bool force)
    {
        if (!_enabled || !_dirty) return;
        var now = _now();
        if (!force && (now - _lastSave).TotalSeconds < 60) return;
        try
        {
            foreach (var k in _map.Where(kv => Decayed(kv.Value, now) < 0.02).Select(kv => kv.Key).ToList()) _map.Remove(k);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
            File.Move(tmp, _path, overwrite: true);
            _lastSave = now; _dirty = false;
        }
        catch { }
    }
}
