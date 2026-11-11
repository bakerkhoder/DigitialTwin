using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace DigitalTwin.Api;

/// <summary>Local provider options and readable tool messages; MAF retains the original tool history.</summary>
public sealed class OllamaChatClient(IChatClient inner, TwinSettings settings) : DelegatingChatClient(inner)
{
    private ChatOptions Prepare(ChatOptions? options)
    {
        var prepared = options?.Clone() ?? new ChatOptions();
        var previousFactory = prepared.RawRepresentationFactory;
        prepared.RawRepresentationFactory = client =>
        {
            var request = previousFactory?.Invoke(client) as ChatRequest ?? new ChatRequest();
            request.Think ??= settings.EnableChatThinking;
            request.Options ??= new RequestOptions();
            request.Options.NumCtx ??= settings.ChatContextLength;
            request.Options.Temperature ??= prepared.Temperature ?? .2f;
            return request;
        };
        return prepared;
    }

    private static IEnumerable<ChatMessage> PrepareMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length == 0) { yield return message; continue; }
            // Ollama's native tool messages accept text; avoid the SDK's extra JSON result wrapper.
            // Only transform the provider snapshot, preserving MAF call ids and session state.
            var other = message.Contents.Where(c => c is not FunctionResultContent).ToList();
            if (other.Count > 0) yield return new ChatMessage(message.Role, other);
            foreach (var result in results)
            {
                var text = result.Result is JsonElement { ValueKind: JsonValueKind.String } element
                    ? element.GetString() ?? "" : result.Result?.ToString() ?? "";
                yield return new ChatMessage(ChatRole.Tool, text);
            }
        }
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(PrepareMessages(messages).ToArray(), Prepare(options), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(PrepareMessages(messages).ToArray(), Prepare(options), cancellationToken))
            yield return update;
    }
}
