using System.Text;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using Winference.Core.Runtime;
using Winference.Infrastructure.Chat;

namespace Winference.App.Services;

/// <summary>
/// Reads PDFs and pictures with the PDF renderer and text recognition (OCR) built into Windows.
/// Each page is rendered to an image and read, so scanned documents work too. Nothing is
/// downloaded and nothing leaves this PC. Other formats go to <see cref="DocumentTextReader"/>.
/// </summary>
public static class WindowsDocumentReader
{
    public const int MaxPdfPages = 60;
    private const uint TargetPixels = 2400;

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        (string[])[.. DocumentTextReader.SupportedExtensions.Concat(Images).Append(".pdf").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public static bool NeedsOcr(string path) =>
        Path.GetExtension(path) is { } extension && (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || Images.Contains(extension));

    /// <param name="progress">Short status such as "Reading page 3 of 12…".</param>
    public static async Task<string> ReadAsync(string path, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!NeedsOcr(path)) return await DocumentTextReader.ReadAsync(path, token);
        var name = Path.GetFileName(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new RuntimeException("document_missing", $"{name} could not be found.");
        if (info.Length > 100L * 1024 * 1024) throw new RuntimeException("document_too_large", $"{name} is larger than 100 MB.");
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new RuntimeException("ocr_unavailable",
                "Windows text recognition isn't set up for your language. Add a language with \"Optical character recognition\" in Settings > Time & language > Language & region.");

        StorageFile file;
        try { file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)); }
        catch (Exception error) when (error is UnauthorizedAccessException or FileNotFoundException)
        {
            throw new RuntimeException("document_access", $"Winference isn't allowed to open {name}.", error);
        }

        string text;
        try
        {
            text = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? await ReadPdfAsync(file, engine, progress, token)
                : await ReadImageAsync(file, engine, progress, token);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidDataException or ArgumentException)
        {
            throw new RuntimeException("document_unreadable",
                $"{name} could not be read. It may be damaged or password-protected.", error);
        }

        text = text.Trim();
        if (text.Length == 0)
            throw new RuntimeException("document_empty", $"No text was found in {name}. Pictures without writing can't be attached yet.");
        return text.Length > DocumentTextReader.MaxTextChars ? text[..DocumentTextReader.MaxTextChars] : text;
    }

    private static async Task<string> ReadPdfAsync(StorageFile file, OcrEngine engine, IProgress<string>? progress, CancellationToken token)
    {
        var document = await PdfDocument.LoadFromFileAsync(file);
        var pages = (int)Math.Min(document.PageCount, MaxPdfPages);
        var builder = new StringBuilder();
        for (var index = 0; index < pages; index++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Reading page {index + 1} of {pages}…");
            using var page = document.GetPage((uint)index);
            var size = page.Size;
            // Render the long side at TargetPixels, within what the OCR engine accepts.
            var longSide = Math.Min(TargetPixels, OcrEngine.MaxImageDimension);
            var scale = longSide / Math.Max(size.Width, size.Height);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, size.Height * scale),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
            });
            var pageText = await RecognizeAsync(stream, engine);
            if (pageText.Length == 0) continue;
            if (pages > 1) builder.Append("[Page ").Append(index + 1).AppendLine("]");
            builder.AppendLine(pageText).AppendLine();
        }
        if (document.PageCount > MaxPdfPages)
            builder.AppendLine($"[Only the first {MaxPdfPages} of {document.PageCount} pages were read.]");
        return builder.ToString();
    }
}
