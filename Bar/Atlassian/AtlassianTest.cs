using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Spread;

/// <summary>
/// `--atlassian-test`: checks the pure parsing with hand-written fixtures (no real data). With `--live` (token in SPREAD_ATLASSIAN_TOKEN
/// or Credential Manager) it also reads from Atlassian with GET requests and prints COUNTS only: never titles or names.
/// </summary>
internal static class AtlassianTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    static IssueLite Issue(string key, string status, string cat, string? assignee = null, double? pts = null, string type = "Task", string? parent = null, string summary = "x", int updatedDaysAgo = 0, string statusId = "1") =>
        new(key, summary, type, status, statusId, cat, assignee, assignee, pts, parent, null, null, DateTime.Now.AddDays(-updatedDaysAgo), Array.Empty<string>(), "PROJ", false);

    public static int Run(string[] args)
    {
        Console.WriteLine("Atlassian parsing checks (synthetic fixtures):");
        var t = OkrTag.Parse("[Q4 O3] Gain more experience");
        Check("[Q4 O3]", t is { Quarter: 4, Objective: 3, KeyResult: null });
        t = OkrTag.Parse("[O1 KR1] Successfully export"); Check("[O1 KR1]", t is { Quarter: null, Objective: 1, KeyResult: 1 });
        t = OkrTag.Parse("[Q3 O2KR2] Add feature"); Check("[Q3 O2KR2]", t is { Quarter: 3, Objective: 2, KeyResult: 2 });
        t = OkrTag.Parse("(NEW) [O3 KR1] Map UNS"); Check("(NEW) [O3 KR1]", t is { Objective: 3, KeyResult: 1 });
        t = OkrTag.Parse("[KTLO] Finish waste-2"); Check("[KTLO]", t is { Objective: null, Label: "KTLO" });
        t = OkrTag.Parse("[Q4] Energy management pilot"); Check("[Q4]", t is { Quarter: 4, Objective: null });
        t = OkrTag.Parse("Establish Global Read-Only BMS Access"); Check("no tag", !t.Any);
        Check("sprint number", AtlassianLogic.SprintNumber("main character energy 27") == "27");
        Check("goal target 3-5", AtlassianLogic.GoalTarget("do not be sorry\nwe want a sprint that has at most 3-5 undone tasks", (0, 9)) == (3, 5));
        Check("goal target fallback", AtlassianLogic.GoalTarget("ship it", (2, 4)) == (2, 4));
        var start = new DateTime(2026, 10, 21, 9, 0, 0); var end = new DateTime(2026, 11, 11, 9, 0, 0);
        var (el, dn, ah) = AtlassianLogic.TimeVsDone(start, end, new DateTime(2026, 11, 5, 15, 0, 0), 23, 47);
        Check("time vs done", Math.Abs(el - 0.7262) < 0.01 && Math.Abs(dn - 23.0 / 47) < 1e-9 && ah == 23 - (int)Math.Round(47 * el), $"{el:0.000} {dn:0.000} {ah}");
        Check("days left text", AtlassianLogic.DaysLeftText(end, new DateTime(2026, 11, 5, 15, 0, 0)) == "6d" && AtlassianLogic.DaysLeftText(end, end.AddHours(1)) == "ended" && AtlassianLogic.DaysLeftText(end, end.AddHours(-5)) == "5h");
        Check("needed per day", Math.Abs(AtlassianLogic.NeededPerDay(24, 5, 6) - 19.0 / 6) < 1e-9 && AtlassianLogic.NeededPerDay(4, 5, 6) == 0);
        Check("date with +0100", AtlassianLogic.ParseDate("2026-11-05T11:38:59.123+0100") is { } d && d.Year == 2026);
        var cols = new List<BoardColumn> { new("To Do", new() { "1" }), new("Done / Production", new() { "9" }) };
        Check("column of", AtlassianLogic.ColumnOf(Issue("A-1", "TODO", "new", statusId: "1"), cols) == "To Do" && AtlassianLogic.ColumnOf(Issue("A-2", "Weird", "new", statusId: "77"), cols) == "Weird");
        Check("blocked/review", AtlassianLogic.IsBlocked(Issue("A-3", "Waiting/Blocked", "new")) && AtlassianLogic.IsReview(Issue("A-4", "In Qa", "indeterminate")) && AtlassianLogic.IsReview(Issue("A-5", "In Review", "indeterminate")));
        Check("work rank order", AtlassianLogic.WorkRank(Issue("A-6", "Blocked", "new")) < AtlassianLogic.WorkRank(Issue("A-7", "In Review", "indeterminate")) && AtlassianLogic.WorkRank(Issue("A-7", "In Review", "indeterminate")) < AtlassianLogic.WorkRank(Issue("A-8", "In Progress", "indeterminate")) && AtlassianLogic.WorkRank(Issue("A-8", "In Progress", "indeterminate")) < AtlassianLogic.WorkRank(Issue("A-9", "TODO", "new")));
        Check("stale", AtlassianLogic.IsStale(Issue("A-10", "In Progress", "indeterminate", updatedDaysAgo: 7), DateTime.Now, 5) && !AtlassianLogic.IsStale(Issue("A-11", "Done", "done", updatedDaysAgo: 9), DateTime.Now, 5));

        // OKR tree: the PROJ-924 style mismatch, a KR without children, and KTLO outside the objectives
        var inits = new List<IssueLite> { Issue("I-1", "TODO", "new", type: "Initiative", summary: "[Q4 O1] Enable reliable facility data integration") };
        var epics = new List<IssueLite>
        {
            Issue("E-1", "In Progress", "indeterminate", type: "Epic", parent: "I-1", summary: "[O1 KR1] Export data"),
            Issue("E-2", "In Progress", "indeterminate", type: "Epic", parent: "I-1", summary: "[O2 KR2] Mislabelled"),
            Issue("E-3", "TODO", "new", type: "Epic", parent: "I-1", summary: "[O1 KR3] Empty"),
            Issue("E-4", "In Progress", "indeterminate", type: "Epic", summary: "[KTLO] Keep the lights on")
        };
        var kids = new List<IssueLite>
        {
            Issue("T-1", "Done", "done", pts: 3, parent: "E-1"), Issue("T-2", "In Progress", "indeterminate", pts: 5, parent: "E-1"),
            Issue("T-3", "Done", "done", parent: "E-2")
        };
        var okr = JiraData.BuildOkr(inits, epics, kids);
        var kr1 = okr.Objectives[0].KeyResults.First(k => k.Epic.Key == "E-1");
        Check("okr progress", kr1.Children == 2 && kr1.ChildrenDone == 1 && kr1.Points == 8 && kr1.PointsDone == 3);
        Check("okr mismatch flag", okr.Objectives[0].KeyResults.First(k => k.Epic.Key == "E-2").Flags.Any(f => f.Contains("O2") && f.Contains("O1")));
        Check("okr no-children flag", okr.Objectives[0].KeyResults.First(k => k.Epic.Key == "E-3").Flags.Contains("no work items yet"));
        Check("okr other group", okr.Other.Count == 1 && okr.Other[0].Epic.Key == "E-4");

        // ADF comment text + mentions
        using var adf = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"mention\",\"attrs\":{\"id\":\"abc\",\"text\":\"@Malte\"}},{\"type\":\"text\",\"text\":\" please review\"}]}]}");
        Check("adf text", AtlassianLogic.AdfText(adf.RootElement) == "@Malte please review");
        Check("adf mention", AtlassianLogic.AdfMentions(adf.RootElement, "abc") && !AtlassianLogic.AdfMentions(adf.RootElement, "zzz"));

        // blocked by what
        IssueLite WithLinks(string key, string status, params IssueLink[] links) =>
            new(key, "x", "Task", status, "1", status == "Done" ? "done" : "indeterminate", null, null, null, null, null, null, DateTime.Now, Array.Empty<string>(), "PROJ", false, links);
        var open = new IssueLink("Blocks", "inward", "B-1", "blocker", "In Progress", "indeterminate");
        var resolved = new IssueLink("Blocks", "inward", "B-2", "blocker", "Done", "done");
        var blocksOther = new IssueLink("Blocks", "outward", "B-3", "blocked one", "TODO", "new");
        var dep = new IssueLink("Dependency", "outward", "B-4", "needed", "TODO", "new");
        Check("blocked by an open link (status not blocked)", AtlassianLogic.IsBlockedEffective(WithLinks("A-1", "In Progress", open)));
        Check("resolved blocker does not block", !AtlassianLogic.IsBlockedEffective(WithLinks("A-2", "In Progress", resolved)));
        Check("'blocks' (outward) does not block this issue", !AtlassianLogic.IsBlockedEffective(WithLinks("A-3", "In Progress", blocksOther)));
        Check("Blocked status without link is blocked", AtlassianLogic.IsBlockedEffective(WithLinks("A-4", "Waiting/Blocked")));
        Check("done issue is never blocked", !AtlassianLogic.IsBlockedEffective(WithLinks("A-5", "Done", open)));
        Check("dependency listed as blocker link, not as 'blocked'", AtlassianLogic.BlockerLinks(WithLinks("A-6", "In Progress", dep)).Count == 1 && !AtlassianLogic.IsBlockedEffective(WithLinks("A-6", "In Progress", dep)));
        var bl = new List<Blocker> { new("B-1", "s", "In Progress", false, "Malik", "m1", "blocked by") };
        Check("verdict: waiting on others", AtlassianLogic.BlockVerdict(bl, "me") == "waiting on others");
        Check("verdict: you can act (mine)", AtlassianLogic.BlockVerdict(new List<Blocker> { new("B-1", "s", "TODO", false, "Me", "me", "blocked by") }, "me") == "you can act");
        Check("verdict: you can act (unowned)", AtlassianLogic.BlockVerdict(new List<Blocker> { new("B-1", "s", "TODO", false, null, null, "blocked by") }, "me") == "you can act");
        Check("verdict: ready to unblock", AtlassianLogic.BlockVerdict(new List<Blocker> { new("B-2", "s", "Done", true, "X", "x", "blocked by") }, "me") == "ready to unblock");
        using var issueJson = JsonDocument.Parse("{\"key\":\"A-9\",\"fields\":{\"summary\":\"s\",\"issuetype\":{\"name\":\"Task\"},\"status\":{\"id\":\"3\",\"name\":\"In Progress\",\"statusCategory\":{\"key\":\"indeterminate\"}},\"updated\":\"2026-11-05T11:00:00.000+0100\",\"issuelinks\":[{\"type\":{\"name\":\"Blocks\"},\"inwardIssue\":{\"key\":\"A-1\",\"fields\":{\"summary\":\"b\",\"status\":{\"name\":\"TODO\",\"statusCategory\":{\"key\":\"new\"}}}}}]}}");
        var parsed = AtlassianLogic.ParseIssue(issueJson.RootElement, "customfield_10006");
        Check("issue links are parsed", parsed?.Links is { Count: 1 } && parsed.Links[0] is { Type: "Blocks", Direction: "inward", Key: "A-1", Resolved: false });

        // drill-down bucketing
        var board = new List<BoardColumn> { new("In review", new() { "10" }), new("Blocked", new() { "20" }) };
        var sprintIssues = new List<IssueLite> { Issue("S-1", "In Review", "indeterminate", statusId: "10"), Issue("S-2", "Blocked", "new", statusId: "20"), Issue("S-3", "In Review", "indeterminate", statusId: "10") };
        Check("column filter", sprintIssues.Count(i => AtlassianLogic.ColumnOf(i, board) == "In review") == 2);

        // alerts
        var me = "Malte";
        var ev = new List<ChangeEvent>
        {
            new(DateTime.Now.AddMinutes(-1), "mention", "A-1", "title", "Anna", "can you check?", true, "https://x/browse/A-1"),
            new(DateTime.Now.AddMinutes(-2), "mention", "A-2", "title", "Malte", "my own comment", true, "https://x/browse/A-2"),
            new(DateTime.Now.AddMinutes(-3), "assignee", "A-3", "title", "Bo", "assigned to Malte", true, "https://x/browse/A-3"),
            new(DateTime.Now.AddMinutes(-4), "comment", "A-4", "title", "Cy", "no mention", false, "https://x/browse/A-4"),
            new(DateTime.Now.AddHours(-3), "mention", "A-5", "old", "Di", "too old", true, "https://x/browse/A-5")
        };
        var conf = new List<ConfItem> { new("Page", "page", "Eve", DateTime.Now.AddMinutes(-5), "x", "https://x/wiki/spaces/SPACE/pages/123/Page") };
        var seen = new HashSet<string>();
        var set = new AlertSettings();
        var first = AtlassianLogic.NewAlerts(ev, conf, me, DateTime.Now.AddMinutes(-30), seen, set);
        Check("alerts: mention + assignment + confluence page", first.Count == 3 && first.Any(a => a.Title.Contains("mentioned you")) && first.Any(a => a.Title.Contains("assigned A-3")) && first.Any(a => a.Kind == ToastKind.Confluence));
        Check("alerts: own comment, plain comment and old events are ignored", first.All(a => !a.Id.Contains("A-2") && !a.Id.Contains("A-4") && !a.Id.Contains("A-5")));
        foreach (var a in first) seen.Add(a.Id);
        Check("alerts: nothing twice", AtlassianLogic.NewAlerts(ev, conf, me, DateTime.Now.AddMinutes(-30), seen, set).Count == 0);
        Check("alerts: switches respected", AtlassianLogic.NewAlerts(ev, conf, me, DateTime.Now.AddMinutes(-30), new HashSet<string>(), new AlertSettings { Mentions = false, Assigned = false, Confluence = false }).Count == 0);
        Check("alerts: first run (no date) does not throw and finds everything", AtlassianLogic.NewAlerts(ev, conf, me, DateTime.MinValue, new HashSet<string>(), set).Count == 4);
        Check("settings: toast position", AtlassianLogic.ValidateSettings(new AtlassianLogic.SettingsInput("1", "PROJ", "", "SPACE", "5", "5", "3", "5", "10", "60", "", "middle")).Count == 1);
        Check("confluence ids", AtlassianLogic.ConfId("https://x/wiki/spaces/SPACE/pages/123/Page") == "123" && AtlassianLogic.ConfId("https://x/wiki/spaces/SPACE/pages/123/Page?focusedCommentId=456") == "456");

        // settings validation
        var good = new AtlassianLogic.SettingsInput("12345", "PROJ", "OTHER, ABC", "SPACE", "5", "5", "3", "5", "10", "60", "2026-12-01");
        Check("settings: valid input", AtlassianLogic.ValidateSettings(good).Count == 0);
        Check("settings: bad board", AtlassianLogic.ValidateSettings(good with { Board = "abc" }).Count == 1);
        Check("settings: bad project", AtlassianLogic.ValidateSettings(good with { Project = "1 bad" }).Count == 1);
        Check("settings: min > max", AtlassianLogic.ValidateSettings(good with { UndoneMin = "9", UndoneMax = "2" }).Count == 1);
        Check("settings: out of range + bad date", AtlassianLogic.ValidateSettings(good with { PollSeconds = "5", TokenExpires = "tomorrow" }).Count == 2);
        Check("settings: empty token date ok", AtlassianLogic.ValidateSettings(good with { TokenExpires = "" }).Count == 0);

        // the client must refuse anything but GET
        var client = new AtlassianClient("example.atlassian.net", "a@b.c", "t");
        var http = (System.Net.Http.HttpClient)typeof(AtlassianClient).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
        bool postBlocked = false, hostBlocked = false;
        try { http.PostAsync("https://example.atlassian.net/rest/api/3/issue", new System.Net.Http.StringContent("{}")).GetAwaiter().GetResult(); } catch (InvalidOperationException) { postBlocked = true; } catch { }
        try { http.GetAsync("https://evil.example.com/").GetAwaiter().GetResult(); } catch (InvalidOperationException) { hostBlocked = true; } catch { }
        Check("client refuses POST", postBlocked);
        Check("client refuses other hosts", hostBlocked);

        if (args.Contains("--live")) _fail += Live().GetAwaiter().GetResult();
        Console.WriteLine(_fail == 0 ? "ATLASSIAN TEST PASSED" : $"ATLASSIAN TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    static async Task<int> Live()
    {
        Console.WriteLine("Live read-only check (counts only):");
        var cfg = Config.Load(); var a = cfg.Atlassian;
        var token = TokenStore.Load();
        if (token == null) { Console.WriteLine("  no token (set SPREAD_ATLASSIAN_TOKEN or connect in the panel)"); return 0; }
        try
        {
            var c = new AtlassianClient(a.Site, AtlassianData.EmailFor(cfg), token);
            var (id, name) = await JiraData.MeAsync(c);
            Console.WriteLine($"  signed in: {(id.Length > 0 ? "yes" : "no")}");
            var (snap, pf) = await JiraData.SprintAsync(c, a.BoardId);
            if (snap == null) Console.WriteLine("  no active sprint");
            else Console.WriteLine($"  sprint '{AtlassianLogic.SprintNumber(snap.Sprint.Name)}': {snap.Issues.Count} issues, {snap.Issues.Count(i => i.Done)} done, {snap.Columns.Count} board columns, points {snap.Issues.Sum(i => i.Points ?? 0):0}, target {AtlassianLogic.GoalTarget(snap.Sprint.Goal, (0, 0))}");
            var work = await JiraData.MyWorkAsync(c, new[] { a.Project }.Concat(a.OtherProjects), pf);
            Console.WriteLine($"  my work: {work.Count} open ({work.Count(AtlassianLogic.IsBlocked)} blocked, {work.Count(AtlassianLogic.IsReview)} in review/QA)");
            if (snap != null)
            {
                var blocks = await JiraData.BlocksAsync(c, snap.Issues.Concat(work), pf);
                Console.WriteLine($"  blocked: {snap.Issues.Count(AtlassianLogic.IsBlockedEffective)} in the sprint ({snap.Issues.Count(i => i.Links is { Count: > 0 })} sprint issues carry links), blockers found for {blocks.Count(b => b.Value.Blockers.Count > 0)}, last-comment reason for {blocks.Count(b => b.Value.LastComment != null)}, unresolved blockers {blocks.Sum(b => b.Value.Blockers.Count(x => !x.Resolved))}");
            }
            var okr = await JiraData.OkrAsync(c, a.Project, pf);
            Console.WriteLine($"  okr: {okr.Objectives.Count} objectives, {okr.Objectives.Sum(o => o.KeyResults.Count)} key results, {okr.Other.Count} epics outside, flags {okr.Objectives.Sum(o => o.KeyResults.Sum(k => k.Flags.Count))}");
            var changes = await JiraData.ChangesAsync(c, c.Site, a.Project, DateTime.Now.AddDays(-2), id, name);
            Console.WriteLine($"  changes (2 days): {changes.Count} ({changes.Count(e => e.Kind == "status")} status, {changes.Count(e => e.Kind.StartsWith("comment") || e.Kind == "mention")} comments, {changes.Count(e => e.ForMe)} for me)");
            var pages = await JiraData.CqlAsync(c, c.Site, $"space = {a.ConfluenceSpace} AND type in (page, blogpost) ORDER BY lastmodified DESC", 8);
            var comments = await JiraData.CqlAsync(c, c.Site, $"space = {a.ConfluenceSpace} AND type = comment ORDER BY lastmodified DESC", 8);
            var mentions = await JiraData.CqlAsync(c, c.Site, "mention = currentUser() ORDER BY lastmodified DESC", 8);
            Console.WriteLine($"  confluence: {pages.Count} pages, {comments.Count} comments, {mentions.Count} mentions; page urls https: {pages.All(p => p.Url.StartsWith("https://"))}");
            var (jira, conf) = await JiraData.QuickSearchAsync(c, c.Site, "kafka");
            Console.WriteLine($"  quick search: {jira.Count} jira, {conf.Count} confluence");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("  FAILED: " + e.GetType().Name + " " + e.Message); return 1; }
    }
}
