using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace Spread;

/// <summary>
/// Clicks the matching icon in the REAL Windows notification area through UI Automation (the buttons support Invoke), so the
/// program receives Windows' own tray click and opens its own menu/window. Needs the taskbar window to exist visibly for a moment:
/// when Spread keeps it hidden it is shown briefly (it is in auto-hide mode, so it stays off-screen) and hidden again.
/// Right-click is not available through Automation.
/// </summary>
internal static class RealTray
{
    static string[] Keys(string describedName, TrayEntry en)
    {
        var keys = new[]
        {
            describedName,
            Path.GetFileNameWithoutExtension(en.ExePath),
            (en.Tooltip ?? "").Split(' ', '-', ':', '.', ',').FirstOrDefault() ?? ""
        };
        return keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.Trim().Length >= 4).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static AutomationElement? FindIcon(AutomationElement root, string[] keys, out int count)
    {
        var all = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon"));
        count = all.Count;
        foreach (AutomationElement b in all)
        {
            string name = b.Current.Name ?? "";
            if (keys.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return b;
        }
        return null;
    }

    static void Invoke(AutomationElement e) => ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>Runs on a worker thread. Returns a short status for the log.</summary>
    public static string Click(TrayEntry en, string describedName)
    {
        bool wasHidden = !TaskbarControl.MainWindowVisible();
        try
        {
            if (wasHidden) { TaskbarControl.ShowTemporarily(5000); Thread.Sleep(350); }   // Automation needs a visible window to expose its content
            var tray = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return "no taskbar window";
            var keys = Keys(describedName, en);
            if (keys.Length == 0) return "no usable name to match";
            var root = AutomationElement.FromHandle(tray);

            var hit = FindIcon(root, keys, out int nVisible);
            if (hit != null) { Invoke(hit); return $"invoked the icon in the tray corner (matched on '{string.Join("/", keys)}')"; }

            // not in the corner: it is in the "hidden icons" flyout
            var chevron = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton")))
                ;
            AutomationElement? hidden = null;
            foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon")))
                if ((b.Current.Name ?? "").IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) >= 0) { hidden = b; break; }
            hidden ??= chevron;
            if (hidden == null) return $"icon not in the corner ({nVisible} corner icons) and no 'Show Hidden Icons' button found";

            Invoke(hidden);
            Thread.Sleep(450);
            var overflow = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"));
            if (overflow == null) return "opened 'Show Hidden Icons' but found no overflow flyout";
            hit = FindIcon(overflow, keys, out int nOverflow);
            if (hit != null) { Invoke(hit); return $"invoked the icon in the hidden-icons flyout (matched on '{string.Join("/", keys)}')"; }
            return $"not found: {nVisible} corner and {nOverflow} hidden icons checked for '{string.Join("/", keys)}'";
        }
        catch (Exception e) { return "failed: " + e.GetType().Name + " " + e.Message; }
    }

    /// <summary>Presses the tray button whose name contains one of <paramref name="keys"/> (corner first, then the hidden-icons flyout). The taskbar must already be on screen.</summary>
    public static string InvokeByKeys(params string[] keys)
    {
        try
        {
            var tray = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return "no taskbar window";
            var root = AutomationElement.FromHandle(tray);
            var hit = FindIcon(root, keys, out int nVisible);
            if (hit != null) { Invoke(hit); return "invoked (tray corner)"; }
            AutomationElement? hidden = null;
            foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon")))
                if ((b.Current.Name ?? "").IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) >= 0) { hidden = b; break; }
            if (hidden == null) return $"not found ({nVisible} corner icons, no 'Show Hidden Icons' button)";
            Invoke(hidden);
            Thread.Sleep(450);
            var overflow = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"));
            if (overflow == null) return "no hidden-icons flyout";
            hit = FindIcon(overflow, keys, out int nHidden);
            if (hit != null) { Invoke(hit); return "invoked (hidden-icons flyout)"; }
            return $"not found ({nVisible} corner, {nHidden} hidden icons)";
        }
        catch (Exception e) { return "failed: " + e.GetType().Name + " " + e.Message; }
    }
}
