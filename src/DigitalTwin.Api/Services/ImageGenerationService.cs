using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalTwin.Api;

/// <summary>
/// Optional, explicitly requested text-to-image capability.
/// Separate from the MAF chat agent: image generation is a user-confirmed API action.
/// Cloudflare uses the Workers AI free daily allocation; OpenRouter image calls
/// require an explicit paid-use opt-in to prevent surprise charges.
/// </summary>
public sealed class ImageGenerationService(HttpClient http, IConfiguration configuration, TwinSettings settings)
{
    public async Task<(byte[] Bytes, string Mime)> GenerateAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1800)
            throw new ArgumentException("Image prompt must contain 1–1,800 characters.");

        var provider = configuration["TWIN_IMAGE_PROVIDER"]?.Trim().ToLowerInvariant() ?? "off";
        return provider switch
        {
            "cloudflare" => await GenerateWithCloudflareAsync(prompt, ct),
            "openrouter" => await GenerateWithOpenRouterAsync(prompt, ct),
            _ => throw new InvalidOperationException("Image generation is disabled. Configure TWIN_IMAGE_PROVIDER=cloudflare or openrouter.")
        };
    }

    private async Task<(byte[] Bytes, string Mime)> GenerateWithCloudflareAsync(string prompt, CancellationToken ct)
    {
        var account = configuration["CLOUDFLARE_ACCOUNT_ID"];
        var token = configuration["CLOUDFLARE_API_TOKEN"];
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN are required.");

        var url = $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(account)}/ai/run/@cf/black-forest-labs/flux-1-schnell";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { prompt, steps = 4 });
        using var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloudflare image API HTTP {(int)response.StatusCode}: {Truncate(json)}");

        using var document = JsonDocument.Parse(json);
        var base64 = document.RootElement.GetProperty("result").GetProperty("image").GetString();
        return (Decode(base64), "image/jpeg");
    }

    private async Task<(byte[] Bytes, string Mime)> GenerateWithOpenRouterAsync(string prompt, CancellationToken ct)
    {
        // No paid calls should occur just because OPENROUTER_API_KEY is configured for chat.
        if (!bool.TryParse(configuration["TWIN_ALLOW_PAID_IMAGES"], out var accepted) || !accepted)
            throw new InvalidOperationException("OpenRouter image generation requires explicit TWIN_ALLOW_PAID_IMAGES=true.");
        var token = settings.OpenRouterApiKey;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Configure Twin:OpenRouterApiKey or OPENROUTER_API_KEY.");
        var model = configuration["TWIN_OPENROUTER_IMAGE_MODEL"] ?? "bytedance-seed/seedream-4.5";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/images");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Title", "DeveloperTwin AI");
        request.Content = JsonContent.Create(new { model, prompt });
        using var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter image API HTTP {(int)response.StatusCode}: {Truncate(json)}");

        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.GetProperty("data")[0];
        var base64 = first.GetProperty("b64_json").GetString();
        var mime = first.TryGetProperty("media_type", out var type) ? type.GetString() : null;
        if (mime is not ("image/jpeg" or "image/png" or "image/webp")) mime = "image/png";
        return (Decode(base64), mime);
    }

    private static byte[] Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) throw new InvalidDataException("Image API returned no image data.");
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length is < 100 or > 15_000_000)
            throw new InvalidDataException("Image response size is invalid.");
        return bytes;
    }

    private static string Truncate(string content) => content[..Math.Min(content.Length, 350)];
}
