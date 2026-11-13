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
    /// <summary>Repositories GitHub failed for (5xx) in the last refresh: their alerts from the refresh before are shown.</summary>
    public static int NotUpdated { get; private set; }
    public static DateTime? LastOk { get; private set; }
    public static DateTime? LastErrorAt { get; private set; }
    public static string LastError { get; private set; } = "";
    public static event Action? Changed;

    static GitHubClient? _client;
    static long _next;
    static int _busy, _gen;
    static DateTime _reposAt = DateTime.MinValue;
    static HashSet<string> _readRepos = new();   // repositories whose alerts are in Alerts (read at least once since the last reset)
    static readonly object Gate = new();
    static string StatePath => Path.Combine(StateDirOverride ?? Config.Dir, "github-state.json");

    /// <summary>Test hooks: the offline tests answer with a fake handler and keep their state file out of %APPDATA%.</summary>
    internal static string? StateDirOverride;
    internal static Func<GitHubConfig, GitHubClient> NewClient = c => new GitHubClient(c);

    static void Raise() => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));

    public static void RefreshNow() => _next = 0;

    /// <summary>
    /// Applies results of the refresh started at generation <paramref name="gen"/>, unless Reset ran since: the settings are edited in place, so a refresh still
    /// running for the old team must not land its repositories, alerts or state file afterwards (it also kept the old team's repositories for 6 hours).
    /// </summary>
    static bool Commit(int gen, Action apply)
    {
        lock (Gate)
        {
            if (gen != _gen) return false;
            apply();
            return true;
        }
    }

    /// <summary>After the settings changed: forget the data and look again. With <paramref name="reseed"/> (another team) the alerts that already exist are remembered silently.</summary>
    public static void Reset(bool reseed)
    {
        lock (Gate)
        {
            _gen++;
            _client = null; Repos = new(); Alerts = new(); NoAccess = 0; NotUpdated = 0; _readRepos = new(); _reposAt = DateTime.MinValue; Status = "starting";
            if (reseed) { try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { } }
            _next = 0;
        }
        Raise();
    }

    /// <summary>For the Test button: is gh signed in, and which repositories does this team have?</summary>
    public static async Task<string> TestAsync(GitHubConfig c)
    {
        try
        {
            if (await Task.Run(() => GitHubAuth.Token(c, true)) == null) return GitHubAuth.HasGh(c) ? "gh is installed but not signed in: run  gh auth login" : "GitHub CLI (gh) was not found.";   // gh runs off the UI thread
            var client = new GitHubClient(c);
            if (c.Repos.Length > 0) return $"Signed in via gh · {c.Repos.Length} fixed repositor{(c.Repos.Length == 1 ? "y" : "ies")}.";
            string q = Uri.EscapeDataString($"org:{c.Org} props.{c.PropertyName}:{c.PropertyValue}");
            var found = await client.GetAllAsync($"/search/repositories?q={q}&per_page=100", "items");
            int active = found.Count(x => !(x.TryGetProperty("archived", out var ar) && ar.ValueKind == JsonValueKind.True));
            return found.Count == 0 ? $"Signed in via gh · no repositories have {c.PropertyName} = {c.PropertyValue} in {c.Org}." : $"Signed in via gh · {found.Count} repositories ({active} active) have {c.PropertyName} = {c.PropertyValue}.";
        }
        catch (GitHubException e) { return e.Status == 401 ? "GitHub rejected the token (is it authorised for the organisation?)." : e.RateLimited ? "GitHub's rate limit is reached: try again in a few minutes." : "GitHub answered HTTP " + e.Status + "."; }
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

    static void Fail(int gen, string status, string error, TimeSpan? wait = null) => Commit(gen, () =>
    {
        Status = status; LastError = error; LastErrorAt = DateTime.Now;
        _next = Environment.TickCount64 + (wait != null ? (long)wait.Value.TotalMilliseconds : status == "no-gh" || status == "auth" ? 120_000 : 60_000);
    });

    internal static async Task RefreshAsync(Config cfg)
    {
        var c = cfg.GitHub;
        int gen;
        GitHubClient client;
        lock (Gate) { gen = _gen; client = _client ??= NewClient(c); }   // this refresh's own client: Reset may drop the shared one meanwhile
        try
        {
            if (GitHubAuth.Token(c) == null) { Fail(gen, GitHubAuth.HasGh(c) ? "auth" : "no-gh", "no token"); Raise(); return; }

            // the team's repositories (cached; they change rarely)
            var repos = Repos;
            if (c.Repos.Length > 0) repos = c.Repos.Select(r => new RepoRef(r, false)).ToList();
            else if (repos.Count == 0 || DateTime.Now - _reposAt > TimeSpan.FromHours(6))
            {
                string q = Uri.EscapeDataString($"org:{c.Org} props.{c.PropertyName}:{c.PropertyValue}");
                var found = await client.GetAllAsync($"/search/repositories?q={q}&per_page=100", "items");
                repos = found.Select(x => new RepoRef(x.GetProperty("full_name").GetString() ?? "", x.TryGetProperty("archived", out var ar) && ar.ValueKind == JsonValueKind.True)).Where(r => r.FullName.Length > 0).ToList();
                if (!Commit(gen, () => _reposAt = DateTime.Now)) return;
            }
            if (!Commit(gen, () => Repos = repos)) return;

            // their open alerts, a few repositories at a time
            var sev = Uri.EscapeDataString(string.Join(",", c.ListSeverities));
            var list = new List<AlertLite>();
            var prev = Alerts; var readBefore = _readRepos; var read = new HashSet<string>();
            int denied = 0, kept = 0, hardFail = 0; bool authFailed = false;
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(repos.Where(r => !r.Archived).Select(async r =>
            {
                await gate.WaitAsync();
                try
                {
                    var items = await client.GetAllAsync($"/repos/{r.FullName}/dependabot/alerts?state=open&severity={sev}&per_page=100");
                    var parsed = items.Select(i => GitHubLogic.ParseAlert(i, r.FullName)).Where(a => a != null).Select(a => a!).ToList();
                    lock (list) { list.AddRange(parsed); read.Add(r.FullName); }
                }
                catch (GitHubException e) when (e.Status == 401) { authFailed = true; }
                catch (GitHubException e) when (e.Status is 403 or 404 && !e.RateLimited) { Interlocked.Increment(ref denied); }
                catch (GitHubException e) when (e.Status >= 500 && readBefore.Contains(r.FullName))
                {
                    // GitHub failed for this one repository: show what it had last time and let the others update (one broken repository froze the whole list)
                    Interlocked.Increment(ref kept);
                    lock (list) { list.AddRange(prev.Where(a => a.Repo == r.FullName)); read.Add(r.FullName); }
                }
                catch (GitHubException e) when (e.Status >= 500) { hardFail = e.Status; }   // never read: no older data to show for it
                finally { gate.Release(); }
            }));
            if (authFailed) { Fail(gen, "auth", "HTTP 401"); Log.Write("github: token rejected"); Raise(); return; }
            if (hardFail != 0) throw new GitHubException(hardFail, "HTTP " + hardFail);

            var alerts = list.OrderByDescending(a => a.Severity == "critical").ThenBy(a => a.Created).ToList();
            if (!Commit(gen, () =>
            {
                Alerts = alerts; _readRepos = read;
                NoAccess = denied; NotUpdated = kept;
                Status = "ok"; LastOk = DateTime.Now;
                _next = Environment.TickCount64 + Math.Max(1, c.RefreshMinutes) * 60_000L;
            })) return;
            if (kept > 0) Log.Write($"github: {kept} repositories not updated (HTTP 5xx)");
            Announce(cfg, gen);
        }
        catch (Exception) when (gen != Volatile.Read(ref _gen)) { return; }   // the settings changed meanwhile: these answers are for the old ones
        catch (GitHubException e) when (e.Status == 401) { Fail(gen, "auth", "HTTP 401"); }
        catch (GitHubException e) when (e.RateLimited) { Fail(gen, "error", "rate limited", e.RetryAfter); Log.Write($"github: rate limited (HTTP {e.Status}), next try in {(int)e.RetryAfter!.Value.TotalMinutes} min"); }   // keeps the data
        catch (GitHubException e) { Fail(gen, "error", "HTTP " + e.Status); Log.Write("github: HTTP " + e.Status); }
        catch (HttpRequestException) { Fail(gen, "offline", "network"); }
        catch (TaskCanceledException) { Fail(gen, "offline", "timeout"); }
        catch (Exception e) { Fail(gen, "error", e.GetType().Name); Log.Write("github: " + e.GetType().Name); }
        Raise();
    }

    // ---------------- new alerts -> sticky toast ----------------

    static State LoadState()
    {
        try { if (File.Exists(StatePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) is { } s) { s.Seen ??= new(); return s; } } catch { }
        return new State();
    }

    static void Announce(Config cfg, int gen)
    {
        List<AlertLite> fresh;
        lock (Gate)   // with Reset: a reseed must not be undone by a refresh for the old team writing its state afterwards
        {
            if (gen != _gen) return;
            var st = LoadState();
            var alerts = Alerts;
            var toastSev = cfg.GitHub.Toast.OnHigh ? new[] { "critical", "high" } : new[] { "critical" };
            fresh = GitHubLogic.NewAlerts(alerts, new HashSet<string>(st.Seen), toastSev, !st.Initialized);
            // everything present is "known" now (so enabling OnHigh later does not flood), and stays remembered while it is open
            st.Initialized = true; st.Seen = StateFile.Remember(st.Seen, alerts.Select(a => a.Id), 500);
            try { StateFile.Write(StatePath, JsonSerializer.Serialize(st)); } catch { }
        }
        if (!cfg.GitHub.Toast.Enabled || fresh.Count == 0) return;
        var (title, body, footer, url) = GitHubLogic.ToastText(fresh, cfg.GitHub);
        string id = fresh.Count == 1 ? "gh:" + fresh[0].Id : "gh:group:" + string.Join(",", fresh.Select(f => f.Id)).GetHashCode();
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ToastManager.Show(new ToastSpec(id, ToastKind.Security, title, body, footer, url, Sticky: true))));
    }

    public static string HealthText(Config cfg) => Status switch
    {
        "ok" => $"updated {LastOk:HH:mm}" + (NoAccess > 0 ? $" · {NoAccess} repo{(NoAccess == 1 ? "" : "s")} not readable" : "")
                + (NotUpdated > 0 ? $" · {NotUpdated} repo{(NotUpdated == 1 ? "" : "s")} not updated (GitHub error)" : ""),
        "no-gh" => "GitHub CLI (gh) not found - install it or set GitHub.GhPath",
        "auth" => "not signed in to GitHub - run: gh auth login",
        "offline" => $"offline since {LastErrorAt:HH:mm}" + (LastOk != null ? $" · data from {LastOk:HH:mm}" : ""),
        "error" => $"GitHub error ({LastError})" + (LastOk != null ? $" · data from {LastOk:HH:mm}" : ""),
        _ => "starting…"
    };
}
