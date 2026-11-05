using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DigitalTwin.Api;
public static class TextExtractor
{
    public static string Extract(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var document = PdfDocument.Open(path);
            return string.Join("\n\n", document.GetPages().Take(250).Select(p => $"[Page {p.Number}]\n{ContentOrderTextExtractor.GetText(p)}"));
        }
        return File.ReadAllText(path);
    }
}
