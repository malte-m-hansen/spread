using System;
using System.Linq;
using System.Threading;

namespace Spread;

/// <summary>`--widget-test`: runs every bar data source once, headless, and prints what it found (no titles of meetings or media).</summary>
internal static class WidgetTest
{
    static void Line(string name, Func<string> f)
    {
        string r;
        try { r = f(); } catch (Exception e) { r = "FAILED: " + e.GetType().Name + " " + e.Message; }
        Console.WriteLine($"{name,-10} {r}");
    }

    public static int Run()
    {
        var cfg = Config.Load();
        Console.WriteLine("Top-bar data sources:");
        Line("network", () => { var n = NetProbe.Read(cfg.VpnAdapterMatch); return $"kind={n.Kind} name={n.Name} vpn={(n.VpnUp ? "UP (" + n.VpnName + ")" : "off")}"; });
        if (Environment.GetCommandLineArgs().Contains("--verbose")) Console.WriteLine("  adapters:" + NetProbe.DescribeAll(cfg.VpnAdapterMatch));
        Line("volume", () => VolumeProbe.TryGet(out var l, out var m) ? $"{l * 100:0}% muted={m}" : "unavailable (no default output device?)");
        Line("load", () => { LoadProbe.Read(); Thread.Sleep(500); var (c, u, t) = LoadProbe.Read(); return $"cpu={c:0}% ram={u:0.0}/{t:0.0} GB"; });
        Line("keyboard", () => $"layout={KeyboardProbe.Current()} installed={KeyboardProbe.InstalledCount()}");
        Line("bell", () => "toasts enabled=" + DndProbe.ToastsEnabled());
        Line("wsl", () => { var r = WslProbe.Read(); return r.running ? $"running, memory={r.bytes / 1073741824.0:0.00} GB" + (r.bytes == 0 ? " (Windows did not tell)" : "") : "not running"; });
        Line("weather", () =>
        {
            var d = WeatherProbe.FetchAsync(cfg.WeatherLocation).GetAwaiter().GetResult();
            return d == null ? "no data" : $"{cfg.WeatherLocation.Name}: {d.Temp:0.0}°C {WeatherProbe.Describe(d.Code)}, wind {d.Wind:0} km/h, {d.Hours.Count} hours, {d.Days.Count} days";
        });
        Line("meeting", () =>
        {
            var (list, status) = OutlookProbe.Read(8);
            if (list == null) return "unavailable: " + status;
            var next = list.FirstOrDefault(m => m.End > DateTime.Now);
            return $"{list.Count} meeting(s) in the next 8 h" + (next != null ? $", next starts in {(next.Start - DateTime.Now).TotalMinutes:0} min, join link={(next.JoinUrl != null ? "yes" : "no")}" : "");
        });
        Line("calendar", () =>
        {
            using var done = new ManualResetEventSlim();
            CalendarResult? r = null;
            OutlookData.Calendar(DateTime.Today, DateTime.Today.AddDays(7), true, x => { r = x; done.Set(); });
            if (!done.Wait(15000)) return "timed out";
            return r!.Status != "ok" ? "unavailable: " + r.Status : $"{r.Items.Count} entries in the next 7 days, {r.Items.Count(m => m.JoinUrl != null)} with a Teams link";
        });
        Line("inbox", () =>
        {
            using var done = new ManualResetEventSlim();
            InboxResult? r = null;
            OutlookData.Inbox(5, false, true, x => { r = x; done.Set(); });
            if (!done.Wait(15000)) return "timed out";
            return r!.Status != "ok" ? "unavailable: " + r.Status : $"{r.Unread} unread, {r.Items.Count} listed";
        });
        Line("holidays", () => $"Easter 2026 = {Holidays.Easter(2026):yyyy-MM-dd} (expect 2026-04-05), DK holidays in 2026: {Holidays.For("DK", 2026).Count}, 2026-11-05 is ISO week {System.Globalization.ISOWeek.GetWeekOfYear(new DateTime(2026, 11, 5))} (expect 45)");
        Line("toasts", () => "listener API: " + NotificationProbe.ListenerStatus() + " | database: " + NotificationProbe.DatabaseStatus());
        Line("toast list", () => NotificationProbe.ListenerCount());
        Line("media", () => { var m = MediaProbe.Read(); return m == null ? "nothing playing" : $"{(m.Playing ? "playing" : "paused")} (title length {m.Title.Length})"; });
        return 0;
    }

    /// <summary>Pure checks of the bar's data logic: no machine probes, no network, synthetic text only. Returns the number of failures.
    /// Run by `--toast-test` (the offline runner of the bar) until it has a switch of its own.</summary>
    public static int Offline()
    {
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}"); if (!ok) fail++; }
        Console.WriteLine("Bar data checks (offline):");
        var teams = OutlookData.TeamsLink;
        Check("teams link: classic meetup-join, ends at whitespace", teams.Match("Join: https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d now").Value == "https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d");
        Check("teams link: short /meet/ link inside <...>", teams.Match("<https://teams.microsoft.com/meet/123456789?p=abc>").Value == "https://teams.microsoft.com/meet/123456789?p=abc");
        Check("teams link: personal Teams", teams.IsMatch("https://teams.live.com/meet/9876543210"));
        Check("teams link: channel links and plain http are not join links", !teams.IsMatch("https://teams.microsoft.com/l/channel/19%3aabc/General") && !teams.IsMatch("http://teams.microsoft.com/l/meetup-join/x"));
        return fail;
    }
}
