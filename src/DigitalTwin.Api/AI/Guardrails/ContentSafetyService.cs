using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

public sealed record ContentAssessment(string ReviewStatus, string[] RiskReasons)
{
    public bool RequiresReview => ReviewStatus == "pending";
}

/// <summary>Heuristic screening supports review; tool authorization remains the enforcement boundary.</summary>
public sealed class ContentSafetyService
{
    private static readonly (string Code, Regex Pattern)[] Rules =
    [
        ("instruction-override", Pattern(@"\bignore\s+(?:(?:all|the|any)\s+)?(?:previous|prior|system|developer|above)\s+(?:\w+\s+){0,3}instructions?\b")),
        ("permission-bypass", Pattern(@"\b(?:bypass|disable|override)\s+(?:(?:all|the|any)\s+)?(?:guardrails?|permissions?|approval|access\s+(?:checks?|controls?))\b|\b(?:automatically|silently)\s+approve\b")),
        ("role-spoofing", Pattern(@"<\|(?:im_start|start_header_id)\|>\s*(?:system|developer)|\[INST\]\s*<<SYS>>")),
        ("data-exfiltration", Pattern(@"\b(?:send|upload|exfiltrate|transmit)\s+(?:\w+\s+){0,5}(?:secrets?|credentials?|private\s+(?:files?|code|documents?))\s+(?:\w+\s+){0,3}(?:https?://|SearchWeb|external)"))
    ];
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public ContentAssessment Assess(string text)
    {
        var reasons = Rules.Where(rule => rule.Pattern.IsMatch(text)).Select(rule => rule.Code).ToList();
        if (SensitiveDataPolicy.ContainsSecret(text)) reasons.Add("possible-credential");
        return new(reasons.Count > 0 ? "pending" : "not-required", reasons.ToArray());
    }
}
