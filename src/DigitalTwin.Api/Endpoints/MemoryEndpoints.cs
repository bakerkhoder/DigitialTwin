namespace DigitalTwin.Api;

public sealed record SaveMemoryRequest(string? Content, string Kind = "preference");

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/memories", (MemoryStore memories) => Results.Ok(memories.List()));
        app.MapPost("/api/memories", (SaveMemoryRequest request, MemoryStore memories, InputGuardrailService input) =>
        {
            if (input.ValidateMessage(request.Content, 1000) is { } error) return Results.BadRequest(new { error });
            if (request.Kind is not ("preference" or "fact")) return Results.BadRequest(new { error = "Memory kind must be preference or fact." });
            var saved = memories.Save(null, request.Content!, request.Kind);
            return Results.Created($"/api/memories/{saved!.Id}", saved);
        });
        app.MapPut("/api/memories/{id:guid}", (Guid id, SaveMemoryRequest request, MemoryStore memories, InputGuardrailService input) =>
        {
            if (input.ValidateMessage(request.Content, 1000) is { } error) return Results.BadRequest(new { error });
            if (request.Kind is not ("preference" or "fact")) return Results.BadRequest(new { error = "Memory kind must be preference or fact." });
            return memories.Save(id.ToString(), request.Content!, request.Kind) is { } saved ? Results.Ok(saved) : Results.NotFound();
        });
        app.MapDelete("/api/memories/{id:guid}", (Guid id, MemoryStore memories) =>
            memories.Delete(id.ToString()) ? Results.NoContent() : Results.NotFound());
        return app;
    }
}
