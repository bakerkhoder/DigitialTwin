namespace DigitalTwin.Api;

public sealed record ValidatedOutput(string Text, bool DiscardSession);

public sealed class OutputValidationService(TwinSettings settings)
{
    public ValidatedOutput Validate(string text, GuardrailRunState state)
    {
        state.ThrowIfStopped();
        if (string.IsNullOrWhiteSpace(text)) throw new GuardrailViolationException("empty_output", "The model returned no usable response. Try again.");
        if (text.Length > settings.Guardrails.MaxOutputCharacters)
            throw new GuardrailViolationException("output_budget", "The response exceeded the output limit. Request a smaller answer.");
        if (state.RequireKnowledge && state.KnowledgeSearches == 0)
            return new("The knowledge base was not searched, so I cannot verify an answer from its documents. Please retry the knowledge question.", true);
        if (state.RequireKnowledge && state.KnowledgeSources.Count == 0)
            return new("I couldn't find relevant information in the knowledge base for this question.", true);
        text = KnowledgeAnswerPresentation.EnsureSources(text, state.KnowledgeSources);
        var redacted = SensitiveDataPolicy.Redact(text);
        var safeText = redacted == text ? text : redacted + "\n\nSensitive values were removed from this response.";
        if (safeText.Length > settings.Guardrails.MaxOutputCharacters)
            throw new GuardrailViolationException("output_budget", "The response exceeded the output limit. Request a smaller answer.");
        return new(safeText, redacted != text);
    }
}
