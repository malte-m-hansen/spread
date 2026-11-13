using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Spread;

internal sealed class AtlassianException : Exception
{
    public int Status { get; }
    public AtlassianException(int status, string msg) : base(msg) { Status = status; }
}

/// <summary>
/// Read-only Atlassian Cloud client. It can ONLY send GET requests (the handler refuses every other verb), only to the configured site
/// or api.atlassian.com, and it never logs bodies or the token. Unscoped tokens go to the site directly; scoped tokens (read-only scopes)
/// go through api.atlassian.com/ex/{jira|confluence}/{cloudId}.
/// </summary>
internal sealed class AtlassianClient
{
    sealed class GetOnlyHandler : DelegatingHandler
    {
        readonly string _site;
        public GetOnlyHandler(string site) : base(new HttpClientHandler { AllowAutoRedirect = false }) { _site = site; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Spread's Atlassian client is read-only: only GET is allowed.");
            string host = request.RequestUri?.Host ?? "";
            if (!host.Equals(_site, StringComparison.OrdinalIgnoreCase) && !host.Equals("api.atlassian.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Request to an unexpected host was blocked.");
            return base.SendAsync(request, ct);
        }
    }

    readonly HttpClient _http;
    readonly string _site;
    string? _cloudId;
    bool _scoped;

    public string Site => _site;
    public bool Scoped => _scoped;

    public AtlassianClient(string site, string email, string token)
    {
        _site = site.Trim().Replace("https://", "").TrimEnd('/');
        _http = new HttpClient(new GetOnlyHandler(_site)) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + token)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    string Url(string path)
    {
        if (!_scoped) return "https://" + _site + path;
        return path.StartsWith("/wiki", StringComparison.Ordinal)
            ? $"https://api.atlassian.com/ex/confluence/{_cloudId}{path}"
            : $"https://api.atlassian.com/ex/jira/{_cloudId}{path}";
    }

    async Task<string> CloudIdAsync()
    {
        using var r = await _http.GetAsync("https://" + _site + "/_edge/tenant_info").ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
        return d.RootElement.GetProperty("cloudId").GetString()!;
    }

    /// <summary>GET a Jira (/rest/...) or Confluence (/wiki/rest/...) path and return the parsed JSON.</summary>
    public async Task<JsonElement> GetAsync(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var resp = await _http.GetAsync(Url(path)).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait).ConfigureAwait(false);
                continue;
            }
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !_scoped && attempt == 0)
            {
                // maybe a scoped token: those only work through api.atlassian.com
                try { _cloudId = await CloudIdAsync().ConfigureAwait(false); _scoped = true; continue; } catch { }
            }
            if (!resp.IsSuccessStatusCode) throw new AtlassianException((int)resp.StatusCode, "HTTP " + (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.Clone();
        }
        throw new AtlassianException(429, "rate limited");
    }
}
