namespace DigitalTwin.Api;

public sealed record GenerateImageRequest(string Prompt);

public static class ImageEndpoints
{
    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/images/generate", async Task<IResult> (
            GenerateImageRequest request,
            ImageGenerationService generator,
            ILogger<ImageGenerationService> logger,
            CancellationToken ct) =>
        {
            try
            {
                var (bytes, mime) = await generator.GenerateAsync(request.Prompt, ct);
                var extension = mime switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };
                return Results.File(bytes, mime, $"DeveloperTwin-generated.{extension}");
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Problem(title: "Image provider unavailable", detail: ex.Message, statusCode: 503); }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Image provider request failed");
                return Results.Problem(title: "Image provider request failed", detail: ex.Message, statusCode: 502);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Image generation failed");
                return Results.Problem(title: "Image generation failed", detail: "The provider returned invalid image data. Check backend logs.", statusCode: 500);
            }
        });
        return endpoints;
    }
}
