using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Spread;

internal sealed class QuickSettingsConfig
{
    /// <summary>Clicking the status group (network, sound, battery) opens Spread's own panel. Off = Windows' Win+A flyout.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Tiles in this order: wifi, bluetooth, power, night, dark, dnd, airplane, awake, vpn. Tiles this PC cannot do hide themselves.</summary>
    public string[] Tiles { get; set; } = new[] { "wifi", "bluetooth", "power", "night", "dark", "dnd", "airplane", "awake", "vpn" };
    /// <summary>Restart / sign out / shut down need a second click.</summary>
    public bool ConfirmPowerActions { get; set; } = true;
    public bool ShowPowerButtons { get; set; } = true;
    /// <summary>0 = Keep awake stays on until you switch it off.</summary>
    public int KeepAwakeMinutes { get; set; } = 0;
    /// <summary>The brightness slider never goes below this (percent), so the screen cannot be made black by accident.</summary>
    public int BrightnessMin { get; set; } = 5;
}

internal sealed class ExtraSetting
{
    public string Name { get; set; } = "";
    /// <summary>An ms-settings: address (e.g. ms-settings:windowsupdate-action) or a program to run (e.g. devmgmt.msc).</summary>
    public string Uri { get; set; } = "";
    public string Keywords { get; set; } = "";
}

internal sealed class EverythingConfig
{
    /// <summary>Search files with voidtools Everything (it must be running in your session). Off = no file section.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Only when Everything runs with -instance NAME.</summary>
    public string InstanceName { get; set; } = "";
    /// <summary>Everything.exe (only needed for "Start Everything" and "Show all"; found automatically when empty).</summary>
    public string ExePath { get; set; } = "";
    public int MaxResults { get; set; } = 8;
    /// <summary>name, name-desc, path, size-desc, date-modified-desc (newest first), date-created-desc ...</summary>
    public string Sort { get; set; } = "date-modified-desc";
    public bool MatchPath { get; set; } = true;
    public bool IncludeFolders { get; set; } = true;
    public int DebounceMs { get; set; } = 100;
    /// <summary>Show a "Start Everything" button when it is not running.</summary>
    public bool OfferStart { get; set; } = true;
}

internal sealed class SearchConfig
{
    /// <summary>Learn which apps you use and list them first.</summary>
    public bool Frecency { get; set; } = true;
    public double FrecencyHalfLifeDays { get; set; } = 14;
    /// <summary>Most tiles in the icon row.</summary>
    public int MaxApps { get; set; } = 20;
    /// <summary>Find Windows settings and tools (Check for updates, Device Manager ...).</summary>
    public bool Settings { get; set; } = true;
    public List<ExtraSetting> ExtraSettings { get; set; } = new();
    public EverythingConfig Everything { get; set; } = new();
    /// <summary>What Ctrl / Shift / Alt do when you open a result with Enter or a click.</summary>
    public SearchKeysConfig Keys { get; set; } = new();
}

/// <summary>Actions: default, newWindow, reveal (show in Explorer), openWith, copyPath, openFolder.</summary>
internal sealed class SearchKeysConfig
{
    public string AppCtrl { get; set; } = "newWindow";
    public string AppShift { get; set; } = "newWindow";
    public string AppAlt { get; set; } = "default";
    public string FileCtrl { get; set; } = "reveal";
    public string FileShift { get; set; } = "openWith";
    public string FileAlt { get; set; } = "copyPath";
    /// <summary>A dim line under the results that says what the held key does.</summary>
    public bool ShowHint { get; set; } = true;
}

internal sealed class WorldClock
{
    public string Name { get; set; } = "";
    public string TimeZoneId { get; set; } = "";
}

internal sealed class Config
{
    /// <summary>bottom, top, left, right, top-left, top-right, bottom-left, bottom-right, none</summary>
    public string HotEdge { get; set; } = "bottom";
    /// <summary>How long the pointer must rest on the edge. 0 = fire the moment it touches.</summary>
    public int HotEdgeDwellMs { get; set; } = 40;
    /// <summary>
    /// "Push" needed after the pointer has reached the edge: raw mouse movement (device counts, roughly pixels at normal speed) towards the edge/corner,
    /// counted while the pointer sits on it. 0 = off (touching the edge is enough). A push that pauses for 0.6 s starts over. Try 100-300.
    /// </summary>
    public int HotEdgePressure { get; set; } = 150;
    /// <summary>"natural" (windows keep their relative desktop positions) or "gnome" (GNOME 40+ rows).</summary>
    public string Layout { get; set; } = "natural";
    /// <summary>Config file version; used to migrate old defaults.</summary>
    public int Version { get; set; }
    /// <summary>0 = the wallpaper backdrop appears instantly; above 0 (default) it fades in during the first part of the animation (feels more fluent).</summary>
    public int BackdropFadeMs { get; set; } = 120;
    /// <summary>The always-visible GNOME-style top bar (reserves screen space).</summary>
    public bool TopBar { get; set; } = true;
    /// <summary>Top bar height in DIPs.</summary>
    public int TopBarHeight { get; set; } = 32;
    /// <summary>Show the tray icons of running programs in the top bar.</summary>
    public bool TrayStrip { get; set; } = true;
    /// <summary>Executable names (e.g. "claude.exe") to leave out of the tray strip.</summary>
    public string[] TrayExclude { get; set; } = Array.Empty<string>();
    /// <summary>"hidden": the Windows taskbar is set to auto-hide and its window kept hidden, so touching the screen edge never shows it. "autohide": only the auto-hide setting. "off": untouched. Always restored on exit.</summary>
    public string HideTaskbar { get; set; } = "hidden";
    // derived from HideTaskbar: not settings of their own, so they are not written to config.json (editing them there did nothing)
    [System.Text.Json.Serialization.JsonIgnore] public bool TaskbarManaged => HideTaskbar.Equals("autohide", StringComparison.OrdinalIgnoreCase) || HideTaskbar.Equals("hidden", StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore] public bool TaskbarWindowHidden => HideTaskbar.Equals("hidden", StringComparison.OrdinalIgnoreCase);
    /// <summary>The overlay is this many pixels narrower than the monitor. A window covering the whole monitor counts as a
    /// fullscreen app: Windows then hides/re-arranges the taskbar and app bars (flicker + a stall a few frames in).</summary>
    public int OverlayInsetPx { get; set; } = 1;
    /// <summary>"workarea": the overview covers the monitor minus the taskbar (the taskbar stays visible and untouched, like GNOME's
    /// top bar). "monitor": it covers the whole monitor (taskbar hidden under it).</summary>
    public string OverlayArea { get; set; } = "workarea";
    /// <summary>Diagnostics: do not register DWM thumbnails (windows show as icon cards).</summary>
    public bool DebugNoThumbnails { get; set; } = false;
    /// <summary>Where the idle (invisible) overlay windows wait. "offscreen": far outside every monitor. "sliver": only the
    /// bottom-right pixel overlaps the monitor, so Windows keeps their surfaces resident (experiment).</summary>
    public string ParkMode { get; set; } = "offscreen";
    public bool HotEdgeIgnoreFullscreen { get; set; } = true;
    /// <summary>e.g. "Ctrl+Alt+Space". Empty disables. Uses RegisterHotKey (no keyboard hook).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>Dependabot alerts of one team in the top bar (a shield + number). Uses your gh login; read-only.</summary>
    public GitHubConfig GitHub { get; set; } = new();
    /// <summary>spread.log detail: "normal" (start/stop, warnings, errors), "verbose" (adds timings, input, snapshots, searches ...) or "off".</summary>
    public string LogLevel { get; set; } = "normal";
    /// <summary>The log file is rotated when it reaches this size (KB).</summary>
    public int LogMaxKb { get; set; } = 1024;
    /// <summary>Old log files kept (spread.1.log ... spread.N.log).</summary>
    public int LogKeep { get; set; } = 3;
    /// <summary>The dock at the bottom of the overview (pinned + running apps).</summary>
    public DockConfig Dock { get; set; } = new();
    /// <summary>The quick-settings panel under the status group in the top bar.</summary>
    public QuickSettingsConfig QuickSettings { get; set; } = new();
    /// <summary>The search in the overview: apps, Windows settings, frequently used apps first, and files through Everything.</summary>
    public SearchConfig Search { get; set; } = new();
    /// <summary>Jira/Confluence item in the top bar (read-only). The API token is NOT here: it lives in Windows Credential Manager.</summary>
    public AtlassianConfig Atlassian { get; set; } = new();
    /// <summary>Apps (process names without .exe) whose windows fold into one "photo stack" in the overview when there are at least StackMinWindows of them.</summary>
    public string[] StackApps { get; set; } = new[] { "Greenshot" };
    public int StackMinWindows { get; set; } = 3;
    /// <summary>How many cards of a stack show their own strip; further cards hide behind the last one.</summary>
    public int StackVisible { get; set; } = 5;
    /// <summary>Strip width of a lower card, as a percentage of the biggest card's long side.</summary>
    public double StackOffsetPct { get; set; } = 4;
    /// <summary>Touchpad gestures: in Windows (Settings > Bluetooth & devices > Touchpad > Three-finger gestures > Custom) let a swipe send these keys. Registered only while the overview is open; empty = off.
    /// Swipe DOWN: go to the window under the pointer (or close the overview when the pointer is on empty space).</summary>
    public string GestureSelectHotkey { get; set; } = "Ctrl+Alt+Down";
    /// <summary>Swipe LEFT / RIGHT: move the selection (search results or windows), then swipe down to open it.</summary>
    public string GestureLeftHotkey { get; set; } = "Ctrl+Alt+Left";
    public string GestureRightHotkey { get; set; } = "Ctrl+Alt+Right";
    /// <summary>Shows/hides the real Windows taskbar (tray icons, Start). Same as the "Taskbar" button in the top bar.</summary>
    public string TaskbarHotkey { get; set; } = "Ctrl+Alt+T";
    /// <summary>Widgets to leave out of the top bar. Ids: weather, meeting, jira, github, vpn, media, wsl, load, keyboard, everything, clipboard, screenshot, bell, network, volume.</summary>
    public string[] DisabledWidgets { get; set; } = Array.Empty<string>();
    /// <summary>Where the weather widget looks (Open-Meteo, no key).</summary>
    public GeoPoint WeatherLocation { get; set; } = new();
    /// <summary>Regex matched against network adapter names; an adapter that is up shows the green VPN dot. Empty = no VPN dot.</summary>
    public string VpnAdapterMatch { get; set; } = "PANGP|GlobalProtect";
    /// <summary>Sent by the clipboard button while Ditto runs (Ditto's own hotkey). Otherwise Win+V.</summary>
    public string ClipboardHotkey { get; set; } = "Ctrl+Shift+V";
    /// <summary>Greenshot's own hotkey for "capture region"; the screenshot button sends it, so Greenshot's normal flow runs.</summary>
    public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+C";
    /// <summary>Clicking the clock opens Spread's own panel (calendar, mail, Teams, notifications) instead of the Windows calendar flyout.</summary>
    public bool ClockPanel { get; set; } = true;
    /// <summary>Clocks shown in the clock panel, e.g. [{"Name":"Singapore","TimeZoneId":"Singapore Standard Time"}].</summary>
    public WorldClock[] WorldClocks { get; set; } = Array.Empty<WorldClock>();
    /// <summary>Apps (display names as shown in the panel) whose notifications are hidden in the clock panel. They still appear in Windows.</summary>
    public string[] IgnoredNotificationApps { get; set; } = Array.Empty<string>();
    /// <summary>Show the first lines of unread mails in the panel. Off by default.</summary>
    public bool MailPreview { get; set; } = false;
    /// <summary>A dot next to the clock while there is unread mail (needs classic Outlook running).</summary>
    public bool ClockBadge { get; set; } = true;
    /// <summary>Public holidays marked in the calendar. Only "DK" is built in; anything else shows none.</summary>
    public string HolidayCountry { get; set; } = "DK";
    /// <summary>Width (DIPs) of the active-app name in the top bar; longer names end with "…". On a narrow bar it shrinks further (down to 70) before widgets are hidden.</summary>
    public int AppNameWidth { get; set; } = 130;
    /// <summary>Most characters of the next meeting's title in the top bar (the rest becomes "…"; the tooltip has the full title). 0 = no limit.</summary>
    public int MeetingTitleMax { get; set; } = 28;
    /// <summary>Show the ISO week number in the clock.</summary>
    public bool ShowWeek { get; set; } = true;
    /// <summary>Tap of the Windows key opens the overview. Needs a low-level keyboard hook, so it is off by default.</summary>
    public bool WinTap { get; set; } = false;
    public double MaxScale { get; set; } = 0.95;
    public string[] ExcludeProcesses { get; set; } = new[] { "seelen-ui", "SeelenUI", "Spread" };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spread");
    public static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>Hand-edited files may have comments and trailing commas.</summary>
    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    /// <summary>
    /// Set when config.json exists but could not be read (a typo while editing it by hand, or the file was locked). Spread then
    /// runs with defaults and must not save: the first pin in the dock or settings change would replace the user's file with defaults.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LoadFailed { get; private set; }

    string _path = FilePath;   // where Save writes (tests load from a temp folder); a field, so it is not serialized

    public void Save()
    {
        if (LoadFailed) { Log.Write("config not saved: config.json could not be read at start (fix it and restart Spread)"); return; }
        try { WriteFile(this, _path); }
        catch (Exception e) { Log.Write("config save failed: " + e.Message); }
    }

    /// <summary>Write to a temporary file, then swap it in: a crash or full disk mid-write never leaves a truncated config.json.</summary>
    static void WriteFile(Config c, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, Opts));
        File.Move(tmp, path, true);
    }

    static string ReadWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (i < 4) { System.Threading.Thread.Sleep(150); }   // an editor may be saving it right now
        }
    }

    public static Config Load() => LoadFrom(FilePath);

    internal static Config LoadFrom(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var opts = Opts;
            if (File.Exists(path))
            {
                Config loaded;
                try { loaded = JsonSerializer.Deserialize<Config>(ReadWithRetry(path), opts) ?? throw new JsonException("the file holds no settings object"); }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                {
                    string where = e is JsonException je && je.LineNumber != null ? $" (line {je.LineNumber + 1})" : "";
                    Log.Write($"config.json could not be read{where}: {e.GetType().Name}. Using defaults; the file is left as it is and will not be overwritten.");
                    return new Config { LoadFailed = true, _path = path };
                }
                loaded._path = path;
                if (loaded.Version < 2)
                {
                    // v2: the old 150 ms dwell was the previous default, not a deliberate choice.
                    if (loaded.HotEdgeDwellMs == 150) loaded.HotEdgeDwellMs = 40;
                    loaded.Version = 2;
                    Persist(loaded);
                }
                if (loaded.Version < 3)
                {
                    // v3: with the taskbar auto-hidden, the bottom edge would also reveal it: the hot corner (GNOME's) is the default now.
                    if (loaded.HotEdge.Equals("bottom", StringComparison.OrdinalIgnoreCase)) loaded.HotEdge = "top-left";
                    loaded.Version = 3;
                    Persist(loaded);
                }
                if (loaded.Version < 4)
                {
                    // v4: the bottom hot edge stays the default (the taskbar window is kept hidden, so it cannot pop up there)
                    if (loaded.HotEdge.Equals("top-left", StringComparison.OrdinalIgnoreCase)) loaded.HotEdge = "bottom";
                    if (loaded.HideTaskbar.Equals("autohide", StringComparison.OrdinalIgnoreCase)) loaded.HideTaskbar = "hidden";
                    loaded.Version = 4;
                    Persist(loaded);
                }
                if (loaded.Version < 5)
                {
                    // v5: top-bar widgets (new keys get their defaults; the file is rewritten so they are visible)
                    loaded.TrayStrip = false;
                    loaded.Version = 5;
                    Persist(loaded);
                }
                if (loaded.Version < 6)
                {
                    // v6: the clipboard button default was a guess (Ctrl+`); this user's Ditto hotkey is Ctrl+Shift+V
                    if (loaded.ClipboardHotkey == "Ctrl+Oem3") loaded.ClipboardHotkey = "Ctrl+Shift+V";
                    loaded.Version = 6;
                    Persist(loaded);
                }
                if (loaded.Version < 7)
                {
                    loaded.Version = 7;   // v7: clock panel settings (new keys get their defaults and are written to the file)
                    Persist(loaded);
                }
                if (loaded.Version < 8)
                {
                    loaded.Version = 8;   // v8: window stacks in the overview
                    Persist(loaded);
                }
                if (loaded.Version < 9)
                {
                    loaded.Version = 9;   // v9: Atlassian section
                    Persist(loaded);
                }
                if (loaded.Version < 10)
                {
                    loaded.Version = 10;   // v10: Atlassian alerts + token expiry
                    Persist(loaded);
                }
                if (loaded.Version < 11)
                {
                    loaded.Version = 11;   // v11: Search section (settings, frecency, Everything)
                    Persist(loaded);
                }
                if (loaded.Version < 12)
                {
                    loaded.Version = 12;   // v12: quick-settings panel
                    Persist(loaded);
                }
                if (loaded.Version < 13)
                {
                    loaded.Version = 13;   // v13: GitHub Dependabot shield
                    Persist(loaded);
                }
                if (loaded.Version < 14)
                {
                    loaded.Version = 14;   // v14: log level + rotation
                    Persist(loaded);
                }
                if (loaded.Version < 15)
                {
                    loaded.Version = 15;   // v15: dock
                    Persist(loaded);
                }
                return loaded;
            }
            var c = new Config { Version = 15, TrayStrip = false, _path = path };
            WriteFile(c, path);
            return c;
        }
        catch (Exception e) { Log.Write("config load failed: " + e.Message); return new Config { LoadFailed = File.Exists(path), _path = path }; }

        // a migration that cannot be written keeps the migrated settings in memory; the next start tries again
        static void Persist(Config c) { try { WriteFile(c, c._path); } catch (Exception e) { Log.Write("config migration not saved: " + e.GetType().Name); } }
    }
}

/// <summary>
/// spread.log with levels and rotation. Level "normal" (default) keeps start/stop, warnings and errors; "verbose" adds the detailed diagnostics
/// (overlay timings, input, snapshots, searches ...); "off" writes nothing. When the file passes LogMaxKb it becomes spread.1.log, older ones shift
/// (spread.2.log ...), and at most LogKeep archives are kept.
/// </summary>
internal static class Log
{
    static readonly object L = new();
    static long _size = -1;

    public static string Level { get; set; } = "normal";   // off | normal | verbose
    public static int MaxKb { get; set; } = 1024;
    public static int Keep { get; set; } = 3;
    public static string FilePath { get; set; } = Path.Combine(Config.Dir, "spread.log");

    public static bool IsVerbose => string.Equals(Level, "verbose", StringComparison.OrdinalIgnoreCase);

    public static void Configure(Config c)
    {
        Level = (c.LogLevel ?? "normal").Trim().ToLowerInvariant();
        if (Level != "off" && Level != "verbose") Level = "normal";
        MaxKb = Math.Max(64, c.LogMaxKb);
        Keep = Math.Clamp(c.LogKeep, 0, 20);
    }

    /// <summary>Detailed diagnostics: only written when the level is "verbose".</summary>
    public static void Debug(string msg) { if (IsVerbose) Write(msg); }

    public static void Write(string msg)
    {
        if (string.Equals(Level, "off", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            lock (L)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                Rotate();
                string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}";
                File.AppendAllText(FilePath, line);
                _size += System.Text.Encoding.UTF8.GetByteCount(line);
            }
        }
        catch { }
    }

    static void Rotate()
    {
        if (_size < 0) _size = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
        if (_size < MaxKb * 1024L) return;
        string dir = Path.GetDirectoryName(FilePath)!, stem = Path.GetFileNameWithoutExtension(FilePath), ext = Path.GetExtension(FilePath);
        string Arch(int i) => Path.Combine(dir, $"{stem}.{i}{ext}");
        try
        {
            if (Keep <= 0) File.Delete(FilePath);
            else
            {
                if (File.Exists(Arch(Keep))) File.Delete(Arch(Keep));
                for (int i = Keep - 1; i >= 1; i--) if (File.Exists(Arch(i))) File.Move(Arch(i), Arch(i + 1), true);
                File.Move(FilePath, Arch(1), true);
            }
        }
        catch { }
        _size = 0;
    }

    /// <summary>For tests: forget the cached file size.</summary>
    internal static void ResetSize() => _size = -1;
}
