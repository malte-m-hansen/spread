using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// Listens to window events (create/destroy/show/hide/move/minimize...) with out-of-context WinEvent hooks.
/// These are accessibility notifications, not keyboard or mouse hooks, and carry no input data.
/// </summary>
internal sealed class WinEvents : IDisposable
{
    public event Action? Changed;
    public event Action<IntPtr>? MinimizeStart;
    public event Action<IntPtr>? Foreground;   // the window that just became foreground

    readonly Native.WinEventProc _proc;
    readonly List<IntPtr> _hooks = new();

    public WinEvents()
    {
        _proc = OnEvent;
        Hook(0x0003, 0x0003);   // EVENT_SYSTEM_FOREGROUND
        Hook(0x0016, 0x0017);   // EVENT_SYSTEM_MINIMIZESTART / MINIMIZEEND
        Hook(0x8000, 0x8003);   // OBJECT_CREATE .. HIDE
        Hook(0x800B, 0x800B);   // OBJECT_LOCATIONCHANGE
        Hook(0x8017, 0x8018);   // OBJECT_CLOAKED / UNCLOAKED (virtual desktops)
    }

    void Hook(uint min, uint max)
    {
        // WINEVENT_OUTOFCONTEXT (0) | WINEVENT_SKIPOWNPROCESS (2)
        var h = Native.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, 2);
        if (h == IntPtr.Zero) Log.Write($"WinEvent hook {min:X}-{max:X} failed"); else _hooks.Add(h);
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;   // OBJID_WINDOW only
        if (ev == 0x0003) { try { Foreground?.Invoke(hwnd); } catch (Exception e) { Log.Write("foreground handler: " + e.Message); } return; }
        if (ev == 0x0016) { try { MinimizeStart?.Invoke(hwnd); } catch (Exception e) { Log.Write("minimize handler: " + e.Message); } }
        Changed?.Invoke();
    }

    public void Dispose() { foreach (var h in _hooks) Native.UnhookWinEvent(h); _hooks.Clear(); }
}

/// <summary>
/// Keeps the window list warm so opening the overview does not have to enumerate windows: it is re-read shortly
/// after anything changes, and at once when the pointer approaches the hot edge.
/// </summary>
internal sealed class WindowCache
{
    readonly Config _cfg;
    readonly DispatcherTimer _idle;
    List<WinInfo> _all = new();
    bool _dirty = true;
    IntPtr _self;
    Func<bool> _overlayVisible = () => false;

    public WindowCache(Config cfg, WinEvents events)
    {
        _cfg = cfg;
        _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _idle.Tick += (_, _) => { _idle.Stop(); if (_dirty && !_overlayVisible()) Refresh(); };
        events.Changed += () => { _dirty = true; _idle.Stop(); _idle.Start(); };   // debounced
    }

    /// <summary>
    /// Re-sorts the cached windows by their current front-to-back order (one EnumWindows pass, ~1 ms). Without this the
    /// clones were stacked in the order of the last full refresh: a window activated since then had its clone behind
    /// windows that really were behind it, so it vanished while the windows travelled home.
    /// </summary>
    void ReorderByZ()
    {
        var z = new Dictionary<IntPtr, int>(256);
        int i = 0;
        Native.EnumWindows((h, _) => { z[h] = i++; return true; }, IntPtr.Zero);
        _all = _all.OrderBy(w => z.TryGetValue(w.Hwnd, out int idx) ? idx : int.MaxValue).ToList();
    }

    public void Attach(IntPtr selfHwnd, Func<bool> overlayVisible) { _self = selfHwnd; _overlayVisible = overlayVisible; }

    void Refresh()
    {
        _all = WindowEnumerator.Enumerate(_self, _cfg);
        _dirty = false;
    }

    /// <summary>Cheap call from the hot-edge thread's proximity trigger.</summary>
    public void Prewarm() { if (_dirty && !_overlayVisible()) Refresh(); }

    public List<WinInfo> Get(out bool fromCache)
    {
        fromCache = !_dirty;
        if (_dirty) Refresh();
        else ReorderByZ();   // activating a window changes the stacking order without any event the cache listens to
        return new List<WinInfo>(_all);
    }
}
