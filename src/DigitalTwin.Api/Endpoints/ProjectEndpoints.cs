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
        app.MapPost("/api/projects/connect", (ConnectFolderRequest request, ProjectCatalog catalog, WorkspaceTools workspace) =>
        {
            try { var project = catalog.Connect(request.Path);
                  return Results.Ok(new { project = project.Id, name = project.Name, files = 0, tree = workspace.ListProject(project.Id) }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
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
