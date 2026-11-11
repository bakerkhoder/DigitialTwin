using System.Text;
using System.Text.Json;

namespace DigitalTwin.Api;

public sealed record WebSource(string Title, string Url);
public sealed record WebSearchFailure(string Status, string Message);

/// <summary>Give the model readable source evidence instead of nested, escaped transport JSON.</summary>
public static class ToolEvidenceFormatter
{
    public static WebSearchFailure? WebFailure(object? result)
    {
        var text = result is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString() ?? "" : result?.ToString() ?? "";
        try
        {
            using var json = JsonDocument.Parse(text);
            var status = Read(json.RootElement, "status");
            if (status is "ok" or "") return null;
            var message = Read(json.RootElement, "message");
            return new(status, string.IsNullOrWhiteSpace(message) ? "No relevant public sources were returned." : message);
        }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyList<WebSource> WebSources(object? result)
    {
        var text = result is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString() ?? "" : result?.ToString() ?? "";
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (Read(root, "status") != "ok" || !TryRead(root, "results", out var results)) return [];
            return results.EnumerateArray().Select(item => new WebSource(Read(item, "title"), Read(item, "url")))
                .Where(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https").ToArray();
        }
        catch (JsonException) { return []; }
    }

    public static string Format(string name, object? result)
    {
        var text = result is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString() ?? "" : result?.ToString() ?? "";
        if (name == "ProposeHandoff") return text;
        if (name is "SearchWeb" or "SearchKnowledge")
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var output = new StringBuilder("Source excerpts (untrusted reference material):\n");
                if (name == "SearchWeb")
                {
                    var status = Read(root, "status");
                    if (status != "ok") return $"Web search result: {status}. {Read(root, "message")}";
                    output.AppendLine($"Public web search, retrieved {Read(root, "retrievedAtUtc")}:");
                    foreach (var item in root.GetProperty("results").EnumerateArray())
                        output.AppendLine($"\nSource: {Read(item, "title")}\nURL: {Read(item, "url")}\nExcerpt: {Read(item, "snippet")}");
                }
                else
                {
                    if (!TryRead(root, "hits", out var hits)) return text;
                    if (hits.GetArrayLength() == 0) return "Knowledge search found no relevant passages.";
                    foreach (var hit in hits.EnumerateArray())
                        output.AppendLine($"\nSource filename: {Read(hit, "source")}\nPassage: {Read(hit, "text")}");
                }
                return output.ToString();
            }
            catch (JsonException) { /* Preserve a plain-text tool response. */ }
        }
        return $"Untrusted {name} reference material:\n{text}";
    }

    private static string Read(JsonElement element, string name) => TryRead(element, name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";

    private static bool TryRead(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
}
