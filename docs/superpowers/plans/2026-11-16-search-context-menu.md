# Search result right-click menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Right-click menu on app and file search results, and Alt+Enter = open file location for apps.

**Architecture:** Two small pure units (`AppLocation` resolves an app id to a path and builds the Explorer arguments; `ResultMenu` lists the menu entries per result kind) are covered by `--search-test`. `OverlayWindow` only turns them into a WPF `ContextMenu` (like `DockContext`) and routes the keys.

**Tech Stack:** C# / WPF (.NET 8), existing `--search-test` self-test (`Search/SearchTest.cs`, helper `Check(name, ok, detail)`).

**Spec:** `docs/superpowers/specs/2026-11-16-search-context-menu-design.md`

## Global Constraints

- Branch `search-context-menu`; commit per task; do not push (the user says when).
- Build to a fresh folder (`dotnet build -c Release -o bin/next97`), never into the folder of the running Spread; never start or kill the user's running instance.
- Files already have Ctrl = show in Explorer, Shift = open with, Alt = copy path: do not change those.
- Right-click must never launch an item or close the overview by itself; only a menu choice does. Copy path keeps the overview open.
- Store (UWP) ids (`Package!App`) get no "Open file location" and no "Copy path".
- Settings/tool results get no menu.
- README stays ASCII-only, no emojis.

## Review Focus

- App id is a Store id, a URL or an unknown known-folder guid: no location, no crash, entries hidden (Task 1, 3).
- Path with spaces, quotes or `&`: the Explorer argument is quoted correctly (Task 1).
- Stale Everything hit (file deleted since indexing): reveal falls back to the parent folder, or does nothing if that is gone too (Task 1).
- A folder hit: no "Open with" (Task 3).
- Alt+Enter on an app with no location: opens it normally, as today (Task 2, 4).
- Existing `config.json` with `AppAlt: "default"`: still honoured (Task 2).

---

### Task 1: AppLocation (id to path, Explorer arguments)

**Files:**
- Create: `Search/AppLocation.cs`
- Modify: `Search/SearchTest.cs` (new `LocationChecks()` called from `Run`)

**Interfaces:**
- Produces:
  - `internal static class AppLocation`
  - `static string? Resolve(string appId)` (real known folders and file system)
  - `internal static string? Resolve(string appId, Func<Guid, string?> knownFolder, Func<string, bool> exists)` (testable core)
  - `internal static string? RevealArguments(string path, Func<string, bool> exists)`: `/select,"<path>"` when `exists(path)`; else the quoted parent folder when that exists; else null.

- [ ] **Step 1: Write the failing tests** in `LocationChecks()` (fake `knownFolder` returns `C:\Program Files` for guid `6D809377-6AF0-444B-8957-A3773F02200E`, null otherwise; fake `exists` from a set):
  - `{6D809377-6AF0-444B-8957-A3773F02200E}\7-Zip\7zFM.exe` -> `C:\Program Files\7-Zip\7zFM.exe` when that path is in the set.
  - same id, path not in the set -> null.
  - `C:\Tools\a.exe` (absolute, exists) -> unchanged; `Microsoft.WindowsTerminal_8wekyb3d8bbwe!App` -> null; `https://example.com` -> null; `{00000000-0000-0000-0000-000000000000}\x.exe` -> null; `""` -> null.
  - `RevealArguments(@"C:\a b\c&d.txt", exists)` -> `/select,"C:\a b\c&d.txt"` when it exists; when it does not but `C:\a b` does -> `"C:\a b"`; when neither -> null.
- [ ] **Step 2: Run** `dotnet build -c Release -o bin/next97` then `dotnet bin/next97/Spread.dll --search-test`. Expected: build error (type missing).
- [ ] **Step 3: Implement** `AppLocation` as above. Known folders via `SHGetKnownFolderPath` P/Invoke (shell32, `KF_FLAG_DEFAULT`, free the returned string with `CoTaskMemFree`); the id's guid is the text inside the leading braces.
- [ ] **Step 4: Run** the same command. Expected: `SEARCH TEST PASSED`.
- [ ] **Step 5: Commit** "Search: resolve an app id to its program path" (end the message with the Co-Authored-By line).

### Task 2: Alt on an app = open file location (keys)

**Files:**
- Modify: `Config.cs` (`SearchKeysConfig.AppAlt` default `"reveal"`, update the doc comment), `Search/SearchKeys.cs` (`Label`), `Search/SearchTest.cs`

**Interfaces:**
- Consumes: existing `SearchKeys.Resolve`, `SearchKeys.Label`, `SearchKeys.Hint`, `SearchAction.Reveal`.
- Produces: `Label(SearchAction.Reveal, KeyTarget.App) == "open file location"` (file target stays `"show in Explorer"`).

- [ ] **Step 1: Write the failing tests** next to the existing key checks (around `SearchTest.cs:183`): default `Resolve(KeyTarget.App, Alt, new SearchKeysConfig()) == SearchAction.Reveal`; `Label(Reveal, App) == "open file location"` and `Label(Reveal, File) == "show in Explorer"`; `new SearchKeysConfig { AppAlt = "default" }` still resolves Alt to `Default`; `Hint(App, Alt, k)` equals `"Alt + Enter / click: open file location"`. Fix any existing check that assumed the old Alt default.
- [ ] **Step 2: Run** `--search-test`. Expected: FAIL on the new checks.
- [ ] **Step 3: Implement** the default and the label.
- [ ] **Step 4: Run** `--search-test`. Expected: `SEARCH TEST PASSED`.
- [ ] **Step 5: Commit** "Search: Alt on an app means open file location".

### Task 3: ResultMenu (menu entries per result kind)

**Files:**
- Create: `Search/ResultMenu.cs`
- Modify: `Search/SearchTest.cs`

**Interfaces:**
- Produces:
  - `internal enum MenuAction { Open, NewWindow, OpenLocation, CopyPath, Pin, Unpin, ShowInExplorer, OpenWith }`
  - `internal sealed record MenuEntry(MenuAction Action, string Header)`
  - `internal static class ResultMenu { static IReadOnlyList<MenuEntry> ForApp(string? location, bool pinned); static IReadOnlyList<MenuEntry> ForFile(bool isFolder); }`

- [ ] **Step 1: Write the failing tests:** `ForApp(@"C:\x.exe", false)` headers are exactly `Open`, `New window`, `Open file location`, `Copy path`, `Pin to dock`; `ForApp(@"C:\x.exe", true)` ends with `Unpin from dock`; `ForApp(null, false)` is `Open`, `New window`, `Pin to dock`; `ForFile(false)` is `Open`, `Show in Explorer`, `Open with...` (use the same ellipsis character the existing menus use), `Copy path`; `ForFile(true)` has no `Open with` entry.
- [ ] **Step 2: Run** `--search-test`. Expected: build error.
- [ ] **Step 3: Implement** the three types.
- [ ] **Step 4: Run** `--search-test`. Expected: `SEARCH TEST PASSED`.
- [ ] **Step 5: Commit** "Search: result menu model".

### Task 4: Wire it into the overlay

**Files:**
- Modify: `OverlayWindow.cs` (tile at ~1584, file row at ~1742, `SearchKey` ~1798, `OpenFile` ~1765, `OpenItem` ~1836, `ActivateSelection` ~2013)

**Interfaces:**
- Consumes: `AppLocation.Resolve(app.AppId)`, `AppLocation.RevealArguments`, `ResultMenu.ForApp/ForFile`, `SearchKeys.Resolve`, `DockBuilder.ResolvePin`, `_cfg.Dock.Pinned`, `RebuildDock()`, `Relayout(animate: true)`.

- [ ] **Step 1: Route modifiers for apps.** Add `void OpenItem(SearchItem item, ModifierKeys mods)` that maps `SearchKeys.Resolve(KeyTarget.App, mods, _cfg.Search.Keys)`: `Reveal` or `OpenFolder` on an app with a location -> reveal it (Explorer, then close the overview like other launches); `NewWindow` -> existing `OpenItem(item, true)`; anything else, or no location -> `OpenItem(item, false)`. Use it from the tile click, `SearchKey` Enter and `ActivateSelection` instead of `NewWin(...)`.
- [ ] **Step 2: Split `OpenFile`** into `OpenFile(h, mods)` (resolves the action) and `RunFileAction(h, SearchAction)`; `Reveal` uses `AppLocation.RevealArguments(h.Full, File.Exists/Directory.Exists)` so a stale hit opens the parent folder.
- [ ] **Step 3: Menu builder** `void ShowResultMenu(SearchItem? item, EverythingHit? hit)` that builds a `ContextMenu` from `ResultMenu`, wires each `MenuAction` (Open, NewWindow, ShowInExplorer, OpenWith -> existing code paths; OpenLocation -> reveal; CopyPath -> `Clipboard.SetText` without closing; Pin/Unpin -> add `"id:" + app.AppId` to `_cfg.Dock.Pinned` or remove entries where `DockBuilder.ResolvePin(p, _apps.All) == app`, then `_cfg.Save()`, `RebuildDock()`, `Relayout(animate: true)`). Opens at the mouse position or, from the keyboard, under the selected result.
- [ ] **Step 4: Triggers.** `MouseRightButtonUp` on app tiles (`item.Kind == SearchKind.App` only) and file rows (mark handled; also select the clicked result); `SearchKey`: `Key.Apps`, and `Key.F10` with Shift, open the menu for the current selection (app tile or file row).
- [ ] **Step 5: Build and self-test:** `dotnet build -c Release -o bin/next97` (0 errors), then every suite: core, bar, layout, search, dock, log, atlassian, github, toast (`dotnet bin/next97/Spread.dll --<name>-test`). Expected: each ends with `PASSED`.
- [ ] **Step 6: Commit** "Search: right-click menu and Alt+Enter for apps".

### Task 5: README and hand-over

**Files:**
- Modify: `README.md` (Using it table: add `Open the location of a result` with `Right click it, or Alt+Enter on an app / Ctrl+Enter on a file`; Search section one line; config note that `Search.Keys.AppAlt` defaults to `reveal` and an old saved `default` must be changed by hand).

- [ ] **Step 1: Edit the README**; check ASCII only: `LC_ALL=C grep -nP '[^\x00-\x7F]' README.md` prints nothing.
- [ ] **Step 2: Manual check list for the user** (the running Spread is theirs; do not start it): right-click an app, a file and a folder result; Alt+Enter on an app; Menu key; Copy path keeps the overview open; a Store app shows no location entries.
- [ ] **Step 3: Commit** "README: search result menu and open file location".
