using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalTwin.Api;

public sealed class VisionConfigurationException(string message) : Exception(message);
public sealed class VisionProviderException(string message) : Exception(message);

public sealed class VisionTools(HttpClient http, TwinSettings settings)
{
    public async Task<string> AnalyzePdf(string filePath, string filename, string question, CancellationToken ct)
    {
        var key = settings.OpenRouterApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new VisionConfigurationException(
                "PDF analysis requires OpenRouter. Configure Twin:OpenRouterApiKey or OPENROUTER_API_KEY and select an active Twin:OpenRouterVisionModel.");

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var dataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(bytes)}";
        var payload = new
        {
            model = settings.OpenRouterVisionModel,
            messages = new[] { new { role = "user", content = new object[] {
                new { type = "text", text = question },
                (object)new { type = "file", file = new { filename, file_data = dataUrl } }
            } } },
            plugins = new[] { new { id = "file-parser", pdf = new { engine = "cloudflare-ai" } } },
            stream = false
        };
        return await SendOpenRouterAsync(payload, key, "PDF analysis", ct);
    }

    public async Task<string> Describe(string filePath, string question, CancellationToken ct)
    {
        // Unlike text chat, qwen3:8b is not a vision model. Do not send images to it.
        var key = settings.OpenRouterApiKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            var mime = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg"
            };
            var payload = new {
                model = settings.OpenRouterVisionModel,
                messages = new[] { new { role = "user", content = new object[] {
                    new { type = "text", text = question },
                    (object)new { type = "image_url", image_url = new {
                        url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" } }
                } } },
                stream = false
            };
            return await SendOpenRouterAsync(payload, key, "vision", ct);
        }

        if (string.IsNullOrWhiteSpace(settings.VisionModel))
            throw new VisionConfigurationException(
                "No image-analysis provider is configured. Set Twin:OpenRouterApiKey or OPENROUTER_API_KEY to use Twin:OpenRouterVisionModel (default: openrouter/free), or set Twin:VisionModel to an installed vision-capable Ollama model.");
        var image = await File.ReadAllBytesAsync(filePath, ct);
        try
        {
            using var response = await http.PostAsJsonAsync($"{settings.OllamaUrl}/api/chat", new {
                model = settings.VisionModel, stream = false,
                messages = new[] { new { role = "user", content = question, images = new[] { Convert.ToBase64String(image) } } }
            }, ct);
            if (!response.IsSuccessStatusCode)
                throw new VisionProviderException(
                    $"Local Ollama vision returned HTTP {(int)response.StatusCode}. Check that Ollama is running and Twin:VisionModel names an installed vision-capable model.");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return result.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (HttpRequestException)
        {
            throw new VisionProviderException(
                "Could not reach the local Ollama vision provider. Check that Ollama is running and Twin:OllamaUrl is correct.");
        }
        catch (JsonException)
        {
            throw new VisionProviderException("Local Ollama vision returned an invalid response.");
        }
    }

    private async Task<string> SendOpenRouterAsync(object payload, string key, string capability, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("X-Title", "DeveloperTwin AI");
        request.Content = JsonContent.Create(payload);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new VisionProviderException(
                $"OpenRouter {capability} returned HTTP {(int)response.StatusCode}. Check the API key, model access, and provider quota.");
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            var result = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ToString();
            if (string.IsNullOrWhiteSpace(result))
                throw new VisionProviderException($"OpenRouter {capability} returned no readable content.");
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new VisionProviderException($"OpenRouter {capability} returned an unreadable response.");
        }
    }
}
