using System;
using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

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

    public static string? Resolve(string appId) => Resolve(appId, KnownFolder, File.Exists, ShellTarget);

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
        // a drive root ends in a backslash, which would swallow the closing quote: it has no spaces, so no quotes
        return !string.IsNullOrEmpty(parent) && exists(parent) ? (parent.EndsWith('\\') ? parent : "\"" + parent + "\"") : null;
    }

    /// <summary>For a real file or folder.</summary>
    public static string? RevealArguments(string path) => RevealArguments(path, p => File.Exists(p) || Directory.Exists(p));

    static bool IsDrivePath(string t) => t.Length > 2 && t[1] == ':' && (t[2] == '\\' || t[2] == '/');

    static readonly ConcurrentDictionary<string, string?> ShellTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program behind an AppUserModelID, as the shell reports it for the Apps folder (what the Start menu shortcut points at). Cached: ids do not change while Spread runs.</summary>
    static string? ShellTarget(string aumid) => ShellTargets.GetOrAdd(aumid, id =>
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            dynamic folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            dynamic? item = folder?.ParseName(id);
            return item == null ? null : (string?)item.ExtendedProperty("System.Link.TargetParsingPath");
        }
        catch (Exception e) { Log.Write("app location lookup failed: " + e.GetType().Name); return null; }
        finally { if (shell != null) Marshal.ReleaseComObject(shell); }
    });

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }
}
