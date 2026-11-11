using System.ClientModel;
using DigitalTwin.Api;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
// Upload routes validate count and file type themselves; allow up to 15 x 8 MB documents.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 135_000_000);
var settings = builder.Configuration.GetSection("Twin").Get<TwinSettings>() ?? new TwinSettings();
if (settings.ChatContextLength is < 1024 or > 131072)
    throw new InvalidOperationException("Twin:ChatContextLength must be between 1024 and 131072.");
_ = TimeZoneInfo.FindSystemTimeZoneById(settings.ChatTimeZone);
// Observability is configured once at the host boundary (HTTP, MAF/model, tools).
builder.AddDeveloperTwinObservability();
// Critical: runtime workspace lives OUTSIDE src/DigitalTwin.Api (never compile imported code).
if (settings.Workspace == "./workspace")
    settings.Workspace = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "workspace"));
else settings.Workspace = Path.GetFullPath(settings.Workspace);
Directory.CreateDirectory(settings.Workspace);
var environmentApiKey = builder.Configuration["OPENROUTER_API_KEY"];
if (!string.IsNullOrWhiteSpace(environmentApiKey))
    settings.OpenRouterApiKey = environmentApiKey;
var provider = settings.Provider.Trim().ToLowerInvariant();
if (provider is not ("auto" or "openrouter" or "ollama"))
    throw new InvalidOperationException("Twin:Provider must be auto, openrouter or ollama.");
if (provider == "openrouter" && string.IsNullOrWhiteSpace(settings.OpenRouterApiKey))
    throw new InvalidOperationException("OPENROUTER_API_KEY is missing.");
var hosted = provider != "ollama" && !string.IsNullOrWhiteSpace(settings.OpenRouterApiKey);

// One shared instance per service: the HTTP endpoints and agent tools see the SAME state.
var projectCatalog = new ProjectCatalog(settings);
var workspace = new WorkspaceTools(settings, projectCatalog);
var knowledge = new KnowledgeStore(new HttpClient(), settings);
var vision = new VisionTools(new HttpClient(), settings);
var authorization = new ToolAuthorizationService();
var search = new WebSearchTools(new HttpClient(), authorization);
var approvals = new ApprovalQueue();
var handoffs = new HandoffTools(approvals, authorization);
builder.Services.AddSingleton(settings);
var repositoryRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
var stateDatabase = new LocalStateDatabase(Path.GetFullPath(settings.StateDatabase, repositoryRoot));
builder.Services.AddSingleton(stateDatabase);
builder.Services.AddSingleton<ConversationStore>();
var memories = new MemoryStore(stateDatabase);
builder.Services.AddSingleton(memories);
builder.Services.AddSingleton<MemoryTools>();
builder.Services.AddDeveloperTwinGuardrails(settings);
builder.Services.AddSingleton(projectCatalog);
builder.Services.AddSingleton(workspace);
builder.Services.AddSingleton(knowledge);
builder.Services.AddKnowledgeRetrieval(settings);
builder.Services.AddSingleton(vision);
builder.Services.AddSingleton<DocumentExtractionService>();
builder.Services.AddSingleton(search);
builder.Services.AddSingleton(approvals);
builder.Services.AddSingleton(handoffs);
builder.Services.AddSingleton<ProjectImporter>();
builder.Services.AddSingleton<ReportExporter>();
// Dedicated image-generation integration; it is NOT an agent tool or downloaded local model.
builder.Services.AddHttpClient<ImageGenerationService>(client => client.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddSingleton<SessionRegistry>();

// Chat defaults to the installed local qwen3:8b model. OpenRouter chat is opt-in
// through Twin:Provider=auto or openrouter; vision/PDF tools use their own provider path.
IChatClient local = new OllamaChatClient(new OllamaApiClient(new Uri(settings.OllamaUrl), settings.ChatModel), settings);
IChatClient client = local;
if (hosted)
{
    IChatClient openRouter = new OpenAI.Chat.ChatClient(
        settings.OpenRouterChatModel,
        new ApiKeyCredential(settings.OpenRouterApiKey!),
        new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") }).AsIChatClient();
    client = settings.EnableLocalFallback
        ? new HostedFirstChatClient(openRouter, local,
            LoggerFactory.Create(log => log.AddConsole()).CreateLogger<HostedFirstChatClient>())
        : openRouter;
}
// Official Microsoft.Extensions.AI chat-client instrumentation. Metadata only:
// do not capture prompts, model responses, or tool arguments from private projects.
// Instrument the outer client ONCE; otherwise model spans may be duplicated.
client = new GuardrailChatClient(client, settings).AsBuilder()
    .UseAIContextProviders(new SavedMemoryContextProvider(memories))
    .UseOpenTelemetry(sourceName: DeveloperTwinTelemetry.GenAiSourceName,
        configure: telemetry => telemetry.EnableSensitiveData = false)
    .Build();
builder.Services.AddSingleton<IChatClient>(client);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
        resource.AddService("DeveloperTwin.Api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("DeveloperTwin.*")
            .AddOtlpExporter();
    });
builder.AddAIAgent("DeveloperTwin", instructions: AgentInstructions.DeveloperTwin)
.WithAITool(AIFunctionFactory.Create(workspace.ListProject))
.WithAITool(AIFunctionFactory.Create(workspace.ReadFile))
.WithAITool(sp => AIFunctionFactory.Create(sp.GetRequiredService<KnowledgeRetrievalService>().SearchKnowledge))
.WithAITool(sp => AIFunctionFactory.Create(sp.GetRequiredService<MemoryTools>().SearchMemories))
.WithAITool(AIFunctionFactory.Create(search.SearchWeb))
.WithAITool(AIFunctionFactory.Create(handoffs.ProposeHandoff))
.WithInMemorySessionStore();

var app = builder.Build();
// Only intended for localhost demos. Use proper identity/authz/CSRF when deployed.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        var expected = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if ((!string.IsNullOrWhiteSpace(origin) && !origin.Equals(expected, StringComparison.OrdinalIgnoreCase)) || site == "cross-site")
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "Cross-site local actions are blocked." });
            return;
        }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/status", () => Results.Ok(new {
    provider = hosted ? "openrouter" : "ollama",
    chatModel = hosted ? settings.OpenRouterChatModel : settings.ChatModel,
    fallback = hosted && settings.EnableLocalFallback ? settings.ChatModel : "disabled",
    visionModel = !string.IsNullOrWhiteSpace(settings.OpenRouterApiKey)
        ? settings.OpenRouterVisionModel
        : settings.VisionModel,
    visionProvider = !string.IsNullOrWhiteSpace(settings.OpenRouterApiKey) ? "openrouter" :
        !string.IsNullOrWhiteSpace(settings.VisionModel) ? "ollama" : "off",
    pdfAnalysisProvider = string.IsNullOrWhiteSpace(settings.OpenRouterApiKey) ? "off" : "openrouter",
    openRouterApiKeyConfigured = !string.IsNullOrWhiteSpace(settings.OpenRouterApiKey),
    embeddings = settings.EmbeddingModel,
    imageGeneration = Environment.GetEnvironmentVariable("TWIN_IMAGE_PROVIDER") ?? "off",
    webSearch = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXA_API_KEY")) ? "exa" :
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAVILY_API_KEY")) ? "tavily" :
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY")) ? "brave" : "off (configure a search API key)",
    workspace = "data/workspace" // show logical location, not machine-specific full path
}));
// Explicitly describe telemetry configuration; no API keys or raw endpoint headers.
app.MapGet("/api/observability/status", (IConfiguration config) =>
{
    var o = config.GetSection("Observability").Get<ObservabilityOptions>() ?? new();
    return Results.Ok(new {
        enabled = o.Enabled,
        service = o.ServiceName,
        traces = o.Enabled,
        metrics = o.Enabled,
        logExport = o.Enabled && o.ExportLogs,
        otlpConfigured = !string.IsNullOrWhiteSpace(o.OtlpEndpoint),
        traceSampleRatio = o.TraceSampleRatio
    });
});
// Non-secret diagnostics for the demonstration. A configured key does not prove
// successful provider calls: inspect providerCalls and use the development smoke test.
app.MapGet("/api/tools/web-search/status", (WebSearchTools tool) => Results.Ok(tool.GetStatus()));

// Smoke-test the public search provider without the LLM. Development AND loopback only.
// Do not expose this endpoint in production; it consumes search API quota.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/tools/search-web", async (SearchWebTestRequest input,
        WebSearchTools tool, TwinSettings configuration, HttpContext ctx, CancellationToken ct) =>
    {
        if (ctx.Connection.RemoteIpAddress is not { } address ||
            !System.Net.IPAddress.IsLoopback(address)) return Results.NotFound();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var state = new GuardrailRunState(configuration.Guardrails, true, false, false, false, timeout, () => { });
        using var scope = GuardrailContext.Enter(state);
        var result = await tool.SearchWeb(input.Query, input.MaxResults, input.Freshness, ct);
        return Results.Content(result, "application/json");
    });
}
app.MapChatEndpoints().MapMemoryEndpoints().MapKnowledgeEndpoints().MapProjectEndpoints().MapReportEndpoints().MapImageEndpoints();
app.MapFallbackToFile("index.html");
app.Run();

// Development-only request contract for an independent provider smoke test.
public sealed record SearchWebTestRequest(string Query, int MaxResults = 3, string Freshness = "any");
