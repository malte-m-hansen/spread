using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Spread;

/// <summary>
/// `--github-test`: parsing, new-alert logic, sticky toasts, GET-only guard with synthetic data. `--live` asks GitHub with your gh login (read-only) and prints
/// counts and timing only. `--show` shows a sample sticky toast.
/// </summary>
internal static class GitHubTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    const string AlertJson = "{\"number\":398,\"state\":\"open\",\"html_url\":\"https://github.com/Contoso/sample-api/security/dependabot/398\",\"created_at\":\"2026-11-06T09:26:17Z\"," +
        "\"dependency\":{\"package\":{\"ecosystem\":\"npm\",\"name\":\"lodash\"},\"manifest_path\":\"package.json\",\"scope\":\"runtime\"}," +
        "\"security_advisory\":{\"ghsa_id\":\"GHSA-xxxx-yyyy-zzzz\",\"cve_id\":\"CVE-2026-0001\",\"summary\":\"Prototype pollution in lodash\",\"severity\":\"critical\"}}";

    static AlertLite A(string repo, int n, string sev) => new(repo, n, sev, "pkg", "npm", "package.json", "runtime", "summary " + n, "GHSA-1", "", DateTime.Now.AddDays(-3), $"https://github.com/{repo}/security/dependabot/{n}");

    sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls, NotModified;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Headers.TryGetValues("If-None-Match", out var v) && v.First() == "\"abc\"") { NotModified++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            bool second = request.RequestUri!.Query.Contains("after=");
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(second ? "[" + AlertJson + "]" : "[" + AlertJson + "," + AlertJson + "]", Encoding.UTF8, "application/json") };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
            if (!second) r.Headers.Add("Link", "<https://api.github.com/repos/Contoso/x/dependabot/alerts?per_page=2&after=CUR>; rel=\"next\"");
            r.Headers.Add("X-RateLimit-Remaining", "4321");
            return Task.FromResult(r);
        }
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("GitHub checks (synthetic data):");
        using var doc = JsonDocument.Parse(AlertJson);
        var a = GitHubLogic.ParseAlert(doc.RootElement, "Contoso/sample-api");
        Check("alert parsed", a is { Number: 398, Severity: "critical", Package: "lodash", Ecosystem: "npm", Cve: "CVE-2026-0001", Ghsa: "GHSA-xxxx-yyyy-zzzz" } && a.Url.EndsWith("/dependabot/398") && a.Id == "Contoso/sample-api#398" && a.ShortRepo == "sample-api");
        Check("garbage alert is ignored", GitHubLogic.ParseAlert(JsonDocument.Parse("{\"x\":1}").RootElement, "r") == null);
        Check("link header: next cursor", GitHubLogic.NextLink("<https://api.github.com/a?after=X>; rel=\"next\", <https://api.github.com/a?after=Z>; rel=\"last\"") == "https://api.github.com/a?after=X" && GitHubLogic.NextLink("<u>; rel=\"prev\"") == null && GitHubLogic.NextLink(null) == null);

        var cfg = new GitHubConfig();
        Check("web link equals the one you use", GitHubLogic.WebUrl(cfg) == "https://github.com/orgs/Contoso/security/alerts/dependabot?q=is%3Aopen+severity%3Acritical+props.ProductTeam%3ATEAM-1", GitHubLogic.WebUrl(cfg));
        Check("bar count uses the configured severities", GitHubLogic.Count(new[] { A("L/a", 1, "critical"), A("L/a", 2, "high"), A("L/b", 1, "critical") }, new[] { "critical" }) == 2 && GitHubLogic.Count(new[] { A("L/a", 1, "high") }, new[] { "critical", "high" }) == 1);

        // new alerts
        var seen = new HashSet<string>();
        var now = new[] { A("L/a", 1, "critical"), A("L/a", 2, "high") };
        Check("first run: nothing is announced but everything is remembered", GitHubLogic.NewAlerts(now, seen, new[] { "critical" }, true).Count == 0 && seen.Contains("L/a#1"));
        Check("same alerts later: nothing new", GitHubLogic.NewAlerts(now, seen, new[] { "critical" }, false).Count == 0);
        var later = now.Concat(new[] { A("L/b", 7, "critical"), A("L/b", 8, "critical"), A("L/b", 9, "high") }).ToArray();
        var fresh = GitHubLogic.NewAlerts(later, seen, new[] { "critical" }, false);
        Check("two new criticals found (high ignored)", fresh.Count == 2 && fresh.All(f => f.Severity == "critical"));
        Check("... and only once", GitHubLogic.NewAlerts(later, seen, new[] { "critical" }, false).Count == 0);
        var gone = new HashSet<string>(seen); gone.Remove("L/a#1");
        Check("an alert that is not remembered is announced again", GitHubLogic.NewAlerts(later, gone, new[] { "critical" }, false).Count == 1);
        var (t1, b1, f1, u1) = GitHubLogic.ToastText(new List<AlertLite> { A("L/sample-api", 5, "critical") }, cfg);
        Check("toast text for one alert", t1.Contains("critical") && b1.Contains("pkg in L/sample-api") && u1!.EndsWith("/dependabot/5") && f1.StartsWith("TEAM-1"));
        var (t2, b2, _, u2) = GitHubLogic.ToastText(fresh, cfg);
        Check("toast text for several alerts is grouped", t2.StartsWith("2 new critical") && u2 == GitHubLogic.WebUrl(cfg, new[] { "critical" }) && b2.Contains("b"), t2);
        Check("age text", GitHubLogic.Age(DateTime.Now.AddDays(-5), DateTime.Now) == "5 d" && GitHubLogic.Age(DateTime.Now.AddHours(-3), DateTime.Now) == "3 h" && GitHubLogic.Age(DateTime.Now, DateTime.Now) == "new");

        // settings validation
        var ok = new GitHubLogic.SettingsInput("Contoso", "ProductTeam", "TEAM-1", "", "critical", "critical, high", "10", "0", "0", "");
        Check("settings: valid input", GitHubLogic.Validate(ok).Count == 0, string.Join("; ", GitHubLogic.Validate(ok)));
        Check("settings: bad organisation", GitHubLogic.Validate(ok with { Org = "not valid!" }).Count == 1);
        Check("settings: team id needed unless repositories are listed", GitHubLogic.Validate(ok with { PropertyValue = "" }).Count == 1 && GitHubLogic.Validate(ok with { PropertyValue = "", PropertyName = "", Repos = "Contoso/a, Contoso/b" }).Count == 0);
        Check("settings: repository format", GitHubLogic.Validate(ok with { Repos = "justaname" }).Count == 1);
        Check("settings: unknown severity / empty bar severities", GitHubLogic.Validate(ok with { BarSeverities = "urgent" }).Count == 1 && GitHubLogic.Validate(ok with { BarSeverities = "" }).Count == 1);
        Check("settings: numbers", GitHubLogic.Validate(ok with { RefreshMinutes = "0", SlaDays = "-1", ToastSeconds = "abc" }).Count == 3);
        Check("settings: missing gh.exe path", GitHubLogic.Validate(ok with { GhPath = @"C:\nope\gh.exe" }).Count == 1);

        // sticky toasts
        var q = new ToastQueue { Capacity = 3, Seconds = 10, StickySeconds = 0 };
        q.Enqueue(new ToastSpec("s1", ToastKind.Security, "t", "b", "f", null, true), 0);
        q.Enqueue(new ToastSpec("n1", ToastKind.Jira, "t", "b", "f", null), 0);
        q.Tick(0); q.Tick(100000);
        Check("sticky toast never expires, normal one does", q.Visible.Count == 1 && q.Visible[0].Spec.Id == "s1");
        for (int i = 2; i <= 5; i++) q.Enqueue(new ToastSpec("s" + i, ToastKind.Security, "t", "b", "f", null, true), 100000);
        q.Tick(100001);
        Check("three sticky toasts fill the stack, the rest waits", q.Visible.Count == 3 && q.Waiting == 2);
        q.Dismiss(q.Visible[0]); q.Tick(100002);
        Check("closing one lets the next in", q.Visible.Count == 3 && q.Waiting == 1);
        var qs = new ToastQueue { Capacity = 3, Seconds = 10, StickySeconds = 30 };
        qs.Enqueue(new ToastSpec("s", ToastKind.Security, "t", "b", "f", null, true), 0); qs.Tick(0); qs.Tick(31);
        Check("sticky with a time limit goes away after it", qs.Visible.Count == 0);

        // GET-only client
        string oldLevel = Log.Level;
        Log.Level = "off";   // nothing of the checks below may end up in the real spread.log
        Environment.SetEnvironmentVariable("SPREAD_GITHUB_TOKEN", "test-token-0123456789-0123456789");
        var client = new GitHubClient(cfg);
        var http = client.HttpForTests;
        bool post = false, host = false;
        try { http.PostAsync("https://api.github.com/repos/x/y/issues", new StringContent("{}")).GetAwaiter().GetResult(); } catch (InvalidOperationException) { post = true; } catch { }
        try { http.GetAsync("https://evil.example.com/").GetAwaiter().GetResult(); } catch (InvalidOperationException) { host = true; } catch { }
        Check("client refuses POST", post); Check("client refuses other hosts", host);
        // paging + ETag with a fake server
        var fake = new FakeHandler();
        var fc = new GitHubClient(cfg, fake);
        var all = fc.GetAllAsync("/repos/Contoso/x/dependabot/alerts?per_page=2").GetAwaiter().GetResult();
        Check("two pages are followed", all.Count == 3 && fake.Calls == 2, $"{all.Count} items, {fake.Calls} calls");
        var again = fc.GetAllAsync("/repos/Contoso/x/dependabot/alerts?per_page=2").GetAwaiter().GetResult();
        Check("second time: 304 answers cost nothing and give the same items", again.Count == 3 && fake.NotModified == 2 && fc.RateRemaining == 4321, $"{again.Count} items, {fake.NotModified} not-modified");

        HostChecks();
        RateLimitChecks();
        ConsoleToolChecks();
        StateFileChecks();
        ParallelEtagCheck();
        RefreshChecks();

        Environment.SetEnvironmentVariable("SPREAD_GITHUB_TOKEN", null);
        Log.Level = oldLevel;

        if (args.Contains("--live")) _fail += Live(new Config().GitHub).GetAwaiter().GetResult();
        Console.WriteLine(_fail == 0 ? "GITHUB TEST PASSED" : $"GITHUB TEST FAILED ({_fail})");
        if (_fail != 0 || !args.Contains("--show")) return _fail == 0 ? 0 : 1;

        Console.WriteLine("Showing a sample sticky toast; close it with the x (the test ends after 25 s) ...");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var c = new Config();
        ToastManager.Init(c, () => false);
        ToastManager.Show(new ToastSpec("gh:sample", ToastKind.Security, "New critical vulnerability", "lodash in Contoso/sample-repo: Prototype pollution in lodash", "TEAM-1 · CVE-2026-0001 · " + DateTime.Now.ToString("HH:mm"), "https://example.com", Sticky: true));
        var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        stop.Tick += (_, _) => app.Shutdown();
        stop.Start();
        app.Run();
        return 0;
    }

    /// <summary>A fake GitHub: answers from a function, records the urls, and can hold requests (to Reset while a refresh is in flight).</summary>
    sealed class Router : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public volatile TaskCompletionSource? Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Urls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            var hold = Hold;
            if (hold != null) { Entered.TrySetResult(); await hold.Task; }
            return Answer(request);
        }
    }

    static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static GitHubException? CatchGh(Func<Task> f) { try { f().GetAwaiter().GetResult(); return null; } catch (GitHubException e) { return e; } }

    static void HostChecks()
    {
        Check("host: github.com", GitHubLogic.WebHost("api.github.com") == "github.com" && GitHubLogic.ApiBase("api.github.com") == "https://api.github.com" && GitHubLogic.ApiHostName("api.github.com") == "api.github.com");
        Check("host: GitHub Enterprise Server (host/api/v3)", GitHubLogic.WebHost("ghe.example.com/api/v3") == "ghe.example.com" && GitHubLogic.ApiHostName("ghe.example.com/api/v3") == "ghe.example.com"
            && GitHubLogic.ApiBase("https://ghe.example.com:8443/api/v3/") == "https://ghe.example.com:8443/api/v3", GitHubLogic.ApiBase("https://ghe.example.com:8443/api/v3/"));
        Check("host: GHE.com (api.X.ghe.com)", GitHubLogic.WebHost("api.acme.ghe.com") == "acme.ghe.com");
        Check("host: anything else falls back to api.github.com", GitHubLogic.ApiHostName("bad host!") == "api.github.com" && GitHubLogic.ApiBase("") == "https://api.github.com" && GitHubLogic.WebHost(null) == "github.com");
        var ghes = new GitHubConfig { Host = "ghe.example.com/api/v3" };
        Check("host: web links follow the host", GitHubLogic.WebUrl(ghes).StartsWith("https://ghe.example.com/orgs/Contoso/security/alerts/dependabot?q="), GitHubLogic.WebUrl(ghes));
        var r = new Router { Answer = _ => Json("[]") };
        new GitHubClient(ghes, r).GetAllAsync("/repos/Contoso/x/dependabot/alerts?per_page=100").GetAwaiter().GetResult();
        Check("host: API calls go to host/api/v3", r.Urls.SequenceEqual(new[] { "https://ghe.example.com/api/v3/repos/Contoso/x/dependabot/alerts?per_page=100" }), string.Join(" ", r.Urls));
        bool blocked = false;
        try { new GitHubClient(ghes).HttpForTests.GetAsync("https://api.github.com/").GetAwaiter().GetResult(); } catch (InvalidOperationException) { blocked = true; } catch { }
        Check("host: the guard is locked to the configured host", blocked);
    }

    static void RateLimitChecks()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var w = GitHubLogic.RateLimitWait(403, "0", now.AddMinutes(10).ToUnixTimeSeconds().ToString(), null, "{\"message\":\"API rate limit exceeded\"}", now);
        Check("rate limit: 403 + remaining 0 waits for the reset", w != null && Math.Abs(w.Value.TotalMinutes - 10) < 0.01, w?.ToString() ?? "null");
        Check("rate limit: secondary (retry-after or the message)", GitHubLogic.RateLimitWait(403, "4000", null, TimeSpan.FromSeconds(120), null, now) == TimeSpan.FromMinutes(2)
            && GitHubLogic.RateLimitWait(403, null, null, null, "{\"message\":\"You have exceeded a secondary rate limit.\"}", now) == TimeSpan.FromMinutes(1));
        Check("rate limit: 429 always, capped at an hour", GitHubLogic.RateLimitWait(429, null, null, null, null, now) == TimeSpan.FromMinutes(1) && GitHubLogic.RateLimitWait(429, null, null, TimeSpan.FromHours(5), null, now) == TimeSpan.FromHours(1));
        Check("rate limit: a permission 403 or a 404 is not one", GitHubLogic.RateLimitWait(403, "4000", null, null, "{\"message\":\"Dependabot alerts are disabled for this repository.\"}", now) == null
            && GitHubLogic.RateLimitWait(404, "0", null, null, null, now) == null);
        var r = new Router();
        r.Answer = _ =>
        {
            var m = Json("{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden);
            m.Headers.Add("X-RateLimit-Remaining", "0"); m.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString());
            return m;
        };
        var e1 = CatchGh(() => new GitHubClient(new GitHubConfig(), r).GetAllAsync("/repos/Contoso/x/dependabot/alerts"));
        r.Answer = _ => Json("{\"message\":\"Dependabot alerts are disabled for this repository.\"}", HttpStatusCode.Forbidden);
        var e2 = CatchGh(() => new GitHubClient(new GitHubConfig(), r).GetAllAsync("/repos/Contoso/x/dependabot/alerts"));
        Check("rate limit: the client tells a rate-limit 403 from a permission 403", e1 is { Status: 403, RateLimited: true } && e1.RetryAfter!.Value.TotalMinutes > 3 && e2 is { Status: 403, RateLimited: false });
    }

    static void ConsoleToolChecks()
    {
        var r = ConsoleTool.Run("cmd.exe", new[] { "/c", "echo hello" }, 10000);
        Check("console tool: output and exit code", r != null && r.Value.exit == 0 && r.Value.output == "hello", r?.ToString() ?? "null");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flood = ConsoleTool.Run("cmd.exe", new[] { "/c", "for /l %i in (1,1,3000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2" }, 20000);
        Check("console tool: a chatty stderr (~180 KB) does not block the tool", flood != null && flood.Value.exit == 0, $"{flood?.ToString() ?? "null"} after {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var hung = ConsoleTool.Run("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, 800);
        Check("console tool: a tool that hangs is killed after the timeout", hung == null && sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    static void StateFileChecks()
    {
        static string[] Ids(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Check("remember: open ids are kept, then the newest others, in order", string.Join(",", StateFile.Remember(Ids("a,b,c"), Ids("c,d"), 3)) == "b,c,d", string.Join(",", StateFile.Remember(Ids("a,b,c"), Ids("c,d"), 3)));
        var many = Enumerable.Range(0, 600).Select(i => "L/r#" + i).ToList();
        var kept = StateFile.Remember(many, many.Take(550), 500);
        Check("remember: more open alerts than the cap are all kept (else they were announced again)", kept.Count == 550 && many.Take(550).All(kept.Contains), kept.Count.ToString());
        Check("remember: duplicates go, the cap holds, the newest stay", string.Join(",", StateFile.Remember(Ids("a,a,b,c"), Array.Empty<string>(), 2)) == "b,c");
        var open = many.Select(id => A(id.Split('#')[0], int.Parse(id.Split('#')[1]), "critical")).ToList();
        var reloaded = new HashSet<string>(StateFile.Remember(Array.Empty<string>(), open.Select(a => a.Id), 500));
        Check("remember + new alerts: 600 open criticals are not announced again", GitHubLogic.NewAlerts(open, reloaded, new[] { "critical" }, false).Count == 0);

        string dir = Path.Combine(Path.GetTempPath(), "spread-gh-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string f = Path.Combine(dir, "state.json");
            StateFile.Write(f, "{\"a\":1}"); StateFile.Write(f, "{\"a\":2}");
            Check("state file: replaced in one piece, no temporary file left", File.ReadAllText(f) == "{\"a\":2}" && !File.Exists(f + ".tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void ParallelEtagCheck()
    {
        var r = new Router();
        r.Answer = q =>
        {
            if (q.Headers.TryGetValues("If-None-Match", out var v) && v.First() == "\"e\"") return new HttpResponseMessage(HttpStatusCode.NotModified);
            var m = Json("[" + AlertJson + "]"); m.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""); return m;
        };
        var c = new GitHubClient(new GitHubConfig(), r);
        bool ok = true;
        for (int round = 0; round < 2; round++)
        {
            var results = Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => c.GetAllAsync($"/repos/Contoso/r{i}/dependabot/alerts")))).GetAwaiter().GetResult();
            ok &= results.All(x => x.Count == 1);
        }
        Check("ETag cache: 64 repositories in parallel, twice, through one client", ok && r.Urls.Count == 128);
    }

    /// <summary>GitHubData end to end with a fake GitHub; the state file goes to a temporary folder.</summary>
    static void RefreshChecks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spread-gh-test-" + Guid.NewGuid().ToString("N"));
        var oldFactory = GitHubData.NewClient;
        var r = new Router();
        GitHubData.StateDirOverride = dir;
        GitHubData.NewClient = c => new GitHubClient(c, r);
        try
        {
            var cfg = new Config();
            cfg.GitHub.Repos = new[] { "Contoso/a", "Contoso/b" };
            string state = Path.Combine(dir, "github-state.json");
            static HttpResponseMessage Ok() => Json("[" + AlertJson + "]");
            static HttpResponseMessage Refused(HttpStatusCode code, string body = "{}", bool exhausted = false)
            {
                var m = Json(body, code);
                if (exhausted) { m.Headers.Add("X-RateLimit-Remaining", "0"); m.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString()); }
                return m;
            }
            static bool IsB(HttpRequestMessage q) => q.RequestUri!.AbsolutePath.Contains("/Contoso/b/");
            void Refresh() => GitHubData.RefreshAsync(cfg).GetAwaiter().GetResult();

            GitHubData.Reset(false);
            r.Answer = _ => Ok();
            Refresh();
            Check("refresh: two repositories, two alerts, ids remembered", GitHubData.Status == "ok" && GitHubData.Alerts.Count == 2 && File.Exists(state) && File.ReadAllText(state).Contains("Contoso/b#398"), GitHubData.HealthText(cfg));

            r.Answer = q => IsB(q) ? Refused(HttpStatusCode.BadGateway) : Ok();
            Refresh();
            Check("refresh: a 5xx for one repository keeps its alerts, the rest updates", GitHubData.Status == "ok" && GitHubData.Alerts.Count == 2 && GitHubData.NotUpdated == 1 && GitHubData.HealthText(cfg).Contains("1 repo not updated"), GitHubData.HealthText(cfg));

            r.Answer = q => IsB(q) ? Refused(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}", exhausted: true) : Ok();
            Refresh();
            Check("refresh: rate limited = error that keeps the data (not 'not readable')", GitHubData.Status == "error" && GitHubData.LastError == "rate limited" && GitHubData.Alerts.Count == 2 && GitHubData.NoAccess == 0, GitHubData.HealthText(cfg));

            r.Answer = q => IsB(q) ? Refused(HttpStatusCode.Forbidden, "{\"message\":\"Dependabot alerts are disabled for this repository.\"}") : Ok();
            Refresh();
            Check("refresh: a permission 403 still counts as not readable", GitHubData.Status == "ok" && GitHubData.Alerts.Count == 1 && GitHubData.NoAccess == 1, GitHubData.HealthText(cfg));

            // Reset (another team) while a refresh is in flight: its answers must not land, nor its state file after the reseed
            r.Answer = _ => Ok();
            r.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            r.Hold = hold;
            var t = Task.Run(() => GitHubData.RefreshAsync(cfg));
            bool entered = r.Entered.Task.Wait(10000);
            GitHubData.Reset(reseed: true);
            r.Hold = null; hold.SetResult();
            t.GetAwaiter().GetResult();
            Check("refresh: results of a refresh started before Reset are discarded", entered && GitHubData.Alerts.Count == 0 && GitHubData.Repos.Count == 0 && GitHubData.Status == "starting" && !File.Exists(state),
                $"entered={entered} {GitHubData.Alerts.Count} alerts, {GitHubData.Status}, state file {File.Exists(state)}");
            Refresh();
            Check("refresh: the next one works (first run after the reseed: remembered, not toasted)", GitHubData.Status == "ok" && GitHubData.Alerts.Count == 2 && File.Exists(state));
        }
        finally
        {
            GitHubData.NewClient = oldFactory; GitHubData.Reset(false); GitHubData.StateDirOverride = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static async Task<int> Live(GitHubConfig c)
    {
        Console.WriteLine("Live check with your gh login (read-only, counts only):");
        bool gh = GitHubAuth.HasGh(c);
        string? token = GitHubAuth.Token(c);
        Console.WriteLine($"  gh found: {gh}, token obtained: {token != null} (length {token?.Length ?? 0}, not printed)");
        if (token == null) return 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client = new GitHubClient(c);
        string q = Uri.EscapeDataString($"org:{c.Org} props.{c.PropertyName}:{c.PropertyValue}");
        var found = await client.GetAllAsync($"/search/repositories?q={q}&per_page=100", "items");
        var repos = found.Select(x => (name: x.GetProperty("full_name").GetString() ?? "", archived: x.GetProperty("archived").GetBoolean())).ToList();
        Console.WriteLine($"  repositories with {c.PropertyName}={c.PropertyValue}: {repos.Count} ({repos.Count(r => !r.archived)} active)");
        var list = new List<AlertLite>(); int denied = 0;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(repos.Where(r => !r.archived).Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                var items = await client.GetAllAsync($"/repos/{r.name}/dependabot/alerts?state=open&severity={Uri.EscapeDataString(string.Join(",", c.ListSeverities))}&per_page=100");
                lock (list) list.AddRange(items.Select(i => GitHubLogic.ParseAlert(i, r.name)).Where(x => x != null).Select(x => x!));
            }
            catch (GitHubException) { Interlocked.Increment(ref denied); }
            finally { gate.Release(); }
        }));
        Console.WriteLine($"  open alerts: critical={list.Count(x => x.Severity == "critical")}, high={list.Count(x => x.Severity == "high")} in {list.Select(x => x.Repo).Distinct().Count()} repos; repos not readable: {denied}");
        Console.WriteLine($"  all urls are https on github.com: {list.All(x => x.Url.StartsWith("https://github.com/"))}; time {sw.ElapsedMilliseconds} ms; rate limit remaining {client.RateRemaining}");
        return 0;
    }
}
