using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DigitalTwin.Api;

public sealed class TextExtractionException(string message) : Exception(message);

public static class TextExtractor
{
    public static string Extract(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var document = PdfDocument.Open(path);
            var pages = document.GetPages().Take(250)
                .Select(page => (page.Number, Text: ContentOrderTextExtractor.GetText(page)))
                .ToArray();
            if (pages.Length == 0 || pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
                throw new TextExtractionException(
                    "No selectable text could be extracted from this PDF. It may be scanned, image-only, or use an unsupported font encoding. OCR is not configured; try a searchable PDF or provide the text separately.");

            var extracted = string.Join("\n\n", pages.Select(page => $"[Page {page.Number}]\n{page.Text}"));
            var replacementCharacters = extracted.Count(character => character == '\uFFFD');
            if (replacementCharacters >= 3 && replacementCharacters > extracted.Length / 100)
                throw new TextExtractionException(
                    "PDF text appears corrupted by an unsupported font encoding. Arabic text requires a valid Unicode mapping in the PDF; OCR is not configured. Try a searchable PDF or provide the text separately.");

            return extracted;
        }
        return File.ReadAllText(path);
    }
}
