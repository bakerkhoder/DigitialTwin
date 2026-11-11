using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

/// <summary>Recover a premature local-information refusal, without routing every question by keywords.</summary>
public static class KnowledgeToolRecovery
{
    private static readonly Regex LocalInformation = new(
        @"your (?:name|identity|background|preferences|personal|documents|files)|personal (?:information|details|data)|uploaded|knowledge base|اسمك|معلوماتك|معلومات شخصية|الملفات|المستندات",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Unavailable = new(
        @"don['’]t (?:have|know)|do not (?:have|know)|can(?:not|['’]t) access|no access|unless you|if you (?:tell|share)|haven['’]t (?:shared|provided)|have not (?:shared|provided)|not (?:provided|shared|available)|لا (?:أعرف|اعرف|أملك|املك|يمكنني)|لم (?:تقدم|تشارك)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsPrematureRefusal(string answer) => LocalInformation.IsMatch(answer) && Unavailable.IsMatch(answer);

    public const string Instructions = """
        The local SearchKnowledge tool is available for the user's uploaded documents,
        profiles, CVs and personal notes. No knowledge search has run for this request.
        Do not assume these sources are inaccessible or lack personal information.
        Reconsider the latest user question using the actual conversation evidence.
        If it already supplies the answer or a correction, answer from that evidence.
        Otherwise use SearchKnowledge with a descriptive query to check available
        documents before claiming the information is unavailable. Cite actual filenames.
        Respect any explicit user instruction not to search documents. Do not invent
        missing facts, and do not claim a persistent memory was saved or deleted.
        """;
}
