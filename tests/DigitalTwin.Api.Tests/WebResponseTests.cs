using System.Net;
using System.Text.Json;
using DigitalTwin.Api;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Xunit;
using static DigitalTwin.Api.Tests.GuardrailTestHarness;

namespace DigitalTwin.Api.Tests;

public sealed class WebResponseTests
{
    [Fact]
    public async Task LongWeatherReplyIsRewrittenWithRealSourcesAndCorrectedHistory()
    {
        var searches = 0;
        var source = Tool((string query) =>
        {
            searches++;
            return JsonSerializer.Serialize(new
            {
                status = "ok", results = new[] { new { Title = "Beirut forecast", Url = "https://example.test/beirut", Snippet = "Beirut is 18 °C and cloudy." } }
            });
        }, "SearchWeb");
        using var harness = new GuardrailTestHarness((turn, messages, _) =>
        {
            if (turn == 1) return Task.FromResult(Call(turn, "SearchWeb", new() { ["query"] = "Beirut weather today" }));
            if (turn == 2) return Task.FromResult(Text("Unnecessarily long draft\n" + string.Join('\n', Enumerable.Repeat("- Unrequested detail", 8))));
            if (turn == 3) return Task.FromResult(Text("{\"summary\":\"For Beirut: conditions vary elsewhere in Lebanon.\",\"details\":[\"Cloudy, 18 °C.\"],\"sources\":[1]}"));
            Assert.Contains(messages, message => message.Role == ChatRole.Assistant && message.Text.Contains("For Beirut:") && message.Text.Contains("https://example.test/beirut"));
            Assert.DoesNotContain(messages, message => message.Role == ChatRole.Assistant && message.Text.Contains("Unnecessarily long draft"));
            return Task.FromResult(Text("The follow-up retains the corrected answer."));
        }, [source]);
        var answer = await harness.Run("How is the weather in Lebanon today?");
        Assert.Contains("For Beirut:", answer);
        Assert.Contains("https://example.test/beirut", answer);
        Assert.Equal(1, searches);
        Assert.Equal(3, harness.Model.Calls);
        Assert.Equal(0, harness.Model.LastToolCount);
        await harness.Run("And tomorrow?");
    }

    [Fact]
    public void ActualSourcesAreAppendedWithoutInventingLinksOrDuplicatingCitations()
    {
        WebSource[] sources = [new("Actual source", "https://example.test/actual")];
        var answer = PublicAnswerPresentation.EnsureSources("A brief answer.", sources);
        Assert.Contains("https://example.test/actual", answer);
        Assert.Equal(answer, PublicAnswerPresentation.EnsureSources(answer, sources));
    }

    [Fact]
    public void StructuredWeatherFormattingUsesRealSourceNumbersAndDoesNotEmitJson()
    {
        WebSource[] sources = [new("Real forecast", "https://example.test/weather")];
        var result = PublicAnswerPresentation.RenderRewrite("{\"summary\":\"Beirut forecast\",\"details\":[\"Cloudy, 18 °C\",\"No rain expected\"],\"sources\":[1]}", sources);
        Assert.Contains("- Cloudy, 18 °C", result);
        Assert.Contains("https://example.test/weather", result);
        Assert.DoesNotContain("\"summary\"", result);
        Assert.Null(PublicAnswerPresentation.RenderRewrite("{\"summary\":\"Weather\",\"details\":[\"Cloudy\"],\"sources\":[99]}", sources));
    }

    [Fact]
    public async Task FailedSearchReportsActualCauseInsteadOfInventingLiveFacts()
    {
        var tool = Tool(() => "{\"status\":\"timeout\",\"message\":\"The search provider timed out.\",\"results\":[]}", "SearchWeb");
        using var harness = new GuardrailTestHarness((turn, _, _) => Task.FromResult(turn == 1
            ? Call(turn, "SearchWeb") : Text("The weather is definitely 99 degrees.")), [tool]);
        var answer = await harness.Run("What is the weather today?");
        Assert.Contains("provider timed out", answer);
        Assert.DoesNotContain("99 degrees", answer);
        Assert.DoesNotContain("cannot access the internet", answer);
    }

    [Fact]
    public void SearchExcerptsAreReadableWithUnicodeAndSourceLinks()
    {
        var json = JsonSerializer.Serialize(new
        {
            status = "ok", retrievedAtUtc = "2026-11-11T10:00:00Z",
            results = new[] { new { Title = "Weather in Beirut", Url = "https://example.test/weather", Snippet = "بيروت: cloudy, 18 °C." } }
        });
        var result = ToolEvidenceFormatter.Format("SearchWeb", json);
        Assert.Contains("بيروت", result);
        Assert.Contains("18 °C", result);
        Assert.Contains("https://example.test/weather", result);
        Assert.DoesNotContain("\\u", result);
        Assert.DoesNotContain("\"data\"", result);
    }

    [Fact]
    public void ProviderErrorsRetainSpecificCause()
    {
        var result = ToolEvidenceFormatter.Format("SearchWeb", "{\"status\":\"provider_error\",\"message\":\"Free quota reached.\",\"results\":[]}");
        Assert.Contains("Free quota reached", result);
        Assert.DoesNotContain("no internet", result);
    }

    [Fact]
    public async Task EmptyFinalAnswerGetsOneToolFreeRecoveryWithoutRepeatingSideEffects()
    {
        var executed = 0;
        var tool = Tool(() => { executed++; return "Source evidence"; }, "ReadFile");
        using var harness = new GuardrailTestHarness((turn, messages, _) => Task.FromResult(turn switch
        {
            1 => Call(turn, "ReadFile"),
            2 => Text(""),
            _ => Text("Here is the requested summary.")
        }), [tool]);
        Assert.Contains("requested summary", await harness.Run());
        Assert.Equal(1, executed);
        Assert.Equal(3, harness.Model.Calls);
        Assert.Equal(0, harness.Model.LastToolCount);
    }

    [Fact]
    public async Task RepeatedEmptyOutputStopsAfterSingleRecovery()
    {
        using var harness = new GuardrailTestHarness((_, _, _) => Task.FromResult(Text("")));
        var error = await Assert.ThrowsAsync<GuardrailViolationException>(() => harness.Run());
        Assert.Equal("empty_output", error.Code);
        Assert.Equal(2, harness.Model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OllamaWireRequestSetsThinkingContextAndPlainToolEvidence(bool thinking)
    {
        using var handler = new OllamaWireHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test") };
        var settings = new TwinSettings { EnableChatThinking = thinking };
        using var client = new OllamaChatClient(new OllamaApiClient(http, settings.ChatModel), settings);
        var original = new FunctionResultContent("call-1", "Source: Beirut\nبيروت: 18 °C\nURL: https://example.test/weather");
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the weather?"), new ChatMessage(ChatRole.Tool, [original])],
            new ChatOptions { MaxOutputTokens = 64 });
        Assert.Equal("READY", response.Text);
        var request = handler.Request!.Value;
        Assert.Equal(thinking, request.GetProperty("think").GetBoolean());
        Assert.Equal(8192, request.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(64, request.GetProperty("options").GetProperty("num_predict").GetInt32());
        var evidence = request.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("بيروت", evidence);
        Assert.DoesNotContain("\\u", evidence);
        Assert.DoesNotContain("CallId", evidence);
        Assert.DoesNotContain("\"Result\"", evidence);
        Assert.Equal("call-1", original.CallId); // MAF history was not modified.
    }

    private sealed class OllamaWireHandler : HttpMessageHandler
    {
        public JsonElement? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Request = json.RootElement.Clone();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"model\":\"qwen3:8b\",\"created_at\":\"2026-11-11T10:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"READY\"},\"done\":true,\"done_reason\":\"stop\"}\n",
                    System.Text.Encoding.UTF8, "application/x-ndjson")
            };
        }
    }
}
