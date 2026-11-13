using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Spread;

/// <summary>
/// Keeps the clock panel's data warm in the background (calendar every ~3 min, mail every minute, notifications every 30 s) so the panel
/// opens with content already there, and feeds the unread badge. RAM only. Nothing is read while Outlook is not running (the calls just fail fast).
/// </summary>
internal static class PanelData
{
    public static CalendarResult? Calendar { get; private set; }
    public static InboxResult? Inbox { get; private set; }
    public static List<ToastItem>? Toasts { get; private set; }
    /// <summary>Unread mail count, or -1 when unknown (Outlook not running).</summary>
    public static int Unread { get; private set; } = -1;
    public static event Action? Changed;

    static long _cal, _mail, _toast;
    static bool _toastBusy;

    public static (DateTime from, DateTime to) DefaultRange()
    {
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        return (first.AddDays(-8), first.AddMonths(1).AddDays(8));
    }

    static void Raise() => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));

    /// <summary>Called every second; does the work only when something is due.</summary>
    public static void Prefetch(Config cfg)
    {
        long now = Environment.TickCount64;
        if (now - _toast > 30_000 && !_toastBusy)
        {
            _toast = now; _toastBusy = true;
            Task.Run(() =>
            {
                try { Toasts = NotificationProbe.List(); } catch { }
                _toastBusy = false;
                Raise();
            });
        }
        if (now - _mail > 60_000) { _mail = now; RefreshMail(cfg); }
        if (now - _cal > 170_000) { _cal = now; RefreshCalendar(); }
    }

    public static void SetToasts(List<ToastItem> list) => Toasts = list;
    public static void RemoveToast(uint id) => Toasts?.RemoveAll(t => t.Id == id);

    public static void RefreshMail(Config cfg) =>
        OutlookData.Inbox(12, cfg.MailPreview, true, r => { Inbox = r; Unread = r.Status == "ok" ? r.Unread : -1; Raise(); });

    public static void RefreshCalendar()
    {
        var (f, t) = DefaultRange();
        OutlookData.Calendar(f, t, true, r => { Calendar = r; Raise(); });
    }

    /// <summary>After opening a mail from the panel: Outlook marks it read a moment later, so look again (twice, to be safe).</summary>
    public static void RefreshMailSoon(Config cfg)
    {
        foreach (int ms in new[] { 1500, 5000 })
            Task.Delay(ms).ContinueWith(_ => RefreshMail(cfg));
    }

    /// <summary>Toasts minus the apps the user chose to ignore.</summary>
    public static List<ToastItem>? VisibleToasts(Config cfg) =>
        Toasts?.Where(t => !cfg.IgnoredNotificationApps.Contains(t.App, StringComparer.OrdinalIgnoreCase)).ToList();
}
