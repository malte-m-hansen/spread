using System;
using System.Linq;

namespace Spread;

/// <summary>
/// `--quicksettings-test`: READ-ONLY probes of what this PC offers (counts and states, no device names) and pure checks.
/// It never changes a setting.
/// </summary>
internal static class QuickSettingsTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }
    static void Info(string name, Func<string> f) { string r; try { r = f(); } catch (Exception e) { r = "failed: " + e.GetType().Name; } Console.WriteLine($"  info {name,-14} {r}"); }

    public static int Run(string[] args)
    {
        Console.WriteLine("Quick settings: pure checks");
        Check("wifi bars", WifiCtl.Bars(100) == 4 && WifiCtl.Bars(60) == 3 && WifiCtl.Bars(30) == 2 && WifiCtl.Bars(5) == 1 && WifiCtl.Bars(0) == 0);
        Check("power mode names", PowerMode.NameOf(Guid.Empty) == "Balanced" && PowerMode.NameOf(new Guid("ded574b5-45a0-4f42-8737-46345c09c238")) == "Best performance" && PowerMode.NameOf(Guid.NewGuid()) == "Custom");
        Check("power modes are four distinct ids", PowerMode.Modes.Select(m => m.id).Distinct().Count() == 4);
        Check("battery text: charging", BatteryInfo.Describe((50, true, true, -1)) == "Charging");
        Check("battery text: full on AC", BatteryInfo.Describe((100, true, false, -1)) == "Fully charged");
        Check("battery text: time left", BatteryInfo.Describe((60, false, false, 192)) == "3 h 12 min left");
        Check("battery text: unknown", BatteryInfo.Describe((60, false, false, -1)) == "On battery");
        string log = "junk\n<gateway-list>\n<entry>\n<gateway>gw-a.vpn.example.com</gateway>\n<tunnel>no</tunnel>\n<manual>yes</manual>\n<description>HQ-1 - All Traffic</description>\n<priority>0</priority>\n</entry>\n<entry>\n<gateway>gw-b.vpn.example.com</gateway>\n<description>1. HQ-2</description>\n</entry>\n<entry>\n<gateway>gw-c.vpn.example.com</gateway>\n<description>Singapore</description>\n</entry>\n<entry>\n<gateway>gw-d.vpn.example.com</gateway>\n<priority>1</priority>\n</entry>\n</gateway-list>";
        var gmap = GlobalProtectEndpoint.ParseGateways(log);
        Check("vpn: portal gateway names parsed", gmap.Count == 3 && gmap["gw-a.vpn.example.com"] == "HQ-1" && gmap["gw-b.vpn.example.com"] == "HQ-2" && gmap["gw-c.vpn.example.com"] == "Singapore", string.Join(",", gmap.Select(kv => kv.Key + "=" + kv.Value)));
        Check("vpn: an entry without description is not mixed up with the next one", !gmap.ContainsKey("gw-d.vpn.example.com"));
        Check("vpn: label from a host name", GlobalProtectEndpoint.LabelOf("", "gw-a.vpn.example.com") == "gw-a" && GlobalProtectEndpoint.LabelOf("Frankfurt 2 - All Traffic", "x") == "FRANKFURT-2".Substring(0, 0) + GlobalProtectEndpoint.LabelOf("Frankfurt 2 - All Traffic", "x"));
        Check("vpn: endpoint by host name", GlobalProtectEndpoint.Resolve("gw-a.vpn.example.com", "", gmap) == "HQ-1");
        Check("vpn: endpoint by IP through DNS", GlobalProtectEndpoint.Resolve("10.1.2.3", "", gmap, h => h.StartsWith("gw-a") ? new[] { "10.1.2.3" } : Array.Empty<string>()) == "HQ-1");
        Check("vpn: unknown address is shown as it is, location is the fallback", GlobalProtectEndpoint.Resolve("192.0.2.9", "", gmap) == "192.0.2.9" && GlobalProtectEndpoint.Resolve("", "Copenhagen", gmap) == "Copenhagen" && GlobalProtectEndpoint.Resolve("", "", gmap) == null);
        Check("vpn: internal network has no endpoint", GlobalProtectEndpoint.Current("internal") == "internal network (no tunnel)");
        var q = new QuickSettingsConfig();
        Check("config: default tiles are all known and unique", q.Tiles.Distinct().Count() == q.Tiles.Length && q.Tiles.All(t => new[] { "wifi", "bluetooth", "power", "night", "dark", "dnd", "airplane", "awake", "vpn" }.Contains(t)));
        Check("config: power buttons confirm by default", q.ConfirmPowerActions && q.ShowPowerButtons && q.Enabled);

        Console.WriteLine("Quick settings: what this PC offers (read-only)");
        Info("output devs", () => AudioCtl.List(false).Count + " (default set: " + AudioCtl.List(false).Any(d => d.IsDefault) + ")");
        Info("input devs", () => AudioCtl.List(true).Count + " (default set: " + AudioCtl.List(true).Any(d => d.IsDefault) + ")");
        Info("volume", () => AudioCtl.TryGet(false, out var v, out var m) ? $"{v * 100:0}% muted={m}" : "unavailable");
        Info("microphone", () => AudioCtl.TryGet(true, out var v, out var m) ? $"{v * 100:0}% muted={m}" : "unavailable");
        using (var done = new System.Threading.ManualResetEventSlim())
        {
            int? br = null;
            BrightnessCtl.ReadAsync(v => { br = v; done.Set(); });
            Info("brightness", () => done.Wait(8000) ? (br == null ? "not controllable: " + BrightnessCtl.LastError : $"{br}%") : "timed out");
            if (br != null)
            {
                using var set = new System.Threading.ManualResetEventSlim(); string? err = "timed out";
                BrightnessCtl.Set(br.Value, e => { err = e; set.Set(); });   // writes the value it already has: no visible change
                set.Wait(8000);
                Check("brightness: writing the current value works", err == null, err ?? "");
                int bad = 0;
                for (int i = 0; i < 15; i++)
                {
                    using var w = new System.Threading.ManualResetEventSlim(); int? got = null; string? e2 = null;
                    BrightnessCtl.Set(br.Value, e => { e2 = e; BrightnessCtl.ReadAsync(v => { got = v; w.Set(); }); });
                    if (!w.Wait(8000) || e2 != null || got != br) bad++;
                }
                Check("brightness: 15 write+read rounds in a row all succeed", bad == 0, bad + " failed: " + BrightnessCtl.LastError);
            }
        }
        Info("radios", () => string.Join(", ", RadioCtl.ListAsync().GetAwaiter().GetResult().Select(r => $"{r.Kind}={r.State}")));
        Info("bluetooth", () => RadioCtl.BluetoothAsync().GetAwaiter().GetResult().Count + " paired device(s)");
        Info("wifi", () => { var n = WifiCtl.Networks(false); return $"{n.Count} visible, connected: {n.Any(x => x.Connected)}, saved among them: {n.Count(x => x.Saved)}"; });
        Info("power mode", () => PowerMode.Get() is { } g ? PowerMode.NameOf(g) : "unavailable");
        Info("dark style", () => ThemeMode.IsDark().ToString());
        Info("do not disturb", () => (!DndProbe.ToastsEnabled()).ToString());
        Info("battery", () => BatteryInfo.Read() is { } b ? $"{b.pct}% - {BatteryInfo.Describe(b)}" : "none");
        Info("keep awake", () => KeepAwake.IsOn.ToString());
        Info("vpn", () => { var n = NetProbe.Read(Config.Load().VpnAdapterMatch); return $"up={n.VpnUp} {n.VpnName}"; });

        Console.WriteLine(_fail == 0 ? "QUICKSETTINGS TEST PASSED" : $"QUICKSETTINGS TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }
}
