using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Spread;

internal sealed class WinInfo
{
    public IntPtr Hwnd;
    public string Title = "";
    public string ClassName = "";
    public uint Pid;
    public string ProcName = "";
    public string? ExePath;
    public bool Minimized;
    public RECT Bounds;   // visible frame in screen pixels (restored bounds if minimized)
    public int CropL, CropT, CropR, CropB;   // invisible resize border to cut off the DWM thumbnail
    public int WinW, WinH;                   // full window rect size (thumbnail source space)
    public ImageSource? Icon;
    public int W => Bounds.Width;
    public int H => Bounds.Height;
}

internal static class WindowEnumerator
{
    static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "NotifyIconOverflowWindow"
    };
    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<WinInfo> Enumerate(IntPtr self, Config cfg)
    {
        var list = new List<WinInfo>();
        uint myPid = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            try { var w = Inspect(h, self, myPid, cfg); if (w != null) list.Add(w); } catch (Exception e) { Log.Write("inspect failed: " + e.Message); }
            return true;
        }, IntPtr.Zero);
        return list;   // EnumWindows walks top-to-bottom in z-order, i.e. most recently used first
    }

    static WinInfo? Inspect(IntPtr h, IntPtr self, uint myPid, Config cfg)
    {
        if (h == self || !Native.IsWindowVisible(h)) return null;
        if (Native.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return null;

        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && !appWindow) return null;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return null;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0) return null;

        int len = Native.GetWindowTextLength(h);
        if (len == 0) return null;
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        string title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cls = new StringBuilder(256);
        Native.GetClassName(h, cls, cls.Capacity);
        string className = cls.ToString();
        if (SkipClasses.Contains(className)) return null;

        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == myPid) return null;

        // UWP windows are hosted by ApplicationFrameHost; the real app lives in the CoreWindow child.
        uint realPid = pid;
        if (className == "ApplicationFrameWindow")
        {
            var core = Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero) { Native.GetWindowThreadProcessId(core, out uint p2); if (p2 != 0) realPid = p2; }
        }

        string procName = "";
        try { procName = Process.GetProcessById((int)realPid).ProcessName; } catch { }
        if (cfg.ExcludeProcesses.Any(x => string.Equals(x, procName, StringComparison.OrdinalIgnoreCase))) return null;

        bool minimized = Native.IsIconic(h);
        RECT bounds;
        if (minimized)
        {
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!Native.GetWindowPlacement(h, ref wp)) return null;
            bounds = wp.rcNormalPosition;
        }
        else if (!Native.GetWindowRect(h, out bounds)) return null;
        if (bounds.Width < 60 || bounds.Height < 40) return null;

        // GNOME lays out the visible frame; Windows' window rect includes an invisible resize border.
        var frame = bounds;
        int cl = 0, ct = 0, cr = 0, cb = 0;
        if (!minimized && Native.DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out RECT f, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0
            && f.Width > 0 && f.Height > 0 && f.Left >= bounds.Left - 64 && f.Right <= bounds.Right + 64)
        {
            frame = f;
            cl = f.Left - bounds.Left; ct = f.Top - bounds.Top; cr = bounds.Right - f.Right; cb = bounds.Bottom - f.Bottom;
            if (cl < 0 || ct < 0 || cr < 0 || cb < 0) { frame = bounds; cl = ct = cr = cb = 0; }
        }

        string? path = GetExePath(realPid);
        return new WinInfo
        {
            Hwnd = h, Title = title, ClassName = className, Pid = realPid, ProcName = procName,
            ExePath = path, Minimized = minimized, Bounds = frame, Icon = LoadIcon(path),
            CropL = cl, CropT = ct, CropR = cr, CropB = cb, WinW = bounds.Width, WinH = bounds.Height
        };
    }

    static string? GetExePath(uint pid)
    {
        var hp = Native.OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return Native.QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(hp); }
    }

    static ImageSource? LoadIcon(string? path)
    {
        if (path == null) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? src = null;
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
            }
        }
        catch { }
        IconCache[path] = src;
        return src;
    }
}
