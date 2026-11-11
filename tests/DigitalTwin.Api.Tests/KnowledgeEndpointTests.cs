using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DigitalTwin.Api.Tests;

public sealed class KnowledgeEndpointTests
{
    [Fact]
    public async Task UploadSearchDeprecateRestoreAndDeleteThroughHttp()
    {
        await using var host = await EndpointHost.StartAsync();
        using var first = await host.Upload("policy.txt", "Outside allowed roots, human approval is required.");
        var firstResult = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.False(firstResult.GetProperty("unchanged").GetBoolean());
        Assert.Equal("local-text", firstResult.GetProperty("extraction").GetString());
        using var repeated = await host.Upload("policy.txt", "Outside allowed roots, human approval is required.");
        var repeatResult = (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.True(repeatResult.GetProperty("unchanged").GetBoolean());
        Assert.Equal(1, host.Backend.Embeddings);
        var document = Assert.Single((await host.Http.GetFromJsonAsync<KnowledgeDocument[]>("/api/knowledge/documents"))!);
        var diagnostics = (await host.Http.GetFromJsonAsync<KnowledgeSearchResult>("/api/knowledge/search/diagnostics?q=approval"))!;
        Assert.Single(diagnostics.Candidates);
        Assert.Single(diagnostics.Hits);

        using var deprecated = await host.Http.PatchAsJsonAsync($"/api/knowledge/documents/{document.Id}/status", new { status = "deprecated" });
        Assert.Equal(HttpStatusCode.NoContent, deprecated.StatusCode);
        Assert.Empty((await host.Http.GetFromJsonAsync<KnowledgeHit[]>("/api/knowledge/search?q=approval"))!);
        Assert.Single(host.Backend.Points);

        using var restored = await host.Http.PatchAsJsonAsync($"/api/knowledge/documents/{document.Id}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Single((await host.Http.GetFromJsonAsync<KnowledgeHit[]>("/api/knowledge/search?q=approval"))!);
        using var deleted = await host.Http.DeleteAsync($"/api/knowledge/documents/{document.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await host.Http.GetFromJsonAsync<KnowledgeDocument[]>("/api/knowledge/documents"))!);
        Assert.Empty(host.Backend.Points);
    }

    [Fact]
    public async Task InvalidQueriesAndStatusUpdatesReturnClientErrors()
    {
        await using var host = await EndpointHost.StartAsync();
        using var query = await host.Http.GetAsync("/api/knowledge/search?q=%20");
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        using var tooLong = await host.Http.GetAsync("/api/knowledge/search/diagnostics?q=" + new string('a', 501));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using var id = await host.Http.PatchAsJsonAsync("/api/knowledge/documents/invalid/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.BadRequest, id.StatusCode);
        using var status = await host.Http.PatchAsJsonAsync("/api/knowledge/documents/aaaaaaaaaaaaaaaaaaaaaaaa/status", new { status = "approved" });
        Assert.Equal(HttpStatusCode.BadRequest, status.StatusCode);
        using var missing = await host.Http.PatchAsJsonAsync("/api/knowledge/documents/aaaaaaaaaaaaaaaaaaaaaaaa/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, host.Backend.Embeddings);
    }

    private sealed class EndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _directory;
        public KnowledgeBackend Backend { get; }
        public HttpClient Http { get; }

        private EndpointHost(WebApplication app, string directory, KnowledgeBackend backend)
        {
            _app = app;
            _directory = directory;
            Backend = backend;
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        }

        public static async Task<EndpointHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var backend = new KnowledgeBackend();
            var directory = Path.Combine(Path.GetTempPath(), "twin-endpoints-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            backend.Settings.Workspace = directory;
            builder.Services.AddSingleton(backend.Settings);
            builder.Services.AddSingleton(backend.Store);
            builder.Services.AddSingleton(backend.Retrieval(new NoOpReranker()));
            builder.Services.AddSingleton<SessionRegistry>();
            builder.Services.AddSingleton<ProjectCatalog>();
            builder.Services.AddSingleton<WorkspaceTools>();
            builder.Services.AddSingleton(new VisionTools(new HttpClient(backend, disposeHandler: false), backend.Settings));
            builder.Services.AddSingleton<DocumentExtractionService>();
            var app = builder.Build();
            app.MapKnowledgeEndpoints();
            await app.StartAsync();
            return new(app, directory, backend);
        }

        public async Task<HttpResponseMessage> Upload(string filename, string content)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(content), "files", filename);
            var response = await Http.PostAsync("/api/knowledge/batch", form);
            response.EnsureSuccessStatusCode();
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.DisposeAsync();
            Backend.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
