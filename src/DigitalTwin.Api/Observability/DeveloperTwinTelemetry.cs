using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DigitalTwin.Api;

/// <summary>
/// One place for names, instruments and safe telemetry configuration.
/// Never attach user prompts, file contents, full paths or API keys to spans.
/// </summary>
public static class DeveloperTwinTelemetry
{
    public const string SourceName = "DeveloperTwin.Operations";
    public const string GenAiSourceName = "DeveloperTwin.GenAI";
    public const string MeterName = "DeveloperTwin.Operations";

    public static readonly ActivitySource Activities = new(SourceName);
    private static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> AgentRuns = Meter.CreateCounter<long>(
        "developertwin.agent.runs", "{run}", "Completed agent attempts");
    public static readonly Histogram<double> AgentDuration = Meter.CreateHistogram<double>(
        "developertwin.agent.duration", "s", "Agent request duration");
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        "developertwin.tool.calls", "{call}", "Tool invocation attempts");
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "developertwin.tool.duration", "s", "Tool invocation duration");
    public static readonly Counter<long> KnowledgeChunks = Meter.CreateCounter<long>(
        "developertwin.knowledge.chunks", "{chunk}", "Successfully ingested chunks");

    /// <summary>
    /// Returns a disposable scope that records tool latency and outcome.
    /// The tool name must come from code, NOT from untrusted user input.
    /// </summary>
    public static ToolScope BeginTool(string toolName) => new(toolName);

    public sealed class ToolScope(string name) : IDisposable
    {
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private readonly Activity? _activity = Activities.StartActivity("tool." + name);
        private string _outcome = "ok";
        public void Fail(string outcome = "error")
        {
            _outcome = outcome;
            _activity?.SetStatus(ActivityStatusCode.Error, "Tool did not complete successfully");
        }
        public void Dispose()
        {
            _timer.Stop();
            // Some tools intentionally return structured errors instead of throwing.
            var finalOutcome = _outcome == "ok" && _activity?.Status == ActivityStatusCode.Error
                ? "error" : _outcome;
            _activity?.SetTag("tool.name", name);
            _activity?.SetTag("tool.outcome", finalOutcome);
            // Only stable, low-cardinality labels; never raw queries or filenames.
            var tags = new TagList { { "tool.name", name }, { "outcome", finalOutcome } };
            ToolCalls.Add(1, tags);
            ToolDuration.Record(_timer.Elapsed.TotalSeconds, tags);
            _activity?.Dispose();
        }
    }
}

public sealed class ObservabilityOptions
{
    public bool Enabled { get; set; } = true;
    public string ServiceName { get; set; } = "developertwin-api";
    public string ServiceVersion { get; set; } = "4.2-training";
    public string OtlpEndpoint { get; set; } = ""; // Empty = no network export.
    public bool ConsoleExporter { get; set; } = false; // Debug only; can be noisy.
    public bool ExportLogs { get; set; } = false; // Explicit opt-in.
    public double TraceSampleRatio { get; set; } = 1.0;
}

public static class ObservabilityRegistration
{
    public static void AddDeveloperTwinObservability(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>()
            ?? new ObservabilityOptions();
        if (!options.Enabled) return;
        if (options.TraceSampleRatio is < 0 or > 1 || double.IsNaN(options.TraceSampleRatio))
            throw new InvalidOperationException("Observability:TraceSampleRatio must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(options.ServiceName))
            throw new InvalidOperationException("Observability:ServiceName is required.");
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            endpoint = new Uri(options.OtlpEndpoint, UriKind.Absolute);
            if (endpoint.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Observability:OtlpEndpoint must use HTTP or HTTPS.");
        }

        // AddOpenTelemetry is hosted by .NET DI; exporters are flushed at shutdown.
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName, serviceVersion: options.ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing.AddSource(DeveloperTwinTelemetry.SourceName)
                    .AddSource(DeveloperTwinTelemetry.GenAiSourceName)
                    // Supports native MAF spans when the installed agent version emits them.
                    .AddSource("Experimental.Microsoft.Agents.AI")
                    .AddSource("Experimental.Microsoft.Agents.AI.Agent")
                    .AddSource("Experimental.Microsoft.Agents.AI.ChatClient")
                    .AddAspNetCoreInstrumentation(cfg =>
                    {
                        // Suppress frequent health probes; debugging focuses on business calls.
                        cfg.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/status") &&
                            !ctx.Request.Path.StartsWithSegments("/api/knowledge/health");
                    })
                    .AddHttpClientInstrumentation()
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)));
                if (endpoint is not null)
                    tracing.AddOtlpExporter(exp => {
                        exp.Endpoint = endpoint;
                        exp.Protocol = OtlpExportProtocol.Grpc;
                    });
                if (options.ConsoleExporter) tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(DeveloperTwinTelemetry.MeterName)
                    // Chat-client / MAF meters may emit token and duration metrics.
                    .AddMeter(DeveloperTwinTelemetry.GenAiSourceName)
                    .AddMeter("Experimental.Microsoft.Agents.AI")
                    .AddMeter("Experimental.Microsoft.Agents.AI.ChatClient")
                    .AddMeter("Experimental.Microsoft.Agents.AI.Agent")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (endpoint is not null)
                    metrics.AddOtlpExporter(exp => {
                        exp.Endpoint = endpoint;
                        exp.Protocol = OtlpExportProtocol.Grpc;
                    });
                if (options.ConsoleExporter) metrics.AddConsoleExporter();
            });

        // ILogger already logs to the ASP.NET Core console. OTLP log export is optional.
        // We never enable GenAI sensitive-data capture for this engineering workspace.
        if (options.ExportLogs)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeScopes = true;
                logging.IncludeFormattedMessage = false;
                logging.ParseStateValues = true;
                if (endpoint is not null)
                    logging.AddOtlpExporter(exp => {
                        exp.Endpoint = endpoint;
                        exp.Protocol = OtlpExportProtocol.Grpc;
                    });
                if (options.ConsoleExporter) logging.AddConsoleExporter();
            });
        }
    }
}
