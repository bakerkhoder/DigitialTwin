using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

public static class SensitiveDataPolicy
{
    private static readonly Regex[] Secrets =
    [
        new(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z]+ )?PRIVATE KEY-----|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new(@"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|tvly-[A-Za-z0-9_-]{16,}|AKIA[A-Z0-9]{16})\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new("(?i)([\"']?[a-z0-9_-]*(?:api[_-]?key|password|secret|access[_-]?token|connectionstring)[\"']?\\s*[:=]\\s*[\"'])([^\"'\\r\\n]{8,})([\"'])", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new(@"(?i)\b(?:password|api_key|access_token)\s*=\s*[^\s;""']{8,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))
    ];
    private static readonly Regex PrivateQuery = new(
        @"(?i)(?:\b[A-Z]:[\\/]|/(?:home|mnt|Users|etc)/|[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b(?:confidential|internal-only|private source|private document)\b)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LocalPath = new(@"(?i)(?:\b[A-Z]:[\\/]|/(?:home|mnt|Users|etc)/)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static bool ContainsSecret(string text) => Secrets.Any(pattern => pattern.IsMatch(text));
    public static bool ContainsPrivateQueryData(string text) => ContainsSecret(text) || PrivateQuery.IsMatch(text);
    public static bool ContainsCredentialOrLocalPath(string text) => ContainsSecret(text) || LocalPath.IsMatch(text);

    public static string Redact(string text)
    {
        for (var i = 0; i < Secrets.Length; i++)
            text = i == 2 ? Secrets[i].Replace(text, "$1[REDACTED]$3") : Secrets[i].Replace(text, "[REDACTED]");
        return text;
    }
}
