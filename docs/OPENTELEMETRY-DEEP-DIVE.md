# DeveloperTwin AI — OpenTelemetry: Complete Engineering and Defense Guide

**Last updated:** 2026-11-05. **Project basis:** DeveloperTwin v4.2 with the `web-tools-and-demos` change applied. **Scope:** .NET 9 Minimal API, Microsoft Agent Framework hosting, `IChatClient` (OpenRouter or Ollama), deterministic C# tools, Qdrant, React development server.

> **Build honesty:** This patch has been checked for file/JSON/XML structure and its diff applies to the baseline. The current authoring container does not have the .NET SDK, so a real `dotnet restore`, compile, and live OTLP ingestion must be validated in your Windows environment before presenting it as tested.

## 1. Why did we add OpenTelemetry?

If a user asks, “Which officer approves a $62,000 PO?”, DeveloperTwin sends `POST /api/agent/ask`. That initiates the MAF agent; the model may invoke `SearchKnowledge`; the knowledge tool calls Ollama to embed the query, searches Qdrant, returns relevant passages, and the model answers. A slow response might be due to the chat provider, embeddings, Qdrant, tool-selection errors, or the host. **HTTP logs alone cannot identify the slow child operation**.

OpenTelemetry (OTel) provides three signals:

- **Trace**: one logical request consisting of spans. A *span* is an operation with a start/end time, `TraceId`, `SpanId`, parent, status, and attributes (tags). Typical tree: `POST /api/agent/ask` → `agent.run` → `chat ...` → `tool.SearchKnowledge` → `knowledge.retrieve` → `HTTP POST /api/embed` and `HTTP POST /points/search` → second `chat ...`. Tool calls depend on the model; do not promise all these spans will exist for every prompt.
- **Metric**: aggregation over requests, e.g. number of agent runs, duration histogram, total tool invocations, and knowledge chunks indexed. Metrics answer trends, not per-request causality.
- **Log**: an event with level, timestamp, structured properties, and often a trace/span correlation ID. For example, a Qdrant `HttpRequestException` can be related to the failing trace. Logs are emitted through the standard ASP.NET Core `ILogger` pipeline; optionally export them via OTLP.

**OpenTelemetry is instrumentation and transport, not a database or dashboard.** The SDK gathers signals; OTLP exports them; Aspire Dashboard is one destination for local viewing. Production destinations may include a Collector, Azure Monitor or Grafana backends.

## 2. Architecture and responsibility boundaries

```
React browser (port 5173)
  └── Vite proxy /api ───────> ASP.NET Core (port 5000)
                               │  ASP.NET HTTP server span (automatic)
                               └── agent.run (custom span)
                                   ├── IChatClient (instrumented; may include model spans)
                                   ├── tool.ListProject / tool.ReadFile (custom)
                                   ├── tool.SearchKnowledge (custom)
                                   │   └── knowledge.retrieve (custom)
                                   │       └── outbound HttpClient (automatic)
                                   └── tool.SearchWeb (custom)
                                       └── Tavily or Brave HttpClient (automatic)
                                            │
                                      OTel SDK in same process
                                     /           |         \
                                   traces      metrics      logs
                                     \           |         /
                                       OTLP/gRPC
                                       localhost:4317
                                            │
                                 Aspire Dashboard (localhost:18888)
```

React frontend telemetry is **not** added in this patch. The scope is intentionally the **server and tools**, matching your MAF assessment. Cross-service trace propagation is performed automatically with trace context by supported HTTP instrumentations, but third-party provider traces generally won't be visible unless the remote provider exports compatible telemetry under your control.

## 3. What source files changed (and why)

| File | Responsibility |
|---|---|
| `src/DigitalTwin.Api/Observability/DeveloperTwinTelemetry.cs` | Central names for `ActivitySource`, `Meter`, instruments, `ObservabilityOptions`, and host-level OTLP configuration. |
| `src/DigitalTwin.Api/DigitalTwin.Api.csproj` | Pin the OpenTelemetry packages to the same release family. |
| `src/DigitalTwin.Api/Program.cs` | Invoke `builder.AddDeveloperTwinObservability()` once, instrument the outer `IChatClient` with sensitive content disabled, add a non-secret status endpoint. |
| `Endpoints/ChatEndpoints.cs` | Record logical `agent.run`, outcome, and duration around both chat routes. |
| `Services/KnowledgeStore.cs` | Record `tool.SearchKnowledge`, `knowledge.retrieve`, `knowledge.ingest`, chunk counter. |
| `Services/WebSearchTools.cs` | Record `tool.SearchWeb`; provider HTTP requests are traced through `HttpClient` instrumentation. |
| `Services/WorkspaceTools.cs`, `Services/HandoffTools.cs` | Track local tool operations without logging arguments or content. |
| `appsettings.json` | Non-secret observability defaults. |
| `scripts/start-observability.ps1` | Optional local Aspire Dashboard container, separate from the backend/frontend startup scripts. |

### Why separate observability from business services?

`Program.cs` registers one OTel pipeline; tools emit spans/counters through a small helper. Business services should not know about exporter URLs, Jaeger, Aspire, or Azure. Changing destinations should **not** require editing the RAG or project-inspection code. This is a small-scale clean architecture principle: instrumentation belongs in infrastructure; tool operations remain deterministic.

## 4. OpenTelemetry's .NET constructs and exact parameters

### `ActivitySource` and `Activity`

`DeveloperTwinTelemetry.Activities` is a `System.Diagnostics.ActivitySource("DeveloperTwin.Operations")`. `StartActivity("knowledge.retrieve")` starts a span if an appropriate tracing listener is active. `Activity.Current` is propagated via async context. Child spans inherit a trace ID from the active parent. `using var activity` reliably ends the span on disposal.

```
using var span = DeveloperTwinTelemetry.Activities.StartActivity("knowledge.retrieve");
span?.SetTag("knowledge.requested_hits", maxResults);  // Safe bounded integer.
```

No prompt, query, filename, exact folder path, raw response, token, email address or full project code is added as a tag. This prevents accidental leakage into a telemetry backend. `SetStatus(ActivityStatusCode.Error, "...generic...")` marks errors. Avoid placing raw exception messages in span attributes when they might contain secrets.

### `Meter`, `Counter<T>` and `Histogram<T>`

`Meter("DeveloperTwin.Operations")` is the source of project-specific metrics. `Counter<long>` only increases; `Histogram<double>` records a numerical distribution. Example:

```
DeveloperTwinTelemetry.AgentRuns.Add(1, new TagList { { "outcome", "ok" } });
DeveloperTwinTelemetry.AgentDuration.Record(1.42, new TagList { { "outcome", "ok" } });
```

Available instruments:

| Name | Type / unit | Interpretation |
|---|---|---|
| `developertwin.agent.runs` | Counter, runs | Agent attempts, grouped by `outcome` (`ok`, `error`, `cancelled`). |
| `developertwin.agent.duration` | Histogram, seconds | End-to-end model + tools in `RunAsync`; excludes earlier file extraction before the agent call. |
| `developertwin.tool.calls` | Counter, calls | Calls to instrumented tools, grouped by fixed `tool.name` and `outcome`. |
| `developertwin.tool.duration` | Histogram, seconds | Tool execution duration. |
| `developertwin.knowledge.chunks` | Counter, chunks | Successful chunks indexed after Qdrant write. |
| `http.server.request.duration` | HTTP instrumentation | Automatic inbound ASP.NET request latency. |
| `http.client.request.duration` | HTTP instrumentation | Automatic outgoing HTTP latency. |

**Low-cardinality labels are important:** `tool.name` is one of five known names; `outcome` has a few fixed values. Never use per-request `sessionId`, raw query, full path, `TraceId`, or username as a metric label: those could create millions of time series and high telemetry costs.

### `AddOpenTelemetry()`, `.WithTracing()`, `.WithMetrics()`

`builder.Services.AddOpenTelemetry()` integrates tracer and meter provider lifetimes with dependency injection. `ConfigureResource(...AddService(...))` adds `service.name` and `service.version` metadata. `WithTracing` specifies sources and instrumentation; `WithMetrics` specifies meters. Both can have exporters. The dedicated `builder.Logging.AddOpenTelemetry()` is independently enabled only with `ExportLogs=true`.

Automatic tracing enabled by the patch:

- `.AddAspNetCoreInstrumentation()`: incoming HTTP requests; filters repetitive `/api/status` and `/api/knowledge/health` spans, but HTTP metrics still count those requests.
- `.AddHttpClientInstrumentation()`: outgoing requests to OpenRouter/Ollama, Qdrant, Tavily or Brave, with URL query-value redaction as provided by the SDK defaults.
- `.AddSource("DeveloperTwin.Operations")`: our custom spans.
- `.AddSource("DeveloperTwin.GenAI")`: model instrumentation from `IChatClient.AsBuilder().UseOpenTelemetry(...)`. The meter provider also subscribes to this name and MAF's experimental meter names; token-usage metrics appear only when the installed framework/provider actually reports them.
- `.AddSource("Experimental.Microsoft.Agents.AI")` and related agent/chat-client sources: listens if the installed MAF version emits these spans. **Listening alone does not activate instrumentation in a MAF implementation that does not emit them.** Verify actual span names in Aspire before claiming native MAF spans.

`UseOpenTelemetry(sourceName: ..., configure: cfg => cfg.EnableSensitiveData = false)` wraps the outer `IChatClient` once. We avoid instrumenting both an inner provider and outer agent indiscriminately, which could duplicate spans.

### Sampling

`TraceSampleRatio` is a number from 0 to 1. With `1.0`, all root traces are sampled, best for a small demo. With `0.1`, roughly 10% of new root traces are retained. The patch uses `ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))`; sampled incoming traces stay sampled downstream. **Sampling affects tracing only**, not metric counting; it is not an API rate limit. For a demonstration, use `1.0` or your test trace might be missing.

### Exporter and endpoint

`OtlpEndpoint`: empty string disables network export. `http://127.0.0.1:4317` uses OTLP/gRPC with the patch's `OtlpExportProtocol.Grpc`. `ConsoleExporter=true` prints telemetry locally (development only). `ExportLogs=true` explicitly enables OTLP/console logging in addition to existing ASP.NET console logs. The Aspire Dashboard port `18888` is **only the UI**; it is not the destination for OTLP. Avoid exporting raw logs from private codebases without a privacy review.

## 5. Configuration: actual appsettings and environment overrides

The patch adds the following section to `src/DigitalTwin.Api/appsettings.json`:

```json
"Observability": {
  "Enabled": true,
  "ServiceName": "developertwin-api",
  "ServiceVersion": "4.2-training",
  "OtlpEndpoint": "",
  "ConsoleExporter": false,
  "ExportLogs": false,
  "TraceSampleRatio": 1.0
}
```

.NET configuration maps environment variables with `__` to `:` in settings keys:

```
$env:Observability__Enabled = "true"
$env:Observability__OtlpEndpoint = "http://127.0.0.1:4317"
$env:Observability__TraceSampleRatio = "1.0"
$env:Observability__ExportLogs = "true"
```

Set these in the **same shell that launches the backend**, before `dotnet watch run`. Restart to apply changes. There are no secrets in this section. For real remote collectors, prefer HTTPS and proper authentication, passed through secrets/environment, not committed to Git. This training patch deliberately targets local unauthenticated OTLP only; harden for production before remote exporting.

## 6. Start locally on Windows (3 terminals)

**Prerequisite:** Docker Desktop, .NET 9 SDK, current project build, configured OpenRouter or local Ollama, Qdrant already running for knowledge queries.

**Terminal A — Aspire Dashboard (once):**

```powershell
cd E:\MafTraining\developer-digital-twin
.\scripts\start-observability.ps1
# Read the login URL from:
docker logs aspire-dashboard
```

Aspire UI: `http://127.0.0.1:18888`; OTLP/gRPC receiver: `http://127.0.0.1:4317`. These do not conflict with Qdrant 6333 or React 5173. If 4317 is already occupied, inspect the owning service; do not create a second receiver on the same port.

**Terminal B — Backend:**

```powershell
cd E:\MafTraining\developer-digital-twin\src\DigitalTwin.Api
$env:Observability__OtlpEndpoint = "http://127.0.0.1:4317"
$env:Observability__ExportLogs = "true"
$env:Observability__TraceSampleRatio = "1.0"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet restore
dotnet build
dotnet watch run --no-launch-profile --urls http://127.0.0.1:5000
```

**Terminal C — React (unchanged):**

```powershell
cd E:\MafTraining\developer-digital-twin\frontend
npm run dev
```

Visit `http://127.0.0.1:5173`. The Vite dev server proxies API routes to `5000`.

**Health/configuration check:**

```
Invoke-RestMethod http://127.0.0.1:5000/api/observability/status
```

Expect `enabled=true`, `otlpConfigured=true`, `traceSampleRatio=1` and `logExport=true` when above settings were supplied. This status endpoint reports configuration only; it does not prove that the receiver accepted data. To verify ingestion, open the Aspire **Traces** page after generating a request.

## 7. Demonstration plan and expected spans

1. Go to React Assistant and ask: **"Explain what a relational primary key is."** Expect HTTP + `agent.run` + model-related spans. No RAG or web tool normally needed.
2. In the Knowledge page, upload `01-procurement-approval-policy.txt`. Expect `knowledge.ingest` and HTTP calls to Ollama embed and Qdrant. The `developertwin.knowledge.chunks` counter increases.
3. Ask: **"According to DEMO-POL-001, who approves $62,000 and how many vendor quotes are required?"** Expect `tool.SearchKnowledge` and `knowledge.retrieve` if the model called the tool. It should answer CFO and at least three quotes.
4. Ask: **"Find current official Microsoft Agent Framework function-tool documentation, cite the URL."** With Tavily/Brave configured, expect `tool.SearchWeb` and outgoing HTTP client span; otherwise a structured unconfigured result.
5. Stop Qdrant temporarily (ONLY if safe and you know the volume/container) and repeat retrieval. The trace should reveal failing HTTP/knowledge operations. Restore Qdrant afterward. Do not delete persistent volumes.
6. Use the Aspire **Metrics** tab: look for `developertwin.agent.runs`, `developertwin.tool.calls`, and duration histograms. The histogram/export interval may delay metrics appearing briefly.
7. If logs are enabled, view the **Structured Logs** tab. Correlate relevant logs with trace context where supported. By default, custom spans tag only safe metadata; do not enable GenAI sensitive content capture.

### What if a span is missing?

- Did the agent actually call the tool? A model can answer from its own knowledge without a tool.
- Is telemetry enabled, and did you configure `OtlpEndpoint` in the correct process?
- Does the ActivitySource string match `.AddSource(...)` exactly?
- Is sampling `1.0` during the demo?
- Is the backend using a provider with tool calling? `qwen3:8b` may select tools differently from OpenRouter free models.
- Does the Aspire UI show the `developertwin-api` service? If not, inspect `docker logs aspire-dashboard` and OTLP port bindings.
- Are `Qdrant` and Ollama embeddings healthy? `GET /api/knowledge/health` HTTP 200 alone doesn't mean the JSON says Qdrant is healthy.
- MAF's built-in instrumentations and GenAI semantic conventions evolve across versions; do not claim native `invoke_agent` / token metrics until observed from your installed framework/provider.

## 8. Safety and cost best practices

1. **No prompts, raw documents, API keys, filenames, internal project paths or PII in telemetry**. `EnableSensitiveData=false` and the custom spans record stable names/counts only.
2. **Never enable** `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` or the MAF equivalent in a confidential workspace, unless authorized and telemetry retention/ACLs are reviewed.
3. Bind the Aspire Dashboard and OTLP receiver to `127.0.0.1` locally; keep its browser-token login. Its standalone OTLP receiver is not automatically authenticated.
4. Use the standard OTLP protocol so exporter destinations remain interchangeable. In production, use an OpenTelemetry Collector and authenticated transport where required.
5. **Metric cardinality** must remain low. Never tag durations with user IDs, URL query strings, or exact project paths.
6. Retain errors and latency metadata, but filter health checks from traces to reduce noise.
7. Preserve normal `ILogger` logs even if telemetry export is disabled; observing a 500 should not depend on a remote collector.
8. Use bounded trace sampling and sane retention for production. A dashboard is not an audit log: audit approvals are authoritative business data and need dedicated durable storage.
9. Distinguish model usage from `agent.run` duration. End-to-end latency includes tool and network time; token cost may be absent when using free providers or local models.
10. Test failure behavior (OpenRouter quota, Qdrant offline, Tavily unavailable) and verify request IDs/trace correlation before a production demo.

## 9. Useful interview questions and concise responses

**Q: What is the difference between trace and span?** A trace describes one end-to-end operation; spans represent timed steps within that operation. Trace IDs link all spans; parent/span IDs express nesting.

**Q: How do OTel, OTLP, Aspire, and Jaeger differ?** OTel is the instrumentation framework; OTLP is the export protocol; Aspire/Jaeger are visualization destinations (Aspire handles traces, logs, metrics; Jaeger focuses on traces).

**Q: What is auto vs manual instrumentation?** ASP.NET Core and `HttpClient` are automatically instrumented via packages. DeveloperTwin's `agent.run`, knowledge retrieval, web search, and project tool spans are manually inserted using `ActivitySource`.

**Q: Why `ActivitySource`/`Meter` instead of manually writing every event to the database?** They use standard .NET APIs, make telemetry vendor-neutral, provide sampling and aggregation, and avoid coupling business code to a telemetry destination.

**Q: What is sampling?** Retaining a subset of traces. It reduces volume/cost without changing application requests or business results. We use 100% in a small demo and would typically lower it in production.

**Q: Why are metric labels controlled?** High-cardinality labels create very large numbers of series. We use fixed tool names and outcomes only.

**Q: What is `service.name`?** A resource attribute identifying telemetry from the application (`developertwin-api`), used for grouping in the dashboard. It is not the human-readable agent name and not the export URL.

**Q: Can you trust telemetry as proof of tool execution?** Yes when you see a span from the actual tool code and correlated outcome; a model saying "I searched" is not evidence. But sampling, incomplete instrumentation, or export failures can hide spans.

**Q: How is this related to MAF?** We use `AddAIAgent` to register a hosted agent and function tools. We instrument the `IChatClient`, the agent execution boundary, and the tools. MAF has native OpenTelemetry support, but which framework-provided spans are present depends on the installed MAF package version and how instrumentation is enabled.

**Q: Does OpenTelemetry replace business audit logs?** No. Security approvals, actor identity and policy decisions should be stored durably and reliably by the business service. OpenTelemetry is diagnostic and may be sampled, exported asynchronously, or dropped.

## 10. Official readings

- Microsoft Agent Framework observability: https://learn.microsoft.com/en-us/agent-framework/agents/observability
- Microsoft OpenTelemetry .NET distro MAF guide: https://github.com/microsoft/opentelemetry-distro-dotnet/blob/main/docs/agent-framework-getting-started.md
- OpenTelemetry .NET signals/instrumentation: https://opentelemetry.io/docs/languages/dotnet/instrumentation/
- Aspire standalone Dashboard: https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone
- OpenTelemetry .NET tracing and exporters: https://opentelemetry.io/docs/languages/dotnet/traces/jaeger/
- GenAI semantic conventions: https://opentelemetry.io/docs/specs/semconv/gen-ai/
- .NET HTTP query redaction: https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.Http/README.md
