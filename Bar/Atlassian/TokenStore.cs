using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Spread;

/// <summary>
/// The Atlassian API token lives in Windows Credential Manager (per Windows user), never in the config file or the log.
/// For tests and scripts the environment variable SPREAD_ATLASSIAN_TOKEN is read first - until Disconnect or Connect is used in this session:
/// from then on only Credential Manager counts (otherwise Disconnect reconnected at once with the variable's token, and a new token was ignored).
/// </summary>
internal static class TokenStore
{
    const string Target = "Spread/Atlassian";
    static volatile bool _envOverridden;

    /// <summary>True while the token comes from SPREAD_ATLASSIAN_TOKEN.</summary>
    public static bool FromEnvironment => !_envOverridden && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPREAD_ATLASSIAN_TOKEN"));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);

    public static string? Load()
    {
        var env = Environment.GetEnvironmentVariable("SPREAD_ATLASSIAN_TOKEN");
        if (!_envOverridden && !string.IsNullOrWhiteSpace(env)) return env.Trim();
        try
        {
            if (!CredRead(Target, 1, 0, out IntPtr p)) return null;
            try
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(p);
                return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
            }
            finally { CredFree(p); }
        }
        catch { return null; }
    }

    public static bool Save(string token)
    {
        var bytes = Encoding.Unicode.GetBytes(token);
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL { Type = 1, TargetName = Target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = 2, UserName = "api-token" };
            bool ok = CredWrite(ref c, 0);
            if (ok) Override();
            return ok;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static void Delete() { Override(); try { CredDelete(Target, 1, 0); } catch { } }

    static void Override()
    {
        if (FromEnvironment) Log.Write("atlassian: SPREAD_ATLASSIAN_TOKEN is ignored for the rest of this session (connect/disconnect was used)");
        _envOverridden = true;
    }
}
