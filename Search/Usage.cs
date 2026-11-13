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
    public static bool KeyMatchesApp(AppEntry a, string key)
    {
        if (key.Length < 3) return false;
        string? idExe = null;
        try { if (a.AppId.Contains('\\') && a.AppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) idExe = Norm(Path.GetFileNameWithoutExtension(a.AppId)); } catch { }
        if (idExe != null) return key == idExe;
        string n = Norm(a.Name);
        return n.Length >= 3 && (n == key || n.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(n, StringComparison.Ordinal));
    }

    public double ScoreOfApp(AppEntry a)
    {
        if (!_enabled) return 0;
        double s = 0;
        foreach (var k in _map.Keys) if (KeyMatchesApp(a, k)) s += ScoreOf(k);
        s += ScoreOf("app:" + Norm(a.Name));   // choices made in Spread are recorded under the app's own name as well
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

    public void Attach(WinEvents events)
    {
        if (!_enabled) return;
        events.Foreground += h =>
        {
            try
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0 || pid == _selfPid) { OnForeground(null); return; }
                var cls = new StringBuilder(64); Native.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") { OnForeground(null); return; }
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
            if (d != null) foreach (var kv in d) _map[kv.Key] = kv.Value;
        }
        catch { }
    }

    public void Save(bool force)
    {
        if (!_enabled || !_dirty) return;
        var now = _now();
        if (!force && (now - _lastSave).TotalSeconds < 60) return;
        try
        {
            foreach (var k in _map.Where(kv => Decayed(kv.Value, now) < 0.02).Select(kv => kv.Key).ToList()) _map.Remove(k);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_map));
            _lastSave = now; _dirty = false;
        }
        catch { }
    }
}
