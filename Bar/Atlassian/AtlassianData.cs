using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Spread;

internal sealed class AlertsConfig
{
    public bool Enabled { get; set; } = true;
    public bool Mentions { get; set; } = true;
    public bool Assigned { get; set; } = true;
    public bool ConfluenceMentions { get; set; } = true;
    /// <summary>How long a toast stays (hover pauses it).</summary>
    public int ToastSeconds { get; set; } = 10;
    /// <summary>No toasts while Windows notifications are switched off.</summary>
    public bool RespectDnd { get; set; } = true;
    /// <summary>How often Spread looks for new mentions/assignments.</summary>
    public int PollSeconds { get; set; } = 60;
    /// <summary>top-center (under the clock), top-right or top-left, always just below the top bar.</summary>
    public string Position { get; set; } = "top-center";
}

internal sealed class AtlassianConfig
{
    public bool Enabled { get; set; } = true;
    public string Site { get; set; } = "yourcompany.atlassian.net";
    /// <summary>Atlassian login e-mail. Empty = the Windows UPN.</summary>
    public string Email { get; set; } = "";
    public int BoardId { get; set; } = 12345;
    public string Project { get; set; } = "PROJ";
    public string[] OtherProjects { get; set; } = new[] { "OTHER" };
    public string ConfluenceSpace { get; set; } = "SPACE";
    public int StaleDays { get; set; } = 5;
    /// <summary>Wanted number of undone issues at the end of the sprint; a range in the sprint goal ("at most 3-5 undone") overrides it.</summary>
    public int[] UndoneTarget { get; set; } = new[] { 3, 5 };
    public int RefreshMinutes { get; set; } = 5;
    /// <summary>Optional yyyy-MM-dd: the API cannot tell when the token expires, so you can say it; a warning shows from 7 days before.</summary>
    public string TokenExpires { get; set; } = "";
    public AlertsConfig Alerts { get; set; } = new();
}

/// <summary>What is remembered between runs: only timestamps and ids, no content.</summary>
internal sealed class AtlassianState
{
    public DateTime LastLook { get; set; }
    public DateTime LastAlert { get; set; }
    public List<string> Alerted { get; set; } = new();
}

/// <summary>
/// Background cache for the Atlassian widget and panel: sprint, my work, OKRs and blockers every few minutes, changes and Confluence activity
/// every poll interval (alerts). RAM only apart from the small state file. Read-only: see <see cref="AtlassianClient"/>.
/// </summary>
internal static class AtlassianData
{
    public static string Status { get; private set; } = "not connected";   // ok | not connected | reconnect | offline | error
    public static SprintSnapshot? Sprint { get; private set; }
    public static List<IssueLite> MyWork { get; private set; } = new();
    public static OkrSnapshot? Okr { get; private set; }
    public static Dictionary<string, BlockInfo> Blocks { get; private set; } = new();
    public static List<ChangeEvent> Changes { get; private set; } = new();
    public static List<ConfItem> ConfPages { get; private set; } = new();
    public static List<ConfItem> ConfComments { get; private set; } = new();
    public static List<ConfItem> ConfMentions { get; private set; } = new();
    public static string MyName { get; private set; } = "";
    public static string MyId { get; private set; } = "";
    public static string PointsField { get; private set; } = "customfield_10006";
    public static string Site => _site;
    public static AtlassianClient? Client { get; private set; }
    public static DateTime? LastOk { get; private set; }
    public static DateTime? LastErrorAt { get; private set; }
    public static string LastError { get; private set; } = "";
    public static bool Scoped => Client?.Scoped ?? false;
    public static event Action? Changed;

    static string _site = "";
    static long _nextMain, _nextFast;
    static int _busy, _fastCount;
    static AtlassianState? _state;
    public static string StatePath => Path.Combine(Config.Dir, "atlassian-state.json");

    static AtlassianState State
    {
        get
        {
            if (_state != null) return _state;
            try { if (File.Exists(StatePath)) _state = JsonSerializer.Deserialize<AtlassianState>(File.ReadAllText(StatePath)); } catch { }
            if (_state == null || _state.LastLook == default) { _state ??= new AtlassianState(); _state.LastLook = DateTime.UtcNow; }
            return _state;
        }
    }

    static void SaveState()
    {
        try { Directory.CreateDirectory(Config.Dir); File.WriteAllText(StatePath, JsonSerializer.Serialize(State)); } catch { }
    }

    /// <summary>Moment of the previous look at the panel; events after it count as new.</summary>
    public static DateTime LastLook => State.LastLook.ToLocalTime();

    public static void MarkLooked() { State.LastLook = DateTime.UtcNow; SaveState(); Raise(); }

    public static int NewCount => Changes.Count(e => e.When > LastLook && (e.Kind is "mention" or "comment" or "status" or "assignee" or "created")) + ConfMentions.Count(m => m.When > LastLook);

    static void Raise() => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));

    public static string EmailFor(Config cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Atlassian.Email)) return cfg.Atlassian.Email;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("whoami", "/upn") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(2000);
            return o.Contains('@') ? o : "";
        }
        catch { return ""; }
    }

    /// <summary>One line for the panel header / tooltip.</summary>
    public static string HealthText(Config cfg)
    {
        string h = Status switch
        {
            "ok" => LastOk != null ? $"connected · updated {LastOk:HH:mm}" : "connected",
            "offline" => $"offline since {LastErrorAt:HH:mm}" + (LastOk != null ? $" · data from {LastOk:HH:mm}" : ""),
            "reconnect" => "token rejected · connect again",
            "error" => $"Atlassian error ({LastError})" + (LastOk != null ? $" · data from {LastOk:HH:mm}" : ""),
            _ => "not connected"
        };
        string? exp = ExpiryWarning(cfg);
        return exp != null ? h + " · " + exp : h;
    }

    public static string? ExpiryWarning(Config cfg)
    {
        if (!DateTime.TryParseExact(cfg.Atlassian.TokenExpires?.Trim() ?? "", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return null;
        int days = (int)Math.Ceiling((d.Date.AddDays(1) - DateTime.Now).TotalDays) - 1;
        return days < 0 ? "token has expired" : days <= 7 ? $"token expires in {days} day{(days == 1 ? "" : "s")}" : null;
    }

    public static bool Healthy => Status == "ok";

    /// <summary>Called every second by the widget; does the work only when something is due.</summary>
    public static void Tick(Config cfg)
    {
        if (!cfg.Atlassian.Enabled) return;
        long now = Environment.TickCount64;
        bool due = Client == null ? now >= _nextMain : (now >= _nextMain || now >= _nextFast);
        if (!due) return;
        if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        Task.Run(async () =>
        {
            try { await RefreshAsync(cfg); }
            finally { System.Threading.Volatile.Write(ref _busy, 0); }
        });
    }

    public static void RefreshNow() { _nextMain = 0; _nextFast = 0; }

    public static void ResetCaches()
    {
        Sprint = null; MyWork = new(); Okr = null; Blocks = new(); Changes = new(); ConfPages = new(); ConfComments = new(); ConfMentions = new();
        RefreshNow(); Raise();
    }

    static void Fail(string status, string error)
    {
        Status = status; LastError = error; LastErrorAt = DateTime.Now;
        _nextMain = _nextFast = Environment.TickCount64 + 60_000;
    }

    static async Task RefreshAsync(Config cfg)
    {
        var a = cfg.Atlassian;
        long now = Environment.TickCount64;
        try
        {
            if (Client == null)
            {
                var token = TokenStore.Load();
                if (token == null) { Status = "not connected"; _nextMain = now + 5000; Raise(); return; }
                var client = new AtlassianClient(a.Site, EmailFor(cfg), token);
                _site = client.Site;
                var (id, name) = await JiraData.MeAsync(client);
                MyId = id; MyName = name; Client = client;
            }
            var c = Client!;
            if (now >= _nextMain)
            {
                var (snap, pf) = await JiraData.SprintAsync(c, a.BoardId);
                PointsField = pf; Sprint = snap;
                MyWork = await JiraData.MyWorkAsync(c, new[] { a.Project }.Concat(a.OtherProjects), pf);
                Okr = await JiraData.OkrAsync(c, a.Project, pf);
                Blocks = await JiraData.BlocksAsync(c, (snap?.Issues ?? new List<IssueLite>()).Concat(MyWork), pf);
                _nextMain = Environment.TickCount64 + Math.Max(1, a.RefreshMinutes) * 60_000L;
            }
            if (now >= _nextFast)
            {
                var since = new[] { LastLook, DateTime.Now.AddDays(-3) }.Min();
                Changes = await JiraData.ChangesAsync(c, c.Site, a.Project, since, MyId, MyName);
                ConfMentions = await JiraData.CqlAsync(c, c.Site, "mention = currentUser() ORDER BY lastmodified DESC", 8);
                if ((_fastCount++ & 1) == 0 || ConfPages.Count == 0)
                {
                    ConfPages = await JiraData.CqlAsync(c, c.Site, $"space = {a.ConfluenceSpace} AND type in (page, blogpost) ORDER BY lastmodified DESC", 8);
                    ConfComments = await JiraData.CqlAsync(c, c.Site, $"space = {a.ConfluenceSpace} AND type = comment ORDER BY lastmodified DESC", 8);
                }
                _nextFast = Environment.TickCount64 + Math.Clamp(a.Alerts.PollSeconds, 30, 600) * 1000L;
                ProcessAlerts(cfg);
            }
            Status = "ok"; LastOk = DateTime.Now;
        }
        catch (AtlassianException e) when (e.Status is 401 or 403) { Client = null; Fail("reconnect", "HTTP " + e.Status); Log.Write("atlassian: token rejected (HTTP " + e.Status + ")"); }
        catch (AtlassianException e) { Fail("error", "HTTP " + e.Status); Log.Write("atlassian: HTTP " + e.Status); }
        catch (HttpRequestException) { Fail("offline", "network"); }
        catch (TaskCanceledException) { Fail("offline", "timeout"); }
        catch (Exception e)
        {
            Fail("error", e.GetType().Name + " – details in the log");
            string where = (e.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
            Log.Write("atlassian internal error: " + e.GetType().Name + " " + where);
        }
        Raise();
    }

    // ---------------- alerts ----------------

    static void ProcessAlerts(Config cfg)
    {
        var al = cfg.Atlassian.Alerts;
        var st = State;
        var set = new AlertSettings { Mentions = al.Mentions, Assigned = al.Assigned, Confluence = al.ConfluenceMentions };
        bool first = st.LastAlert == default;   // very first run: remember what exists, do not toast it
        var alerted = new HashSet<string>(st.Alerted);
        var found = AtlassianLogic.NewAlerts(Changes, ConfMentions, MyName, first ? DateTime.MinValue : st.LastAlert.ToLocalTime(), alerted, set);
        foreach (var f in found) alerted.Add(f.Id);
        st.Alerted = alerted.TakeLast(200).ToList();
        st.LastAlert = DateTime.UtcNow;
        SaveState();
        if (first || !al.Enabled || found.Count == 0) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var f in found.TakeLast(5)) ToastManager.Show(new ToastSpec(f.Id, f.Kind, f.Title, f.Body, f.Footer, f.Url));
        }));
    }

    /// <summary>Checks the token with one read-only call; on success it is stored in Credential Manager.</summary>
    public static async Task<string?> ConnectAsync(Config cfg, string site, string email, string token)
    {
        try
        {
            var client = new AtlassianClient(site, email, token.Trim());
            var (id, name) = await JiraData.MeAsync(client);
            if (!TokenStore.Save(token.Trim())) return "Could not store the token in Windows Credential Manager.";
            cfg.Atlassian.Site = client.Site; cfg.Atlassian.Email = email.Trim(); cfg.Save();
            _site = client.Site; MyId = id; MyName = name; Client = client; Status = "ok"; LastOk = DateTime.Now;
            RefreshNow(); Raise();
            return null;
        }
        catch (AtlassianException e) { return e.Status is 401 or 403 ? "Atlassian rejected the e-mail/token combination." : "Atlassian answered HTTP " + e.Status + "."; }
        catch (Exception e) { return "Could not reach Atlassian (" + e.GetType().Name + ")."; }
    }

    /// <summary>For the settings view: who am I, scoped or not, round-trip time, and the board's name.</summary>
    public static async Task<string> TestAsync(Config cfg, int boardId)
    {
        try
        {
            var token = TokenStore.Load();
            if (token == null) return "No token stored: connect first.";
            var client = Client ?? new AtlassianClient(cfg.Atlassian.Site, EmailFor(cfg), token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (id, name) = await JiraData.MeAsync(client);
            sw.Stop();
            string board = "";
            try { var b = await client.GetAsync($"/rest/agile/1.0/board/{boardId}"); board = b.TryGetProperty("name", out var n) ? $" · board: {n.GetString()}" : ""; }
            catch (AtlassianException e) { board = $" · board {boardId}: HTTP {e.Status}"; }
            return $"Signed in as {name} · {(client.Scoped ? "scoped token" : "unscoped token (can write - prefer a read-only scoped token)")} · {sw.ElapsedMilliseconds} ms{board}";
        }
        catch (AtlassianException e) { return e.Status is 401 or 403 ? "Atlassian rejected the token." : "Atlassian answered HTTP " + e.Status + "."; }
        catch (Exception e) { return "Could not reach Atlassian (" + e.GetType().Name + ")."; }
    }

    public static void SendTestAlert()
    {
        ToastManager.Show(new ToastSpec("test:" + DateTime.Now.Ticks, ToastKind.Jira, "Test alert", "This is how a mention looks. Click to open Jira.", "Spread · " + DateTime.Now.ToString("HH:mm"), $"https://{(string.IsNullOrEmpty(_site) ? "example.com" : _site)}/"));
    }

    public static void Disconnect()
    {
        TokenStore.Delete(); Client = null; Status = "not connected";
        ResetCaches();
    }
}
