using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

/// <summary>
/// Read-only public web search. The MAF agent chooses WHEN to call this tool;
/// this service validates WHAT it may send and handles provider failures safely.
/// </summary>
public sealed class WebSearchTools(HttpClient http)
{
    private long _providerCalls;
    private string _lastOutcome = "not-called";

    // These are diagnostics only. No API key is ever returned to the browser.
    public object GetStatus() => new
    {
        provider = CurrentProvider(),
        configured = CurrentProvider() != "off",
        providerCalls = Interlocked.Read(ref _providerCalls),
        lastOutcome = Volatile.Read(ref _lastOutcome)
    };

    private static string CurrentProvider() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXA_API_KEY")) ? "exa" :
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAVILY_API_KEY")) ? "tavily" :
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY")) ? "brave" : "off";

    [Description("Search the PUBLIC web for current external facts such as official framework documentation, package versions, vulnerabilities, or recently changed APIs. Use only when current public evidence is needed and the answer is not already in uploaded knowledge or project files. Never send confidential code, internal policies, local paths, emails, or secrets. Return source URLs and snippets, not a final answer.")]
    public async Task<string> SearchWeb(
        [Description("A SHORT, PUBLIC search query, e.g. 'site:learn.microsoft.com agent framework function tools C#'. Do not paste private documents or repository source. Required; maximum 220 characters.")] string query,
        [Description("Number of public sources to retrieve, integer 1..5. Usually 3; default 3.")] int maxResults = 3,
        [Description("Recency filter: 'any' for evergreen docs, 'day', 'week', or 'month' for recent updates. Default 'any'.")] string freshness = "any")
    {
        using var toolTelemetry = DeveloperTwinTelemetry.BeginTool("SearchWeb");
        query = query?.Trim() ?? "";
        freshness = freshness?.Trim().ToLowerInvariant() ?? "any";
        if (query.Length is < 3 or > 220 || query.Contains('\n') || query.Contains('\r'))
            return Error("invalid_request", "query must be 3–220 characters on one line.");
        if (maxResults is < 1 or > 5)
            return Error("invalid_request", "maxResults must be between 1 and 5.");
        if (freshness is not ("any" or "day" or "week" or "month"))
            return Error("invalid_request", "freshness must be any, day, week, or month.");

        // Defence in depth. Arbitrary company names cannot be detected here: the
        // agent instructions AND the user's data policies remain important too.
        if (Regex.IsMatch(query,
                @"(?i)(?:[A-Z]:\\|/home/|/mnt/|-----BEGIN [A-Z ]*PRIVATE KEY|\b(?:sk-|tvly-|ghp_)[a-z0-9_-]{12,})"))
            return Error("blocked", "Search query looks like a local path or credential. Reformulate as a generic public technical question.");

        var provider = CurrentProvider();
        if (provider == "off")
            return Error("unconfigured", "Set EXA_API_KEY, TAVILY_API_KEY or BRAVE_SEARCH_API_KEY in the backend process, then restart it.");

        try
        {
            using var request = provider switch
            {
                "exa" => CreateExaRequest(query, maxResults, freshness),
                "tavily" => CreateTavilyRequest(query, maxResults, freshness),
                _ => CreateBraveRequest(query, maxResults, freshness)
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Interlocked.Increment(ref _providerCalls);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Provider rejected the API key or plan.",
                    HttpStatusCode.TooManyRequests => "Provider rate limit or free quota reached.",
                    _ => $"Search provider returned HTTP {code}."
                };
                return Error("provider_error", message, provider);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var items = provider switch
            {
                "exa" => ExtractExa(doc.RootElement, maxResults),
                "tavily" => ExtractTavily(doc.RootElement, maxResults),
                _ => ExtractBrave(doc.RootElement, maxResults)
            };
            Volatile.Write(ref _lastOutcome, items.Length == 0 ? "empty" : "ok");
            return JsonSerializer.Serialize(new
            {
                status = items.Length == 0 ? "empty" : "ok",
                provider,
                retrievedAtUtc = DateTimeOffset.UtcNow,
                results = items
            });
        }
        catch (OperationCanceledException)
        {
            return Error("timeout", "Search provider did not respond within 12 seconds.", provider);
        }
        catch (HttpRequestException)
        {
            return Error("network_error", "Could not reach the configured search provider.", provider);
        }
        catch (JsonException)
        {
            return Error("provider_error", "Search provider returned invalid JSON.", provider);
        }
    }

    // Exa offers a no-card starter credit. Use the economical cached/lite search
    // instead of its more expensive deep research and summarization modes.
    private static HttpRequestMessage CreateExaRequest(string query, int count, string freshness)
    {
        var data = new Dictionary<string, object>
        {
            ["query"] = query,
            ["type"] = "lite",
            ["numResults"] = count,
            ["contents"] = new { text = new { maxCharacters = 600 } }
        };
        if (freshness != "any")
        {
            var days = freshness switch { "day" => 1, "week" => 7, _ => 30 };
            data["startPublishedDate"] = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.exa.ai/search")
        {
            Content = JsonContent.Create(data)
        };
        request.Headers.Add("x-api-key", Environment.GetEnvironmentVariable("EXA_API_KEY"));
        return request;
    }

    private static SearchResult[] ExtractExa(JsonElement root, int count)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        return results.EnumerateArray().Take(count).Select(x => new SearchResult(
            Read(x, "title", 150), Read(x, "url", 800), Read(x, "text", 500)))
            .Where(x => IsPublicUrl(x.Url)).ToArray();
    }

    private static HttpRequestMessage CreateTavilyRequest(string query, int count, string freshness)
    {
        var data = new Dictionary<string, object>
        {
            ["query"] = query,
            ["max_results"] = count,
            ["search_depth"] = "basic",
            ["include_answer"] = false,
            ["include_raw_content"] = false
        };
        if (freshness != "any") data["time_range"] = freshness;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("TAVILY_API_KEY"));
        request.Content = JsonContent.Create(data);
        return request;
    }

    private static HttpRequestMessage CreateBraveRequest(string query, int count, string freshness)
    {
        var path = "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) + "&count=" + count;
        if (freshness != "any")
        {
            var braveFreshness = freshness switch
            {
                "day" => "pd",
                "week" => "pw",
                "month" => "pm",
                _ => ""
            };
            path += "&freshness=" + braveFreshness;
        }
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Subscription-Token", Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY"));
        return request;
    }

    private static SearchResult[] ExtractTavily(JsonElement root, int count)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        return results.EnumerateArray().Take(count).Select(x => new SearchResult(
            Read(x, "title", 150), Read(x, "url", 800), Read(x, "content", 500)))
            .Where(x => IsPublicUrl(x.Url)).ToArray();
    }

    private static SearchResult[] ExtractBrave(JsonElement root, int count)
    {
        if (!root.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        return results.EnumerateArray().Take(count).Select(x => new SearchResult(
            Read(x, "title", 150), Read(x, "url", 800), Read(x, "description", 500)))
            .Where(x => IsPublicUrl(x.Url)).ToArray();
    }

    private static bool IsPublicUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string Read(JsonElement element, string name, int limit)
    {
        if (!element.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return "";
        var value = p.GetString() ?? "";
        return value.Length <= limit ? value : value[..limit];
    }

    private string Error(string status, string message, string provider = "off")
    {
        Volatile.Write(ref _lastOutcome, status);
        if (System.Diagnostics.Activity.Current?.DisplayName == "tool.SearchWeb")
        {
            System.Diagnostics.Activity.Current.SetTag("tool.outcome", status);
            System.Diagnostics.Activity.Current.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Search unavailable");
        }
        return JsonSerializer.Serialize(new { status, provider, message, results = Array.Empty<SearchResult>() });
    }

    private sealed record SearchResult(string Title, string Url, string Snippet);
}
