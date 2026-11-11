namespace DigitalTwin.Api;

public sealed class ToolAuthorizationService
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        { "ListProject", "ReadFile", "SearchKnowledge", "SearchWeb", "ProposeHandoff" };
    private static readonly HashSet<string> ProtectedSegments = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".aws", ".ssh", ".azure", ".codex", ".agents" };

    public void AuthorizeTool(string name)
    {
        var state = GuardrailContext.Required;
        state.ThrowIfStopped();
        if (!Allowed.Contains(name)) throw new GuardrailViolationException("tool_denied", "This tool is not authorized for the agent.");
        if (name is "ListProject" or "ReadFile" or "SearchKnowledge") state.MarkPrivate();
        if (name == "SearchWeb") AuthorizeWebSearch();
        if (name == "ProposeHandoff") AuthorizeHandoff();
    }

    public void AuthorizeWebSearch(string? query = null)
    {
        GuardrailContext.Current?.ThrowIfStopped();
        // Validate what leaves the app. Reading a project or document does not disable public search.
        if (query is not null && (query.Length is < 3 or > 220 || query.Contains('\n') || query.Contains('\r') || SensitiveDataPolicy.ContainsCredentialOrLocalPath(query)))
            throw new GuardrailViolationException("private_web_query", "Use a short public query without credentials or local file paths.");
    }

    public void AuthorizeHandoff()
    {
        var state = GuardrailContext.Required;
        state.ThrowIfStopped();
        if (!state.AllowHandoff) throw new GuardrailViolationException("handoff_denied", "Handoff proposals require explicit request permission. Use Request review on the completed answer, or enable allowHandoffProposal in the API request.");
    }

    public static bool IsSensitivePath(string path) => path.Replace('\\', '/').Split('/').Any(segment =>
        ProtectedSegments.Contains(segment) ||
        segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        segment.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        segment.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        segment.EndsWith(".user", StringComparison.OrdinalIgnoreCase));
}
