using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalTwin.Api;
public sealed class VisionTools(HttpClient http, TwinSettings settings)
{
    public async Task<string> Describe(string filePath, string question, CancellationToken ct)
    {
        // Unlike text chat, qwen3:8b is not a vision model. Do not send images to it.
        var key = settings.OpenRouterApiKey;
        if (!string.IsNullOrWhiteSpace(key) && !settings.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            var mime = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg"
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.TryAddWithoutValidation("X-Title", "DeveloperTwin AI");
            // OpenAI-compatible multimodal payload; the image remains server-side until this action is called.
            req.Content = JsonContent.Create(new {
                model = settings.OpenRouterVisionModel,
                messages = new[] { new { role = "user", content = new object[] {
                    new { type = "text", text = question },
                    (object)new { type = "image_url", image_url = new {
                        url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" } }
                } } },
                stream = false
            });
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenRouter vision returned HTTP {(int)res.StatusCode}: {body[..Math.Min(body.Length, 350)]}");
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            return content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ToString();
        }

        if (string.IsNullOrWhiteSpace(settings.VisionModel))
            throw new InvalidOperationException("Vision requires OPENROUTER_API_KEY, or configure a locally installed vision-capable Ollama model in Twin:VisionModel.");
        var image = await File.ReadAllBytesAsync(filePath, ct);
        using var response = await http.PostAsJsonAsync($"{settings.OllamaUrl}/api/chat", new {
            model = settings.VisionModel, stream = false,
            messages = new[] { new { role = "user", content = question, images = new[] { Convert.ToBase64String(image) } } }
        }, ct);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return result.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
