using System;
using System.IO;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Spread;

internal sealed record ToastItem(uint Id, string App, string AppId, string Title, string Body, DateTime Time);

/// <summary>
/// The toast notifications in the Windows notification centre, through the supported UserNotificationListener API.
/// Texts are shown on screen only: never logged or saved. Dismissing removes the notification from the notification centre.
/// </summary>
internal static class NotificationProbe
{
    public static System.Collections.Generic.List<ToastItem> List()
    {
        var res = new System.Collections.Generic.List<ToastItem>();
        var all = UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        foreach (var n in all)
        {
            try
            {
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                string title = texts != null && texts.Count > 0 ? texts[0].Text : "";
                string body = texts != null && texts.Count > 1 ? string.Join("  ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Skip(texts, 1), t => t.Text)) : "";
                res.Add(new ToastItem(n.Id, n.AppInfo?.DisplayInfo?.DisplayName ?? "", n.AppInfo?.AppUserModelId ?? "", title, body, n.CreationTime.LocalDateTime));
            }
            catch { }
        }
        return System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderByDescending(res, t => t.Time));
    }

    public static void Remove(uint id) { try { UserNotificationListener.Current.RemoveNotification(id); } catch (Exception e) { Log.Write("dismiss notification failed: " + e.GetType().Name); } }

    public static bool IsTeams(ToastItem t) =>
        t.AppId.Contains("MSTeams", StringComparison.OrdinalIgnoreCase) || t.AppId.Contains("Teams", StringComparison.OrdinalIgnoreCase) || t.App.Contains("Teams", StringComparison.OrdinalIgnoreCase);

    public static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");

    /// <summary>Supported API: allowed / denied / unspecified, or why it cannot even be asked.</summary>
    public static string ListenerStatus()
    {
        try { return UserNotificationListener.Current.GetAccessStatus().ToString(); }
        catch (Exception e) { return "not usable: " + e.GetType().Name + " " + e.Message; }
    }

    /// <summary>Counts only (no text): can the listener actually return the current toasts?</summary>
    public static string ListenerCount()
    {
        try
        {
            var list = UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
            var apps = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var n in list) { string a = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; apps[a] = apps.TryGetValue(a, out var c) ? c + 1 : 1; }
            return $"{list.Count} toasts from {apps.Count} apps: " + string.Join(", ", System.Linq.Enumerable.Select(apps, kv => $"{kv.Key} x{kv.Value}"));
        }
        catch (Exception e) { return "failed: " + e.GetType().Name + " " + e.Message; }
    }

    public static string DatabaseStatus()
    {
        try
        {
            if (!File.Exists(DbPath)) return "database not found";
            using var fs = new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return $"readable ({fs.Length / 1024} KB)";
        }
        catch (Exception e) { return "not readable: " + e.GetType().Name; }
    }
}
