using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO.Compression;

namespace DigitalTwin.Api;

public class TextExtractionException(string message) : Exception(message);
public sealed class OcrRequiredException(string message) : TextExtractionException(message);

public static class TextExtractor
{
    public const int MaxCharacters = 280_000;

    public static string Extract(string path)
    {
        if (Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            // Bound decompressed input before the Open XML DOM is loaded.
            using (var archive = ZipFile.OpenRead(path))
                if (archive.Entries.Count > 10_000 || archive.Entries.Sum(e => e.Length) > 32_000_000)
                    throw new TextExtractionException("DOCX exceeds the decompressed document limit (32 MB). Split it into smaller files.");
            using var document = WordprocessingDocument.Open(path, false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new TextExtractionException("DOCX has no readable document body.");
            return CheckLength(string.Join('\n', body.Descendants<Paragraph>().Select(p => p.InnerText)));
        }
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var document = PdfDocument.Open(path);
            if (document.NumberOfPages > 250)
                throw new TextExtractionException("PDF exceeds 250 pages. Split it into smaller documents.");
            var pages = document.GetPages()
                .Select(page => (page.Number, Text: ContentOrderTextExtractor.GetText(page)))
                .ToArray();
            if (pages.Length == 0 || pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
                throw new OcrRequiredException(
                    "No selectable text could be extracted from this PDF. It may be scanned, image-only, or use an unsupported font encoding. OCR is not configured; try a searchable PDF or provide the text separately.");

            var extracted = string.Join("\n\n", pages.Select(page => $"[Page {page.Number}]\n{page.Text}"));
            var replacementCharacters = extracted.Count(character => character == '\uFFFD');
            if (replacementCharacters >= 3 && replacementCharacters > extracted.Length / 100)
                throw new OcrRequiredException(
                    "PDF text appears corrupted by an unsupported font encoding. Arabic text requires a valid Unicode mapping in the PDF; OCR is not configured. Try a searchable PDF or provide the text separately.");

            return CheckLength(extracted);
        }
        return CheckLength(File.ReadAllText(path));
    }

    public static string CheckLength(string text) => text.Length <= MaxCharacters ? text
        : throw new TextExtractionException($"Document exceeds {MaxCharacters:N0} extracted characters. Split it into smaller documents.");
}
