using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Chat;

/// <summary>Extracts plain text from a document the user attaches to a chat. Nothing is executed.</summary>
public static partial class DocumentTextReader
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTextChars = 1_000_000;

    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg",
        ".conf", ".log", ".rst", ".tex", ".srt", ".vtt", ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".c",
        ".h", ".cpp", ".hpp", ".go", ".rs", ".rb", ".php", ".swift", ".sql", ".ps1", ".sh", ".bat", ".cmd", ".css",
        ".scss", ".xaml", ".csproj", ".props", ".gradle", ".r", ".lua", ".dart", ".vue", ".svelte"
    };

    private static readonly HashSet<string> Markup = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    /// <summary>Extensions offered in the file picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [.. PlainText.Concat(Markup).Append(".docx").Order(StringComparer.OrdinalIgnoreCase)];

    public static bool IsSupported(string path) =>
        Path.GetExtension(path) is { Length: > 0 } extension &&
        (PlainText.Contains(extension) || Markup.Contains(extension) || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase));

    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new RuntimeException("document_unsupported", "PDF files can't be read yet. Save the PDF as text or a Word document and attach that.");
        if (!IsSupported(path))
            throw new RuntimeException("document_unsupported", $"{Path.GetFileName(path)} isn't a supported document. Attach a PDF, picture, Word, text, Markdown, code or HTML file.");

        var info = new FileInfo(path);
        if (!info.Exists) throw new RuntimeException("document_missing", $"{info.Name} could not be found.");
        if (info.Length > MaxFileBytes)
            throw new RuntimeException("document_too_large", $"{info.Name} is larger than {MaxFileBytes / (1024 * 1024)} MB.");

        string text;
        try
        {
            text = extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
                ? await Task.Run(() => ReadDocx(path, cancellationToken), cancellationToken).ConfigureAwait(false)
                : await ReadPlainAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or XmlException)
        {
            throw new RuntimeException("document_unreadable", $"{info.Name} could not be read. It may be damaged.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new RuntimeException("document_access", $"Winference isn't allowed to open {info.Name}.", error);
        }
        catch (IOException error)
        {
            throw new RuntimeException("document_access", $"{info.Name} could not be opened. Close it in other apps and try again.", error);
        }

        if (Markup.Contains(extension)) text = StripMarkup(text);
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length == 0) throw new RuntimeException("document_empty", $"{info.Name} has no readable text.");
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];
        return text;
    }
}
