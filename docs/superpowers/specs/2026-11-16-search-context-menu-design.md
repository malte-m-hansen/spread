# Search results: right-click menu and "open file location"

## Intent
When a search result is an app or an Everything file, the user wants to see where it lives and do a few
other things with it, without leaving the overview. Today files already have Ctrl+Enter (show in Explorer),
Shift (open with) and Alt (copy path), but only as hidden keys; apps have nothing, and there is no right-click.

## Goals
- Right-click on a result opens a menu (same look as the dock's menu).
  - App: Open, New window, Open file location, Copy path, Pin to dock / Unpin from dock.
  - File: Open, Show in Explorer, Open with, Copy path. A folder has no "Open with".
- Alt+Enter (and Alt+click) on an app = open file location.
- Menu key / Shift+F10 opens the same menu for the selected result (my call: cheap, and keyboard users need it).

## Non-goals
- The real Windows shell menu (Properties, Send to, third-party entries).
- A menu on settings/tool results (nothing useful to add; right-click does nothing there).
- Store (UWP) apps: their install folder (WindowsApps) cannot be opened by a normal user, so they get no
  "Open file location" / "Copy path"; the rest of the menu works.

## Anti-goals
- Right-click must never launch or close the overview by itself; only a menu choice does.
- No change to what Ctrl/Shift/Alt do for files.

## Constraints
- Windows start-menu ids (`Get-StartApps`) are `{KnownFolderGuid}\relative\app.exe`, absolute paths, an AppUserModelID
  (`Chrome`, `308046B0AF4A39CB`, ...), or `Package!App` (Store). There is no existing id-to-path code; it has to be written.
  AppUserModelIDs are resolved through the shell (`shell:AppsFolder`, property `System.Link.TargetParsingPath`); added after
  the final review found that about half of the start-menu apps on the author machine are of this kind (242 of 306 resolve now).
  Web links and Store apps have no location.
- Copy path never closes the overview (as the file Alt action today); every other choice does, as today.

## Approaches
1. **Own WPF ContextMenu in the overlay, built like the dock menu** (chosen). Consistent, fast, testable.
2. Windows' real shell menu (IContextMenu): more entries, but heavy COM, loads shell extensions, awkward
   focus inside the overlay. Rejected.

## Design (decided)
- `Search/AppLocation.cs`: `Resolve(appId)` returns the existing exe/file path or null. Handles
  `{KnownFolderGuid}\rest` (via SHGetKnownFolderPath), an absolute path, and returns null for Store ids,
  URLs, unknown guids and files that do not exist.
- `Search/ResultMenu.cs`: pure model, `For(app/file, resolvedPath, pinned)` returns the list of menu actions.
  The overlay turns it into MenuItems. This is what the tests check.
- `OverlayWindow`: right-click on an app tile and a file row, plus Menu key / Shift+F10 on the selection.
  Actions reuse LaunchApp, OpenFile and DockPin logic; pin spec for an app is `id:<AppId>`.
- `SearchKeys`: app Alt default changes from `default` to `reveal` (shows the exe selected in Explorer);
  label "open file location"; when the app has no resolvable path, Alt falls back to the normal open.
  An existing config.json that already saved `AppAlt: "default"` keeps that; the README says how to change it.
- README: key table and Search.Keys description updated.

## Left to the builder
Exact menu wording and separators, where the code lives inside OverlayWindow, test names.
