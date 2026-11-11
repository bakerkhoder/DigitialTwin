using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace DigitalTwin.Api;

/// <summary>Chat middleware bounds inference loops and model output, including streaming callers.</summary>
public sealed class GuardrailChatClient(IChatClient inner, TwinSettings settings) : DelegatingChatClient(inner)
{
    private ChatOptions Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var state = GuardrailContext.Required;
        state.BeforeModel();
        var characters = messages.SelectMany(m => m.Contents).Sum(content => content switch
        {
            TextContent text => (long)text.Text.Length,
            FunctionResultContent result => result.Result is string value ? value.Length : JsonSerializer.Serialize(result.Result).Length,
            FunctionCallContent call => JsonSerializer.Serialize(call.Arguments).Length,
            _ => 0L
        });
        if (characters > 120_000)
            throw new GuardrailViolationException("context_budget", "This conversation exceeds the context limit. Start a new conversation or reduce the supplied files.");
        var bounded = options?.Clone() ?? new ChatOptions();
        bounded.MaxOutputTokens = Math.Min(bounded.MaxOutputTokens ?? settings.Guardrails.MaxOutputTokens, settings.Guardrails.MaxOutputTokens);
        return bounded;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        var response = await base.GetResponseAsync(snapshot, Prepare(snapshot, options), cancellationToken);
        if (response.Messages.Any(m => m.Contents.OfType<FunctionCallContent>().Any())) return response;
        if (!string.IsNullOrWhiteSpace(response.Text))
            return await PresentPublicAnswer(response, snapshot, options, cancellationToken);
        // One bounded recovery of an empty final answer. Do not repeat any tools or their side effects.
        var retryMessages = snapshot.Append(new ChatMessage(ChatRole.User,
            "Finish answering my latest question using the available source excerpts. Give a brief, readable answer with source links. Do not call tools or describe JSON. If facts are unavailable, state the specific limitation." )).ToArray();
        var retryOptions = Prepare(retryMessages, options);
        retryOptions.Tools = [];
        retryOptions.ToolMode = ChatToolMode.None;
        var recovered = await base.GetResponseAsync(retryMessages, retryOptions, cancellationToken);
        return await PresentPublicAnswer(recovered, snapshot, options, cancellationToken);
    }

    private async Task<ChatResponse> PresentPublicAnswer(ChatResponse response, ChatMessage[] messages,
        ChatOptions? options, CancellationToken ct)
    {
        var state = GuardrailContext.Required;
        var sources = state.WebSources;
        using var presentation = DeveloperTwinTelemetry.Activities.StartActivity("answer.present");
        presentation?.SetTag("answer.web_sources", sources.Count);
        if (sources.Count == 0 && state.WebFailure is { } failure)
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"{failure.Message} I couldn't verify updated information for this request. Please try again."))
                { ModelId = response.ModelId, Usage = response.Usage, ResponseId = response.ResponseId, FinishReason = response.FinishReason };
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(response.Text)) return response;
        var question = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (state.CanCallModel && PublicAnswerPresentation.NeedsConciseRewrite(question, response.Text))
        {
            presentation?.SetTag("answer.rewrite", true);
            var rewrite = PublicAnswerPresentation.RewriteMessages(question, response.Text, state.WebEvidence);
            // Fresh options keep inherited agent instructions/tool schemas out of the presentation pass.
            var rewriteOptions = Prepare(rewrite, new ChatOptions
            {
                Temperature = .1f, MaxOutputTokens = 1024,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(PublicAnswerPresentation.RewriteSchema(sources.Count))
            });
            rewriteOptions.Tools = [];
            rewriteOptions.ToolMode = ChatToolMode.None;
            rewriteOptions.MaxOutputTokens = Math.Min(rewriteOptions.MaxOutputTokens ?? 1024, 1024);
            try
            {
                var concise = await base.GetResponseAsync(rewrite, rewriteOptions, ct);
                if (PublicAnswerPresentation.RenderRewrite(concise.Text, sources) is { } formatted)
                    response = new ChatResponse(new ChatMessage(ChatRole.Assistant, formatted))
                        { ModelId = concise.ModelId, Usage = concise.Usage, ResponseId = concise.ResponseId, FinishReason = concise.FinishReason };
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            { /* Keep the usable original answer if optional presentation fails. */ }
        }
        var text = PublicAnswerPresentation.EnsureSources(response.Text, sources);
        if (text == response.Text) return response;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            { ModelId = response.ModelId, Usage = response.Usage, ResponseId = response.ResponseId, FinishReason = response.FinishReason };
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        await foreach (var update in base.GetStreamingResponseAsync(snapshot, Prepare(snapshot, options), cancellationToken))
            yield return update;
    }
}
