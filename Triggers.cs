using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// Opens the overview when the mouse rests on a screen edge/corner. Polling only (no hooks), on its own thread
/// driven by a high-resolution waitable timer so the trigger is not tied to the UI thread or the 15 ms clock tick.
/// </summary>
internal sealed class HotEdge
{
    const int NearPx = 60;   // pointer "approaching": warm up the window list

    readonly Config _cfg;
    readonly Action<long> _fire;       // gets the Stopwatch timestamp of the detection (for latency logging)
    readonly Action _prewarm;
    readonly Func<bool> _overlayVisible;
    readonly Stopwatch _dwell = new();
    volatile bool _run;
    bool _armed = true, _wasNear;
    int _pushX, _pushY;            // raw movement since the pointer reached the zone (written on the UI thread)
    long _lastPush;
    System.Windows.Interop.HwndSource? _raw;

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, byte[]? data, ref uint size, uint headerSize);
    const int WM_INPUT = 0x00FF;

    /// <summary>Receives raw mouse movement (also while the pointer is stuck at the screen edge) on the UI thread.</summary>
    void StartRaw()
    {
        try
        {
            var p = new System.Windows.Interop.HwndSourceParameters("SpreadRawMouse") { ParentWindow = new IntPtr(-3) /*HWND_MESSAGE*/, Width = 0, Height = 0 };
            _raw = new System.Windows.Interop.HwndSource(p);
            _raw.AddHook(RawHook);
            var d = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = 0x100 /*RIDEV_INPUTSINK*/, Target = _raw.Handle } };
            if (!RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) Log.Write("hot edge: raw mouse input not available, pressure ignored");
        }
        catch (Exception e) { Log.Write("hot edge: raw input failed: " + e.GetType().Name); }
    }

    IntPtr RawHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        try
        {
            uint size = 0;
            GetRawInputData(lParam, 0x10000003 /*RID_INPUT*/, null, ref size, 24);
            if (size < 48 || size > 128) return IntPtr.Zero;
            var buf = new byte[size];
            if (GetRawInputData(lParam, 0x10000003, buf, ref size, 24) == uint.MaxValue) return IntPtr.Zero;
            if (BitConverter.ToUInt32(buf, 0) != 0 /*RIM_TYPEMOUSE*/) return IntPtr.Zero;
            if ((BitConverter.ToUInt16(buf, 24) & 1) != 0) return IntPtr.Zero;   // absolute device (pen, touch screen): no push to measure
            int dx = BitConverter.ToInt32(buf, 36), dy = BitConverter.ToInt32(buf, 40);
            if (dx == 0 && dy == 0) return IntPtr.Zero;
            long now = Environment.TickCount64;
            if (now - _lastPush > 600) { _pushX = 0; _pushY = 0; }
            _lastPush = now;
            _pushX += dx; _pushY += dy;
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>Movement towards the configured edge/corner since the pointer came to rest on it.</summary>
    int Pushed()
    {
        if (Environment.TickCount64 - _lastPush > 600) return 0;
        string e = _cfg.HotEdge.ToLowerInvariant();
        int x = _pushX, y = _pushY;
        int px = e.EndsWith("left") ? Math.Max(0, -x) : e.EndsWith("right") ? Math.Max(0, x) : 0;
        int py = e.StartsWith("top") ? Math.Max(0, -y) : e.StartsWith("bottom") ? Math.Max(0, y) : 0;
        return px + py;
    }

    public HotEdge(Config cfg, Action<long> fire, Action prewarm, Func<bool> overlayVisible)
    {
        _cfg = cfg; _fire = fire; _prewarm = prewarm; _overlayVisible = overlayVisible;
    }

    public void Start()
    {
        if (_cfg.HotEdge.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        _run = true;
        if (_cfg.HotEdgePressure > 0) System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(StartRaw));
        new Thread(Loop) { IsBackground = true, Name = "Spread hot edge", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void Stop() { _run = false; try { _raw?.Dispose(); } catch { } }

    void Loop()
    {
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003
        IntPtr timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x1F0003);
        if (timer != IntPtr.Zero)
        {
            long due = -40000;   // 4 ms, relative
            if (!Native.SetWaitableTimer(timer, ref due, 4, IntPtr.Zero, IntPtr.Zero, false)) { Native.CloseHandle(timer); timer = IntPtr.Zero; }
        }
        while (_run)
        {
            if (timer != IntPtr.Zero) Native.WaitForSingleObject(timer, 50); else Thread.Sleep(4);
            try { Tick(); } catch (Exception e) { Log.Write("hot edge tick: " + e.Message); Thread.Sleep(100); }
        }
        if (timer != IntPtr.Zero) Native.CloseHandle(timer);
    }

    void Tick()
    {
        if (!Native.GetCursorPos(out var p)) return;
        var hmon = Native.MonitorFromPoint(p, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!Native.GetMonitorInfo(hmon, ref mi)) return;
        var m = mi.rcMonitor;

        bool inZone = InZone(p, m, 1), near = InZone(p, m, NearPx);
        if (near && !_wasNear && !_overlayVisible())
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(_prewarm, DispatcherPriority.Background);
        _wasNear = near;

        if (!inZone) { _pushX = 0; _pushY = 0; }   // only movement made while sitting on the edge counts
        if (!near) { _armed = true; _dwell.Reset(); return; }
        if (_overlayVisible()) { _armed = false; _dwell.Reset(); return; }   // re-arms only after the pointer has left the edge
        if (!inZone || !_armed) { _dwell.Reset(); return; }
        if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0) { _dwell.Reset(); return; }   // dragging/resizing
        if (_cfg.HotEdgeIgnoreFullscreen && ForegroundIsFullscreen(m)) { _dwell.Reset(); return; }

        if (!_dwell.IsRunning) _dwell.Restart();
        if (_dwell.ElapsedMilliseconds >= _cfg.HotEdgeDwellMs && (_cfg.HotEdgePressure <= 0 || _raw == null || Pushed() >= _cfg.HotEdgePressure))
        {
            _armed = false; _dwell.Reset();
            long ts = Stopwatch.GetTimestamp();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => _fire(ts)));
        }
    }

    bool InZone(POINT p, RECT m, int t)
    {
        string e = _cfg.HotEdge.ToLowerInvariant();
        bool top = p.Y < m.Top + t, bottom = p.Y >= m.Bottom - t, left = p.X < m.Left + t, right = p.X >= m.Right - t;
        return e switch
        {
            "bottom" => bottom, "top" => top, "left" => left, "right" => right,
            "top-left" => top && left, "top-right" => top && right,
            "bottom-left" => bottom && left, "bottom-right" => bottom && right,
            _ => false
        };
    }

    internal static bool ForegroundIsFullscreen(RECT m)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !Native.GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom
               && (Native.GetWindowLongPtr(fg, -16).ToInt64() & 0x00C00000) == 0;   // no caption = borderless fullscreen
    }
}

/// <summary>Optional: tap of the Windows key. Uses a low-level keyboard hook, so it is disabled unless configured.</summary>
internal sealed class WinKeyHook : IDisposable
{
    readonly Action _onTap;
    readonly Native.HookProc _proc;
    IntPtr _hook;
    bool _winDown, _otherKey;

    public WinKeyHook(Action onTap)
    {
        _onTap = onTap;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("keyboard hook failed");
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & 0x10) == 0)   // ignore injected keys
            {
                int msg = wParam.ToInt32();
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                bool isWin = k.vkCode == 0x5B || k.vkCode == 0x5C;
                if (isWin)
                {
                    if (down && !_winDown) { _winDown = true; _otherKey = false; }
                    else if (up)
                    {
                        if (_winDown && !_otherKey)
                        {
                            // Press an unassigned key so Windows does not open the Start menu on release.
                            Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(_onTap);
                        }
                        _winDown = false;
                    }
                }
                else if (down && _winDown) _otherKey = true;
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() { if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } }
}

internal static class HotkeyParser
{
    public static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": mods |= 1; break;
                case "ctrl": case "control": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }
}
