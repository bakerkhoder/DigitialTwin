using System.Text.Json;

namespace DigitalTwin.Api;

public sealed record ExportRequest(string Title, string Content, string Format);

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reports/export", (ExportRequest request, ReportExporter exporter, ILogger<ReportExporter> logger) =>
        {
            try
            {
                var output = exporter.Export(request.Format, request.Title, request.Content);
                var file = string.Concat(request.Title.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));
                return Results.File(output.Bytes, output.Mime, file[..Math.Min(70, file.Length)] + "." + output.Extension);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Report export failed for format {Format}", request.Format);
                return Results.Problem(title: "Report generation failed",
                    detail: $"The {request.Format.ToUpperInvariant()} exporter failed. See backend logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        // Optional deterministic alternative to agent tool calling: queue the visible report for review.
        app.MapPost("/api/approvals/propose", (HandoffDraft draft, ApprovalQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 120 ||
                string.IsNullOrWhiteSpace(draft.Content) || draft.Content.Length > 35000)
                return Results.BadRequest(new { error = "Title 1–120 characters and report 1–35,000 characters required." });
            var proposal = queue.Propose("handoff-export", draft.Title, JsonSerializer.Serialize(draft));
            return Results.Ok(new { id = proposal.Id, pending = true });
        });
        app.MapGet("/api/approvals", (ApprovalQueue queue) => Results.Ok(queue.List()));
        app.MapGet("/api/approvals/{id:guid}", (Guid id, ApprovalQueue queue) =>
            queue.TryGet(id, out var proposal) && proposal is not null ? Results.Ok(proposal) : Results.NotFound());
        app.MapPost("/api/approvals/{id:guid}/reject", (Guid id, ApprovalQueue queue) =>
            queue.TryRemove(id, out _) ? Results.Ok(new { rejected = true }) : Results.NotFound());
        // Approval exports a local report; it never changes an external system.
        app.MapPost("/api/approvals/{id:guid}/approve", (Guid id, string? format, ApprovalQueue queue,
            ReportExporter exporter, ProjectCatalog catalog, WorkspaceTools workspace, ILogger<ReportExporter> logger) =>
        {
            if (!queue.TryGet(id, out var proposal) || proposal is null) return Results.NotFound();
            if (proposal.Kind == "folder-access")
            {
                FolderAccessDraft? folder;
                try { folder = JsonSerializer.Deserialize<FolderAccessDraft>(proposal.Payload); }
                catch (JsonException) { return Results.BadRequest(new { error = "Folder request is invalid." }); }
                if (folder is null) return Results.BadRequest(new { error = "Folder request is invalid." });

                try { _ = catalog.ValidateCandidatePath(folder.Path); }
                catch (UnauthorizedAccessException ex)
                { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
                catch (Exception ex) when (ex is IOException or ArgumentException)
                { return Results.BadRequest(new { error = ex.Message }); }

                using var activity = DeveloperTwinTelemetry.Activities.StartActivity("folder.access.decision");
                if (!queue.TryRemove(id, out _))
                    return Results.Conflict(new { error = "This approval request was already decided." });
                activity?.SetTag("approval.id", id);
                activity?.SetTag("approval.decision", "approved");
                try
                {
                    var project = catalog.Connect(folder.Path, explicitlyApprovedOutsideRoots: true);
                    var tree = workspace.ListProject(project.Id);
                    return Results.Ok(new { project = project.Id, name = project.Name, tree });
                }
                catch (UnauthorizedAccessException ex)
                { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
                catch (Exception ex) when (ex is IOException or ArgumentException)
                { return Results.BadRequest(new { error = ex.Message }); }
            }
            if (proposal.Kind != "handoff-export") return Results.BadRequest(new { error = "Unsupported approval." });
            var draft = JsonSerializer.Deserialize<HandoffDraft>(proposal.Payload);
            if (draft is null) return Results.BadRequest(new { error = "Draft is invalid." });
            try
            {
                var output = exporter.Export(format ?? "pdf", draft.Title, draft.Content);
                queue.TryRemove(id, out _); // Remove only after successful file generation.
                return Results.File(output.Bytes, output.Mime, "approved-handoff." + output.Extension);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not generate reviewed report for approval {ApprovalId}", id);
                return Results.Problem(title: "Could not generate the reviewed report",
                    detail: "The report exporter failed. See backend logs for details.", statusCode: 500);
            }
        });
        return app;
    }
}
