using UglyToad.PdfPig;

namespace BillAgent.Worker.Services;

/// <summary>
/// Extracts plain text from a PDF byte stream using PdfPig.
///
/// Pure C#, no native deps. Works for PDFs with selectable/embedded text
/// (which is most utility bills). Scanned image-only PDFs will return empty
/// or near-empty text — those will fall back to LLM vision in a later day.
/// </summary>
public class PdfTextExtractor
{
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
    {
        _logger = logger;
    }

    public string Extract(byte[] pdfBytes, string filename = "unknown")
    {
        using var doc = PdfDocument.Open(pdfBytes);
        var sb = new System.Text.StringBuilder();

        foreach (var page in doc.GetPages())
        {
            sb.AppendLine($"--- page {page.Number} ---");
            sb.AppendLine(page.Text);
        }

        var text = sb.ToString();
        _logger.LogInformation(
            "Extracted {Chars} chars from {File} ({Pages} pages)",
            text.Length, filename, doc.NumberOfPages);

        return text;
    }
}
