using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace Spread;

internal sealed record MeetingItem(string Id, string Title, DateTime Start, DateTime End, string Location, string? JoinUrl, bool AllDay);
internal sealed record MailItem(string Id, string From, string Subject, DateTime Received, string Preview);
internal sealed record CalendarResult(List<MeetingItem> Items, string Status);
internal sealed record InboxResult(int Unread, List<MailItem> Items, string Status);

/// <summary>
/// Everything the clock panel needs from classic Outlook, on ONE dedicated STA thread. Only attaches to a running Outlook, never starts one.
/// Mail and meeting text goes to the screen only: it is never logged or saved. Results are cached for 60 s.
/// </summary>
internal static class OutlookData
{
    [DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    static readonly Regex Teams = new(@"https://teams\.(microsoft|live)\.com/(l/meetup-join|meet)/[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly BlockingCollection<Action> Queue = new();
    static readonly object CacheLock = new();
    static readonly Dictionary<string, (long at, object value)> Cache = new();
    static Thread? _thread;

    static void Ensure()
    {
        if (_thread != null) return;
        _thread = new Thread(() =>
        {
            foreach (var a in Queue.GetConsumingEnumerable())
            {
                try { a(); } catch (Exception e) { Log.Write("outlook worker: " + e.GetType().Name); }
            }
        }) { IsBackground = true, Name = "Spread Outlook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the Outlook thread; <paramref name="done"/> is called on that thread (marshal to the UI yourself).</summary>
    static void Run<T>(Func<T> work, Action<T> done, T onError)
    {
        Ensure();
        Queue.Add(() =>
        {
            T r;
            try { r = work(); } catch (Exception e) { Log.Write("outlook: " + e.GetType().Name); r = onError; }
            done(r);
        });
    }

    static bool TryCache<T>(string key, out T value)
    {
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var c) && Environment.TickCount64 - c.at < 60_000 && c.value is T t) { value = t; return true; }
        value = default!;
        return false;
    }

    static void Put(string key, object v) { lock (CacheLock) Cache[key] = (Environment.TickCount64, v); }

    static object? Attach(out string status)
    {
        status = "ok";
        if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0) { status = "Outlook (classic) is not installed"; return null; }
        if (GetActiveObject(ref clsid, IntPtr.Zero, out var o) != 0 || o == null) { status = "Outlook is not running"; return null; }
        return o;
    }

    static string F(DateTime d) => d.ToString("g", CultureInfo.CurrentCulture);

    // ---------------- calendar ----------------

    public static void Calendar(DateTime from, DateTime to, bool force, Action<CalendarResult> done)
    {
        string key = $"cal-{from:yyyyMMdd}-{to:yyyyMMdd}";
        if (!force && TryCache<CalendarResult>(key, out var hit)) { done(hit); return; }
        Run(() => { var r = ReadCalendar(from, to); if (r.Status == "ok") Put(key, r); return r; }, done, new CalendarResult(new(), "Outlook could not be read"));
    }

    static CalendarResult ReadCalendar(DateTime from, DateTime to)
    {
        var o = Attach(out string status);
        if (o == null) return new CalendarResult(new(), status);
        dynamic app = o;
        dynamic ns = app.GetNamespace("MAPI");
        dynamic cal = ns.GetDefaultFolder(9);
        dynamic items = cal.Items;
        items.IncludeRecurrences = true;
        items.Sort("[Start]");
        dynamic res;
        try { res = items.Restrict($"[Start] <= '{F(to)}' AND [End] >= '{F(from)}'"); }
        catch { res = items.Restrict($"[Start] <= '{to:MM/dd/yyyy hh:mm tt}' AND [End] >= '{from:MM/dd/yyyy hh:mm tt}'"); }
        var list = new List<MeetingItem>();
        int n = 0, bodies = 0;
        var bodyUntil = DateTime.Now.AddDays(2);
        foreach (dynamic it in res)
        {
            if (++n > 300) break;
            try
            {
                if ((int)it.BusyStatus == 0 && !(bool)it.AllDayEvent) continue;   // "free" timed entries are not meetings
                DateTime s = it.Start, e = it.End;
                bool allDay = (bool)it.AllDayEvent;
                string loc = (string)(it.Location ?? "");
                string? url = null;
                var m = Teams.Match(loc);
                if (!m.Success && !allDay && s < bodyUntil && e > DateTime.Now.AddHours(-12) && bodies < 25) { bodies++; m = Teams.Match((string)(it.Body ?? "")); }
                if (m.Success) url = m.Value;
                list.Add(new MeetingItem((string)it.EntryID, (string)(it.Subject ?? "(no title)"), s, e, loc, url, allDay));
            }
            catch { }
        }
        return new CalendarResult(list.OrderBy(x => x.Start).ToList(), "ok");
    }

    // ---------------- mail ----------------

    public static void Inbox(int top, bool preview, bool force, Action<InboxResult> done)
    {
        string key = $"inbox-{top}-{preview}";
        if (!force && TryCache<InboxResult>(key, out var hit)) { done(hit); return; }
        Run(() => { var r = ReadInbox(top, preview); if (r.Status == "ok") Put(key, r); return r; }, done, new InboxResult(0, new(), "Outlook could not be read"));
    }

    static InboxResult ReadInbox(int top, bool preview)
    {
        var o = Attach(out string status);
        if (o == null) return new InboxResult(0, new(), status);
        dynamic app = o;
        dynamic ns = app.GetNamespace("MAPI");
        dynamic folder = ns.GetDefaultFolder(6);   // olFolderInbox
        int unread = (int)folder.UnReadItemCount;
        var list = new List<MailItem>();
        if (top > 0)
        {
            dynamic items = folder.Items;
            items.Sort("[ReceivedTime]", true);
            dynamic res = items.Restrict("[Unread] = true");
            int n = 0;
            foreach (dynamic it in res)
            {
                if (++n > top) break;
                try
                {
                    string body = "";
                    if (preview) { body = (string)(it.Body ?? ""); body = Regex.Replace(body, @"\s+", " ").Trim(); if (body.Length > 140) body = body[..140] + "…"; }
                    list.Add(new MailItem((string)it.EntryID, (string)(it.SenderName ?? ""), (string)(it.Subject ?? "(no subject)"), (DateTime)it.ReceivedTime, body));
                }
                catch { }
            }
        }
        return new InboxResult(unread, list, "ok");
    }

    /// <summary>Only the unread count (cheap), for the badge next to the clock.</summary>
    public static void UnreadCount(Action<int> done) =>
        Run(() => { var o = Attach(out _); if (o == null) return -1; dynamic app = o; return (int)app.GetNamespace("MAPI").GetDefaultFolder(6).UnReadItemCount; }, done, -1);

    // ---------------- actions ----------------

    public static void Open(string entryId) =>
        Run(() =>
        {
            var o = Attach(out _);
            if (o == null) return 0;
            dynamic app = o;
            dynamic item = app.Session.GetItemFromID(entryId);
            item.Display();
            string caption = "";
            try { caption = (string)item.GetInspector.Caption; } catch { }
            try { item.GetInspector.Activate(); } catch { }
            FrontByCaption(caption);
            return 0;
        }, _ => { }, 0);

    /// <summary>Outlook opens the message window behind the others (it is not the foreground process): find it by its caption and bring it forward.</summary>
    static void FrontByCaption(string caption)
    {
        if (caption.Length < 3) return;
        for (int i = 0; i < 20; i++)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                int n = Native.GetWindowTextLength(h); if (n == 0) return true;
                var sb = new System.Text.StringBuilder(n + 1); Native.GetWindowText(h, sb, n + 1);
                if (sb.ToString().Contains(caption, StringComparison.Ordinal)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                if (Native.IsIconic(found)) Native.ShowWindow(found, Native.SW_RESTORE);
                Native.ForceForeground(found);
                Log.Debug("outlook: message window brought to the front");
                return;
            }
            System.Threading.Thread.Sleep(100);
        }
    }

    public static void NewMail() =>
        Run(() => { var o = Attach(out _); if (o != null) { dynamic app = o; app.CreateItem(0).Display(); } return 0; }, _ => { }, 0);

    public static void NewMeeting() =>
        Run(() => { var o = Attach(out _); if (o != null) { dynamic app = o; app.CreateItem(1).Display(); } return 0; }, _ => { }, 0);
}
