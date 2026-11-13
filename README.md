# Spread

A GNOME Shell style desktop for Windows 11. Spread adds an activities overview that spreads out all your windows with live previews, a top bar with a clock and status widgets, a dock, and a search that finds apps, Windows settings and files.

It is a single WPF application (.NET 8). It does not replace Explorer; it sits on top of it and can hide the Windows taskbar while it runs. The taskbar comes back when Spread exits, and after a crash it is repaired the next time Spread starts.

## Features

**Overview**
- Opens from a screen edge, a hotkey, the tray icon or the Activities button.
- Live window previews (DWM thumbnails) that animate from their real positions, like GNOME.
- Two layouts: `natural` (windows keep their relative desktop positions) and `gnome` (GNOME's row layout, ported line by line).
- Minimized windows are shown from a recent in-memory snapshot.
- Windows of chosen apps (for example Greenshot) fold into a "photo stack"; scroll on it to flip through.
- Close windows from the overview (close button, middle click or Ctrl+W).

**Search** (start typing in the overview)
- Apps from the Start menu, ranked by how often you use them.
- Windows settings pages and tools ("check for updates", "device manager").
- Files through [Everything](https://www.voidtools.com/), if it is running.
- An app that is already running is switched to instead of started again.

**Dock**
- Pinned and running apps at the bottom of the overview.
- Click to go to an app's latest window, right click for its windows, pin/unpin, new window and "bring all windows here".
- Pins can be imported from the Windows taskbar the first time.

**Top bar**
- Activities button, the name of the active app (click for its windows), and a centred clock.
- Clock panel: calendar with public holidays (Danish), agenda, unread mail and Teams meetings from classic Outlook, and Windows notifications.
- Quick settings: volume and microphone, audio device, brightness, Wi-Fi, Bluetooth, power mode, dark mode, do not disturb, keep awake, VPN and power actions.
- Widgets: weather, next meeting, Jira sprint, GitHub Dependabot alerts, VPN, media controls, WSL, system load, clipboard, screenshot and notification bell.
- A Taskbar button (Ctrl+Alt+T) brings the real Windows taskbar back for a moment, for tray icons and Start.

## Requirements

- Windows 11 (x64)
- .NET 8 SDK

Optional, for the features that use them:
- [Everything](https://www.voidtools.com/) for file search
- Classic Outlook for the calendar, mail and meeting widgets
- [GitHub CLI](https://cli.github.com/) (`gh auth login`) for the Dependabot widget
- An Atlassian API token for the Jira widget

## Build and run

```
dotnet build -c Release -o bin\out
dotnet bin\out\Spread.dll
```

Spread has no .exe of its own; it always runs as `dotnet Spread.dll`. Only one instance runs at a time. A running Spread locks its `Spread.dll`, so build into a new output folder (or quit Spread first) when you want to try a change.

To start it with Windows, use "Start with Windows" in the tray icon's menu.

## Using it

| Action | How |
| --- | --- |
| Open / close the overview | Push the pointer against the bottom screen edge, press Ctrl+Alt+Space, click Activities, or click the tray icon |
| Go to a window | Click it, or use the arrow keys / Tab and press Enter |
| Close a window | Its close button, middle click, or Ctrl+W on the selected window |
| Flip through a photo stack | Mouse wheel, or Page Up / Page Down |
| Search | Just start typing; Enter opens the selected result |
| Search result in a new window / show file in Explorer | Ctrl+Enter (configurable under `Search.Keys`) |
| Leave the overview | Esc (the first Esc clears the search), or click on empty space |
| Show the real taskbar | Ctrl+Alt+T, or the Taskbar button in the top bar |

Touchpad gestures can be mapped to the overview: set a three-finger swipe in Windows to send Ctrl+Alt+Down / Left / Right (see `GestureSelectHotkey` in the config).

## Configuration

Settings live in `%APPDATA%\Spread\config.json`. The file is created with defaults on first start and can be opened from the tray menu ("Open settings folder") or from quick settings. Restart Spread after editing it (quick settings has a "Restart Spread" button). If the file cannot be read, Spread starts with defaults and leaves the file untouched.

Some useful settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| `HotEdge` | `bottom` | Edge or corner that opens the overview (`top-left`, `none`, ...) |
| `HotEdgePressure` | `150` | How hard to push against the edge; `0` = touching it is enough |
| `Hotkey` | `Ctrl+Alt+Space` | Global hotkey for the overview |
| `WinTap` | `false` | A tap on the Windows key opens the overview (uses a keyboard hook) |
| `Layout` | `natural` | `natural` or `gnome` |
| `HideTaskbar` | `hidden` | `hidden`, `autohide` or `off` |
| `TopBar` | `true` | Show the top bar |
| `DisabledWidgets` | `[]` | Widget ids to leave out, e.g. `["wsl", "media"]` |
| `StackApps` | `["Greenshot"]` | Apps whose windows fold into a photo stack |
| `Dock.Enabled` | `true` | Show the dock in the overview |
| `Search.Everything.Enabled` | `true` | Search files through Everything |
| `WeatherLocation` | Copenhagen | Name, latitude and longitude for the weather widget |
| `LogLevel` | `normal` | `normal`, `verbose` or `off` |

The Jira and GitHub widgets are set up from their own panels in the top bar. The Atlassian API token is stored in Windows Credential Manager, never in the config file; GitHub uses your existing `gh` login.

## Privacy

- The only network requests are the weather lookup ([Open-Meteo](https://open-meteo.com/), no account) and the Jira and GitHub widgets' API calls.
- The log (`%APPDATA%\Spread\spread.log`) never contains window titles, mail, meeting or notification text, or file names.
- Window snapshots are kept in memory only.
- The Jira and GitHub clients are read-only: they can only send GET requests, and only to the configured hosts.

## Command-line options

| Option | Meaning |
| --- | --- |
| `--toggle` | Open or close the overview of the running instance (for a shortcut or script) |
| `--show` | Start and open the overview at once |
| `--restore-taskbar` | Make the Windows taskbar always visible again (repair after a crash) |
| `--list-windows` | Print which windows the overview would show |

## Tests

The self-tests run from the built dll and do not touch your settings:

```
dotnet bin\out\Spread.dll --core-test
```

Offline test switches: `--core-test`, `--layout-test`, `--search-test`, `--dock-test`, `--bar-test`, `--toast-test`, `--log-test`, `--atlassian-test`, `--github-test`. Adding `--live` to the Atlassian, GitHub, search or dock tests also runs a few real, read-only queries.

`node tools\parity.js bin\out\Spread.dll` compares the `gnome` layout with GNOME Shell's own layout code.

## Project layout

```
Program.cs          start-up, tray icon, command-line options, self-tests
Config.cs           settings (config.json) and the log
OverlayWindow.cs    the overview: animation, input, search results, dock
StageWindow.cs      the window behind the overview (wallpaper and previews)
Layout.cs           window layouts (natural, GNOME, photo stacks)
Windows.cs          which windows exist and how they are shown
TopBarWindow.cs     the top bar
TaskbarControl.cs   hiding and restoring the Windows taskbar
Triggers.cs         hot edge, hotkey and Windows key tap
Bar/                top bar widgets, clock panel, quick settings, notifications
Bar/Atlassian/      Jira and Confluence widget
Bar/GitHub/         Dependabot widget
Search/             app and settings search, usage ranking, Everything
Dock/               the dock
tools/              GNOME layout parity test
```

## If something goes wrong

If Spread is ended in a way that leaves the Windows taskbar hidden, start it again (it repairs the taskbar on start-up) or run:

```
dotnet bin\out\Spread.dll --restore-taskbar
```

The tray menu also has "Restore Windows taskbar".
