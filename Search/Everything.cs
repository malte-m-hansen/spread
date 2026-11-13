using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Spread;

internal enum EverythingState { Unknown, Ready, NotRunning, Blocked, Error }

internal sealed record EverythingHit(string Name, string Dir, bool IsFolder, long Size, DateTime Modified)
{
    public string Full => string.IsNullOrEmpty(Dir) ? Name : Path.Combine(Dir, Name);
}

internal sealed record EverythingResult(EverythingState State, int Total, List<EverythingHit> Hits, long Ms);

/// <summary>
/// Wire format of Everything 1.4's IPC (WM_COPYDATA, the same protocol the Everything SDK / es.exe use). Pure functions so they can be tested.
/// </summary>
internal static class EverythingProtocol
{
    public const uint WM_COPYDATA = 0x4A;
    public const int COPYDATA_QUERY2W = 18;
    public const uint MATCHPATH = 0x4;
    public const uint REQ_NAME = 0x1, REQ_PATH = 0x2, REQ_FULLPATH = 0x4, REQ_EXT = 0x8, REQ_SIZE = 0x10, REQ_DATE_CREATED = 0x20, REQ_DATE_MODIFIED = 0x40, REQ_DATE_ACCESSED = 0x80, REQ_ATTRIBUTES = 0x100,
        REQ_FILELIST = 0x200, REQ_RUNCOUNT = 0x400, REQ_DATE_RUN = 0x800, REQ_DATE_RECENT = 0x1000, REQ_HL_NAME = 0x2000, REQ_HL_PATH = 0x4000, REQ_HL_FULL = 0x8000;
    public const uint ITEM_FOLDER = 0x1;

    public static uint SortOf(string s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "name" => 1, "name-desc" => 2, "path" => 3, "path-desc" => 4, "size" => 5, "size-desc" => 6, "extension" => 7, "extension-desc" => 8,
        "date-created" => 11, "date-created-desc" => 12, "date-modified" => 13, "date-modified-desc" => 14, _ => 14
    };

    /// <summary>EVERYTHING_IPC_QUERY2: seven DWORDs followed by the zero-terminated UTF-16 search text.</summary>
    public static byte[] EncodeQuery2(uint replyHwnd, uint replyMessage, uint searchFlags, uint offset, uint maxResults, uint requestFlags, uint sortType, string text)
    {
        var body = Encoding.Unicode.GetBytes(text + "\0");
        var buf = new byte[28 + body.Length];
        void W(int i, uint v) => BitConverter.GetBytes(v).CopyTo(buf, i * 4);
        W(0, replyHwnd); W(1, replyMessage); W(2, searchFlags); W(3, offset); W(4, maxResults); W(5, requestFlags); W(6, sortType);
        body.CopyTo(buf, 28);
        return buf;
    }

    /// <summary>EVERYTHING_IPC_LIST2: header (totitems, numitems, offset, request_flags, sort_type), items (flags, data_offset), then the data per item in request-flag order.</summary>
    public static List<EverythingHit> DecodeList2(byte[] d, out int total)
    {
        total = 0;
        var hits = new List<EverythingHit>();
        if (d.Length < 20) return hits;
        uint U(int p) => BitConverter.ToUInt32(d, p);
        total = (int)U(0);
        int num = (int)U(4);
        uint req = U(12);
        if (num < 0 || 20 + (long)num * 8 > d.Length) return hits;
        for (int i = 0; i < num; i++)
        {
            uint flags = U(20 + i * 8);
            int p = (int)U(24 + i * 8);
            if (p < 0 || p > d.Length) continue;
            string name = "", dir = ""; long size = -1; DateTime mod = DateTime.MinValue;
            bool ok = true;
            string Str()
            {
                if (p + 4 > d.Length) { ok = false; return ""; }
                int len = (int)BitConverter.ToUInt32(d, p); p += 4;
                if (len < 0 || p + (len + 1L) * 2 > d.Length) { ok = false; return ""; }   // long: a length near 2^30 overflowed the check and threw below
                string s = Encoding.Unicode.GetString(d, p, len * 2); p += (len + 1) * 2;
                return s;
            }
            long Big() { if (p + 8 > d.Length) { ok = false; return 0; } long v = BitConverter.ToInt64(d, p); p += 8; return v; }
            if ((req & REQ_NAME) != 0) name = Str();
            if ((req & REQ_PATH) != 0 && ok) dir = Str();
            if ((req & REQ_FULLPATH) != 0 && ok) { var full = Str(); if (name.Length == 0) { name = Path.GetFileName(full); dir = Path.GetDirectoryName(full) ?? ""; } }
            if ((req & REQ_EXT) != 0 && ok) Str();
            if ((req & REQ_SIZE) != 0 && ok) size = Big();
            if ((req & REQ_DATE_CREATED) != 0 && ok) Big();
            if ((req & REQ_DATE_MODIFIED) != 0 && ok) { long ft = Big(); if (ok && ft > 0) { try { mod = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { } } }
            if (!ok || name.Length == 0) continue;
            hits.Add(new EverythingHit(name, dir, (flags & ITEM_FOLDER) != 0, size < 0 ? -1 : size, mod));
        }
        return hits;
    }
}

/// <summary>
/// Talks to the Everything client that runs in YOUR session (its index service may run as SYSTEM, that does not matter). Queries run on a dedicated
/// thread with a message-only window, so the UI never blocks; a newer query makes older ones that have not started yet return immediately.
/// The text is handed to Everything exactly as typed (Everything syntax works). File names are never logged.
/// </summary>
internal sealed class EverythingClient
{
    [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public UIntPtr dwData; public uint cbData; public IntPtr lpData; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeoutMs, out IntPtr result);

    readonly EverythingConfig _cfg;
    Thread? _thread;
    Dispatcher? _disp;
    HwndSource? _src;
    IntPtr _hwnd;
    int _latest;
    uint _replyCounter = 0x53500000;
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pendings = new();

    public EverythingState State { get; private set; } = EverythingState.Unknown;

    public EverythingClient(EverythingConfig cfg) { _cfg = cfg; }

    /// <summary>Creates the IPC thread and window now, so the first search does not pay for it.</summary>
    public void Warm() => EnsureThread();

    string WindowClass => "EVERYTHING_TASKBAR_NOTIFICATION" + (string.IsNullOrWhiteSpace(_cfg.InstanceName) ? "" : "_(" + _cfg.InstanceName.Trim() + ")");

    readonly ManualResetEventSlim _ready = new();

    /// <summary>
    /// Starts the IPC thread once (UI thread only) without waiting for it; QueryAsync waits off the UI thread. Before, a thread that took over 3 s to
    /// start (busy PC) left _disp null, QueryAsync threw on it and the next query started a second thread. _disp is set last, once the window exists.
    /// </summary>
    void EnsureThread()
    {
        if (_thread != null) return;
        _thread = new Thread(() =>
        {
            try
            {
                var p = new HwndSourceParameters("SpreadEverythingIpc") { ParentWindow = new IntPtr(-3) /*HWND_MESSAGE*/, Width = 0, Height = 0, WindowStyle = 0 };
                _src = new HwndSource(p);
                _src.AddHook(Hook);
                _hwnd = _src.Handle;
                _disp = Dispatcher.CurrentDispatcher;
            }
            catch (Exception e) { Log.Write("everything ipc window failed: " + e.GetType().Name); }
            finally { _ready.Set(); }
            if (_disp != null) Dispatcher.Run();
        }) { IsBackground = true, Name = "Spread Everything IPC" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Everything answers asynchronously: it sends the result to our window (WM_COPYDATA) when the search is done.</summary>
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)EverythingProtocol.WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (cds.cbData > 0 && cds.lpData != IntPtr.Zero && _pendings.TryRemove((uint)cds.dwData, out var tcs))
            {
                var bytes = new byte[cds.cbData];
                Marshal.Copy(cds.lpData, bytes, 0, bytes.Length);
                tcs.TrySetResult(bytes);
            }
            handled = true;
            return (IntPtr)1;
        }
        return IntPtr.Zero;
    }

    /// <summary>Sends the query (on the IPC thread). Returns the reply slot, or a state when it could not be sent.</summary>
    (EverythingState state, uint reply, TaskCompletionSource<byte[]>? slot) Send(string text, int id)
    {
        if (id != Volatile.Read(ref _latest)) return (State, 0, null);   // a newer keystroke already replaced this query
        var target = FindWindowW(WindowClass, null);
        if (target == IntPtr.Zero) return (State = EverythingState.NotRunning, 0, null);
        string q = _cfg.IncludeFolders ? text : "file: " + text;
        uint flags = _cfg.MatchPath ? EverythingProtocol.MATCHPATH : 0;
        uint req = EverythingProtocol.REQ_NAME | EverythingProtocol.REQ_PATH | EverythingProtocol.REQ_SIZE | EverythingProtocol.REQ_DATE_MODIFIED;
        uint reply = ++_replyCounter;
        var buf = EverythingProtocol.EncodeQuery2((uint)(long)_hwnd, reply, flags, 0, (uint)Math.Clamp(_cfg.MaxResults, 1, 100), req, EverythingProtocol.SortOf(_cfg.Sort), q);
        IntPtr mem = Marshal.AllocHGlobal(buf.Length);
        try
        {
            Marshal.Copy(buf, 0, mem, buf.Length);
            var cds = new COPYDATASTRUCT { dwData = (UIntPtr)EverythingProtocol.COPYDATA_QUERY2W, cbData = (uint)buf.Length, lpData = mem };
            var slot = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendings[reply] = slot;
            var r = SendMessageTimeoutW(target, EverythingProtocol.WM_COPYDATA, _hwnd, ref cds, 0x2 /*SMTO_ABORTIFHUNG*/, 1500, out _);
            if (r == IntPtr.Zero)
            {
                _pendings.TryRemove(reply, out _);
                int err = Marshal.GetLastWin32Error();
                return (State = err == 5 ? EverythingState.Blocked : EverythingState.Error, 0, null);
            }
            return (EverythingState.Ready, reply, slot);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public async Task<EverythingResult> QueryAsync(string text)
    {
        EnsureThread();
        int id = Interlocked.Increment(ref _latest);
        var sw = Stopwatch.StartNew();
        if (!_ready.IsSet) await Task.Run(() => _ready.Wait(3000)).ConfigureAwait(false);
        var disp = _disp;
        if (disp == null) return new EverythingResult(State = EverythingState.Error, 0, new(), sw.ElapsedMilliseconds);
        var sent = new TaskCompletionSource<(EverythingState state, uint reply, TaskCompletionSource<byte[]>? slot)>();
        _ = disp.BeginInvoke(new Action(() =>
        {
            try { sent.SetResult(Send(text, id)); }
            catch (Exception e) { Log.Write("everything: " + e.GetType().Name); sent.SetResult((State = EverythingState.Error, 0, null)); }
        }));
        var (state, reply, slot) = await sent.Task.ConfigureAwait(false);
        if (slot == null) return new EverythingResult(state, 0, new(), sw.ElapsedMilliseconds);
        var done = await Task.WhenAny(slot.Task, Task.Delay(1500)).ConfigureAwait(false);
        if (done != slot.Task) { _pendings.TryRemove(reply, out _); State = EverythingState.Error; return new EverythingResult(State, 0, new(), sw.ElapsedMilliseconds); }
        var hits = EverythingProtocol.DecodeList2(slot.Task.Result, out int total);
        State = EverythingState.Ready;
        return new EverythingResult(State, total, hits, sw.ElapsedMilliseconds);
    }

    // ---------------- helpers for the UI ----------------

    public string? FindExe()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ExePath) && File.Exists(_cfg.ExePath)) return _cfg.ExePath;
        try
        {
            foreach (var n in new[] { "Everything", "Everything64", "Everything1.5a" })
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { var f = p.MainModule?.FileName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
                    finally { p.Dispose(); }
                }
        }
        catch { }
        foreach (var c in new[] { @"C:\Program Files\Everything\Everything.exe", @"C:\Program Files (x86)\Everything\Everything.exe", @"C:\Program Files\Everything 1.5a\Everything64.exe" })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Starts the Everything client in the background (like at login); the configured named instance, which is the one queries go to.</summary>
    public bool Start()
    {
        var exe = FindExe();
        if (exe == null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("-startup");
            if (!string.IsNullOrWhiteSpace(_cfg.InstanceName)) { psi.ArgumentList.Add("-instance"); psi.ArgumentList.Add(_cfg.InstanceName.Trim()); }
            Process.Start(psi);
            return true;
        }
        catch (Exception e) { Log.Write("everything start failed: " + e.GetType().Name); return false; }
    }

    /// <summary>Opens Everything's own window with this search.</summary>
    public void ShowAll(string text)
    {
        var exe = FindExe();
        if (exe == null) return;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("-search"); psi.ArgumentList.Add(text);
            if (!string.IsNullOrWhiteSpace(_cfg.InstanceName)) { psi.ArgumentList.Add("-instance"); psi.ArgumentList.Add(_cfg.InstanceName.Trim()); }
            Process.Start(psi);
        }
        catch (Exception e) { Log.Write("everything open failed: " + e.GetType().Name); }
    }
}
