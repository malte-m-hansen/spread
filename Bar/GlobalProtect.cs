using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Spread;

/// <summary>
/// Which GlobalProtect gateway ("endpoint", e.g. HQ-1) the VPN is on. GlobalProtect does not put that into one clean place, so this combines:
/// the gateway address the agent keeps in the registry while tunnelled (DEM\gateway-address / gateway-location) and the names the portal gave its gateways
/// (the "description" of each gateway in the agent's own log, e.g. gw-a = "HQ-1 - All Traffic"). Without a tunnel (internal network) there is no endpoint.
/// </summary>
internal static class GlobalProtectEndpoint
{
    static readonly Regex Entry = new(@"<gateway>(?<h>[^<]+)</gateway>(?:(?!</entry>).)*?<description>(?<d>[^<]*)</description>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Code = new(@"\b(?<c>[A-Za-z]{2,5})[- ]?(?<n>\d{1,2})\b", RegexOptions.Compiled);

    static Dictionary<string, string>? _map;
    static long _mapAt;
    static readonly Dictionary<string, string[]> Dns_ = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"HQ-1 - All Traffic" / "1. HQ-1" -> "HQ-1"; anything else -> the description (or the short host name).</summary>
    public static string LabelOf(string description, string host)
    {
        var m = Code.Match(description ?? "");
        if (m.Success) return m.Groups["c"].Value.ToUpperInvariant() + "-" + m.Groups["n"].Value;
        string d = (description ?? "").Trim();
        if (d.Length > 0) return d.Length > 28 ? d[..27] + "…" : d;
        return host.Split('.')[0];
    }

    /// <summary>gateway host name -> label, from the portal's gateway list as written in the agent's log text.</summary>
    public static Dictionary<string, string> ParseGateways(string logText)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Entry.Matches(logText))
        {
            string host = m.Groups["h"].Value.Trim();
            if (host.Length > 0) res[host] = LabelOf(m.Groups["d"].Value, host);   // later entries (newer) win
        }
        return res;
    }

    /// <summary>Gateway address (host name or IP) or location text -> label. <paramref name="resolver"/> turns a host name into addresses (DNS).</summary>
    public static string? Resolve(string? address, string? location, IReadOnlyDictionary<string, string> map, Func<string, IEnumerable<string>>? resolver = null)
    {
        string a = (address ?? "").Trim();
        int colon = a.LastIndexOf(':');
        if (colon > 0 && a.IndexOf(':') == colon) a = a[..colon];   // host:port (not IPv6)
        if (a.Length > 0)
        {
            if (map.TryGetValue(a, out var label)) return label;
            if (IPAddress.TryParse(a, out _) && resolver != null)
                foreach (var kv in map)
                    if (resolver(kv.Key).Contains(a)) return kv.Value;
            return a;
        }
        string l = (location ?? "").Trim();
        return l.Length > 0 ? l : null;
    }

    static Dictionary<string, string> Map()
    {
        if (_map != null && Environment.TickCount64 - _mapAt < 300_000) return _map;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Palo Alto Networks\GlobalProtect\PanGPA.log");
            if (File.Exists(f))
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long take = Math.Min(2_500_000, fs.Length);
                fs.Seek(-take, SeekOrigin.End);
                using var sr = new StreamReader(fs);
                map = ParseGateways(sr.ReadToEnd());
            }
        }
        catch { }
        _mapAt = Environment.TickCount64;
        return _map = map;
    }

    static IEnumerable<string> Resolver(string host)
    {
        if (!Dns_.TryGetValue(host, out var ips))
        {
            try { ips = System.Net.Dns.GetHostAddresses(host).Select(x => x.ToString()).ToArray(); } catch { ips = Array.Empty<string>(); }
            Dns_[host] = ips;
        }
        return ips;
    }

    /// <summary>Short text for the current state: "HQ-1", "internal network (no tunnel)" or the raw status when nothing better is known.</summary>
    public static string Current(string status)
    {
        if (string.Equals(status, "internal", StringComparison.OrdinalIgnoreCase)) return "internal network (no tunnel)";
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Palo Alto Networks\GlobalProtect\DEM");
            string? addr = k?.GetValue("gateway-address") as string, loc = k?.GetValue("gateway-location") as string;
            var r = Resolve(addr, loc, Map(), Resolver);
            if (r != null) return r;
        }
        catch { }
        return status.Length > 0 ? status : "connected";
    }
}
