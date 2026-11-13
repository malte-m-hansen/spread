using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Spread;

/// <summary>
/// Hides the Windows taskbar and puts it back afterwards. Two levels:
/// "autohide": the documented AppBar setting (same as the Settings toggle). The taskbar still slides in when the pointer touches its edge.
/// "hidden": auto-hide (so it reserves no space) plus keeping the taskbar window itself hidden, so touching the edge does nothing.
/// Marker files record that *we* changed it, so a crash can be repaired at the next start; "--restore-taskbar" and the tray menu entry always work.
/// </summary>
internal static class TaskbarControl
{
    static string Marker => Path.Combine(Config.Dir, "taskbar-hidden.flag");
    static string WinMarker => Path.Combine(Config.Dir, "taskbar-window-hidden.flag");
    static volatile bool _keepWindowHidden;
    static long _pauseGuardUntil;

    /// <summary>Is the real taskbar window visible right now? (false while Spread keeps it hidden)</summary>
    public static bool MainWindowVisible()
    {
        var h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        return h != IntPtr.Zero && Native.IsWindowVisible(h);
    }

    /// <summary>
    /// Show the taskbar window for a short time (UI Automation can only read the tray of a visible window). Because the taskbar is in
    /// auto-hide mode it stays off-screen; the guard timer hides it again afterwards.
    /// </summary>
    public static void ShowTemporarily(int ms)
    {
        Interlocked.Exchange(ref _pauseGuardUntil, Environment.TickCount64 + ms);
        foreach (var h in TrayWindows()) Native.ShowWindow(h, 8 /*SW_SHOWNA*/);
    }

    static Native.APPBARDATA NewData()
    {
        var d = new Native.APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        d.hWnd = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        return d;
    }

    public static uint GetState() { var d = NewData(); return (uint)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d); }

    static void SetState(uint state) { var d = NewData(); d.lParam = (IntPtr)state; Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d); }

    public static bool IsHiddenBySpread => File.Exists(Marker) || File.Exists(WinMarker);

    static IEnumerable<IntPtr> TrayWindows()
    {
        var main = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr h = IntPtr.Zero;
        while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
    }

    static void HideWindows()
    {
        bool any = false;
        foreach (var h in TrayWindows()) if (Native.IsWindowVisible(h)) { Native.ShowWindow(h, 0 /*SW_HIDE*/); any = true; }
        if (any && !File.Exists(WinMarker)) { Directory.CreateDirectory(Config.Dir); File.WriteAllText(WinMarker, "1"); }
    }

    static void ShowWindows()
    {
        foreach (var h in TrayWindows()) Native.ShowWindow(h, 8 /*SW_SHOWNA*/);
        if (File.Exists(WinMarker)) File.Delete(WinMarker);
    }

    /// <summary>Switch the taskbar to auto-hide, and optionally keep its window hidden. Claims nothing it did not change.</summary>
    public static void Hide(bool hideWindow = false)
    {
        try
        {
            uint s = GetState();
            if ((s & Native.ABS_AUTOHIDE) != 0 && !IsHiddenBySpread) Log.Write("taskbar was already auto-hide: leaving that part alone");
            else
            {
                if (!File.Exists(Marker)) { Directory.CreateDirectory(Config.Dir); File.WriteAllText(Marker, s.ToString()); }
                SetState(s | Native.ABS_AUTOHIDE);
            }
            _keepWindowHidden = hideWindow;
            if (hideWindow) HideWindows();
            Log.Write($"taskbar hidden (autohide, window hidden={hideWindow}; original state {s})");
        }
        catch (Exception e) { Log.Write("taskbar hide failed: " + e.Message); }
    }

    static bool _revealed;
    public static bool Revealed => _revealed;

    /// <summary>Temporarily bring the real taskbar back on screen (full, not auto-hide), or put it away again.</summary>
    public static void SetRevealed(bool on)
    {
        try
        {
            if (on == _revealed) return;
            _revealed = on;
            if (on)
            {
                // overlay mode: keep auto-hide (no screen space is reserved, so windows stay put), show the window, let it slide in over them
                _keepWindowHidden = false;
                foreach (var h in TrayWindows()) Native.ShowWindow(h, 8);
                Native.GetCursorPos(out var p);
                Native.SetCursorPos(p.X, Native.GetSystemMetrics(1) - 1);   // touching the bottom edge makes an auto-hide taskbar slide in
                var main = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
                if (main != IntPtr.Zero) Native.SetForegroundWindow(main);   // while it is the active window it stays visible
                Log.Debug("taskbar revealed (overlay)");
            }
            else
            {
                Hide(true);
                Log.Debug("taskbar put away again");
            }
        }
        catch (Exception e) { Log.Write("taskbar reveal failed: " + e.Message); }
    }

    /// <summary>Called by a timer: Explorer shows its taskbar window again now and then (Win key, Explorer events).</summary>
    public static void Guard()
    {
        try { if (_keepWindowHidden && Environment.TickCount64 >= Interlocked.Read(ref _pauseGuardUntil)) HideWindows(); } catch { }
    }

    /// <summary>Undo <see cref="Hide"/>. With force, the taskbar is made always-visible even if Spread did not hide it.</summary>
    public static bool Restore(bool force = false)
    {
        try
        {
            if (!IsHiddenBySpread && !force) return false;
            _keepWindowHidden = false;
            uint s = GetState();
            if ((s & Native.ABS_AUTOHIDE) != 0) SetState(s & ~Native.ABS_AUTOHIDE);
            ShowWindows();
            if (File.Exists(Marker)) File.Delete(Marker);
            Log.Write("taskbar restored");
            return true;
        }
        catch (Exception e) { Log.Write("taskbar restore failed: " + e.Message); return false; }
    }

    /// <summary>After an Explorer restart the setting is lost and a new taskbar window exists: apply everything again.</summary>
    public static void ReapplyIfNeeded()
    {
        try
        {
            if (!IsHiddenBySpread) return;
            uint s = GetState();
            if ((s & Native.ABS_AUTOHIDE) == 0) SetState(s | Native.ABS_AUTOHIDE);
            if (_keepWindowHidden) HideWindows();
        }
        catch { }
    }
}
