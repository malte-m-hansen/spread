using System;
using System.Windows.Input;

namespace Spread;

internal enum SearchAction { Default, NewWindow, Reveal, OpenWith, CopyPath, OpenFolder, RunAsAdmin }
internal enum KeyTarget { App, File }

/// <summary>What Enter / a click does with a search result while Ctrl, Shift or Alt is held. Pure, so it can be tested.</summary>
internal static class SearchKeys
{
    public static SearchAction Parse(string? text, SearchAction fallback) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "default" or "open" => SearchAction.Default,
        "newwindow" or "new" => SearchAction.NewWindow,
        "reveal" or "explorer" => SearchAction.Reveal,
        "openwith" => SearchAction.OpenWith,
        "copypath" or "copy" => SearchAction.CopyPath,
        "openfolder" or "folder" => SearchAction.OpenFolder,
        "runasadmin" or "admin" => SearchAction.RunAsAdmin,
        _ => fallback
    };

    /// <summary>Alt first, then Ctrl+Shift (run as administrator), then Ctrl, then Shift; nothing held = Default. Apps and files use the same keys.</summary>
    public static SearchAction Resolve(KeyTarget target, ModifierKeys mods, SearchKeysConfig c)
    {
        bool app = target == KeyTarget.App;
        if (mods.HasFlag(ModifierKeys.Alt)) return Parse(app ? c.AppAlt : c.FileAlt, SearchAction.Reveal);
        if (mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift)) return Parse(app ? c.AppCtrlShift : c.FileCtrlShift, SearchAction.RunAsAdmin);
        if (mods.HasFlag(ModifierKeys.Control)) return Parse(app ? c.AppCtrl : c.FileCtrl, app ? SearchAction.NewWindow : SearchAction.OpenWith);
        if (mods.HasFlag(ModifierKeys.Shift)) return Parse(app ? c.AppShift : c.FileShift, app ? SearchAction.NewWindow : SearchAction.OpenWith);
        return SearchAction.Default;
    }

    public static string Label(SearchAction a, KeyTarget target) => a switch
    {
        SearchAction.NewWindow => "new window",
        SearchAction.Reveal => target == KeyTarget.App ? "open file location" : "show in Explorer",
        SearchAction.OpenWith => "open with…",
        SearchAction.CopyPath => "copy path",
        SearchAction.OpenFolder => "open folder",
        SearchAction.RunAsAdmin => "run as administrator",
        _ => target == KeyTarget.App ? "open / go to it" : "open"
    };

    /// <summary>The hint under the results: all meanings when no key is held, only the held key's meaning otherwise.</summary>
    /// <param name="hasLocation">False for an app without a program folder (Store apps): its Alt and Ctrl+Shift actions then just open it, and the hint says so.</param>
    /// <param name="canRunAsAdmin">False when the program cannot be started elevated (a help file, a Store app).</param>
    public static string Hint(KeyTarget target, ModifierKeys mods, SearchKeysConfig c, bool hasLocation = true, bool canRunAsAdmin = true)
    {
        SearchAction Act(ModifierKeys m)
        {
            var a = Resolve(target, m, c);
            bool app = target == KeyTarget.App;
            if (app && !hasLocation && a is SearchAction.Reveal or SearchAction.OpenFolder) return SearchAction.Default;
            if (((app && !hasLocation) || !canRunAsAdmin) && a == SearchAction.RunAsAdmin) return SearchAction.Default;
            return a;
        }
        if (mods != ModifierKeys.None && (mods & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            var a = Act(mods);
            string key = mods.HasFlag(ModifierKeys.Alt) ? "Alt" : mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift) ? "Ctrl+Shift" : mods.HasFlag(ModifierKeys.Control) ? "Ctrl" : "Shift";
            return $"{key} + Enter / click: {Label(a, target)}";
        }
        string Part(string key, ModifierKeys m) { var a = Act(m); return a == SearchAction.Default ? "" : $"   {key}: {Label(a, target)}"; }
        return "Enter: " + Label(SearchAction.Default, target) + Part("Ctrl", ModifierKeys.Control) + Part("Shift", ModifierKeys.Shift)
            + Part("Ctrl+Shift", ModifierKeys.Control | ModifierKeys.Shift) + Part("Alt", ModifierKeys.Alt);
    }
}
