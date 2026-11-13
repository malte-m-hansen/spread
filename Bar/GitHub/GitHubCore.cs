using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
    /// <summary>The API host: api.github.com, or "ghe.example.com/api/v3" for GitHub Enterprise Server. gh and the browser links use the matching web host.</summary>
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
        return $"https://{WebHost(c.Host)}/orgs/{c.Org}/security/alerts/dependabot?q=" + string.Join("+", parts.Select(Uri.EscapeDataString));
    }

    /// <summary>"https://ghe.example.com:8443/api/v3/" -> ("ghe.example.com", ":8443/api/v3"). Anything that does not look like a host falls back to api.github.com.</summary>
    static (string host, string rest) SplitHost(string? apiHost)
    {
        string h = (apiHost ?? "").Trim();
        int s = h.IndexOf("://", StringComparison.Ordinal); if (s >= 0) h = h[(s + 3)..];
        h = h.TrimEnd('/');
        int cut = h.IndexOfAny(new[] { '/', ':' });
        string host = cut >= 0 ? h[..cut] : h, rest = cut >= 0 ? h[cut..] : "";
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9.-]+$")) return ("api.github.com", "");
        return (host, Regex.IsMatch(rest, @"^(:\d{1,5})?(/[A-Za-z0-9._~/-]*)?$") ? rest : "");
    }

    /// <summary>The bare host name of the configured API host (no scheme, port or path): what the request guard compares with. "ghe.example.com/api/v3" -> ghe.example.com.</summary>
    public static string ApiHostName(string? apiHost) => SplitHost(apiHost).host;

    /// <summary>Base of every API url: https:// + the configured host including a GitHub Enterprise Server path (/api/v3), without a trailing slash.</summary>
    public static string ApiBase(string? apiHost) { var (h, rest) = SplitHost(apiHost); return "https://" + h + rest; }

    /// <summary>The web host for gh (-h) and the links opened in the browser: api.github.com -> github.com, api.X.ghe.com -> X.ghe.com, GitHub Enterprise Server -> its host.</summary>
    public static string WebHost(string? apiHost)
    {
        string h = ApiHostName(apiHost);
        if (h.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return "github.com";
        if (h.StartsWith("api.", StringComparison.OrdinalIgnoreCase) && h.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)) return h[4..];
        return h;
    }

    /// <summary>
    /// GitHub says "rate limited" with 429, or with a 403 that carries x-ratelimit-remaining: 0, a retry-after header or a "rate limit" message. Such a 403 is not
    /// "this repository is not readable": counting it as one silently dropped the repository's alerts from an "ok" result. Returns how long to wait, or null.
    /// </summary>
    public static TimeSpan? RateLimitWait(int status, string? remaining, string? resetEpoch, TimeSpan? retryAfter, string? body, DateTimeOffset now)
    {
        if (status != 403 && status != 429) return null;
        bool exhausted = remaining?.Trim() == "0";
        bool limited = status == 429 || retryAfter != null || exhausted || (body?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!limited) return null;
        var wait = retryAfter ?? TimeSpan.Zero;
        if (exhausted && long.TryParse(resetEpoch?.Trim(), out long reset) && reset > 0 && reset < 253402300799)
        {
            var untilReset = DateTimeOffset.FromUnixTimeSeconds(reset) - now;
            if (untilReset > wait) wait = untilReset;
        }
        return wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
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
    static long _tokenAt;
    static readonly object Gate = new();

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

    /// <summary>
    /// Token for the configured host: environment (tests), then `gh auth token` (kept in memory only; Spread stores no GitHub token). One gh at a time:
    /// the parallel repository requests that all got a 401 share one refresh instead of starting gh four times.
    /// </summary>
    public static string? Token(GitHubConfig c, bool refresh = false)
    {
        var env = Environment.GetEnvironmentVariable("SPREAD_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        lock (Gate)
        {
            if (_token != null && (!refresh || Environment.TickCount64 - _tokenAt < 5000)) return _token;
            var gh = FindGh(c);
            if (gh != null)
            {
                try
                {
                    var r = ConsoleTool.Run(gh, new[] { "auth", "token", "-h", GitHubLogic.WebHost(c.Host) }, 6000);
                    if (r == null) Log.Write("gh token: gh did not answer in time");
                    else if (r.Value.exit == 0 && r.Value.output.Length > 20) { _tokenAt = Environment.TickCount64; return _token = r.Value.output; }
                }
                catch (Exception e) { Log.Write("gh token failed: " + e.GetType().Name); }
            }
            return _token = null;
        }
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
    /// <summary>Concurrent: the repositories are fetched four at a time through one client (a plain Dictionary can corrupt itself under parallel writes).</summary>
    readonly ConcurrentDictionary<string, (string etag, List<JsonElement> items, string? next)> _etags = new();
    public int RateRemaining { get; private set; } = -1;
    /// <summary>For the GET-only checks in --github-test.</summary>
    internal HttpClient HttpForTests => _http;

    public GitHubClient(GitHubConfig cfg, HttpMessageHandler? inner = null)
    {
        _cfg = cfg;
        _http = inner != null ? new HttpClient(inner) : new HttpClient(new GetOnlyHandler(GitHubLogic.ApiHostName(cfg.Host)));
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
        string? url = path.StartsWith("http") ? path : GitHubLogic.ApiBase(_cfg.Host) + path;
        for (int page = 0; url != null && page < maxPages; page++)
        {
            _etags.TryGetValue(url, out var cached);
            var resp = await SendAsync(url, cached.etag, false).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) { resp.Dispose(); resp = await SendAsync(url, null, true).ConfigureAwait(false); }   // gh may have refreshed its token
            using (resp)
            {
                string? Header(string name) => resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
                if (int.TryParse(Header("X-RateLimit-Remaining"), out int rem)) RateRemaining = rem;
                if (resp.StatusCode == HttpStatusCode.NotModified && cached.items != null) { all.AddRange(cached.items); url = cached.next; continue; }
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    var ra = resp.Headers.RetryAfter;
                    TimeSpan? retryAfter = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null);
                    string? body = code == 403 ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : null;   // only searched for "rate limit", never logged
                    throw new GitHubException(code, "HTTP " + code, GitHubLogic.RateLimitWait(code, Header("X-RateLimit-Remaining"), Header("X-RateLimit-Reset"), retryAfter, body, DateTimeOffset.UtcNow));
                }
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
    /// <summary>Set when GitHub is rate limiting: how long to wait before asking again.</summary>
    public TimeSpan? RetryAfter { get; }
    public bool RateLimited => RetryAfter != null;
    public GitHubException(int status, string msg, TimeSpan? retryAfter = null) : base(msg) { Status = status; RetryAfter = retryAfter; }
}

// ---------------- small helpers (also used by the Atlassian widget) ----------------

/// <summary>
/// Runs a small console tool (gh, whoami) and returns its exit code and trimmed output, or null when it did not finish in time. Both pipes are drained
/// asynchronously (a full stderr pipe would otherwise block the tool), and a tool that hangs (gh waiting on a keyring, whoami on a domain controller) is
/// killed with its children: reading stdout to the end before waiting made the timeout useless and left the background refresh stuck for good.
/// </summary>
internal static class ConsoleTool
{
    public static (int exit, string output)? Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();   // drained, not used
        if (!p.WaitForExit(timeoutMs) || !output.Wait(1000))   // the second wait: a child that inherited the pipe can keep it open after the tool exits
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        return (p.ExitCode, output.Result.Trim());
    }
}

/// <summary>The small state files (ids and timestamps only).</summary>
internal static class StateFile
{
    /// <summary>
    /// Write to a temporary file, flush it to disk, then replace: a crash or power cut mid-write leaves the previous file instead of a truncated one
    /// (a corrupt GitHub state re-seeds silently and so swallows the toast of an alert that appeared meanwhile).
    /// </summary>
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.UTF8.GetBytes(text));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// The ids to keep, oldest first: every id in <paramref name="keep"/> (for GitHub the alerts open now: dropping one of those announced it again on the next
    /// refresh once more than <paramref name="max"/> were open), then the most recent others up to <paramref name="max"/>. The order comes from the list; the
    /// order of a HashSet is undefined.
    /// </summary>
    public static List<string> Remember(IEnumerable<string> previous, IEnumerable<string> keep, int max)
    {
        var order = new List<string>();
        var known = new HashSet<string>();
        var must = new HashSet<string>(keep);
        foreach (var id in previous.Concat(must)) if (known.Add(id)) order.Add(id);
        var others = order.Where(id => !must.Contains(id)).ToList();
        var recent = new HashSet<string>(others.Skip(Math.Max(0, others.Count - Math.Max(0, max - must.Count))));
        return order.Where(id => must.Contains(id) || recent.Contains(id)).ToList();
    }
}
