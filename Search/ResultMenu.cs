using System.Collections.Generic;

namespace Spread;

internal enum MenuAction { Open, NewWindow, RunAsAdmin, OpenLocation, CopyPath, Pin, Unpin, ShowInExplorer, OpenWith }

internal sealed record MenuEntry(MenuAction Action, string Header);

/// <summary>What the right-click menu of a search result offers. Pure, so it can be tested; the overlay turns it into a ContextMenu.</summary>
internal static class ResultMenu
{
    /// <param name="location">The program's path, or null when it has none (Store apps): then there is no location and no path to copy.</param>
    public static IReadOnlyList<MenuEntry> ForApp(string? location, bool pinned)
    {
        var list = new List<MenuEntry> { new(MenuAction.Open, "Open"), new(MenuAction.NewWindow, "New window") };
        if (location != null && AppLocation.CanRunAsAdmin(location)) list.Add(new(MenuAction.RunAsAdmin, "Run as administrator"));
        if (location != null)
        {
            list.Add(new(MenuAction.OpenLocation, "Open file location"));
            list.Add(new(MenuAction.CopyPath, "Copy path"));
        }
        list.Add(pinned ? new(MenuAction.Unpin, "Unpin from dock") : new(MenuAction.Pin, "Pin to dock"));
        return list;
    }

    /// <param name="path">The full path: a program file also offers Run as administrator.</param>
    public static IReadOnlyList<MenuEntry> ForFile(bool isFolder, string? path = null)
    {
        var list = new List<MenuEntry> { new(MenuAction.Open, "Open") };
        if (!isFolder && path != null && AppLocation.CanRunAsAdmin(path)) list.Add(new(MenuAction.RunAsAdmin, "Run as administrator"));
        list.Add(new(MenuAction.ShowInExplorer, "Show in Explorer"));
        if (!isFolder) list.Add(new(MenuAction.OpenWith, "Open with…"));
        list.Add(new(MenuAction.CopyPath, "Copy path"));
        return list;
    }
}
