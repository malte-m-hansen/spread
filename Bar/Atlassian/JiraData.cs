using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Spread;

/// <summary>Read-only queries against Jira and Confluence Cloud. Everything returned is kept in RAM only.</summary>
internal static class JiraData
{
    public static string Fields(string pointsField) => $"summary,issuetype,status,assignee,parent,labels,duedate,updated,project,priority,issuelinks,{pointsField}";

    static string S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static async Task<(string accountId, string name)> MeAsync(AtlassianClient c)
    {
        var me = await c.GetAsync("/rest/api/3/myself");
        return (S(me, "accountId"), S(me, "displayName"));
    }

    public static async Task<List<JsonElement>> SearchAsync(AtlassianClient c, string jql, string fields, int max, string? expand = null)
    {
        var list = new List<JsonElement>();
        string? next = null;
        do
        {
            string url = $"/rest/api/3/search/jql?maxResults={Math.Min(100, max - list.Count)}&fields={Uri.EscapeDataString(fields)}&jql={Uri.EscapeDataString(jql)}"
                         + (expand != null ? "&expand=" + expand : "") + (next != null ? "&nextPageToken=" + Uri.EscapeDataString(next) : "");
            var r = await c.GetAsync(url);
            if (r.TryGetProperty("issues", out var arr)) foreach (var i in arr.EnumerateArray()) list.Add(i);
            next = r.TryGetProperty("nextPageToken", out var nt) && nt.ValueKind == JsonValueKind.String ? nt.GetString() : null;
            if (r.TryGetProperty("isLast", out var il) && il.ValueKind == JsonValueKind.True) break;
        } while (next != null && list.Count < max);
        return list;
    }

    // ---------------- sprint ----------------

    public static async Task<(SprintSnapshot? snap, string pointsField)> SprintAsync(AtlassianClient c, int boardId)
    {
        var cfg = await c.GetAsync($"/rest/agile/1.0/board/{boardId}/configuration");
        string pf = "customfield_10006";
        if (cfg.TryGetProperty("estimation", out var est) && est.TryGetProperty("field", out var ef) && !string.IsNullOrEmpty(S(ef, "fieldId"))) pf = S(ef, "fieldId");
        var cols = new List<BoardColumn>();
        if (cfg.TryGetProperty("columnConfig", out var cc) && cc.TryGetProperty("columns", out var ca))
            foreach (var col in ca.EnumerateArray())
            {
                var ids = new HashSet<string>();
                if (col.TryGetProperty("statuses", out var sa)) foreach (var s in sa.EnumerateArray()) ids.Add(S(s, "id"));
                cols.Add(new BoardColumn(S(col, "name"), ids));
            }
        var sprints = await c.GetAsync($"/rest/agile/1.0/board/{boardId}/sprint?state=active&maxResults=1");
        if (!sprints.TryGetProperty("values", out var vals) || vals.GetArrayLength() == 0) return (null, pf);
        var sp = vals[0];
        var info = new SprintInfo(sp.GetProperty("id").GetInt64(), S(sp, "name"), AtlassianLogic.ParseDate(S(sp, "startDate")) ?? DateTime.Now, AtlassianLogic.ParseDate(S(sp, "endDate")) ?? DateTime.Now.AddDays(14), S(sp, "goal"));
        var issues = new List<IssueLite>();
        int start = 0, total = int.MaxValue;
        while (start < total && start < 400)
        {
            var r = await c.GetAsync($"/rest/agile/1.0/sprint/{info.Id}/issue?startAt={start}&maxResults=100&fields={Uri.EscapeDataString(Fields(pf))}");
            total = r.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            int n = 0;
            foreach (var i in r.GetProperty("issues").EnumerateArray()) { var il = AtlassianLogic.ParseIssue(i, pf); if (il != null) issues.Add(il); n++; }
            if (n == 0) break;
            start += n;
        }
        return (new SprintSnapshot(info, cols, issues), pf);
    }

    // ---------------- my work ----------------

    public static async Task<List<IssueLite>> MyWorkAsync(AtlassianClient c, IEnumerable<string> projects, string pf)
    {
        string jql = $"assignee = currentUser() AND statusCategory != Done AND project in ({string.Join(",", projects)}) ORDER BY updated DESC";
        var raw = await SearchAsync(c, jql, Fields(pf), 60);
        return raw.Select(r => AtlassianLogic.ParseIssue(r, pf)).Where(i => i != null).Select(i => i!).ToList();
    }

    // ---------------- blocked by what ----------------

    static readonly Dictionary<string, (DateTime updated, BlockInfo info)> BlockCache = new();

    /// <summary>
    /// For issues that are blocked (status or an open "is blocked by" link): the blockers (with their assignee, one extra query) and,
    /// for Blocked-status issues without any link, the newest comment as the likely reason. Cached per key + updated time.
    /// </summary>
    public static async Task<Dictionary<string, BlockInfo>> BlocksAsync(AtlassianClient c, IEnumerable<IssueLite> issues, string pf)
    {
        var blocked = issues.Where(AtlassianLogic.IsBlockedEffective).GroupBy(i => i.Key).Select(g => g.First()).Take(20).ToList();
        var result = new Dictionary<string, BlockInfo>();
        var todo = new List<IssueLite>();
        foreach (var i in blocked)
        {
            if (BlockCache.TryGetValue(i.Key, out var hit) && hit.updated == i.Updated) result[i.Key] = hit.info; else todo.Add(i);
        }
        if (todo.Count == 0) return result;
        var keys = todo.SelectMany(AtlassianLogic.BlockerLinks).Select(l => l.Key).Distinct().ToList();
        var owner = new Dictionary<string, (string? name, string? id)>();
        if (keys.Count > 0)
        {
            var raw = await SearchAsync(c, $"key in ({string.Join(",", keys)})", "assignee,status", 40);
            foreach (var r in raw)
            {
                var f = r.GetProperty("fields");
                string? nm = null, id = null;
                if (f.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object) { nm = S(a, "displayName"); id = S(a, "accountId"); }
                owner[S(r, "key")] = (nm, id);
            }
        }
        foreach (var i in todo)
        {
            var blockers = AtlassianLogic.BlockerLinks(i).Select(l => new Blocker(l.Key, l.Summary, l.Status, l.Resolved,
                owner.TryGetValue(l.Key, out var o) ? o.name : null, owner.TryGetValue(l.Key, out var o2) ? o2.id : null, l.Type == "Blocks" ? "blocked by" : "depends on")).ToList();
            string? last = null;
            if (blockers.Count == 0 && AtlassianLogic.IsBlocked(i))
            {
                try
                {
                    var cm = await c.GetAsync($"/rest/api/3/issue/{i.Key}/comment?maxResults=1&orderBy=-created");
                    if (cm.TryGetProperty("comments", out var cs) && cs.GetArrayLength() > 0)
                    {
                        var x = cs[0];
                        string who = x.TryGetProperty("author", out var au) ? S(au, "displayName") : "";
                        string text = x.TryGetProperty("body", out var body) ? AtlassianLogic.AdfText(body) : "";
                        last = $"{who}: {AtlassianLogic.Short(text, 140)}";
                    }
                }
                catch (AtlassianException) { }
            }
            var info = new BlockInfo(blockers, last);
            BlockCache[i.Key] = (i.Updated, info);
            result[i.Key] = info;
        }
        return result;
    }

    // ---------------- OKRs ----------------

    public static async Task<OkrSnapshot> OkrAsync(AtlassianClient c, string project, string pf)
    {
        async Task<List<IssueLite>> Q(string jql, int max) =>
            (await SearchAsync(c, jql, Fields(pf), max)).Select(r => AtlassianLogic.ParseIssue(r, pf)).Where(i => i != null).Select(i => i!).ToList();

        var inits = await Q($"project = {project} AND issuetype = Initiative AND statusCategory != Done", 40);
        var epics = await Q($"project = {project} AND issuetype = Epic AND statusCategory != Done", 80);
        var children = new List<IssueLite>();
        for (int i = 0; i < epics.Count; i += 30)
        {
            var keys = string.Join(",", epics.Skip(i).Take(30).Select(e => e.Key));
            children.AddRange(await Q($"parent in ({keys})", 300));
        }
        return BuildOkr(inits, epics, children);
    }

    public static OkrSnapshot BuildOkr(List<IssueLite> inits, List<IssueLite> epics, List<IssueLite> children)
    {
        var byParent = children.Where(c => c.ParentKey != null).GroupBy(c => c.ParentKey!).ToDictionary(g => g.Key, g => g.ToList());
        KeyResultNode Node(IssueLite e, ObjectiveNode? parent)
        {
            var kids = byParent.TryGetValue(e.Key, out var l) ? l : new List<IssueLite>();
            var tag = OkrTag.Parse(e.Summary);
            var flags = new List<string>();
            if (kids.Count == 0) flags.Add("no work items yet");
            if (parent != null && tag.Objective != null && parent.Tag.Objective != null && tag.Objective != parent.Tag.Objective)
                flags.Add($"tag says O{tag.Objective} but it sits under O{parent.Tag.Objective}");
            return new KeyResultNode(e, tag, kids.Count, kids.Count(k => k.Done), kids.Sum(k => k.Points ?? 0), kids.Where(k => k.Done).Sum(k => k.Points ?? 0), flags);
        }
        var objectives = new List<ObjectiveNode>();
        var used = new HashSet<string>();
        foreach (var ini in inits.OrderBy(i => OkrTag.Parse(i.Summary).Objective ?? 99).ThenBy(i => i.Key))
        {
            var obj = new ObjectiveNode(ini, OkrTag.Parse(ini.Summary), new List<KeyResultNode>());
            foreach (var e in epics.Where(e => e.ParentKey == ini.Key).OrderBy(e => OkrTag.Parse(e.Summary).KeyResult ?? 99).ThenBy(e => e.Key)) { obj.KeyResults.Add(Node(e, obj)); used.Add(e.Key); }
            objectives.Add(obj);
        }
        var other = epics.Where(e => !used.Contains(e.Key)).OrderBy(e => e.Key).Select(e => Node(e, null)).ToList();
        return new OkrSnapshot(objectives, other);
    }

    // ---------------- changes since a moment ----------------

    public static async Task<List<ChangeEvent>> ChangesAsync(AtlassianClient c, string site, string project, DateTime since, string myId, string myName)
    {
        string jql = $"project = {project} AND updated >= \"{since.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}\" ORDER BY updated DESC";
        var issues = await SearchAsync(c, jql, "summary,comment,status,created,issuetype", 40, "changelog");
        var list = new List<ChangeEvent>();
        foreach (var iss in issues)
        {
            string key = S(iss, "key"), url = $"https://{site}/browse/{key}";
            var f = iss.GetProperty("fields");
            string title = S(f, "summary");
            var created = AtlassianLogic.ParseDate(S(f, "created"));
            if (created != null && created >= since) list.Add(new ChangeEvent(created.Value, "created", key, title, "", "new issue", false, url));
            if (iss.TryGetProperty("changelog", out var cl) && cl.TryGetProperty("histories", out var hs))
                foreach (var h in hs.EnumerateArray())
                {
                    var when = AtlassianLogic.ParseDate(S(h, "created"));
                    if (when == null || when < since) continue;
                    string who = h.TryGetProperty("author", out var au) ? S(au, "displayName") : "";
                    if (!h.TryGetProperty("items", out var its)) continue;
                    foreach (var it in its.EnumerateArray())
                    {
                        string field = S(it, "field");
                        if (field == "status") list.Add(new ChangeEvent(when.Value, "status", key, title, who, $"{S(it, "fromString")} → {S(it, "toString")}", false, url));
                        else if (field == "assignee")
                        {
                            bool me = S(it, "to") == myId;
                            list.Add(new ChangeEvent(when.Value, "assignee", key, title, who, string.IsNullOrEmpty(S(it, "toString")) ? "unassigned" : "assigned to " + S(it, "toString"), me, url));
                        }
                    }
                }
            if (f.TryGetProperty("comment", out var cm) && cm.TryGetProperty("comments", out var cs))
                foreach (var cmt in cs.EnumerateArray())
                {
                    var when = AtlassianLogic.ParseDate(S(cmt, "created"));
                    if (when == null || when < since) continue;
                    string who = cmt.TryGetProperty("author", out var au) ? S(au, "displayName") : "";
                    bool mention = cmt.TryGetProperty("body", out var body) && AtlassianLogic.AdfMentions(body, myId);
                    string text = cmt.TryGetProperty("body", out var b2) ? AtlassianLogic.AdfText(b2) : "";
                    list.Add(new ChangeEvent(when.Value, mention ? "mention" : "comment", key, title, who, AtlassianLogic.Short(text, 140), mention, url));
                }
        }
        return list.OrderByDescending(e => e.When).Take(60).ToList();
    }

    // ---------------- Confluence ----------------

    public static async Task<List<ConfItem>> CqlAsync(AtlassianClient c, string site, string cql, int limit)
    {
        var r = await c.GetAsync($"/wiki/rest/api/search?limit={limit}&cql={Uri.EscapeDataString(cql)}&expand=content.history.lastUpdated");
        var list = new List<ConfItem>();
        if (!r.TryGetProperty("results", out var res)) return list;
        foreach (var x in res.EnumerateArray())
        {
            string kind = x.TryGetProperty("content", out var ct) ? S(ct, "type") : S(x, "entityType");
            string who = "";
            if (x.TryGetProperty("content", out var c2) && c2.TryGetProperty("history", out var hi) && hi.TryGetProperty("lastUpdated", out var lu) && lu.TryGetProperty("by", out var by)) who = S(by, "displayName");
            string excerpt = System.Text.RegularExpressions.Regex.Replace(S(x, "excerpt"), @"\s+", " ").Trim();
            string url = S(x, "url");
            list.Add(new ConfItem(S(x, "title"), kind, who, AtlassianLogic.ParseDate(S(x, "lastModified")) ?? DateTime.Now, AtlassianLogic.Short(excerpt, 120), url.StartsWith("/") ? $"https://{site}/wiki{url}" : url));
        }
        return list;
    }

    public static string Esc(string q) => q.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static async Task<(List<(string key, string summary, string status)> jira, List<ConfItem> conf)> QuickSearchAsync(AtlassianClient c, string site, string q)
    {
        var jira = new List<(string, string, string)>();
        try
        {
            var raw = await SearchAsync(c, $"text ~ \"{Esc(q)}*\" ORDER BY updated DESC", "summary,status", 6);
            foreach (var r in raw) jira.Add((S(r, "key"), S(r.GetProperty("fields"), "summary"), S(r.GetProperty("fields").GetProperty("status"), "name")));
        }
        catch (AtlassianException) { }
        var conf = new List<ConfItem>();
        try { conf = await CqlAsync(c, site, $"siteSearch ~ \"{Esc(q)}\" AND type in (page, blogpost) ORDER BY lastmodified DESC", 6); } catch (AtlassianException) { }
        return (jira, conf);
    }
}
