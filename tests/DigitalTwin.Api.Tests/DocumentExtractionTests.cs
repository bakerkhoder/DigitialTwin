using System.Net;
using DigitalTwin.Api;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace DigitalTwin.Api.Tests;

public sealed class DocumentExtractionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "twin-extraction-" + Guid.NewGuid().ToString("N"));
    public DocumentExtractionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SearchablePdfIsExtractedLocallyEvenWhenExternalAnalysisIsAllowed()
    {
        var path = WritePdf("Approval is required outside allowed project roots.");
        using var provider = new RecordingProvider();
        using var http = new HttpClient(provider);
        var settings = new TwinSettings { OpenRouterApiKey = "test-key" };
        var extracted = await new DocumentExtractionService(new VisionTools(http, settings), settings)
            .ExtractAsync(path, "policy.pdf", true, default);
        Assert.Equal("local-pdf", extracted.Method);
        Assert.Contains("Approval is required", extracted.Text);
        Assert.Contains("[Page 1]", extracted.Text);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ScannedPdfRequiresUploadConsentBeforeExternalProcessing()
    {
        var path = WritePdf(null);
        using var provider = new RecordingProvider();
        using var http = new HttpClient(provider);
        var settings = new TwinSettings { OpenRouterApiKey = "test-key" };
        var extractor = new DocumentExtractionService(new VisionTools(http, settings), settings);
        var error = await Assert.ThrowsAsync<TextExtractionException>(() => extractor.ExtractAsync(path, "scan.pdf", false, default));
        Assert.Contains("needs OCR", error.Message);
        Assert.Equal(0, provider.Calls);
        var extracted = await extractor.ExtractAsync(path, "scan.pdf", true, default);
        Assert.Equal("openrouter-pdf", extracted.Method);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ImageCannotUseHostedProviderWithoutUploadConsent()
    {
        var path = Path.Combine(_directory, "image.png");
        await File.WriteAllBytesAsync(path, [137, 80, 78, 71]);
        using var provider = new RecordingProvider();
        using var http = new HttpClient(provider);
        var settings = new TwinSettings { OpenRouterApiKey = "test-key" };
        await Assert.ThrowsAsync<VisionConfigurationException>(() =>
            new DocumentExtractionService(new VisionTools(http, settings), settings).ExtractAsync(path, "image.png", false, default));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task DocxExtractsParagraphAndTableTextLocally()
    {
        var path = Path.Combine(_directory, "requirements.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var part = document.AddMainDocumentPart();
            var body = new Body();
            body.Append(new Paragraph(new Run(new Text("Project requirements"))));
            body.Append(new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("Approval policy")))))));
            part.Document = new Document(body);
        }
        using var provider = new RecordingProvider();
        using var http = new HttpClient(provider);
        var settings = new TwinSettings();
        var extracted = await new DocumentExtractionService(new VisionTools(http, settings), settings)
            .ExtractAsync(path, "requirements.docx", false, default);
        Assert.Equal("local-docx", extracted.Method);
        Assert.Contains("Project requirements\nApproval policy", extracted.Text);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(".json", "{\"enabled\":true}")]
    [InlineData(".md", "# Policy\nApproval required.")]
    [InlineData(".csv", "name,value\npolicy,approval")]
    [InlineData(".cs", "public record Policy(bool ApprovalRequired);")]
    public async Task TextFormatsKeepTheirContents(string extension, string content)
    {
        var path = Path.Combine(_directory, "file" + extension);
        await File.WriteAllTextAsync(path, content);
        using var provider = new RecordingProvider();
        using var http = new HttpClient(provider);
        var settings = new TwinSettings();
        var result = await new DocumentExtractionService(new VisionTools(http, settings), settings)
            .ExtractAsync(path, Path.GetFileName(path), false, default);
        Assert.Equal(content, result.Text);
        Assert.Equal("local-text", result.Method);
        Assert.Equal(0, provider.Calls);
    }

    private string WritePdf(string? text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        if (text is not null)
            page.AddText(text, 12, new PdfPoint(30, 750), builder.AddStandard14Font(Standard14Font.Helvetica));
        var path = Path.Combine(_directory, Guid.NewGuid() + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class RecordingProvider : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Extracted OCR content\"}}]}")
            });
        }
    }
}
