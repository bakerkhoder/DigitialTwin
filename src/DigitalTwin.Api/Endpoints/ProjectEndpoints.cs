namespace DigitalTwin.Api;

public sealed record ConnectFolderRequest(string Path);

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", (ProjectCatalog catalog) => Results.Ok(catalog.List()));
        app.MapGet("/api/workspace/tree", (string? path, WorkspaceTools workspace) =>
        {
            try { return Results.Ok(new { tree = workspace.ListProject(path ?? ".") }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/projects/connect", (ConnectFolderRequest request, ProjectCatalog catalog,
            ApprovalQueue queue, WorkspaceTools workspace) =>
        {
            using var activity = DeveloperTwinTelemetry.Activities.StartActivity("folder.access.request");
            try
            {
                var path = catalog.ValidateCandidatePath(request.Path);
                if (catalog.IsUnderAllowedRoot(path))
                {
                    var project = catalog.Connect(path);
                    activity?.SetTag("approval.status", "pre-approved-root");
                    return Results.Ok(new
                    {
                        project = project.Id,
                        name = project.Name,
                        files = 0,
                        tree = workspace.ListProject(project.Id),
                        status = "connected"
                    });
                }

                var action = queue.Propose(
                    "folder-access",
                    $"Read-only folder access: {Path.GetFileName(path)}",
                    System.Text.Json.JsonSerializer.Serialize(new FolderAccessDraft(path)));
                activity?.SetTag("approval.id", action.Id);
                activity?.SetTag("approval.status", "pending");
                return Results.Accepted($"/api/approvals/{action.Id}", new
                {
                    approvalId = action.Id,
                    status = "pending",
                    message = "This folder is outside configured allowed roots. Approval is required before it can be connected."
                });
            }
            catch (UnauthorizedAccessException ex)
            { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        })
            .WithSummary("Request human approval to scan a local project folder")
            .WithDescription("Connects folders under Twin:AllowedProjectRoots directly; queues folders outside configured roots for human approval before scanning.");
        app.MapPost("/api/projects/import", async (HttpRequest request, ProjectImporter importer, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected ZIP form upload." });
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "Select a ZIP file." });
            try { var imported = await importer.Import(file, ct);
                  return Results.Ok(new { project = imported.Project, files = imported.Files, tree = imported.Tree }); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();
        return app;
    }
}
