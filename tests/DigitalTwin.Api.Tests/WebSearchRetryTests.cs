using System.Net;
using System.Text.Json;
using DigitalTwin.Api;
using Microsoft.Extensions.AI;
using Xunit;
using static DigitalTwin.Api.Tests.GuardrailTestHarness;

namespace DigitalTwin.Api.Tests;

// Provider selection uses process environment variables. Isolate these tests from
// other collections and restore the original value after every test.
[CollectionDefinition("Web search provider environment", DisableParallelization = true)]
public sealed class WebSearchProviderEnvironmentCollection;

[Collection("Web search provider environment")]
public sealed class WebSearchRetryTests : IDisposable
{
    private readonly string? _originalExaKey = Environment.GetEnvironmentVariable("EXA_API_KEY");

    public WebSearchRetryTests() => Environment.SetEnvironmentVariable("EXA_API_KEY", "test-only-not-a-real-key");
    public void Dispose() => Environment.SetEnvironmentVariable("EXA_API_KEY", _originalExaKey);

    [Theory]
    [InlineData("timeout")]
    [InlineData("network")]
    [InlineData("408")]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    public async Task TemporaryFailureRecoversWithinTheSameSearch(string failure)
    {
        using var handler = new SearchHandler((attempt, ct) => attempt == 1 ? Fail(failure, ct) : Task.FromResult(Success()));
        using var http = new HttpClient(handler);
        var web = new WebSearchTools(http, new ToolAuthorizationService(), TimeSpan.FromMilliseconds(100));

        using var result = JsonDocument.Parse(await web.SearchWeb("Beirut weather today"));

        Assert.Equal("ok", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("https://example.test/beirut", result.RootElement.GetProperty("results")[0].GetProperty("Url").GetString());
        Assert.Equal(2, handler.Calls);
        Assert.NotSame(handler.Requests[0], handler.Requests[1]);
        Assert.Equal(handler.Payloads[0], handler.Payloads[1]);
        var diagnostics = JsonSerializer.SerializeToElement(web.GetStatus());
        Assert.Equal(2, diagnostics.GetProperty("providerCalls").GetInt32());
        Assert.Equal(1, diagnostics.GetProperty("providerRetries").GetInt32());
        Assert.Equal("ok", diagnostics.GetProperty("lastOutcome").GetString());
    }

    [Theory]
    [InlineData("timeout", "timeout")]
    [InlineData("network", "network_error")]
    [InlineData("503", "provider_error")]
    public async Task PersistentFailureStopsAfterTwoAttempts(string failure, string expectedStatus)
    {
        using var handler = new SearchHandler((_, ct) => Fail(failure, ct));
        using var http = new HttpClient(handler);
        var web = new WebSearchTools(http, new ToolAuthorizationService(), TimeSpan.FromMilliseconds(100));

        using var result = JsonDocument.Parse(await web.SearchWeb("Beirut weather today"));

        Assert.Equal(expectedStatus, result.RootElement.GetProperty("status").GetString());
        Assert.Empty(result.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(2, handler.Calls);
        if (failure == "timeout")
            Assert.Contains("2 attempts", result.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task InvalidRequestsCredentialsAndQuotasAreNotRetried(int status)
    {
        using var handler = new SearchHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var web = new WebSearchTools(http, new ToolAuthorizationService());

        using var result = JsonDocument.Parse(await web.SearchWeb("Beirut weather today"));

        Assert.Equal("provider_error", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidProviderJsonIsNotRetried()
    {
        using var handler = new SearchHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") }));
        using var http = new HttpClient(handler);
        var web = new WebSearchTools(http, new ToolAuthorizationService());

        using var result = JsonDocument.Parse(await web.SearchWeb("Beirut weather today"));

        Assert.Equal("provider_error", result.RootElement.GetProperty("status").GetString());
        Assert.Contains("invalid JSON", result.RootElement.GetProperty("message").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationStopsWithoutRetrying()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new SearchHandler((_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Success());
        });
        using var http = new HttpClient(handler);
        var web = new WebSearchTools(http, new ToolAuthorizationService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => web.SearchWeb("Beirut weather today", ct: cancellation.Token));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MafReceivesRecoveredEvidenceWithoutAnotherUserTurnOrModelToolCall()
    {
        using var handler = new SearchHandler((attempt, ct) => attempt == 1 ? Fail("503", ct) : Task.FromResult(Success()));
        using var http = new HttpClient(handler);
        var web = new WebSearchTools(http, new ToolAuthorizationService());
        using var harness = new GuardrailTestHarness((turn, messages, _) =>
        {
            if (turn == 1) return Task.FromResult(Call(turn, "SearchWeb", new() { ["query"] = "Beirut weather today" }));
            Assert.Contains(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains("Cloudy, 18") == true);
            return Task.FromResult(Text("For Beirut: cloudy, 18 degrees Celsius."));
        }, [AIFunctionFactory.Create(web.SearchWeb)]);

        var answer = await harness.Run("How is the weather in Lebanon today?");

        Assert.Contains("18 degrees Celsius", answer);
        Assert.Contains("https://example.test/beirut", answer);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, harness.Model.Calls);
    }

    private static async Task<HttpResponseMessage> Fail(string failure, CancellationToken ct)
    {
        if (failure == "timeout") await Task.Delay(Timeout.Infinite, ct);
        if (failure == "network") throw new HttpRequestException("Simulated connection failure.");
        return new HttpResponseMessage((HttpStatusCode)int.Parse(failure));
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"results":[{"title":"Beirut forecast","url":"https://example.test/beirut","text":"Cloudy, 18 degrees Celsius."}]}""")
    };

    private sealed class SearchHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Payloads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await respond(++Calls, cancellationToken);
        }
    }
}
