using System;
using System.Windows.Input;

namespace Spread;

internal enum SearchAction { Default, NewWindow, Reveal, OpenWith, CopyPath, OpenFolder }
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
        _ => fallback
    };

    /// <summary>Alt first, then Ctrl (Ctrl+Shift counts as Ctrl), then Shift; nothing held = Default.</summary>
    public static SearchAction Resolve(KeyTarget target, ModifierKeys mods, SearchKeysConfig c)
    {
        bool app = target == KeyTarget.App;
        if (mods.HasFlag(ModifierKeys.Alt)) return Parse(app ? c.AppAlt : c.FileAlt, app ? SearchAction.Default : SearchAction.CopyPath);
        if (mods.HasFlag(ModifierKeys.Control)) return Parse(app ? c.AppCtrl : c.FileCtrl, app ? SearchAction.NewWindow : SearchAction.Reveal);
        if (mods.HasFlag(ModifierKeys.Shift)) return Parse(app ? c.AppShift : c.FileShift, app ? SearchAction.NewWindow : SearchAction.OpenWith);
        return SearchAction.Default;
    }

    public static string Label(SearchAction a, KeyTarget target) => a switch
    {
        SearchAction.NewWindow => "new window",
        SearchAction.Reveal => "show in Explorer",
        SearchAction.OpenWith => "open with…",
        SearchAction.CopyPath => "copy path",
        SearchAction.OpenFolder => "open folder",
        _ => target == KeyTarget.App ? "open / go to it" : "open"
    };

    /// <summary>The hint under the results: all meanings when no key is held, only the held key's meaning otherwise.</summary>
    public static string Hint(KeyTarget target, ModifierKeys mods, SearchKeysConfig c)
    {
        if (mods != ModifierKeys.None && (mods & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            var a = Resolve(target, mods, c);
            string key = mods.HasFlag(ModifierKeys.Alt) ? "Alt" : mods.HasFlag(ModifierKeys.Control) ? "Ctrl" : "Shift";
            return $"{key} + Enter / click: {Label(a, target)}";
        }
        string Part(string key, ModifierKeys m) { var a = Resolve(target, m, c); return a == SearchAction.Default ? "" : $"   {key}: {Label(a, target)}"; }
        return "Enter: " + Label(SearchAction.Default, target) + Part("Ctrl", ModifierKeys.Control) + Part("Shift", ModifierKeys.Shift) + Part("Alt", ModifierKeys.Alt);
    }
}
