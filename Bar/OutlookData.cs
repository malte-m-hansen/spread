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

    /// <summary>A Teams join link (classic /l/meetup-join/ and the newer short /meet/ links, work and personal Teams). Shared with <see cref="OutlookProbe"/>.</summary>
    internal static readonly Regex TeamsLink = new(@"https://teams\.(microsoft|live)\.com/(l/meetup-join|meet)/[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly BlockingCollection<Action> Queue = new();
    static readonly object CacheLock = new();
    static readonly Dictionary<string, (long at, object value)> Cache = new();
    /// <summary>Keys of background reads that wait in the queue (not yet started). Guarded by <see cref="CacheLock"/>.</summary>
    static readonly HashSet<string> Waiting = new();
    static readonly object StartLock = new();
    static Thread? _thread;

    /// <summary>Started on first use; callers come from the UI thread and from the thread pool, so the start is locked (one worker only).</summary>
    static void Ensure()
    {
        lock (StartLock)
        {
            if (_thread != null) return;
            _thread = new Thread(() =>
            {
                foreach (var a in Queue.GetConsumingEnumerable())
                {
                    long t0 = Environment.TickCount64;
                    try { a(); } catch (Exception e) { Log.Write("outlook worker: " + e.GetType().Name); }
                    long ms = Environment.TickCount64 - t0;
                    if (ms > 15_000) Log.Write($"outlook: a call took {ms / 1000} s (Outlook busy or showing a dialog?)");
                }
            }) { IsBackground = true, Name = "Spread Outlook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the Outlook thread; <paramref name="done"/> is called on that thread (marshal to the UI yourself).
    /// A COM call into a hung Outlook has no timeout, so everything queued behind it waits: a background read whose <paramref name="coalesce"/> key
    /// is already waiting is dropped (the queue does not grow by one refresh per minute while Outlook hangs), and an interactive action
    /// (<paramref name="maxWaitMs"/> &gt; 0) that waited longer than that is skipped instead of opening a window minutes after the click.
    /// </summary>
    static void Run<T>(Func<T> work, Action<T> done, T onError, string? coalesce = null, int maxWaitMs = 0)
    {
        if (coalesce != null) lock (CacheLock) { if (!Waiting.Add(coalesce)) return; }
        long queued = Environment.TickCount64;
        Ensure();
        Queue.Add(() =>
        {
            if (coalesce != null) lock (CacheLock) Waiting.Remove(coalesce);   // once started, a new request is queued again (this read may miss a change made from now on)
            long waited = Environment.TickCount64 - queued;
            if (maxWaitMs > 0 && waited > maxWaitMs) { Log.Write($"outlook: dropped a request that waited {waited / 1000} s"); return; }
            T r;
            try { r = work(); } catch (Exception e) { Log.Write("outlook: " + e.GetType().Name); r = onError; }
            done(r);
        });
    }

    const int ActionMaxWaitMs = 20_000;

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
    /// <summary>The US form for the fallback query. Formatted with the invariant culture: with the current one (e.g. da-DK) '/' became '-' and AM/PM was empty.</summary>
    internal static string Us(DateTime d) => d.ToString("MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture);

    // ---------------- calendar ----------------

    /// <param name="background">A periodic refresh: dropped while the same refresh still waits in the queue.</param>
    public static void Calendar(DateTime from, DateTime to, bool force, Action<CalendarResult> done, bool background = false)
    {
        string key = $"cal-{from:yyyyMMdd}-{to:yyyyMMdd}";
        if (!force && TryCache<CalendarResult>(key, out var hit)) { done(hit); return; }
        Run(() => { var r = ReadCalendar(from, to); if (r.Status == "ok") Put(key, r); return r; }, done, new CalendarResult(new(), "Outlook could not be read"), background ? "bg-" + key : null);
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
        catch { res = items.Restrict($"[Start] <= '{Us(to)}' AND [End] >= '{Us(from)}'"); }
        var list = new List<MeetingItem>();
        int n = 0, bodies = 0;
        var bodyUntil = DateTime.Now.AddDays(2);
        foreach (dynamic it in res)
        {
            try
            {
                if (++n > 300) break;
                if ((int)it.BusyStatus == 0 && !(bool)it.AllDayEvent) continue;   // "free" timed entries are not meetings
                DateTime s = it.Start, e = it.End;
                bool allDay = (bool)it.AllDayEvent;
                string loc = (string)(it.Location ?? "");
                string? url = null;
                var m = TeamsLink.Match(loc);
                if (!m.Success && !allDay && s < bodyUntil && e > DateTime.Now.AddHours(-12) && bodies < 25) { bodies++; m = TeamsLink.Match((string)(it.Body ?? "")); }
                if (m.Success) url = m.Value;
                list.Add(new MeetingItem((string)it.EntryID, (string)(it.Subject ?? "(no title)"), s, e, loc, url, allDay));
            }
            catch { }
            finally { Release((object)it); }
        }
        return new CalendarResult(list.OrderBy(x => x.Start).ToList(), "ok");
    }

    // ---------------- mail ----------------

    /// <param name="background">A periodic refresh: dropped while the same refresh still waits in the queue.</param>
    public static void Inbox(int top, bool preview, bool force, Action<InboxResult> done, bool background = false)
    {
        string key = $"inbox-{top}-{preview}";
        if (!force && TryCache<InboxResult>(key, out var hit)) { done(hit); return; }
        Run(() => { var r = ReadInbox(top, preview); if (r.Status == "ok") Put(key, r); return r; }, done, new InboxResult(0, new(), "Outlook could not be read"), background ? "bg-" + key : null);
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
                try
                {
                    if (++n > top) break;
                    string body = "";
                    if (preview) { body = (string)(it.Body ?? ""); body = Regex.Replace(body, @"\s+", " ").Trim(); if (body.Length > 140) body = body[..140] + "…"; }
                    list.Add(new MailItem((string)it.EntryID, (string)(it.SenderName ?? ""), (string)(it.Subject ?? "(no subject)"), (DateTime)it.ReceivedTime, body));
                }
                catch { }
                finally { Release((object)it); }
            }
        }
        return new InboxResult(unread, list, "ok");
    }

    /// <summary>
    /// Each item of a loop is a COM object that otherwise stays open until a garbage collection. Outlook in Exchange online mode refuses to open more than
    /// a few hundred items at once ("the number of items you can open simultaneously is limited"), and the calendar loop reads up to 300 every 3 minutes.
    /// </summary>
    static void Release(object? o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
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
            Display(app.Session.GetItemFromID(entryId));
            return 0;
        }, _ => { }, 0, null, ActionMaxWaitMs);

    /// <summary>
    /// Opens a calendar entry. Every occurrence of a recurring meeting carries the series' EntryID, so opening by id alone shows the whole series:
    /// the occurrence that starts at <paramref name="start"/> is opened instead (a moved occurrence is looked up among the series' exceptions).
    /// Falls back to the item itself when the occurrence cannot be found.
    /// </summary>
    public static void OpenMeeting(string entryId, DateTime start) =>
        Run(() =>
        {
            var o = Attach(out _);
            if (o == null) return 0;
            dynamic app = o;
            dynamic item = app.Session.GetItemFromID(entryId);
            object? occ = null;
            try { if ((bool)item.IsRecurring) occ = Occurrence(item, start); }
            catch (Exception e) { Log.Debug("outlook: occurrence lookup failed: " + e.GetType().Name); }
            if (occ != null) Display(occ); else Display(item);
            return 0;
        }, _ => { }, 0, null, ActionMaxWaitMs);

    static object? Occurrence(dynamic master, DateTime start)
    {
        dynamic pattern = master.GetRecurrencePattern();
        try { return pattern.GetOccurrence(start); } catch { }   // wants the ORIGINAL start: fails for an occurrence that was moved
        foreach (dynamic ex in pattern.Exceptions)
        {
            try
            {
                if ((bool)ex.Deleted) continue;
                dynamic ai = ex.AppointmentItem;
                if ((DateTime)ai.Start == start) return ai;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Shows an item's window; bringing it to the front polls for up to 2 s, so that runs on the thread pool and not on the Outlook thread.</summary>
    static void Display(dynamic item)
    {
        item.Display();
        string caption = "";
        try { caption = (string)item.GetInspector.Caption; } catch { }
        try { item.GetInspector.Activate(); } catch { }
        if (caption.Length >= 3) System.Threading.Tasks.Task.Run(() => FrontByCaption(caption));
    }

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
        Run(() => { var o = Attach(out _); if (o != null) { dynamic app = o; app.CreateItem(0).Display(); } return 0; }, _ => { }, 0, null, ActionMaxWaitMs);

    public static void NewMeeting() =>
        Run(() => { var o = Attach(out _); if (o != null) { dynamic app = o; app.CreateItem(1).Display(); } return 0; }, _ => { }, 0, null, ActionMaxWaitMs);
}
