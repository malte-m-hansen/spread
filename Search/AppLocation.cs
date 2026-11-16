using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Spread;

/// <summary>
/// Where does a start-menu app live? The ids from Get-StartApps are "{KnownFolderGuid}\relative\app.exe", an absolute path, or
/// "Package!App" for Store apps (their folder under WindowsApps cannot be opened by a normal user, so they have no location).
/// </summary>
internal static class AppLocation
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string? Resolve(string appId) => Resolve(appId, KnownFolder, File.Exists);

    internal static string? Resolve(string appId, Func<Guid, string?> knownFolder, Func<string, bool> exists)
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
        else if (appId.Length > 2 && appId[1] == ':' && (appId[2] == '\\' || appId[2] == '/')) path = appId;
        return path != null && exists(path) ? path : null;
    }

    /// <summary>The arguments for explorer.exe that show the item: select it, or open its folder when it is gone; null when neither exists.</summary>
    internal static string? RevealArguments(string path, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (exists(path)) return "/select,\"" + path + "\"";
        string? parent = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(parent) && exists(parent) ? "\"" + parent + "\"" : null;
    }

    /// <summary>For a real file or folder.</summary>
    public static string? RevealArguments(string path) => RevealArguments(path, p => File.Exists(p) || Directory.Exists(p));

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }
}
