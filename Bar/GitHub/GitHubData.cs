using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Spread;

/// <summary>
/// Dependabot alerts of one team's repositories, kept fresh in the background. Read-only (GET), RAM only apart from the ids of alerts already announced.
/// Status: ok | no-gh | auth | offline | error.
/// </summary>
internal static class GitHubData
{
    sealed class State { public bool Initialized { get; set; } public List<string> Seen { get; set; } = new(); }

    public static string Status { get; private set; } = "starting";
    public static List<AlertLite> Alerts { get; private set; } = new();
    public static List<RepoRef> Repos { get; private set; } = new();
    public static int NoAccess { get; private set; }
    public static DateTime? LastOk { get; private set; }
    public static DateTime? LastErrorAt { get; private set; }
    public static string LastError { get; private set; } = "";
    public static event Action? Changed;

    static GitHubClient? _client;
    static long _next;
    static int _busy;
    static DateTime _reposAt = DateTime.MinValue;
    static string StatePath => Path.Combine(Config.Dir, "github-state.json");

    static void Raise() => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));

    public static void RefreshNow() => _next = 0;

    /// <summary>After the settings changed: forget the data and look again. With <paramref name="reseed"/> (another team) the alerts that already exist are remembered silently.</summary>
    public static void Reset(bool reseed)
    {
        _client = null; Repos = new(); Alerts = new(); NoAccess = 0; _reposAt = DateTime.MinValue; Status = "starting";
        if (reseed) { try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { } }
        _next = 0;
        Raise();
    }

    /// <summary>For the Test button: is gh signed in, and which repositories does this team have?</summary>
    public static async Task<string> TestAsync(GitHubConfig c)
    {
        try
        {
            if (GitHubAuth.Token(c, true) == null) return GitHubAuth.HasGh(c) ? "gh is installed but not signed in: run  gh auth login" : "GitHub CLI (gh) was not found.";
            var client = new GitHubClient(c);
            if (c.Repos.Length > 0) return $"Signed in via gh · {c.Repos.Length} fixed repositor{(c.Repos.Length == 1 ? "y" : "ies")}.";
            string q = Uri.EscapeDataString($"org:{c.Org} props.{c.PropertyName}:{c.PropertyValue}");
            var found = await client.GetAllAsync($"/search/repositories?q={q}&per_page=100", "items");
            int active = found.Count(x => !(x.TryGetProperty("archived", out var ar) && ar.ValueKind == JsonValueKind.True));
            return found.Count == 0 ? $"Signed in via gh · no repositories have {c.PropertyName} = {c.PropertyValue} in {c.Org}." : $"Signed in via gh · {found.Count} repositories ({active} active) have {c.PropertyName} = {c.PropertyValue}.";
        }
        catch (GitHubException e) { return e.Status == 401 ? "GitHub rejected the token (is it authorised for the organisation?)." : "GitHub answered HTTP " + e.Status + "."; }
        catch (Exception e) { return "Could not reach GitHub (" + e.GetType().Name + ")."; }
    }

    public static void Tick(Config cfg)
    {
        if (!cfg.GitHub.Enabled) return;
        if (Environment.TickCount64 < _next) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        Task.Run(async () =>
        {
            try { await RefreshAsync(cfg); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    static void Fail(string status, string error)
    {
        Status = status; LastError = error; LastErrorAt = DateTime.Now;
        _next = Environment.TickCount64 + (status == "no-gh" || status == "auth" ? 120_000 : 60_000);
    }

    static async Task RefreshAsync(Config cfg)
    {
        var c = cfg.GitHub;
        try
        {
            if (GitHubAuth.Token(c) == null) { Fail(GitHubAuth.HasGh(c) ? "auth" : "no-gh", "no token"); Raise(); return; }
            _client ??= new GitHubClient(c);

            // the team's repositories (cached; they change rarely)
            if (c.Repos.Length > 0) Repos = c.Repos.Select(r => new RepoRef(r, false)).ToList();
            else if (Repos.Count == 0 || DateTime.Now - _reposAt > TimeSpan.FromHours(6))
            {
                string q = Uri.EscapeDataString($"org:{c.Org} props.{c.PropertyName}:{c.PropertyValue}");
                var found = await _client.GetAllAsync($"/search/repositories?q={q}&per_page=100", "items");
                Repos = found.Select(x => new RepoRef(x.GetProperty("full_name").GetString() ?? "", x.TryGetProperty("archived", out var ar) && ar.ValueKind == JsonValueKind.True)).Where(r => r.FullName.Length > 0).ToList();
                _reposAt = DateTime.Now;
            }

            // their open alerts, a few repositories at a time
            var sev = Uri.EscapeDataString(string.Join(",", c.ListSeverities));
            var list = new List<AlertLite>();
            int denied = 0; bool authFailed = false;
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(Repos.Where(r => !r.Archived).Select(async r =>
            {
                await gate.WaitAsync();
                try
                {
                    var items = await _client.GetAllAsync($"/repos/{r.FullName}/dependabot/alerts?state=open&severity={sev}&per_page=100");
                    var parsed = items.Select(i => GitHubLogic.ParseAlert(i, r.FullName)).Where(a => a != null).Select(a => a!).ToList();
                    lock (list) list.AddRange(parsed);
                }
                catch (GitHubException e) when (e.Status == 401) { authFailed = true; }
                catch (GitHubException e) when (e.Status is 403 or 404) { Interlocked.Increment(ref denied); }
                finally { gate.Release(); }
            }));
            if (authFailed) { Fail("auth", "HTTP 401"); Log.Write("github: token rejected"); Raise(); return; }

            Alerts = list.OrderByDescending(a => a.Severity == "critical").ThenBy(a => a.Created).ToList();
            NoAccess = denied;
            Status = "ok"; LastOk = DateTime.Now;
            _next = Environment.TickCount64 + Math.Max(1, c.RefreshMinutes) * 60_000L;
            Announce(cfg);
        }
        catch (GitHubException e) when (e.Status == 401) { Fail("auth", "HTTP 401"); }
        catch (GitHubException e) { Fail("error", "HTTP " + e.Status); Log.Write("github: HTTP " + e.Status); }
        catch (HttpRequestException) { Fail("offline", "network"); }
        catch (TaskCanceledException) { Fail("offline", "timeout"); }
        catch (Exception e) { Fail("error", e.GetType().Name); Log.Write("github: " + e.GetType().Name); }
        Raise();
    }

    // ---------------- new alerts -> sticky toast ----------------

    static State LoadState()
    {
        try { if (File.Exists(StatePath)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State(); } catch { }
        return new State();
    }

    static void Announce(Config cfg)
    {
        var st = LoadState();
        var seen = new HashSet<string>(st.Seen);
        var toastSev = cfg.GitHub.Toast.OnHigh ? new[] { "critical", "high" } : new[] { "critical" };
        var fresh = GitHubLogic.NewAlerts(Alerts, seen, toastSev, !st.Initialized);
        foreach (var a in Alerts) seen.Add(a.Id);   // everything present is "known" now (so enabling OnHigh later does not flood)
        st.Initialized = true; st.Seen = seen.TakeLast(500).ToList();
        try { Directory.CreateDirectory(Config.Dir); File.WriteAllText(StatePath, JsonSerializer.Serialize(st)); } catch { }
        if (!cfg.GitHub.Toast.Enabled || fresh.Count == 0) return;
        var (title, body, footer, url) = GitHubLogic.ToastText(fresh, cfg.GitHub);
        string id = fresh.Count == 1 ? "gh:" + fresh[0].Id : "gh:group:" + string.Join(",", fresh.Select(f => f.Id)).GetHashCode();
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ToastManager.Show(new ToastSpec(id, ToastKind.Security, title, body, footer, url, Sticky: true))));
    }

    public static string HealthText(Config cfg) => Status switch
    {
        "ok" => $"updated {LastOk:HH:mm}" + (NoAccess > 0 ? $" · {NoAccess} repo{(NoAccess == 1 ? "" : "s")} not readable" : ""),
        "no-gh" => "GitHub CLI (gh) not found - install it or set GitHub.GhPath",
        "auth" => "not signed in to GitHub - run: gh auth login",
        "offline" => $"offline since {LastErrorAt:HH:mm}" + (LastOk != null ? $" · data from {LastOk:HH:mm}" : ""),
        "error" => $"GitHub error ({LastError})" + (LastOk != null ? $" · data from {LastOk:HH:mm}" : ""),
        _ => "starting…"
    };
}
