using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spread;

// Pure models and parsing for the Atlassian widget (no network here: testable with hand-written fixtures).

internal sealed record IssueLite(string Key, string Summary, string Type, string Status, string StatusId, string StatusCat, string? Assignee, string? AssigneeId,
    double? Points, string? ParentKey, string? ParentType, DateTime? Due, DateTime Updated, string[] Labels, string Project, bool IsSubtask, IReadOnlyList<IssueLink>? Links = null)
{
    public bool Done => StatusCat == "done";
}

/// <summary>A link on an issue. Direction "inward" = the other issue acts on this one ("is blocked by"), "outward" = this one acts on the other ("blocks").</summary>
internal sealed record IssueLink(string Type, string Direction, string Key, string Summary, string Status, string StatusCat)
{
    public bool Resolved => StatusCat == "done";
}
internal sealed record Blocker(string Key, string Summary, string Status, bool Resolved, string? Assignee, string? AssigneeId, string Relation);
internal sealed record BlockInfo(List<Blocker> Blockers, string? LastComment);

/// <summary>Something worth a toast.</summary>
internal sealed record AlertCandidate(string Id, ToastKind Kind, string Title, string Body, string Footer, string? Url, DateTime When);
internal sealed class AlertSettings { public bool Mentions = true, Assigned = true, Confluence = true; }

internal sealed record BoardColumn(string Name, HashSet<string> StatusIds);
internal sealed record SprintInfo(long Id, string Name, DateTime Start, DateTime End, string Goal);
internal sealed record SprintSnapshot(SprintInfo Sprint, List<BoardColumn> Columns, List<IssueLite> Issues);

internal sealed record KeyResultNode(IssueLite Epic, OkrTag Tag, int Children, int ChildrenDone, double Points, double PointsDone, List<string> Flags);
internal sealed record ObjectiveNode(IssueLite Initiative, OkrTag Tag, List<KeyResultNode> KeyResults);
internal sealed record OkrSnapshot(List<ObjectiveNode> Objectives, List<KeyResultNode> Other);

/// <summary><paramref name="WhoId"/>: the author's accountId ("" when unknown), so your own changes are not counted as new for you.</summary>
internal sealed record ChangeEvent(DateTime When, string Kind, string Key, string Title, string Who, string Detail, bool ForMe, string Url, string WhoId = "");
internal sealed record ConfItem(string Title, string Kind, string Who, DateTime When, string Excerpt, string Url);

internal sealed record OkrTag(int? Quarter, int? Objective, int? KeyResult, string? Label)
{
    static readonly Regex Bracket = new(@"\[(?<t>[^\]]{1,40})\]", RegexOptions.Compiled);
    static readonly Regex Q = new(@"(?<![A-Za-z])Q(?<n>[1-4])(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex O = new(@"(?<![A-Za-z])O(?<n>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex KR = new(@"KR(?<n>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool Any => Quarter != null || Objective != null || KeyResult != null || Label != null;

    /// <summary>"[Q4 O3]", "[O1 KR1]", "[Q3 O2KR2]", "(NEW) [O3 KR1]", "[KTLO]", "[Q4]" ...</summary>
    public static OkrTag Parse(string title)
    {
        foreach (Match m in Bracket.Matches(title ?? ""))
        {
            string t = m.Groups["t"].Value;
            var q = Q.Match(t); var o = O.Match(t); var k = KR.Match(t);
            if (q.Success || o.Success || k.Success)
                return new OkrTag(q.Success ? int.Parse(q.Groups["n"].Value) : null, o.Success ? int.Parse(o.Groups["n"].Value) : null, k.Success ? int.Parse(k.Groups["n"].Value) : null, null);
            return new OkrTag(null, null, null, t.Trim());
        }
        return new OkrTag(null, null, null, null);
    }
}

internal static class AtlassianLogic
{
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s, @"([+-]\d{2})(\d{2})$", "$1:$2");   // Jira: +0100 -> +01:00
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return d.LocalDateTime;
        return null;
    }

    public static string SprintNumber(string name)
    {
        var m = Regex.Match(name ?? "", @"(\d+)\s*$");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>"... at most 3-5 undone tasks" in the sprint goal overrides the configured default.</summary>
    public static (int min, int max) GoalTarget(string? goal, (int min, int max) fallback)
    {
        var m = Regex.Match(goal ?? "", @"at\s+most\s+(\d+)\s*(?:-|–|to)\s*(\d+)\s+undone", RegexOptions.IgnoreCase);
        if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        m = Regex.Match(goal ?? "", @"at\s+most\s+(\d+)\s+undone", RegexOptions.IgnoreCase);
        return m.Success ? (0, int.Parse(m.Groups[1].Value)) : fallback;
    }

    public static (double elapsed, double done, int ahead) TimeVsDone(DateTime start, DateTime end, DateTime now, int done, int total)
    {
        double span = Math.Max(1, (end - start).TotalSeconds);
        double elapsed = Math.Clamp((now - start).TotalSeconds / span, 0, 1);
        double doneFrac = total == 0 ? 0 : (double)done / total;
        return (elapsed, doneFrac, done - (int)Math.Round(total * elapsed));
    }

    public static double NeededPerDay(int undone, int targetMax, double daysLeft) => Math.Max(0, undone - targetMax) / Math.Max(1, daysLeft);

    public static string DaysLeftText(DateTime end, DateTime now)
    {
        var left = end - now;
        if (left.TotalSeconds <= 0) return "ended";
        return left.TotalDays >= 1 ? $"{Math.Ceiling(left.TotalDays):0}d" : $"{Math.Max(1, Math.Ceiling(left.TotalHours)):0}h";
    }

    /// <summary>Board column of an issue (by status id); falls back to the status name.</summary>
    public static string ColumnOf(IssueLite i, List<BoardColumn> cols) => cols.FirstOrDefault(c => c.StatusIds.Contains(i.StatusId))?.Name ?? i.Status;

    public static bool IsBlocked(IssueLite i) => i.Status.Contains("Blocked", StringComparison.OrdinalIgnoreCase);
    public static bool IsReview(IssueLite i) => i.Status.Contains("Review", StringComparison.OrdinalIgnoreCase) || i.Status.Equals("In Qa", StringComparison.OrdinalIgnoreCase);

    public static bool IsStale(IssueLite i, DateTime now, int days) => !i.Done && (now - i.Updated).TotalDays >= days;

    /// <summary>Blocked / Waiting first, then review/QA, then in progress, then the rest.</summary>
    public static int WorkRank(IssueLite i) => IsBlocked(i) ? 0 : IsReview(i) ? 1 : i.StatusCat == "indeterminate" ? 2 : 3;

    public static int CurrentQuarter(DateTime now) => (now.Month - 1) / 3 + 1;

    public static IssueLite? ParseIssue(JsonElement e, string pointsField)
    {
        try
        {
            var f = e.GetProperty("fields");
            string Str(JsonElement el, string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var st = f.GetProperty("status");
            string? assignee = null, assigneeId = null;
            if (f.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object) { assignee = Str(a, "displayName"); assigneeId = Str(a, "accountId"); }
            double? pts = null;
            if (f.TryGetProperty(pointsField, out var p) && p.ValueKind == JsonValueKind.Number) pts = p.GetDouble();
            string? pk = null, pt = null;
            if (f.TryGetProperty("parent", out var par) && par.ValueKind == JsonValueKind.Object)
            {
                pk = Str(par, "key");
                if (par.TryGetProperty("fields", out var pf) && pf.TryGetProperty("issuetype", out var pit)) pt = Str(pit, "name");
            }
            DateTime? due = null;
            if (f.TryGetProperty("duedate", out var dd) && dd.ValueKind == JsonValueKind.String && DateTime.TryParse(dd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dv)) due = dv;
            var labels = f.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
            var it = f.GetProperty("issuetype");
            string key = Str(e, "key");
            string project = f.TryGetProperty("project", out var pr) ? Str(pr, "key") : key.Contains('-') ? key[..key.IndexOf('-')] : "";
            bool sub = it.TryGetProperty("subtask", out var sb) && sb.ValueKind == JsonValueKind.True;
            List<IssueLink>? links = null;
            if (f.TryGetProperty("issuelinks", out var il) && il.ValueKind == JsonValueKind.Array)
            {
                links = new List<IssueLink>();
                foreach (var l2 in il.EnumerateArray())
                {
                    string ltype = l2.TryGetProperty("type", out var lt) ? Str(lt, "name") : "";
                    foreach (var (prop, dir) in new[] { ("inwardIssue", "inward"), ("outwardIssue", "outward") })
                    {
                        if (!l2.TryGetProperty(prop, out var other) || other.ValueKind != JsonValueKind.Object) continue;
                        var of = other.TryGetProperty("fields", out var o2) ? o2 : default;
                        var ost = of.ValueKind == JsonValueKind.Object && of.TryGetProperty("status", out var o3) ? o3 : default;
                        string cat = ost.ValueKind == JsonValueKind.Object && ost.TryGetProperty("statusCategory", out var o4) ? Str(o4, "key") : "";
                        links.Add(new IssueLink(ltype, dir, Str(other, "key"), of.ValueKind == JsonValueKind.Object ? Str(of, "summary") : "", ost.ValueKind == JsonValueKind.Object ? Str(ost, "name") : "", cat));
                    }
                }
            }
            return new IssueLite(key, Str(f, "summary"), Str(it, "name"), Str(st, "name"), Str(st, "id"),
                st.TryGetProperty("statusCategory", out var sc) ? Str(sc, "key") : "", assignee, assigneeId, pts, pk, pt, due,
                AtlassianLogic.ParseDate(Str(f, "updated")) ?? DateTime.Now, labels, project, sub, links);
        }
        catch { return null; }
    }

    // ----- Atlassian Document Format (comment bodies) -----

    public static string AdfText(JsonElement n)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement x)
        {
            if (x.ValueKind != JsonValueKind.Object) return;
            string type = x.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "text" && x.TryGetProperty("text", out var tx)) sb.Append(tx.GetString());
            else if (type == "mention" && x.TryGetProperty("attrs", out var at) && at.TryGetProperty("text", out var mt)) sb.Append(mt.GetString());
            else if (type == "hardBreak") sb.Append(' ');
            if (x.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var ch in c.EnumerateArray()) { Walk(ch); if (type is "paragraph" or "heading") sb.Append(' '); }
        }
        Walk(n);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static bool AdfMentions(JsonElement n, string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return false;
        bool found = false;
        void Walk(JsonElement x)
        {
            if (found || x.ValueKind != JsonValueKind.Object) return;
            if (x.TryGetProperty("type", out var t) && t.GetString() == "mention" && x.TryGetProperty("attrs", out var at) && at.TryGetProperty("id", out var id) && id.GetString() == accountId) { found = true; return; }
            if (x.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array) foreach (var ch in c.EnumerateArray()) Walk(ch);
        }
        Walk(n);
        return found;
    }

    // ----- blocked by what -----

    /// <summary>Links that hold this issue back: "is blocked by" (Blocks, inward) and "depends on" (Dependency, outward).</summary>
    public static List<IssueLink> BlockerLinks(IssueLite i) =>
        (i.Links ?? new List<IssueLink>()).Where(l => (l.Type == "Blocks" && l.Direction == "inward") || (l.Type == "Dependency" && l.Direction == "outward")).ToList();

    /// <summary>Blocked by status ("Blocked", "Waiting/Blocked") OR by an unresolved "is blocked by" link.</summary>
    public static bool IsBlockedEffective(IssueLite i) =>
        !i.Done && (IsBlocked(i) || BlockerLinks(i).Any(l => l.Type == "Blocks" && !l.Resolved));

    /// <summary>"you can act" (a blocker is mine or has no owner), "ready to unblock" (all blockers resolved), else "waiting on others".</summary>
    public static string BlockVerdict(IReadOnlyList<Blocker> blockers, string myId)
    {
        if (blockers.Count == 0) return "";
        var open = blockers.Where(b => !b.Resolved).ToList();
        if (open.Count == 0) return "ready to unblock";
        return open.Any(b => b.AssigneeId == null || b.AssigneeId == myId) ? "you can act" : "waiting on others";
    }

    /// <summary>
    /// The badge "new since your last look": comments, mentions, status/assignee changes and new issues after <paramref name="lastLook"/>, plus Confluence
    /// mentions; minus what you did yourself (by accountId; Confluence only has the name, as in <see cref="NewAlerts"/>).
    /// </summary>
    public static int CountNew(IEnumerable<ChangeEvent> changes, IEnumerable<ConfItem> confMentions, DateTime lastLook, string myId, string myName) =>
        changes.Count(e => e.When > lastLook && (e.Kind is "mention" or "comment" or "status" or "assignee" or "created") && !(myId.Length > 0 && e.WhoId == myId))
        + confMentions.Count(m => m.When > lastLook && !(myName.Length > 0 && string.Equals(m.Who, myName, StringComparison.OrdinalIgnoreCase)));

    // ----- alerts -----

    public static string ConfId(string url)
    {
        var m = Regex.Match(url ?? "", @"focusedCommentId=(\d+)");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url ?? "", @"/pages/(\d+)");
        return m.Success ? m.Groups[1].Value : url ?? "";
    }

    /// <summary>
    /// Mentions of me and assignments to me that are new: not written by me, not alerted before (ids), not older than the mark minus a safety margin.
    /// Pages mentioning me alert once per page; comments are unique.
    /// </summary>
    public static List<AlertCandidate> NewAlerts(IEnumerable<ChangeEvent> changes, IEnumerable<ConfItem> confMentions, string myName, DateTime lastAlert, HashSet<string> alerted, AlertSettings set)
    {
        var res = new List<AlertCandidate>();
        var floor = lastAlert > DateTime.MinValue.AddHours(1) ? lastAlert.AddMinutes(-10) : DateTime.MinValue;   // MinValue = "first run": no arithmetic on it
        foreach (var e in changes)
        {
            if (!e.ForMe || e.When < floor || string.Equals(e.Who, myName, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Kind == "mention" && !set.Mentions) continue;
            if (e.Kind == "assignee" && !set.Assigned) continue;
            if (e.Kind != "mention" && e.Kind != "assignee") continue;
            string id = $"j:{e.Key}:{e.Kind}:{e.When.Ticks}";
            if (alerted.Contains(id)) continue;
            string who = e.Who.Length > 0 ? e.Who : "Someone";
            res.Add(new AlertCandidate(id, ToastKind.Jira,
                e.Kind == "mention" ? $"{who} mentioned you" : $"{who} assigned {e.Key} to you",
                e.Kind == "mention" ? e.Detail : Short(e.Title, 90), $"{e.Key} · {Short(e.Title, 40)} · {e.When:HH:mm}", e.Url, e.When));
        }
        if (set.Confluence)
            foreach (var c in confMentions)
            {
                if (c.When < floor || string.Equals(c.Who, myName, StringComparison.OrdinalIgnoreCase)) continue;
                string id = "c:" + ConfId(c.Url);
                if (alerted.Contains(id)) continue;
                res.Add(new AlertCandidate(id, ToastKind.Confluence, (c.Who.Length > 0 ? c.Who : "Someone") + " mentioned you in Confluence", Short(c.Title, 90), $"{c.When:HH:mm}" + (c.Excerpt.Length > 0 ? " · " + Short(c.Excerpt, 50) : ""), c.Url, c.When));
            }
        return res.OrderBy(a => a.When).ToList();
    }

    // ----- settings validation -----

    public sealed record SettingsInput(string Board, string Project, string Others, string Space, string StaleDays, string RefreshMinutes, string UndoneMin, string UndoneMax, string ToastSeconds, string PollSeconds, string TokenExpires, string ToastPosition = "top-center");

    static bool Int(string s, int min, int max, out int v) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    public static List<string> ValidateSettings(SettingsInput i)
    {
        var errors = new List<string>();
        if (!Int(i.Board, 1, 99_999_999, out _)) errors.Add("Board id must be a number.");
        if (!Regex.IsMatch(i.Project.Trim(), @"^[A-Za-z][A-Za-z0-9_]{1,19}$")) errors.Add("Project key looks like PROJ.");
        foreach (var p in i.Others.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Regex.IsMatch(p, @"^[A-Za-z][A-Za-z0-9_]{1,19}$")) { errors.Add($"Other project '{p}' looks wrong."); break; }
        if (!Regex.IsMatch(i.Space.Trim(), @"^[A-Za-z0-9~]{1,40}$")) errors.Add("Confluence space key looks like SPACE.");
        if (!Int(i.StaleDays, 1, 60, out _)) errors.Add("Stale days: 1 to 60.");
        if (!Int(i.RefreshMinutes, 1, 60, out _)) errors.Add("Refresh minutes: 1 to 60.");
        bool a = Int(i.UndoneMin, 0, 99, out int umin), b = Int(i.UndoneMax, 0, 99, out int umax);
        if (!a || !b || umin > umax) errors.Add("Undone target: two numbers, min <= max.");
        if (!Int(i.ToastSeconds, 3, 60, out _)) errors.Add("Toast seconds: 3 to 60.");
        if (!Int(i.PollSeconds, 30, 600, out _)) errors.Add("Alert check: 30 to 600 seconds.");
        if (!new[] { "top-center", "top-right", "top-left" }.Contains(i.ToastPosition.Trim().ToLowerInvariant())) errors.Add("Toast position: top-center, top-right or top-left.");
        if (i.TokenExpires.Trim().Length > 0 && !DateTime.TryParseExact(i.TokenExpires.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors.Add("Token expires: yyyy-mm-dd or empty.");
        return errors;
    }

    public static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
