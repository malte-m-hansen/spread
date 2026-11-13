using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Spread;

/// <summary>
/// The Atlassian API token lives in Windows Credential Manager (per Windows user), never in the config file or the log.
/// For tests the environment variable SPREAD_ATLASSIAN_TOKEN is read first.
/// </summary>
internal static class TokenStore
{
    const string Target = "Spread/Atlassian";

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
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
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
            return CredWrite(ref c, 0);
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static void Delete() { try { CredDelete(Target, 1, 0); } catch { } }
}
