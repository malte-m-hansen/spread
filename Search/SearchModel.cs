using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spread;

internal enum SearchKind { App, Setting, Tool }

/// <summary>One tile in the icon strip: an app, a Windows settings page or a Windows tool.</summary>
internal sealed class SearchItem
{
    public SearchKind Kind;
    public string Title = "";
    public string Subtitle = "";
    public AppEntry? App;
    public SettingsEntry? Setting;
    /// <summary>Key under which choosing this item is remembered.</summary>
    public string UsageKey = "";
}

internal sealed class SettingsEntry
{
    public string Id = "", Title = "", Keywords = "", Uri = "", Exe = "", Args = "";
    public bool Tool;
    public string[] KeywordList => Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Ranks apps and settings for a query: match tier first, then how much you use it, then apps before settings, then shorter names.</summary>
internal sealed class SearchEngine
{
    readonly AppCatalog _apps;
    readonly List<SettingsEntry> _settings;
    readonly UsageStore _usage;
    readonly SearchConfig _cfg;

    public UsageStore Usage => _usage;

    public SearchEngine(AppCatalog apps, List<SettingsEntry> settings, UsageStore usage, SearchConfig cfg)
    {
        _apps = apps; _settings = settings; _usage = usage; _cfg = cfg;
    }

    static readonly char[] Seps = { ' ', '-', '.', '_', '/', '(', ')', ',', '&', ':', '|' };

    /// <summary>-1 = no match. 0 exact title, 1 title starts with, 2 every typed word starts a word of the title, 3 title contains it, 4/5 the same on title + keywords.</summary>
    public static int Tier(string title, string[] keywords, string query)
    {
        string q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return -1;
        string t = title.ToLowerInvariant();
        if (t == q) return 0;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 1;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tw = t.Split(Seps, StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => tw.Any(x => x.StartsWith(w, StringComparison.Ordinal)))) return 2;
        if (t.Contains(q, StringComparison.Ordinal)) return 3;
        if (keywords.Length == 0) return -1;
        string hay = t + " " + string.Join(" ", keywords).ToLowerInvariant();
        var hw = hay.Split(Seps, StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => hw.Any(x => x.StartsWith(w, StringComparison.Ordinal)))) return 4;
        if (words.All(w => hay.Contains(w, StringComparison.Ordinal))) return 5;
        return -1;
    }

    public List<SearchItem> Query(string text)
    {
        string q = text.Trim();
        var res = new List<SearchItem>();
        if (q.Length == 0) return res;
        var c = new List<(int tier, double fr, int kind, int len, SearchItem item)>();
        foreach (var a in _apps.All)
        {
            int t = Tier(a.Name, Array.Empty<string>(), q);
            if (t < 0) continue;
            c.Add((t, _usage.ScoreOfApp(a), 0, a.Name.Length, new SearchItem { Kind = SearchKind.App, Title = a.Name, App = a, UsageKey = "app:" + UsageStore.Norm(a.Name) }));
        }
        if (_cfg.Settings)
            foreach (var s in _settings)
            {
                int t = Tier(s.Title, s.KeywordList, q);
                if (t < 0) continue;
                c.Add((t, _usage.ScoreOf("setting:" + s.Id), 1, s.Title.Length,
                    new SearchItem { Kind = s.Tool ? SearchKind.Tool : SearchKind.Setting, Title = s.Title, Subtitle = s.Tool ? "Tool" : "Setting", Setting = s, UsageKey = "setting:" + s.Id }));
            }
        return c.OrderBy(x => x.tier).ThenByDescending(x => Math.Round(x.fr, 2)).ThenBy(x => x.kind).ThenBy(x => x.len)
                .Take(Math.Max(1, _cfg.MaxApps)).Select(x => x.item).ToList();
    }
}

/// <summary>The built-in list of Windows settings pages and tools, plus the user's ExtraSettings from the config.</summary>
internal static class SettingsProvider
{
    static SettingsEntry S(string id, string title, string keywords, string uri) => new() { Id = id, Title = title, Keywords = keywords, Uri = uri };
    static SettingsEntry T(string id, string title, string keywords, string exe, string args = "") => new() { Id = id, Title = title, Keywords = keywords, Exe = exe, Args = args, Tool = true };

    public static List<SettingsEntry> Load(SearchConfig cfg)
    {
        var all = Builtin();
        // tools that do not exist on this Windows (Home editions have no gpedit etc.) are left out
        all = all.Where(e => !e.Tool || ToolExists(e.Exe)).ToList();
        foreach (var x in cfg.ExtraSettings)
        {
            if (string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Uri)) continue;
            string id = "extra:" + UsageStore.Norm(x.Name);
            all.RemoveAll(e => e.Id == id || string.Equals(e.Title, x.Name, StringComparison.OrdinalIgnoreCase));
            bool isUri = x.Uri.Contains(':') && !x.Uri.Contains('\\') && !x.Uri.Contains("/") || x.Uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);
            all.Add(new SettingsEntry { Id = id, Title = x.Name, Keywords = x.Keywords ?? "", Uri = isUri ? x.Uri : "", Exe = isUri ? "" : x.Uri, Tool = !isUri });
        }
        return all;
    }

    static bool ToolExists(string exe)
    {
        if (exe.Contains(':') || exe.Contains('\\')) return File.Exists(exe);
        string sys = Environment.SystemDirectory;
        return File.Exists(Path.Combine(sys, exe)) || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe));
    }

    public static List<SettingsEntry> Builtin() => new()
    {
        // ---- Windows Update ----
        S("update-check", "Check for updates", "windows update; updates; upgrade; patches; install updates", "ms-settings:windowsupdate-action"),
        S("update", "Windows Update", "updates; upgrade; patches", "ms-settings:windowsupdate"),
        S("update-history", "Update history", "windows update; installed updates; failed updates", "ms-settings:windowsupdate-history"),
        S("update-advanced", "Windows Update advanced options", "update options; restart; active hours", "ms-settings:windowsupdate-options"),
        S("update-optional", "Optional updates", "drivers; windows update; extra updates", "ms-settings:windowsupdate-optionalupdates"),
        S("update-pause", "Pause updates", "windows update; delay updates", "ms-settings:windowsupdate"),
        S("delivery-optimization", "Delivery Optimization", "windows update; bandwidth; peer to peer", "ms-settings:delivery-optimization"),
        S("insider", "Windows Insider Program", "insider; preview builds; beta", "ms-settings:windowsinsider"),
        // ---- System ----
        S("display", "Display", "screen; monitor; resolution; scale; brightness; orientation", "ms-settings:display"),
        S("night-light", "Night light", "display; blue light; warm colors; eye strain", "ms-settings:nightlight"),
        S("display-advanced", "Advanced display", "refresh rate; monitor; hz", "ms-settings:display-advanced"),
        S("sound", "Sound", "audio; volume; speakers; microphone; output; input", "ms-settings:sound"),
        S("notifications", "Notifications", "alerts; do not disturb; toasts", "ms-settings:notifications"),
        S("focus", "Focus", "do not disturb; focus session; quiet", "ms-settings:quietmomentshome"),
        S("power", "Power & battery", "power mode; sleep; battery; energy; screen timeout", "ms-settings:powersleep"),
        S("battery-saver", "Battery saver", "power; low battery", "ms-settings:batterysaver"),
        S("storage", "Storage", "disk space; storage sense; cleanup; free up space", "ms-settings:storagesense"),
        S("multitasking", "Multitasking", "snap; virtual desktops; alt tab; windows", "ms-settings:multitasking"),
        S("clipboard", "Clipboard", "clipboard history; paste", "ms-settings:clipboard"),
        S("remote-desktop", "Remote Desktop", "rdp; remote access", "ms-settings:remotedesktop"),
        S("projecting", "Projecting to this PC", "cast; miracast; wireless display", "ms-settings:project"),
        S("nearby", "Nearby sharing", "share; cross device", "ms-settings:crossdevice"),
        S("about", "About", "pc name; device specifications; windows version; rename pc; system info", "ms-settings:about"),
        S("troubleshoot", "Troubleshoot", "fix problems; diagnostics", "ms-settings:troubleshoot"),
        S("recovery", "Recovery", "reset this pc; restore; advanced startup", "ms-settings:recovery"),
        S("activation", "Activation", "windows license; product key", "ms-settings:activation"),
        S("developers", "For developers", "developer mode; dev drive; terminal", "ms-settings:developers"),
        // ---- Bluetooth, devices, network ----
        S("bluetooth", "Bluetooth & devices", "bluetooth; pair; add device; headphones; mouse; keyboard", "ms-settings:bluetooth"),
        S("printers", "Printers & scanners", "printer; scanner; print", "ms-settings:printers"),
        S("mouse", "Mouse", "pointer; cursor speed; scroll; buttons", "ms-settings:mousetouchpad"),
        S("touchpad", "Touchpad", "gestures; scroll; tap", "ms-settings:devices-touchpad"),
        S("typing", "Typing", "autocorrect; text suggestions; keyboard", "ms-settings:typing"),
        S("autoplay", "AutoPlay", "usb; removable drives", "ms-settings:autoplay"),
        S("usb", "USB", "usb devices; charging", "ms-settings:usb"),
        S("network", "Network & internet", "internet; connection; status", "ms-settings:network"),
        S("wifi", "Wi-Fi", "wireless; wlan; networks; connect", "ms-settings:network-wifi"),
        S("ethernet", "Ethernet", "cable; lan; network adapter", "ms-settings:network-ethernet"),
        S("vpn", "VPN", "virtual private network; globalprotect; connect", "ms-settings:network-vpn"),
        S("airplane", "Airplane mode", "flight mode; wireless off", "ms-settings:network-airplanemode"),
        S("hotspot", "Mobile hotspot", "share internet; tethering", "ms-settings:network-mobilehotspot"),
        S("proxy", "Proxy", "proxy server; network", "ms-settings:network-proxy"),
        S("network-advanced", "Advanced network settings", "adapters; network reset; sharing", "ms-settings:network-advancedsettings"),
        S("data-usage", "Data usage", "metered connection; network", "ms-settings:datausage"),
        // ---- Personalization ----
        S("personalization", "Personalization", "themes; colors; background; lock screen", "ms-settings:personalization"),
        S("background", "Background", "wallpaper; desktop picture", "ms-settings:personalization-background"),
        S("colors", "Colors", "dark mode; light mode; accent color; theme", "ms-settings:colors"),
        S("themes", "Themes", "dark mode; appearance; desktop theme", "ms-settings:themes"),
        S("lockscreen", "Lock screen", "screen saver; lock", "ms-settings:lockscreen"),
        S("start", "Start", "start menu; pinned; recommended", "ms-settings:personalization-start"),
        S("taskbar", "Taskbar", "taskbar behaviors; icons; alignment; search box", "ms-settings:taskbar"),
        S("fonts", "Fonts", "install fonts; typefaces", "ms-settings:fonts"),
        // ---- Apps ----
        S("apps", "Installed apps", "apps & features; uninstall; programs", "ms-settings:appsfeatures"),
        S("default-apps", "Default apps", "default browser; file associations; open with", "ms-settings:defaultapps"),
        S("optional-features", "Optional features", "windows features; add a feature", "ms-settings:optionalfeatures"),
        S("startup-apps", "Startup apps", "autostart; run at startup; boot programs", "ms-settings:startupapps"),
        S("apps-websites", "Apps for websites", "web apps; open links", "ms-settings:appsforwebsites"),
        S("video-playback", "Video playback", "hdr; video settings", "ms-settings:videoplayback"),
        // ---- Accounts ----
        S("your-info", "Your info", "account; profile picture; microsoft account", "ms-settings:yourinfo"),
        S("accounts-email", "Email & accounts", "add account; work account", "ms-settings:emailandaccounts"),
        S("signin", "Sign-in options", "password; pin; windows hello; fingerprint; face", "ms-settings:signinoptions"),
        S("work-school", "Access work or school", "company account; connect; mdm; enroll", "ms-settings:workplace"),
        S("other-users", "Other users", "family; add user; accounts", "ms-settings:otherusers"),
        S("backup", "Windows backup", "sync; onedrive backup; restore", "ms-settings:windowsbackup"),
        // ---- Time & language ----
        S("datetime", "Date & time", "clock; time zone; set time automatically; calendar", "ms-settings:dateandtime"),
        S("language", "Language & region", "display language; region; keyboard layout; add language", "ms-settings:regionlanguage"),
        S("region-format", "Regional format", "date format; number format; first day of week", "ms-settings:regionformatting"),
        S("speech", "Speech", "voice; microphone language", "ms-settings:speech"),
        // ---- Gaming ----
        S("game-bar", "Game Bar", "xbox; capture; recording", "ms-settings:gaming-gamebar"),
        S("game-mode", "Game Mode", "gaming; performance", "ms-settings:gaming-gamemode"),
        // ---- Accessibility ----
        S("accessibility", "Accessibility", "ease of access; vision; hearing; interaction", "ms-settings:easeofaccess"),
        S("text-size", "Text size", "accessibility; bigger text; font size", "ms-settings:easeofaccess-display"),
        S("mouse-pointer", "Mouse pointer and touch", "accessibility; cursor size; pointer color", "ms-settings:easeofaccess-cursorandpointersize"),
        S("magnifier", "Magnifier", "accessibility; zoom screen", "ms-settings:easeofaccess-magnifier"),
        S("narrator", "Narrator", "accessibility; screen reader", "ms-settings:easeofaccess-narrator"),
        S("color-filters", "Color filters", "accessibility; color blindness", "ms-settings:easeofaccess-colorfilter"),
        S("contrast-themes", "Contrast themes", "accessibility; high contrast", "ms-settings:easeofaccess-highcontrast"),
        S("captions", "Closed captions", "accessibility; subtitles", "ms-settings:easeofaccess-closedcaptioning"),
        S("accessibility-keyboard", "Keyboard accessibility", "sticky keys; filter keys; on-screen keyboard", "ms-settings:easeofaccess-keyboard"),
        // ---- Privacy & security ----
        S("windows-security", "Windows Security", "antivirus; defender; firewall; virus protection; threat", "ms-settings:windowsdefender"),
        S("privacy", "Privacy & security", "permissions; app permissions", "ms-settings:privacy"),
        S("privacy-general", "Privacy general", "advertising id; permissions", "ms-settings:privacy-general"),
        S("location", "Location", "gps; location services; privacy", "ms-settings:privacy-location"),
        S("camera", "Camera", "webcam; privacy; app access", "ms-settings:privacy-webcam"),
        S("microphone", "Microphone", "privacy; app access; recording", "ms-settings:privacy-microphone"),
        S("activity-history", "Activity history", "privacy; timeline", "ms-settings:privacy-activityhistory"),
        S("find-my-device", "Find my device", "locate; lost laptop", "ms-settings:findmydevice"),
        S("search-permissions", "Searching Windows", "search; indexing; safe search", "ms-settings:search-permissions"),
        S("file-system-access", "File system access", "privacy; apps; documents", "ms-settings:privacy-broadfilesystemaccess"),
        // ---- Tools ----
        T("tool-devmgmt", "Device Manager", "drivers; hardware; devices", "devmgmt.msc"),
        T("tool-services", "Services", "windows services; start stop service", "services.msc"),
        T("tool-eventvwr", "Event Viewer", "logs; errors; windows logs", "eventvwr.msc"),
        T("tool-taskschd", "Task Scheduler", "scheduled tasks; cron; automate", "taskschd.msc"),
        T("tool-diskmgmt", "Disk Management", "partitions; volumes; format; drive letter", "diskmgmt.msc"),
        T("tool-compmgmt", "Computer Management", "management console; shared folders; local users", "compmgmt.msc"),
        T("tool-perfmon", "Performance Monitor", "counters; perfmon; performance", "perfmon.msc"),
        T("tool-resmon", "Resource Monitor", "cpu; memory; disk; network usage; resmon", "resmon.exe"),
        T("tool-taskmgr", "Task Manager", "processes; kill; performance; startup", "taskmgr.exe"),
        T("tool-msinfo", "System Information", "msinfo32; hardware; bios; system summary", "msinfo32.exe"),
        T("tool-regedit", "Registry Editor", "regedit; registry", "regedit.exe"),
        T("tool-env", "Environment Variables", "path; env; system properties; user variables", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables"),
        T("tool-sysprops", "System Properties", "advanced system settings; computer name; remote; performance options", "SystemPropertiesAdvanced.exe"),
        T("tool-ncpa", "Network Connections", "network adapters; ncpa; change adapter options", "ncpa.cpl"),
        T("tool-appwiz", "Programs and Features", "uninstall a program; appwiz; installed programs", "appwiz.cpl"),
        T("tool-features", "Windows Features", "turn windows features on or off; hyper-v; wsl; telnet", "optionalfeatures.exe"),
        T("tool-powercfg", "Power Options (classic)", "power plan; high performance; balanced; lid close", "powercfg.cpl"),
        T("tool-mmsys", "Sound (classic)", "playback devices; recording devices; sound control panel", "mmsys.cpl"),
        T("tool-mouse", "Mouse properties (classic)", "main.cpl; pointer options; buttons", "main.cpl"),
        T("tool-timedate", "Date and Time (classic)", "timedate.cpl; clock; internet time", "timedate.cpl"),
        T("tool-inetcpl", "Internet Options", "inetcpl; proxy; certificates; browser settings", "inetcpl.cpl"),
        T("tool-firewall", "Windows Defender Firewall", "firewall.cpl; allow an app; ports", "firewall.cpl"),
        T("tool-wf", "Firewall with Advanced Security", "wf.msc; inbound rules; outbound rules", "wf.msc"),
        T("tool-certmgr", "Certificates (current user)", "certmgr; certificate manager; trusted roots", "certmgr.msc"),
        T("tool-lusrmgr", "Local Users and Groups", "lusrmgr; administrators group; local accounts", "lusrmgr.msc"),
        T("tool-gpedit", "Local Group Policy Editor", "gpedit; group policy; policies", "gpedit.msc"),
        T("tool-control", "Control Panel", "classic control panel", "control.exe"),
        T("tool-winver", "About Windows (winver)", "version; build number; edition", "winver.exe"),
        T("tool-mstsc", "Remote Desktop Connection", "mstsc; rdp client", "mstsc.exe"),
        T("tool-dxdiag", "DirectX Diagnostic Tool", "dxdiag; graphics; display info", "dxdiag.exe"),
        T("tool-cleanmgr", "Disk Cleanup", "cleanmgr; free space; temporary files", "cleanmgr.exe"),
        T("tool-dfrgui", "Defragment and Optimize Drives", "dfrgui; trim; optimize", "dfrgui.exe"),
        T("tool-mdsched", "Windows Memory Diagnostic", "mdsched; ram test", "mdsched.exe"),
        T("tool-charmap", "Character Map", "charmap; symbols; special characters", "charmap.exe"),
        T("tool-msconfig", "System Configuration", "msconfig; boot options; services startup", "msconfig.exe"),
        T("tool-snip", "Snipping Tool", "screenshot; capture; screen clip", "SnippingTool.exe"),
    };
}
