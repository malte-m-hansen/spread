using System;
using System.Collections.Generic;
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
        Environment.SetEnvironmentVariable("SPREAD_GITHUB_TOKEN", "test-token-0123456789-0123456789");
        var client = new GitHubClient(cfg);
        var http = (HttpClient)typeof(GitHubClient).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
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
        Environment.SetEnvironmentVariable("SPREAD_GITHUB_TOKEN", null);

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
