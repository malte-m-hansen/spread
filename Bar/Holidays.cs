using System;
using System.Collections.Generic;

namespace Spread;

/// <summary>Public holidays computed offline (no network). Only Denmark ("DK") is built in.</summary>
internal static class Holidays
{
    /// <summary>Easter Sunday (anonymous Gregorian algorithm).</summary>
    public static DateTime Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(y, month, day);
    }

    public static Dictionary<DateTime, string> For(string country, int y)
    {
        var d = new Dictionary<DateTime, string>();
        if (!string.Equals(country, "DK", StringComparison.OrdinalIgnoreCase)) return d;
        var e = Easter(y);
        d[new DateTime(y, 1, 1)] = "Nytårsdag";
        d[e.AddDays(-3)] = "Skærtorsdag";
        d[e.AddDays(-2)] = "Langfredag";
        d[e] = "Påskedag";
        d[e.AddDays(1)] = "2. påskedag";
        d[e.AddDays(39)] = "Kristi himmelfartsdag";
        d[e.AddDays(49)] = "Pinsedag";
        d[e.AddDays(50)] = "2. pinsedag";
        d[new DateTime(y, 12, 25)] = "Juledag";
        d[new DateTime(y, 12, 26)] = "2. juledag";
        return d;   // Store bededag was abolished in 2024
    }
}
