namespace DigitalTwin.Api;

public static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/knowledge/health", async (KnowledgeStore knowledge, CancellationToken ct) =>
        {
            var status = await knowledge.Health(ct);
            return Results.Ok(new { qdrant = status.Qdrant, ollama = status.Ollama, message = status.Message });
        });
        // Collection metadata comes from Qdrant itself: deleting a document deletes
        // both the payload passages AND their vectors, including legacy chunks.
        app.MapGet("/api/knowledge/documents", async (KnowledgeStore knowledge, CancellationToken ct) =>
        {
            try { return Results.Ok(await knowledge.ListDocuments(ct)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Could not list knowledge documents", detail: ex.Message, statusCode: 503); }
        });
        app.MapDelete("/api/knowledge/documents/{id}", async (string id, KnowledgeStore knowledge, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (id.Length != 24 || !id.All(Uri.IsHexDigit)) return Results.BadRequest(new { error = "Invalid document identifier." });
            try { if (!await knowledge.DeleteDocument(id, ct)) return Results.NotFound();
                  sessions.InvalidateAllAfterKnowledgeChange();
                  return Results.NoContent(); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Could not delete document", detail: ex.Message, statusCode: 503); }
        });
        app.MapGet("/api/knowledge/search", async (string q, KnowledgeStore knowledge, CancellationToken ct) =>
        {
            try { return Results.Ok(await knowledge.Search(q, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Knowledge retrieval unavailable", detail: ex.Message, statusCode: 503); }
        });
        app.MapPost("/api/knowledge/batch", async (HttpRequest request, WorkspaceTools workspace,
            KnowledgeStore knowledge, VisionTools vision, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data." });
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count is < 1 or > 15) return Results.BadRequest(new { error = "Choose between 1 and 15 files." });
            var health = await knowledge.Health(ct);
            if (!health.Qdrant || !health.Ollama)
                return Results.Problem(title: "Knowledge services are unavailable", detail: health.Message, statusCode: 503);
            var outcomes = new List<object>();
            foreach (var file in form.Files)
            {
                var filename = Path.GetFileName(file.FileName);
                var ext = Path.GetExtension(filename).ToLowerInvariant();
                bool image = ext is ".png" or ".jpg" or ".jpeg" or ".webp";
                if (file.Length is <= 0 or > 8_000_000)
                { outcomes.Add(new { file = filename, error = "Maximum 8 MB per file." }); continue; }
                if (!image && ext is not (".pdf" or ".txt" or ".md" or ".cs" or ".js" or ".jsx" or ".ts" or ".tsx" or ".json" or ".xml" or ".sql" or ".html" or ".css" or ".csv"))
                { outcomes.Add(new { file = filename, error = "Unsupported file extension." }); continue; }
                var temporary = workspace.ResolveUploadName(Guid.NewGuid().ToString("N") + ext);
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                try
                {
                    await using (var output = File.Create(temporary)) await file.CopyToAsync(output, ct);
                    var content = image
                        ? await vision.Describe(temporary, "Describe this document image and extract its important information.", ct)
                        : TextExtractor.Extract(temporary);
                    if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("No extractable content. A scanned PDF may need OCR.");
                    int chunks = await knowledge.Ingest(filename, content, ct);
                    sessions.InvalidateAllAfterKnowledgeChange();
                    outcomes.Add(new { file = filename, chunks });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { outcomes.Add(new { file = filename, error = ex.Message }); }
                finally { File.Delete(temporary); }
            }
            return Results.Ok(new { results = outcomes });
        }).DisableAntiforgery();
        return app;
    }
}
