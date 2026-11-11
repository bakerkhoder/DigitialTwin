using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DigitalTwin.Api;

/// <summary>All HTTP agent runs enter the same native MAF run/function middleware pipeline.</summary>
public sealed class GuardrailAgentRunner(TwinSettings settings, SessionRegistry sessions,
    InputGuardrailService input, ToolAuthorizationService authorization, OutputValidationService output)
{
    public bool HasPrivateContext(string sessionId) => sessions.HasPrivateContext(sessionId);
    public async Task<string> RunAsync(AIAgent agent, string sessionId, string prompt, string userMessage,
        bool privateContext, bool allowWebSearch, bool requireKnowledge, CancellationToken ct, bool allowHandoffProposal = false)
    {
        if (!string.IsNullOrWhiteSpace(userMessage) && input.ValidateMessage(userMessage) is { } inputError)
            throw new GuardrailViolationException("input_denied", inputError);
        var sensitiveInput = SensitiveDataPolicy.ContainsPrivateQueryData(userMessage);
        var gate = sessions.GetLock(sessionId);
        await gate.WaitAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.Guardrails.RunTimeoutSeconds));
        var state = new GuardrailRunState(settings.Guardrails, allowWebSearch, requireKnowledge,
            allowHandoffProposal, privateContext || sessions.HasPrivateContext(sessionId) || sensitiveInput,
            timeout, () => sessions.MarkPrivate(sessionId));
        using var scope = GuardrailContext.Enter(state);
        if (state.HasPrivateContext) state.MarkPrivate();
        try
        {
            var session = await sessions.GetOrCreate(sessionId, agent, timeout.Token);
            var guarded = agent.AsBuilder()
                .Use(runFunc: RunMiddlewareAsync, runStreamingFunc: null)
                .Use(FunctionMiddlewareAsync)
                .Build();
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(settings.ChatTimeZone));
            var datedPrompt = $"Current date/time: {localNow:yyyy-MM-dd HH:mm zzz} ({settings.ChatTimeZone}).\nLatest user request:\n{prompt}";
            var result = await guarded.RunAsync(datedPrompt, session, cancellationToken: timeout.Token);
            state.ThrowIfStopped();
            if (state.DiscardSession) sessions.DiscardContext(sessionId);
            return result.Text;
        }
        catch (Exception) when (state.Aborted is not null)
        {
            sessions.DiscardContext(sessionId);
            throw state.Aborted;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            sessions.DiscardContext(sessionId);
            throw new GuardrailViolationException("run_timeout", "The agent reached its execution time limit. Narrow the request and try again.");
        }
        catch
        {
            sessions.DiscardContext(sessionId);
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task<AgentResponse> RunMiddlewareAsync(IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent next, CancellationToken ct)
    {
        using var activity = DeveloperTwinTelemetry.Activities.StartActivity("agent.run");
        var timer = Stopwatch.StartNew();
        var outcome = "ok";
        try
        {
            var snapshot = messages.ToArray();
            if (snapshot.Sum(m => (long)m.Text.Length) > 60000)
                throw new GuardrailViolationException("input_budget", "The supplied context is too large. Reduce the files or project scope.");
            var response = await next.RunAsync(snapshot, session, options, ct);
            var validated = output.Validate(response.Text, GuardrailContext.Required);
            if (validated.DiscardSession)
            {
                // A rejected or redacted model response must not remain hidden in next-turn history.
                // The session id is held by the outer run, which invalidates this cached instance below.
                GuardrailContext.Required.DiscardConversationContext();
            }
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, validated.Text));
        }
        catch (GuardrailViolationException ex)
        {
            outcome = ex.Code;
            activity?.SetStatus(ActivityStatusCode.Error, "Guardrail blocked agent execution");
            throw;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "error"; throw; }
        finally
        {
            activity?.SetTag("agent.name", "DeveloperTwin");
            activity?.SetTag("agent.outcome", outcome);
            activity?.SetTag("agent.model_calls", GuardrailContext.Required.ModelCalls);
            activity?.SetTag("agent.web_sources", GuardrailContext.Required.WebSources.Count);
            var tags = new TagList { { "outcome", outcome } };
            DeveloperTwinTelemetry.AgentRuns.Add(1, tags);
            DeveloperTwinTelemetry.AgentDuration.Record(timer.Elapsed.TotalSeconds, tags);
        }
    }

    private async ValueTask<object?> FunctionMiddlewareAsync(AIAgent agent, FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        var state = GuardrailContext.Required;
        await state.ToolGate.WaitAsync(ct);
        using var activity = DeveloperTwinTelemetry.Activities.StartActivity("guardrail.tool");
        try
        {
            var arguments = JsonSerializer.Serialize(context.Arguments.OrderBy(p => p.Key, StringComparer.Ordinal));
            state.BeforeTool(context.Function.Name, arguments);
            authorization.AuthorizeTool(context.Function.Name);
            var result = await next(context, ct);
            var text = SensitiveDataPolicy.Redact(ToolEvidenceFormatter.Format(context.Function.Name, result));
            if (context.Function.Name == "SearchWeb")
            {
                state.RecordWebEvidence(text, ToolEvidenceFormatter.WebSources(result));
                state.WebFailure = ToolEvidenceFormatter.WebFailure(result);
            }
            if (text.Length > 32000) text = text[..32000] + "\n[Tool result truncated by the context limit.]";
            return text;
        }
        catch (GuardrailViolationException ex)
        {
            activity?.SetTag("guardrail.code", ex.Code);
            activity?.SetStatus(ActivityStatusCode.Error, "Tool denied");
            return JsonSerializer.Serialize(new { error = ex.Message, code = ex.Code });
        }
        catch (UnauthorizedAccessException)
        {
            activity?.SetTag("guardrail.code", "path_denied");
            return JsonSerializer.Serialize(new { error = "The requested path is outside approved access or contains a protected file.", code = "path_denied" });
        }
        finally { state.ToolGate.Release(); }
    }
}
