using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spread;

/// <summary>
/// Where does a start-menu app live? The ids from Get-StartApps are "{KnownFolderGuid}\relative\app.exe", an absolute path, or
/// an AppUserModelID such as "Chrome" or "308046B0AF4A39CB" (the shell knows the program behind it: System.Link.TargetParsingPath), or
/// "Package!App" for Store apps (their folder under WindowsApps cannot be opened by a normal user, so they have no location).
/// </summary>
internal static class AppLocation
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Waits for the shell lookup (at most 2 s) when an AppUserModelID is not known yet: for one action the user asked for. Not for the hint line.</summary>
    public static string? Resolve(string appId) => Resolve(appId, KnownFolder, File.Exists, id => Targets.Wait(id, 2000));

    /// <summary>Does this id need the shell to tell its program (an AppUserModelID or Store id), as opposed to a known-folder id or a drive path?</summary>
    internal static bool NeedsShell(string appId) => !string.IsNullOrWhiteSpace(appId) && appId[0] != '{' && !IsDrivePath(appId);

    /// <summary>
    /// The location from what is known right now, never waiting: false when the shell has not been asked about this id yet (ask with <see cref="Request"/>).
    /// For the key hint, which is refreshed on every key press on the UI thread.
    /// </summary>
    public static bool TryGetCached(string appId, out string? path)
    {
        if (NeedsShell(appId))
        {
            if (!Targets.TryGet(appId, out var target)) { path = null; return false; }
            path = Resolve(appId, KnownFolder, File.Exists, _ => target);
            return true;
        }
        path = Resolve(appId, KnownFolder, File.Exists);
        return true;
    }

    /// <summary>Asks the shell about an id in the background (if it is not known yet); <paramref name="done"/> runs on the worker thread afterwards, also when the lookup failed.</summary>
    public static void Request(string appId, Action done)
    {
        if (!NeedsShell(appId)) { done(); return; }
        Targets.Request(appId, done);
    }

    internal static string? Resolve(string appId, Func<Guid, string?> knownFolder, Func<string, bool> exists, Func<string, string?>? shellTarget = null)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        string? path = null;
        if (appId[0] == '{')
        {
            int slash = appId.IndexOf('\\');
            int close = appId.IndexOf('}');
            if (slash < 0 || close < 0 || close > slash || !Guid.TryParse(appId.AsSpan(1, close - 1), out var g)) return null;
            string? dir = knownFolder(g);
            if (dir != null) path = Path.Combine(dir, appId[(slash + 1)..]);
        }
        else if (IsDrivePath(appId)) path = appId;
        else if (shellTarget?.Invoke(appId) is { } target && IsDrivePath(target)) path = target;   // web links give a URL, Store apps nothing: no location
        return path != null && exists(path) ? path : null;
    }

    /// <summary>The arguments for explorer.exe that show the item: select it, or open its folder when it is gone; null when neither exists.</summary>
    internal static string? RevealArguments(string path, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (exists(path)) return "/select,\"" + path + "\"";
        string? parent = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(parent) && exists(parent) ? ExplorerPathArgument(parent) : null;
    }

    /// <summary>
    /// A folder as an explorer.exe argument. A drive root ("C:\", or a bare "C:") ends in a backslash, which would swallow the closing quote: it has no
    /// spaces, so it goes unquoted. Anything else is quoted, without a trailing backslash.
    /// </summary>
    internal static string ExplorerPathArgument(string folder)
    {
        if (folder.Length == 2 && folder[1] == ':') return folder + "\\";
        return folder.Length == 3 && folder[1] == ':' && folder[2] == '\\' ? folder : "\"" + folder.TrimEnd('\\') + "\"";
    }

    /// <summary>For a real file or folder.</summary>
    public static string? RevealArguments(string path) => RevealArguments(path, p => File.Exists(p) || Directory.Exists(p));

    static readonly string[] Elevatable = { ".exe", ".com", ".bat", ".cmd", ".msc", ".lnk" };

    /// <summary>Only programs (and shortcuts to them) have a "run as administrator"; a help file or a web link does not.</summary>
    internal static bool CanRunAsAdmin(string path) => !string.IsNullOrEmpty(path) && Array.Exists(Elevatable, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Start the program through the shell with the "runas" verb: Windows shows the UAC prompt. Its own folder is the working directory.</summary>
    internal static System.Diagnostics.ProcessStartInfo AdminStart(string path) =>
        new(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) ?? "" };

    /// <summary>
    /// "Package!App": a Store / MSIX app. It has no program file Spread can start itself, but many of them (Windows Terminal, Notepad, Paint) offer
    /// "Run as administrator" on their shell item, as the Start menu shows; true sandboxed apps (Calculator) do not.
    /// </summary>
    internal static bool IsPackagedId(string appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.Contains('!') && appId[0] != '{' && appId.IndexOfAny(new[] { '\\', '/', ':' }) < 0;

    /// <summary>Start a Store app elevated through its shell item (the Start menu's "Run as administrator"). Throws when the app has no such verb.</summary>
    internal static System.Diagnostics.ProcessStartInfo PackagedAdminStart(string appId) =>
        new("shell:AppsFolder\\" + appId) { UseShellExecute = true, Verb = "runas" };

    static bool IsDrivePath(string t) => t.Length > 2 && t[1] == ':' && (t[2] == '\\' || t[2] == '/');

    static readonly ShellTargetCache Targets = new(ShellLookup);

    /// <summary>
    /// The program behind an AppUserModelID, as the shell reports it for the Apps folder (what the Start menu shortcut points at). Runs on the shell worker thread.
    /// Throws when the shell fails (that is not an answer, so it is not cached); null when the shell knows no such item or no program.
    /// </summary>
    static string? ShellLookup(string aumid)
    {
        object? shell = null, folder = null, item = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("no Shell.Application");
            shell = Activator.CreateInstance(type);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder") ?? throw new InvalidOperationException("no Apps folder");
            item = ((dynamic)folder).ParseName(aumid);
            return item == null ? null : (string?)((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath");
        }
        finally
        {
            // the COM objects are released at once, not left to the finalizer
            foreach (var o in new[] { item, folder, shell }) if (o != null) { try { Marshal.FinalReleaseComObject(o); } catch { } }
        }
    }

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }
}

/// <summary>
/// Shell answers for AppUserModelIDs. The shell (Shell.Application over COM) is slow and wants an STA thread, so every lookup runs on ONE dedicated
/// background thread. Only real answers are remembered: a path, or a definite "no program" (ids do not change while Spread runs). A lookup that
/// throws is not an answer and is tried again next time.
/// </summary>
internal sealed class ShellTargetCache
{
    readonly Func<string, string?> _lookup;
    readonly ConcurrentDictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);   // "" = no such item / no program
    readonly Dictionary<string, List<Action>> _pending = new(StringComparer.OrdinalIgnoreCase);

    public ShellTargetCache(Func<string, string?> lookup) => _lookup = lookup;

    /// <summary>True when the answer is known (path null = the shell has no program for it).</summary>
    public bool TryGet(string id, out string? path)
    {
        if (_known.TryGetValue(id, out var v)) { path = v.Length == 0 ? null : v; return true; }
        path = null; return false;
    }

    /// <summary>Asks the shell on the calling thread (the worker, or a test) and remembers a real answer. A failure gives null and is not remembered.</summary>
    public string? Lookup(string id)
    {
        if (TryGet(id, out var cached)) return cached;
        try { var t = _lookup(id); _known[id] = t ?? ""; return t; }
        catch (Exception e) { Log.Write("app location lookup failed: " + e.GetType().Name); return null; }
    }

    /// <summary>Queues the lookup on the worker (one lookup per id at a time); <paramref name="done"/> runs on the worker when it is over, whatever came out.</summary>
    public void Request(string id, Action done)
    {
        if (TryGet(id, out _)) { done(); return; }
        lock (_pending)
        {
            if (_pending.TryGetValue(id, out var waiting)) { waiting.Add(done); return; }
            _pending[id] = new List<Action> { done };
        }
        Worker.Post(() =>
        {
            try { Lookup(id); }
            finally
            {
                List<Action> list;
                lock (_pending) { list = _pending[id]; _pending.Remove(id); }
                foreach (var d in list) { try { d(); } catch (Exception e) { Log.Write("app location callback failed: " + e.GetType().Name); } }
            }
        });
    }

    /// <summary>The answer, waiting at most <paramref name="ms"/> for the worker; null when unknown or when the shell is too slow (the lookup still lands in the cache).</summary>
    public string? Wait(string id, int ms)
    {
        if (TryGet(id, out var p)) return p;
        var ev = new ManualResetEventSlim();
        Request(id, ev.Set);
        ev.Wait(ms);
        return TryGet(id, out p) ? p : null;
    }

    /// <summary>The one STA thread for all shell lookups; started on first use.</summary>
    static class Worker
    {
        static readonly BlockingCollection<Action> Queue = new();
        static readonly object StartLock = new();
        static Thread? _thread;

        public static void Post(Action a)
        {
            lock (StartLock)
            {
                if (_thread == null)
                {
                    _thread = new Thread(() =>
                    {
                        foreach (var w in Queue.GetConsumingEnumerable())
                            try { w(); } catch (Exception e) { Log.Write("shell worker: " + e.GetType().Name); }
                    }) { IsBackground = true, Name = "Spread Shell" };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();
                }
            }
            Queue.Add(a);
        }
    }
}
