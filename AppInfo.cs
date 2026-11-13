using System;
using System.Reflection;

namespace Spread;

/// <summary>The version of this build. It comes from <c>&lt;Version&gt;</c> in Spread.csproj, which the release workflow checks against the git tag.</summary>
internal static class AppInfo
{
    public static string Version { get; } = Parse(typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>"0.1.0+abc123" (the SDK appends the commit) -> "0.1.0"; nothing usable -> "dev".</summary>
    internal static string Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return "dev";
        int plus = informational.IndexOf('+');
        string v = (plus >= 0 ? informational[..plus] : informational).Trim();
        return v.Length == 0 ? "dev" : v;
    }
}
