namespace DigitalTwin.Api;

public sealed record KnowledgeStatusRequest(string Status);
public sealed record KnowledgeReviewRequest(string Revision);

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
        app.MapPatch("/api/knowledge/documents/{id}/status", async (string id, KnowledgeStatusRequest input,
            KnowledgeStore knowledge, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (id.Length != 24 || !id.All(Uri.IsHexDigit)) return Results.BadRequest(new { error = "Invalid document identifier." });
            if (input.Status is not ("active" or "deprecated"))
                return Results.BadRequest(new { error = "Status must be active or deprecated." });
            try
            {
                if (!await knowledge.SetDocumentStatus(id, input.Status, ct)) return Results.NotFound();
                sessions.InvalidateAllAfterKnowledgeChange();
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Could not update document status", detail: ex.Message, statusCode: 503); }
        });
        // Human review routes are deliberately absent from the agent tool allowlist.
        app.MapGet("/api/knowledge/documents/{id}/review", async (string id, KnowledgeStore knowledge, CancellationToken ct) =>
        {
            if (id.Length != 24 || !id.All(Uri.IsHexDigit)) return Results.BadRequest(new { error = "Invalid document identifier." });
            try
            {
                var review = await knowledge.GetReview(id, ct);
                return review is null ? Results.NotFound() : Results.Ok(review);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Document review unavailable", detail: ex.Message, statusCode: 503); }
        });
        app.MapPost("/api/knowledge/documents/{id}/review/approve", async (string id, KnowledgeReviewRequest input,
            KnowledgeStore knowledge, SessionRegistry sessions, CancellationToken ct) =>
        {
            if (id.Length != 24 || !id.All(Uri.IsHexDigit) || input.Revision is not { Length: 64 } || !input.Revision.All(Uri.IsHexDigit))
                return Results.BadRequest(new { error = "A valid document id and reviewed revision are required." });
            try
            {
                if (!await knowledge.ApproveReview(id, input.Revision, ct)) return Results.NotFound();
                sessions.InvalidateAllAfterKnowledgeChange();
                return Results.NoContent();
            }
            catch (KnowledgeReviewConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (GuardrailViolationException ex) { return Results.UnprocessableEntity(new { error = ex.Message, code = ex.Code }); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Could not approve document", detail: ex.Message, statusCode: 503); }
        });
        app.MapGet("/api/knowledge/search", async (string q, KnowledgeRetrievalService retrieval, CancellationToken ct) =>
        {
            try { return Results.Ok((await retrieval.SearchAsync(q, ct)).Hits); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Knowledge retrieval unavailable", detail: ex.Message, statusCode: 503); }
        });
        app.MapGet("/api/knowledge/search/diagnostics", async (string q, KnowledgeRetrievalService retrieval, CancellationToken ct) =>
        {
            try { return Results.Ok(await retrieval.SearchAsync(q, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { return Results.Problem(title: "Knowledge retrieval unavailable", detail: ex.Message, statusCode: 503); }
        });
        app.MapPost("/api/knowledge/batch", async (HttpRequest request, WorkspaceTools workspace,
            KnowledgeStore knowledge, DocumentExtractionService extractor, InputGuardrailService inputGuard,
            SessionRegistry sessions, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data." });
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count is < 1 or > 15) return Results.BadRequest(new { error = "Choose between 1 and 15 files." });
            var allowExternalAnalysis = bool.TryParse(form["allowExternalAnalysis"], out var allowExternal) && allowExternal;
            var outcomes = new List<object>();
            foreach (var file in form.Files)
            {
                var filename = Path.GetFileName(file.FileName);
                var ext = Path.GetExtension(filename).ToLowerInvariant();
                if (inputGuard.ValidateUpload(filename, file.Length, 8_000_000) is { } uploadError)
                { outcomes.Add(new { file = filename, error = uploadError }); continue; }
                var temporary = workspace.ResolveUploadName(Guid.NewGuid().ToString("N") + ext);
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                try
                {
                    await using (var output = File.Create(temporary)) await file.CopyToAsync(output, ct);
                    var extracted = await extractor.ExtractAsync(temporary, filename, allowExternalAnalysis, ct);
                    if (string.IsNullOrWhiteSpace(extracted.Text)) throw new TextExtractionException("No readable content was extracted.");
                    var result = await knowledge.Ingest(filename, extracted.Text, ct);
                    if (!result.Unchanged) sessions.InvalidateAllAfterKnowledgeChange();
                    outcomes.Add(new { file = filename, result.Chunks, result.Unchanged, result.Revision, result.Status,
                        result.ReviewStatus, result.RiskReasons, extraction = extracted.Method });
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
