namespace DigitalTwin.Api;

public sealed class InputGuardrailService
{
    public string? ValidateMessage(string? message, int maximum = 12000)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > maximum || message.Contains('\0'))
            return $"Message must be 1–{maximum:N0} characters and contain no null characters.";
        return SensitiveDataPolicy.ContainsSecret(message) ? "Remove possible credentials from the message before sending it to a model." : null;
    }

    public string? ValidateUpload(string filename, long length, long maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 180 || filename.Any(char.IsControl))
            return "Use a filename of 1–180 characters without control characters.";
        if (length <= 0 || length > maximumBytes) return $"Maximum {maximumBytes / 1_000_000} MB per file.";
        if (!DocumentExtractionService.IsSupported(Path.GetExtension(filename).ToLowerInvariant()))
            return "Unsupported file extension.";
        return null;
    }

}
