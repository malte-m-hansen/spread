using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Spread;

// Data sources for the bar widgets. Static and UI-free so `--widget-test` can run them headlessly.

internal sealed class GeoPoint
{
    public string Name { get; set; } = "Copenhagen";
    public double Lat { get; set; } = 55.6761;
    public double Lon { get; set; } = 12.5683;
}

// ---------------- network + VPN ----------------

internal sealed record NetState(string Kind, string Name, bool VpnUp, string VpnName);

internal static class GlobalProtectProbe
{
    /// <summary>"internal", "connected"... as written by the agent; empty when unknown. Readable without admin.</summary>
    public static string Status()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Palo Alto Networks\GlobalProtect\DEM"); return (k?.GetValue("tunnel-status") as string) ?? ""; }
        catch { return ""; }
    }

    public static bool IsActive(string status) => status.Length > 0 && !new[] { "disconnected", "none", "unknown", "disabled", "not-connected", "disconnecting" }.Contains(status.ToLowerInvariant());
}

internal static class NetProbe
{
    static readonly Regex Virtual = new("Virtual|vEthernet|WSL|Hyper-V|VMware|VirtualBox|Bluetooth|Loopback|TAP-|Npcap", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static NetState Read(string vpnMatch)
    {
        string kind = "none", name = "", vname = "";
        bool vpn = false;
        Regex? rx = null;
        try { if (!string.IsNullOrWhiteSpace(vpnMatch)) rx = new Regex(vpnMatch, RegexOptions.IgnoreCase); } catch { }
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            string text = ni.Description + " " + ni.Name;
            if (rx != null && rx.IsMatch(text)) { vpn = true; vname = ni.Name; continue; }
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel || Virtual.IsMatch(text)) continue;
            bool gateway = ni.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
            if (!gateway) continue;
            string k = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet";
            if (kind == "none" || (kind == "wifi" && k == "ethernet")) { kind = k; name = ni.Name; }
        }
        if (vpn && rx != null && rx.IsMatch("GlobalProtect") && !vname.StartsWith("GlobalProtect")) vname = "GlobalProtect: " + GlobalProtectEndpoint.Current(GlobalProtectProbe.Status());
        // GlobalProtect with split tunnelling / internal gateway brings no adapter up: ask the agent's own state
        if (!vpn && rx != null && rx.IsMatch("GlobalProtect"))
        {
            string st = GlobalProtectProbe.Status();
            if (GlobalProtectProbe.IsActive(st)) { vpn = true; vname = "GlobalProtect: " + GlobalProtectEndpoint.Current(st); }
            else if (st.Length > 0) vname = "GlobalProtect: " + st;
        }
        return new NetState(kind, name, vpn, vname);
    }

    public static string DescribeAll(string vpnMatch)
    {
        var sb = new StringBuilder();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            sb.Append($"\n    {ni.OperationalStatus,-8} {ni.NetworkInterfaceType,-18} {ni.Name} / {ni.Description}");
        return sb.ToString();
    }
}

// ---------------- volume (Core Audio, no NuGet) ----------------

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>Master volume of the default speakers. Use from one (STA) thread.</summary>
internal static class VolumeProbe
{
    static IAudioEndpointVolume? _ep;
    static int _age;
    static Guid _ctx = Guid.Empty;

    static IAudioEndpointVolume? Endpoint()
    {
        if (_ep != null && ++_age < 15) return _ep;   // the default device can change: look again every ~15 reads
        _age = 0; _ep = null;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out var dev) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var o) != 0) return null;
            _ep = (IAudioEndpointVolume)o;
        }
        catch { _ep = null; }
        return _ep;
    }

    public static bool TryGet(out float level, out bool mute)
    {
        level = 0; mute = false;
        try
        {
            var ep = Endpoint();
            if (ep == null) return false;
            if (ep.GetMasterVolumeLevelScalar(out level) != 0) { _ep = null; return false; }
            ep.GetMute(out mute);
            return true;
        }
        catch { _ep = null; return false; }
    }

    public static void Set(float level) { try { Endpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _ctx); } catch { _ep = null; } }
    public static void SetMute(bool m) { try { Endpoint()?.SetMute(m, ref _ctx); } catch { _ep = null; } }
}

// ---------------- CPU / memory ----------------

internal static class LoadProbe
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    static long _i, _k, _u;
    static bool _have;

    /// <summary>CPU % since the previous call (0 on the first call), memory used/total in GB.</summary>
    public static (double cpu, double usedGb, double totalGb) Read()
    {
        double cpu = 0;
        if (GetSystemTimes(out long i, out long k, out long u))
        {
            if (_have)
            {
                double total = (k - _k) + (u - _u), idle = i - _i;
                if (total > 0) cpu = Math.Clamp(100.0 * (1.0 - idle / total), 0, 100);
            }
            _i = i; _k = k; _u = u; _have = true;
        }
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        double gb = 1024.0 * 1024 * 1024;
        return (cpu, (m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }
}

// ---------------- keyboard layout ----------------

internal static class KeyboardProbe
{
    public static int InstalledCount() { try { return System.Windows.Forms.InputLanguage.InstalledInputLanguages.Count; } catch { return 1; } }

    public static string Current()
    {
        try
        {
            uint tid = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            int lang = (int)((long)Native.GetKeyboardLayout(tid) & 0xFFFF);
            return new CultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch { return "??"; }
    }
}

// ---------------- notifications on/off ----------------

internal static class DndProbe
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string Name = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    public static bool ToastsEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(Key); return !(k?.GetValue(Name) is int i && i == 0); }
        catch { return true; }
    }

    public static void Set(bool enabled)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue(Name, enabled ? 1 : 0, RegistryValueKind.DWord); }
        catch (Exception e) { Log.Write("notifications toggle failed: " + e.Message); }
    }
}

// ---------------- WSL ----------------

internal static class WslProbe
{
    /// <summary>running?, memory in bytes (0 when Windows does not tell us).</summary>
    public static (bool running, long bytes) Read()
    {
        foreach (var name in new[] { "vmmemWSL", "vmmem" })
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); } catch { continue; }
            if (ps.Length == 0) continue;
            long b = 0;
            foreach (var p in ps) { try { b += p.WorkingSet64; } catch { } p.Dispose(); }
            return (true, b);
        }
        return (false, 0);
    }

    /// <summary>Running distributions. wsl.exe can hang while the WSL service is stuck: the output is read asynchronously so the 4 s limit really applies.</summary>
    public static string[] RunningDistros()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l --running") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.Unicode };
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { try { p.Kill(entireProcessTree: true); } catch { } Log.Write("wsl -l --running did not answer within 4 s"); return Array.Empty<string>(); }
            if (!read.Wait(2000)) return Array.Empty<string>();
            string o = read.Result;
            return o.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("Windows Subsystem", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("There are no", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Der er ingen", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Replace("(Default)", "").Trim()).Where(l => l.Length > 0).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static void Shutdown()
    {
        try { Process.Start(new ProcessStartInfo("wsl.exe", "--shutdown") { UseShellExecute = false, CreateNoWindow = true })?.Dispose(); }
        catch (Exception e) { Log.Write("wsl --shutdown failed: " + e.Message); }
    }
}

// ---------------- weather (Open-Meteo, no key) ----------------

internal sealed record WeatherHour(DateTime Time, double Temp, int Code);
internal sealed record WeatherDay(DateTime Date, double Max, double Min, int Code);
internal sealed record WeatherData(double Temp, int Code, bool IsDay, double Wind, List<WeatherHour> Hours, List<WeatherDay> Days, DateTime? Sunrise = null, DateTime? Sunset = null);

internal static class WeatherProbe
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string CacheFile => Path.Combine(Config.Dir, "weather.json");

    public static async Task<WeatherData?> FetchAsync(GeoPoint g)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={g.Lat}&longitude={g.Lon}&current=temperature_2m,weather_code,is_day,wind_speed_10m&hourly=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset&forecast_days=4&timezone=auto");
        string json = await Http.GetStringAsync(url).ConfigureAwait(false);
        var d = Parse(json);
        if (d != null) { try { Directory.CreateDirectory(Config.Dir); File.WriteAllText(CacheFile, json); } catch { } }
        return d;
    }

    public static WeatherData? LoadCache()
    {
        try { return File.Exists(CacheFile) ? Parse(File.ReadAllText(CacheFile)) : null; } catch { return null; }
    }

    public static WeatherData? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var cur = r.GetProperty("current");
            var hours = new List<WeatherHour>();
            var h = r.GetProperty("hourly");
            var ht = h.GetProperty("time"); var hT = h.GetProperty("temperature_2m"); var hC = h.GetProperty("weather_code");
            for (int i = 0; i < ht.GetArrayLength(); i++)
                if (DateTime.TryParse(ht[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    hours.Add(new WeatherHour(t, hT[i].ValueKind == JsonValueKind.Null ? 0 : hT[i].GetDouble(), hC[i].ValueKind == JsonValueKind.Null ? 0 : hC[i].GetInt32()));
            var days = new List<WeatherDay>();
            var d = r.GetProperty("daily");
            var dt = d.GetProperty("time"); var dMax = d.GetProperty("temperature_2m_max"); var dMin = d.GetProperty("temperature_2m_min"); var dC = d.GetProperty("weather_code");
            for (int i = 0; i < dt.GetArrayLength(); i++)
                if (DateTime.TryParse(dt[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    days.Add(new WeatherDay(t, dMax[i].GetDouble(), dMin[i].GetDouble(), dC[i].GetInt32()));
            DateTime? sr = null, ss = null;
            try
            {
                if (d.TryGetProperty("sunrise", out var srA) && DateTime.TryParse(srA[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)) sr = a;
                if (d.TryGetProperty("sunset", out var ssA) && DateTime.TryParse(ssA[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)) ss = b;
            }
            catch { }
            return new WeatherData(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(),
                cur.GetProperty("is_day").GetInt32() == 1, cur.GetProperty("wind_speed_10m").GetDouble(), hours, days, sr, ss);
        }
        catch { return null; }
    }

    public static string Glyph(int code, bool day) => code switch
    {
        0 => day ? "☀" : "🌙",
        1 => day ? "🌤" : "🌙",
        2 => "⛅",
        3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 57 => "🌦",
        >= 61 and <= 67 => "🌧",
        >= 71 and <= 77 => "🌨",
        >= 80 and <= 82 => "🌦",
        85 or 86 => "🌨",
        >= 95 => "⛈",
        _ => "·"
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast", 45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle", >= 61 and <= 67 => "Rain", >= 71 and <= 77 => "Snow", >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers", >= 95 => "Thunderstorm", _ => ""
    };
}

// ---------------- next meeting (classic Outlook, already running) ----------------

internal sealed record Meeting(string Title, DateTime Start, DateTime End, string Location, string? JoinUrl);

internal static class OutlookProbe
{
    [DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    static readonly Regex Teams = new(@"https://teams\.microsoft\.com/l/meetup-join/[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Must run on an STA thread. Only attaches to a running Outlook; never starts one. Meeting titles are never logged.</summary>
    public static (List<Meeting>? list, string status) Read(int hours)
    {
        try
        {
            if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0) return (null, "Outlook (classic) is not installed");
            if (GetActiveObject(ref clsid, IntPtr.Zero, out var o) != 0 || o == null) return (null, "Outlook is not running");
            dynamic app = o;
            dynamic ns = app.GetNamespace("MAPI");
            dynamic cal = ns.GetDefaultFolder(9);   // olFolderCalendar
            dynamic items = cal.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            var now = DateTime.Now;
            string F(DateTime d) => d.ToString("g", CultureInfo.CurrentCulture);
            dynamic res;
            try { res = items.Restrict($"[Start] <= '{F(now.AddHours(hours))}' AND [End] >= '{F(now)}'"); }
            catch { res = items.Restrict($"[Start] <= '{now.AddHours(hours):MM/dd/yyyy hh:mm tt}' AND [End] >= '{now:MM/dd/yyyy hh:mm tt}'"); }
            var list = new List<Meeting>();
            int n = 0;
            foreach (dynamic it in res)
            {
                if (++n > 40) break;
                try
                {
                    if ((bool)it.AllDayEvent) continue;
                    if ((int)it.BusyStatus == 0) continue;   // free
                    DateTime s = it.Start, e = it.End;
                    if (e < now) continue;
                    string subject = (string)(it.Subject ?? "(no title)");
                    string loc = (string)(it.Location ?? "");
                    string? url = null;
                    var m = Teams.Match(loc);
                    if (!m.Success && list.Count < 4) { string body = (string)(it.Body ?? ""); m = Teams.Match(body); }
                    if (m.Success) url = m.Value;
                    list.Add(new Meeting(subject, s, e, loc, url));
                }
                catch { }
            }
            return (list.OrderBy(m => m.Start).ToList(), "ok");
        }
        catch (Exception e) { return (null, "Outlook not readable: " + e.GetType().Name); }
    }
}
