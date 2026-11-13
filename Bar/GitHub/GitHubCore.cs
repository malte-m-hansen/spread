using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Spread;

// ---------------- config ----------------

internal sealed class GitHubToastConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Also toast new HIGH alerts (they are sticky too).</summary>
    public bool OnHigh { get; set; } = false;
    /// <summary>0 = the toast stays until you close it; otherwise it disappears after this many seconds.</summary>
    public int Seconds { get; set; } = 0;
}

internal sealed class GitHubConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "api.github.com";
    public string Org { get; set; } = "Contoso";
    /// <summary>Repositories of the team are found with this custom property (org:Contoso props.ProductTeam:TEAM-1).</summary>
    public string PropertyName { get; set; } = "ProductTeam";
    public string PropertyValue { get; set; } = "TEAM-1";
    /// <summary>Full names (Contoso/repo). When not empty these are used instead of the property search.</summary>
    public string[] Repos { get; set; } = Array.Empty<string>();
    /// <summary>Severities counted in the number next to the shield.</summary>
    public string[] BarSeverities { get; set; } = new[] { "critical" };
    public string[] ListSeverities { get; set; } = new[] { "critical", "high" };
    public int RefreshMinutes { get; set; } = 10;
    /// <summary>Path of gh.exe when it is not on PATH.</summary>
    public string GhPath { get; set; } = "";
    /// <summary>Critical alerts older than this many days are marked in the menu (0 = off).</summary>
    public int SlaDays { get; set; } = 0;
    public GitHubToastConfig Toast { get; set; } = new();
}

// ---------------- model + pure logic ----------------

internal sealed record AlertLite(string Repo, int Number, string Severity, string Package, string Ecosystem, string Manifest, string Scope, string Summary, string Ghsa, string Cve, DateTime Created, string Url)
{
    public string Id => $"{Repo}#{Number}";
    public string ShortRepo => Repo.Contains('/') ? Repo[(Repo.IndexOf('/') + 1)..] : Repo;
}

internal sealed record RepoRef(string FullName, bool Archived);

internal static class GitHubLogic
{
    static string S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static AlertLite? ParseAlert(JsonElement a, string repo)
    {
        try
        {
            var adv = a.GetProperty("security_advisory");
            var dep = a.TryGetProperty("dependency", out var d) ? d : default;
            var pkg = dep.ValueKind == JsonValueKind.Object && dep.TryGetProperty("package", out var p) ? p : default;
            DateTime created = DateTime.TryParse(S(a, "created_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var c) ? c.ToLocalTime() : DateTime.Now;
            return new AlertLite(repo, a.GetProperty("number").GetInt32(), S(adv, "severity").ToLowerInvariant(), S(pkg, "name"), S(pkg, "ecosystem"), S(dep, "manifest_path"), S(dep, "scope"),
                S(adv, "summary"), S(adv, "ghsa_id"), adv.TryGetProperty("cve_id", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() ?? "" : "", created, S(a, "html_url"));
        }
        catch { return null; }
    }

    /// <summary>GitHub's cursor pagination: Link: &lt;https://...&amp;after=XYZ&gt;; rel="next".</summary>
    public static string? NextLink(string? linkHeader)
    {
        if (string.IsNullOrEmpty(linkHeader)) return null;
        foreach (var part in linkHeader.Split(','))
        {
            var m = Regex.Match(part, @"<(?<u>[^>]+)>\s*;\s*rel=""next""");
            if (m.Success) return m.Groups["u"].Value;
        }
        return null;
    }

    /// <summary>The same list you see on github.com: is:open severity:critical props.ProductTeam:TEAM-1.</summary>
    public static string WebUrl(GitHubConfig c, IEnumerable<string>? severities = null)
    {
        var sev = (severities ?? c.BarSeverities).Where(s => s.Length > 0).ToList();
        var parts = new List<string> { "is:open" };
        if (sev.Count > 0) parts.Add("severity:" + string.Join(",", sev));
        if (c.PropertyName.Length > 0 && c.PropertyValue.Length > 0) parts.Add($"props.{c.PropertyName}:{c.PropertyValue}");
        return $"https://github.com/orgs/{c.Org}/security/alerts/dependabot?q=" + string.Join("+", parts.Select(Uri.EscapeDataString));
    }

    public static int Count(IEnumerable<AlertLite> alerts, IEnumerable<string> severities) =>
        alerts.Count(a => severities.Contains(a.Severity, StringComparer.OrdinalIgnoreCase));

    /// <summary>Alerts we have not told the user about. The first run (no state yet) only remembers them.</summary>
    public static List<AlertLite> NewAlerts(IEnumerable<AlertLite> current, HashSet<string> seen, IEnumerable<string> severities, bool firstRun)
    {
        var fresh = current.Where(a => severities.Contains(a.Severity, StringComparer.OrdinalIgnoreCase) && !seen.Contains(a.Id)).ToList();
        foreach (var a in fresh) seen.Add(a.Id);
        return firstRun ? new List<AlertLite>() : fresh;
    }

    public static string Age(DateTime created, DateTime now)
    {
        var d = now - created;
        return d.TotalDays >= 2 ? $"{(int)d.TotalDays} d" : d.TotalHours >= 1 ? $"{(int)d.TotalHours} h" : "new";
    }

    public sealed record SettingsInput(string Org, string PropertyName, string PropertyValue, string Repos, string BarSeverities, string ListSeverities, string RefreshMinutes, string SlaDays, string ToastSeconds, string GhPath);

    static readonly string[] Severities = { "critical", "high", "medium", "low" };

    public static List<string> Validate(SettingsInput i)
    {
        var errors = new List<string>();
        if (!Regex.IsMatch(i.Org.Trim(), @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$")) errors.Add("Organisation looks like Contoso.");
        var repos = i.Repos.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var r in repos) if (!Regex.IsMatch(r, @"^[\w.-]+/[\w.-]+$")) { errors.Add($"Repository '{r}' should look like owner/name."); break; }
        if (repos.Length == 0)
        {
            if (!Regex.IsMatch(i.PropertyName.Trim(), @"^[A-Za-z0-9_.-]{1,50}$")) errors.Add("Team property looks like ProductTeam (or give a list of repositories).");
            if (!Regex.IsMatch(i.PropertyValue.Trim(), @"^[A-Za-z0-9_.\- ]{1,60}$")) errors.Add("Team id looks like TEAM-1 (or give a list of repositories).");
        }
        foreach (var (label, text) in new[] { ("Bar severities", i.BarSeverities), ("Menu severities", i.ListSeverities) })
        {
            var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList();
            if (parts.Count == 0 || parts.Any(p => !Severities.Contains(p))) errors.Add($"{label}: use critical, high, medium, low (comma separated).");
        }
        if (!int.TryParse(i.RefreshMinutes.Trim(), out int rm) || rm < 1 || rm > 120) errors.Add("Refresh: 1 to 120 minutes.");
        if (!int.TryParse(i.SlaDays.Trim(), out int sd) || sd < 0 || sd > 365) errors.Add("Mark older than: 0 to 365 days.");
        if (!int.TryParse(i.ToastSeconds.Trim(), out int ts) || ts < 0 || ts > 86400) errors.Add("Toast seconds: 0 (until closed) to 86400.");
        if (i.GhPath.Trim().Length > 0 && !File.Exists(i.GhPath.Trim())) errors.Add("gh.exe was not found at that path.");
        return errors;
    }

    /// <summary>The toast for one or several new alerts.</summary>
    public static (string title, string body, string footer, string? url) ToastText(List<AlertLite> fresh, GitHubConfig c)
    {
        string team = c.PropertyValue.Length > 0 ? c.PropertyValue : c.Org;
        var worst = fresh.Any(a => a.Severity == "critical") ? "critical" : "high";
        if (fresh.Count == 1)
        {
            var a = fresh[0];
            string id = a.Cve.Length > 0 ? a.Cve : a.Ghsa;
            return ($"New {a.Severity} vulnerability", $"{a.Package} in {a.Repo}: {a.Summary}", $"{team}{(id.Length > 0 ? " · " + id : "")} · {a.Created:HH:mm}", a.Url);
        }
        var repos = fresh.Select(a => a.ShortRepo).Distinct().ToList();
        return ($"{fresh.Count} new {worst} vulnerabilities in {team}", string.Join(", ", repos.Take(4)) + (repos.Count > 4 ? $" and {repos.Count - 4} more" : ""), $"{team} · Dependabot", WebUrl(c, new[] { worst }));
    }
}

// ---------------- token (gh CLI by default) ----------------

internal static class GitHubAuth
{
    static string? _token;

    public static string? FindGh(GitHubConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.GhPath) && File.Exists(c.GhPath)) return c.GhPath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "gh.exe"); if (File.Exists(p)) return p; } catch { }
        }
        foreach (var p in new[] { @"C:\Program Files\GitHub CLI\gh.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\GitHub CLI\gh.exe") })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Token for api.github.com: environment (tests), then `gh auth token` (kept in memory only), then a stored personal access token.</summary>
    public static string? Token(GitHubConfig c, bool refresh = false)
    {
        var env = Environment.GetEnvironmentVariable("SPREAD_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (_token != null && !refresh) return _token;
        var gh = FindGh(c);
        if (gh != null)
        {
            try
            {
                var psi = new ProcessStartInfo(gh, "auth token -h github.com") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd().Trim();
                if (!p.WaitForExit(6000)) { try { p.Kill(); } catch { } return _token = null; }
                if (p.ExitCode == 0 && o.Length > 20) return _token = o;
            }
            catch (Exception e) { Log.Write("gh token failed: " + e.GetType().Name); }
        }
        return _token = null;
    }

    public static bool HasGh(GitHubConfig c) => FindGh(c) != null;
}

// ---------------- client (GET only) ----------------

internal sealed class GitHubClient
{
    sealed class GetOnlyHandler : DelegatingHandler
    {
        readonly string _host;
        public GetOnlyHandler(string host) : base(new HttpClientHandler { AllowAutoRedirect = false }) { _host = host; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Spread's GitHub client is read-only: only GET is allowed.");
            if (!string.Equals(request.RequestUri?.Host, _host, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Request to an unexpected host was blocked.");
            return base.SendAsync(request, ct);
        }
    }

    readonly HttpClient _http;
    readonly GitHubConfig _cfg;
    readonly Dictionary<string, (string etag, List<JsonElement> items, string? next)> _etags = new();
    public int RateRemaining { get; private set; } = -1;

    public GitHubClient(GitHubConfig cfg, HttpMessageHandler? inner = null)
    {
        _cfg = cfg;
        _http = inner != null ? new HttpClient(inner) : new HttpClient(new GetOnlyHandler(cfg.Host));
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Spread", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    async Task<HttpResponseMessage> SendAsync(string url, string? etag, bool refreshToken)
    {
        string? token = GitHubAuth.Token(_cfg, refreshToken);
        if (token == null) throw new GitHubException(0, "no token");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return await _http.SendAsync(req).ConfigureAwait(false);
    }

    /// <summary>Gets every page (cursor links) of a list endpoint. <paramref name="itemsProperty"/> is used for responses like { items: [...] }.</summary>
    public async Task<List<JsonElement>> GetAllAsync(string path, string? itemsProperty = null, int maxPages = 6)
    {
        var all = new List<JsonElement>();
        string? url = path.StartsWith("http") ? path : $"https://{_cfg.Host}{path}";
        for (int page = 0; url != null && page < maxPages; page++)
        {
            _etags.TryGetValue(url, out var cached);
            var resp = await SendAsync(url, cached.etag, false).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) { resp.Dispose(); resp = await SendAsync(url, null, true).ConfigureAwait(false); }   // gh may have refreshed its token
            using (resp)
            {
                if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rl) && int.TryParse(rl.FirstOrDefault(), out int rem)) RateRemaining = rem;
                if (resp.StatusCode == HttpStatusCode.NotModified && cached.items != null) { all.AddRange(cached.items); url = cached.next; continue; }
                if (!resp.IsSuccessStatusCode) throw new GitHubException((int)resp.StatusCode, "HTTP " + (int)resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var root = doc.RootElement;
                var arr = itemsProperty != null && root.TryGetProperty(itemsProperty, out var ip) ? ip : root;
                var items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
                string? next = resp.Headers.TryGetValues("Link", out var lk) ? GitHubLogic.NextLink(string.Join(",", lk)) : null;
                if (resp.Headers.ETag != null && _etags.Count < 200) _etags[url] = (resp.Headers.ETag.ToString(), items, next);
                all.AddRange(items);
                url = next;
            }
        }
        return all;
    }
}

internal sealed class GitHubException : Exception
{
    public int Status { get; }
    public GitHubException(int status, string msg) : base(msg) { Status = status; }
}
