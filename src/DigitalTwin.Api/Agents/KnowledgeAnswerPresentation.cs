using System.Text.RegularExpressions;

namespace DigitalTwin.Api;

/// <summary>Show actual retrieved filenames when a model omits its document references.</summary>
public static class KnowledgeAnswerPresentation
{
    public static string EnsureSources(string answer, IReadOnlyCollection<string> sources)
    {
        if (string.IsNullOrWhiteSpace(answer) || sources.Count == 0 ||
            sources.Any(source => answer.Contains(source, StringComparison.OrdinalIgnoreCase))) return answer;
        var names = sources.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Take(5).Select(CodeSpan);
        // This labels what was consulted. It does not assert that an automatic
        // citation is independent verification of every claim in the model answer.
        return answer.TrimEnd() + "\n\nDocuments consulted: " + string.Join(", ", names) + ".";
    }

    private static string CodeSpan(string filename)
    {
        var longest = Regex.Matches(filename, "`+").Select(match => match.Length).DefaultIfEmpty(0).Max();
        var delimiter = new string('`', longest + 1);
        return delimiter + " " + filename + " " + delimiter;
    }
}
