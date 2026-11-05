using Microsoft.Extensions.AI;

namespace DigitalTwin.Api;

/// <summary>
/// Sends normal (non-streaming) MAF agent calls to OpenRouter first.
/// If the hosted request fails, optionally retry using the user's ALREADY INSTALLED Ollama model.
/// Note: streaming follows the hosted provider only; no fallback after partial output.
/// There is no local-first retry (which would upload sensitive documents unexpectedly).
/// </summary>
public sealed class HostedFirstChatClient(
    IChatClient hosted,
    IChatClient local,
    ILogger<HostedFirstChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Snapshot turns so a retry sends EXACTLY the same input and tool specifications.
        var history = messages.ToArray();
        try
        {
            return await hosted.GetResponseAsync(history, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                  ex is not OperationCanceledException &&
                                  ex is not ArgumentException &&
                                  ex is not UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "OpenRouter request failed; falling back to local Ollama chat model.");
            return await local.GetResponseAsync(history, options, cancellationToken);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => hosted.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : hosted.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        hosted.Dispose();
        local.Dispose();
    }
}
