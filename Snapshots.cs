using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// DWM cannot preview minimized windows, and the "about to minimize" notification arrives too late to capture them.
/// So Spread keeps one reduced, in-memory snapshot of the window you are using: refreshed when you switch away from it
/// and every few seconds while you are active (skipped when idle). When you then minimize it, a recent image exists.
/// Only the foreground window is ever captured; images live in RAM only and are never written to disk.
/// </summary>
internal sealed class SnapshotCache
{
    const int MaxEntries = 24, MaxLongSide = 1280, PeriodMs = 3000, IdleSkipMs = 8000;

    readonly Dictionary<IntPtr, BitmapSource> _map = new();
    readonly Dictionary<IntPtr, long> _when = new();
    readonly Dictionary<IntPtr, string> _outcome = new();   // diagnostics: last result per window, logged when it changes
    int _tickCount;
    readonly object _lock = new();
    readonly Config _cfg;
    readonly DispatcherTimer _timer;
    readonly uint _selfPid = (uint)Environment.ProcessId;
    readonly Dictionary<uint, string> _procNames = new();
    Func<bool> _overlayVisible = () => false;
    IntPtr _lastForeground;
    int _busy;
    int _okCount, _skippedIconic;
    long _pauseUntil, _nextAllowed;

    /// <summary>No captures for a while (called when the pointer nears the hot edge: an overview open is imminent).</summary>
    public void Pause(int ms) => _pauseUntil = Environment.TickCount64 + ms;

    public SnapshotCache(Config cfg, WinEvents events)
    {
        _cfg = cfg;
        events.Foreground += OnForeground;
        events.MinimizeStart += h => Request(h, "minimize");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(PeriodMs) };
        _timer.Tick += (_, _) => Periodic();
        _timer.Start();
    }

    public void Attach(Func<bool> overlayVisible) => _overlayVisible = overlayVisible;

    public BitmapSource? Get(IntPtr hwnd)
    {
        lock (_lock) return _map.TryGetValue(hwnd, out var s) && Native.IsWindow(hwnd) ? s : null;
    }

    void OnForeground(IntPtr now)
    {
        var prev = _lastForeground;
        _lastForeground = now;
        // The window you just left is usually still fully rendered: take its final image now.
        if (prev != IntPtr.Zero && prev != now && !_overlayVisible()) Request(prev, "left");
        // a window you only looked at briefly and then minimized has no "left" image (it is minimized by then): take one soon after it came to the front
        if (now != IntPtr.Zero)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (_, _) => { t.Stop(); if (_lastForeground == now && !_overlayVisible()) Request(now, "arrived"); };
            t.Start();
        }
    }

    void Periodic()
    {
        if (_overlayVisible()) return;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        // idle for a while and we already hold an image: nothing has changed
        var lii = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (Native.GetLastInputInfo(ref lii) && unchecked((uint)Environment.TickCount - lii.dwTime) > IdleSkipMs)
        {
            lock (_lock) if (_map.ContainsKey(fg)) return;
        }
        Request(fg, "tick");
        if (++_tickCount % 7 == 0) Sweep(fg);   // about every 20 s
    }

    /// <summary>
    /// Windows that are open but not in front (an Outlook you keep in the background) would never get an image, and a minimized
    /// window cannot be captured at all. So now and then one visible window without a recent image is captured too.
    /// </summary>
    void Sweep(IntPtr fg)
    {
        long now = Environment.TickCount64;
        if (now < _pauseUntil || now < _nextAllowed) return;
        var lii = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (Native.GetLastInputInfo(ref lii) && unchecked((uint)Environment.TickCount - lii.dwTime) > IdleSkipMs) return;
        Task.Run(() =>
        {
            try
            {
                IntPtr pick = IntPtr.Zero;
                foreach (var w in WindowEnumerator.Enumerate(IntPtr.Zero, _cfg))
                {
                    if (w.Minimized || w.Hwnd == fg) continue;
                    long age;
                    lock (_lock) age = _when.TryGetValue(w.Hwnd, out var t) ? now - t : long.MaxValue;
                    if (age > 180_000) { pick = w.Hwnd; break; }
                }
                if (pick != IntPtr.Zero) System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => Request(pick, "sweep")));
            }
            catch (Exception e) { Log.Write("snapshot sweep: " + e.Message); }
        });
    }

    /// <summary>Cheap checks on the UI thread, the actual capture on a worker (single flight).</summary>
    void Request(IntPtr hwnd, string reason)
    {
        try
        {
            long nowMs = Environment.TickCount64;
            if (reason != "minimize" && nowMs < _pauseUntil) return;
            if (reason == "tick" && nowMs < _nextAllowed) return;   // back off after expensive captures
            bool iconic = false;
            if (hwnd == IntPtr.Zero || !Listable(hwnd, out iconic))
            {
                if (reason == "minimize") { _skippedIconic++; if (_skippedIconic <= 20) Log.Debug($"snapshot skipped ({reason}): window already {(iconic ? "minimized" : "not listable")}"); }
                return;
            }
            if (Native.IsHungAppWindow(hwnd)) { Note(hwnd, reason, "app not responding"); return; }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // one capture at a time
            Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var img = CaptureCore(hwnd, reason);
                    sw.Stop();
                    // the more a capture costs, the longer we leave the app alone afterwards
                    _nextAllowed = Environment.TickCount64 + Math.Max(PeriodMs, (long)(sw.ElapsedMilliseconds * 12));
                    if (img != null)
                    {
                        lock (_lock) { _map[hwnd] = img; _when[hwnd] = Environment.TickCount64; Prune(); }
                        _okCount++;
                        if (reason != "tick" || _okCount % 40 == 1) Log.Debug($"snapshot ok ({reason}), {img.PixelWidth}x{img.PixelHeight}, took {sw.ElapsedMilliseconds} ms, {_map.Count} cached");
                    }
                }
                catch (Exception e) { Log.Write("snapshot failed: " + e.Message); }
                finally { Volatile.Write(ref _busy, 0); }
            });
        }
        catch (Exception e) { Log.Write("snapshot request: " + e.Message); }
    }

    /// <summary>Would this window appear in the overview? (visible, not cloaked, a real app window, not ours / excluded)</summary>
    bool Listable(IntPtr h, out bool iconic)
    {
        iconic = Native.IsIconic(h);
        if (iconic || !Native.IsWindow(h) || !Native.IsWindowVisible(h)) return false;
        if (Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        var cls = new StringBuilder(128); Native.GetClassName(h, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == _selfPid) return false;
        string? name;
        lock (_procNames) _procNames.TryGetValue(pid, out name);
        if (name == null)
        {
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
            lock (_procNames) { if (_procNames.Count > 500) _procNames.Clear(); _procNames[pid] = name; }   // pids are reused: keep it small
        }
        return !_cfg.ExcludeProcesses.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs on the capture worker (via <see cref="Note"/>) while the UI thread may be adding names: locked.</summary>
    string ProcOf(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        lock (_procNames) return _procNames.TryGetValue(pid, out var n) && n.Length > 0 ? n : "pid" + pid;
    }

    void Note(IntPtr h, string reason, string outcome)
    {
        string key = outcome;
        lock (_lock)
        {
            if (_outcome.TryGetValue(h, out var last) && last == key) return;
            _outcome[h] = key;
        }
        Log.Debug($"snapshot {ProcOf(h)} ({reason}): {outcome}");
    }

    BitmapSource? CaptureCore(IntPtr hwnd, string reason)
    {
        var img = CaptureCore2(hwnd, reason, out string why);
        Note(hwnd, reason, img != null ? "ok" : why);
        return img;
    }

    BitmapSource? CaptureCore2(IntPtr hwnd, string reason, out string why)
    {
        why = "size out of range";
        if (!Native.GetWindowRect(hwnd, out var r) || r.Width < 80 || r.Height < 60 || r.Width > 12000 || r.Height > 8000) { why = $"size {r.Width}x{r.Height} not capturable"; return null; }

        // crop to the visible frame (cut off the invisible resize border)
        int cl = 0, ct = 0, cr = 0, cb = 0;
        if (Native.DwmGetWindowAttribute(hwnd, 9, out RECT f, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0 && f.Width > 0 && f.Height > 0)
        {
            cl = Math.Max(0, f.Left - r.Left); ct = Math.Max(0, f.Top - r.Top);
            cr = Math.Max(0, r.Right - f.Right); cb = Math.Max(0, r.Bottom - f.Bottom);
        }

        using var full = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);   // no alpha: PrintWindow leaves it undefined
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            try { Native.PrintWindow(hwnd, hdc, 0x2 /*PW_RENDERFULLCONTENT*/); }
            finally { g.ReleaseHdc(hdc); }
        }
        // if the window started minimizing while we captured, the image is garbage
        if (Native.IsIconic(hwnd)) { why = "became minimized during capture"; return null; }

        int cw = r.Width - cl - cr, ch = r.Height - ct - cb;
        if (cw < 40 || ch < 40) { why = "visible area too small"; return null; }
        double k = Math.Min(1.0, MaxLongSide / (double)Math.Max(cw, ch));
        int tw = Math.Max(1, (int)Math.Round(cw * k)), th = Math.Max(1, (int)Math.Round(ch * k));
        using var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, new Rectangle(0, 0, tw, th), new Rectangle(cl, ct, cw, ch), GraphicsUnit.Pixel);
        }

        if (IsBlank(small))
        {
            why = $"BLANK {r.Width}x{r.Height} (app did not render into PrintWindow)";
            return null;
        }

        IntPtr hbm = small.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DeleteObject(hbm); }
    }

    /// <summary>A capture that is (almost) one flat colour means the app did not render into PrintWindow.</summary>
    static bool IsBlank(Bitmap b)
    {
        int min = 255, max = 0, samples = 0;
        for (int y = 2; y < b.Height; y += Math.Max(1, b.Height / 24))
            for (int x = 2; x < b.Width; x += Math.Max(1, b.Width / 40))
            {
                var c = b.GetPixel(x, y);
                int l = (c.R + c.G + c.B) / 3;
                min = Math.Min(min, l); max = Math.Max(max, l); samples++;
            }
        return samples == 0 || max - min < 6;
    }

    /// <summary>`--capture-test`: can minimized windows be rendered with PrintWindow at all? Prints only sizes and a blank/non-blank flag.</summary>
    public static int ProbeMinimized()
    {
        var cfg = Config.Load();
        var wins = WindowEnumerator.Enumerate(IntPtr.Zero, cfg);
        Console.WriteLine($"{wins.Count} windows, {wins.Count(w => w.Minimized)} minimized");
        foreach (var w in wins)
        {
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            Native.GetWindowPlacement(w.Hwnd, ref wp);
            int nw = wp.rcNormalPosition.Width, nh = wp.rcNormalPosition.Height;
            Native.GetWindowRect(w.Hwnd, out var cur);
            Console.WriteLine($"-- {w.ProcName} minimized={w.Minimized}");
            foreach (uint flags in new uint[] { 0, 2 })
            {
                foreach (var (label, bw, bh) in new[] { ("normal size", nw, nh), ("current rect", cur.Width, cur.Height) })
                {
                    if (bw < 20 || bh < 20 || bw > 8000 || bh > 8000) continue;
                    using var bmp = new Bitmap(bw, bh, PixelFormat.Format24bppRgb);
                    bool ok;
                    using (var g = Graphics.FromImage(bmp)) { IntPtr hdc = g.GetHdc(); try { ok = Native.PrintWindow(w.Hwnd, hdc, flags); } finally { g.ReleaseHdc(hdc); } }
                    Console.WriteLine($"  {w.ProcName,-18} flags={flags} {label,-12} {bw}x{bh}: PrintWindow={ok} blank={IsBlank(bmp)} bodyColours={BodyColours(bmp)}");
                }
            }
        }
        return 0;
    }

    static int BodyColours(Bitmap b)
    {
        var set = new HashSet<int>();
        for (int y = Math.Min(100, b.Height / 4); y < b.Height - 2; y += Math.Max(1, b.Height / 40))
            for (int x = 2; x < b.Width - 2; x += Math.Max(1, b.Width / 60)) set.Add(b.GetPixel(x, y).ToArgb() & 0xF8F8F8);
        return set.Count;
    }

    void Prune()
    {
        foreach (var dead in _map.Keys.Where(h => !Native.IsWindow(h)).ToList()) { _map.Remove(dead); _when.Remove(dead); }
        while (_map.Count > MaxEntries)
        {
            var oldest = _when.OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault(_map.ContainsKey);
            if (oldest == IntPtr.Zero) oldest = _map.Keys.First();
            _map.Remove(oldest); _when.Remove(oldest);
        }
    }
}
