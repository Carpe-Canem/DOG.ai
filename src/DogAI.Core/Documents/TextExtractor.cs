using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace DogAI.Core.Documents;

/// <summary>A file could not be read as a document. The message says why, in plain words.</summary>
public sealed class DocumentReadException(string message) : Exception(message);

/// <param name="Text">The document's text. Word headings are written as markdown "#" lines.</param>
/// <param name="Kind">"pdf", "docx", "markdown", "code" or "text".</param>
/// <param name="PageStarts">PDFs: where each page starts in <paramref name="Text"/> (index 0 is page 1). Empty otherwise.</param>
public sealed record ExtractedText(string Text, string Kind, IReadOnlyList<int> PageStarts)
{
    /// <summary>The page (from 1) that holds <paramref name="offset"/>, or null when the document has no pages.</summary>
    public int? PageAt(int offset)
    {
        if (PageStarts.Count == 0) return null;
        var page = 1;
        for (var i = 0; i < PageStarts.Count && PageStarts[i] <= offset; i++) page = i + 1;
        return page;
    }

    /// <summary>Whether lines starting with "#" are headings (in code they are usually comments).</summary>
    public bool HasMarkdownHeadings => Kind is "markdown" or "docx";
}

/// <summary>
/// Reads the text out of a file the user adds: PDF (its text layer), Word (.docx), markdown, code and plain text.
/// Files are untrusted: XML is read with DTDs refused, and sizes are capped.
/// </summary>
public static class TextExtractor
{
    public const long MaxFileBytes = 200L * 1024 * 1024;
    public const int MaxChars = 20_000_000;
    private const long MaxUnzippedXml = 200L * 1024 * 1024;

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx", ".rst" };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".xaml", ".csproj", ".py", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp",
        ".cc", ".go", ".rs", ".rb", ".php", ".lua", ".sql", ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".json", ".yaml", ".yml", ".toml", ".ini",
        ".xml", ".html", ".htm", ".css", ".scss", ".vue", ".svelte", ".r", ".m", ".scala", ".dart", ".fs", ".vb",
    };

    /// <summary>File types offered in the "Add files" dialog. Any other file is accepted if it turns out to be text.</summary>
    public const string DialogFilter =
        "Documents (*.pdf;*.docx;*.txt;*.md;code)|*.pdf;*.docx;*.txt;*.md;*.markdown;*.csv;*.log;*.cs;*.py;*.js;*.ts;*.java;*.c;*.cpp;*.h;*.go;*.rs;*.json;*.xml;*.html;*.yaml;*.yml;*.sql;*.ps1;*.sh|All files (*.*)|*.*";

    public static ExtractedText Read(string fileName, byte[] bytes)
    {
        if (bytes.LongLength > MaxFileBytes) throw new DocumentReadException("It is larger than 200 MB, which is more than DOG AI reads.");
        var extension = Path.GetExtension(fileName);
        ExtractedText result;
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || StartsWith(bytes, "%PDF-"u8))
            result = ReadPdf(bytes);
        else if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            result = ReadDocx(bytes);
        else if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
            throw new DocumentReadException("Old Word files (.doc) cannot be read. Save it as .docx or PDF in Word and add that instead.");
        else
            result = ReadText(bytes, MarkdownExtensions.Contains(extension) ? "markdown" : CodeExtensions.Contains(extension) ? "code" : "text");

        if (result.Text.Length > MaxChars) throw new DocumentReadException("It has more than 20 million characters of text, which is more than DOG AI reads.");
        if (result.Text.Trim().Length == 0)
            throw new DocumentReadException(result.Kind == "pdf"
                ? "This PDF has no text in it. It is probably scanned pages (pictures of text), which DOG AI cannot read."
                : "There is no text in it.");
        return result;
    }

    private static ExtractedText ReadPdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
            var text = new StringBuilder();
            var starts = new List<int>();
            foreach (var page in pdf.GetPages())
            {
                if (text.Length > 0) text.Append("\n\n");
                starts.Add(text.Length);
                text.Append(Tidy(ContentOrderTextExtractor.GetText(page)));
                if (text.Length > MaxChars) break;
            }
            return new ExtractedText(text.ToString(), "pdf", starts);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new DocumentReadException("This PDF is protected with a password, so it cannot be read.");
        }
        catch (Exception e) when (e is not DocumentReadException)
        {
            throw new DocumentReadException("This PDF could not be read. It may be damaged. (" + e.Message + ")");
        }
    }

    private static ExtractedText ReadDocx(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentReadException("This is not a Word document (it has no document part).");
            if (entry.Length > MaxUnzippedXml) throw new DocumentReadException("This Word document is too large to read.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(reader);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = doc.Root?.Element(w + "body") ?? throw new DocumentReadException("This Word document has no body.");
            var text = new StringBuilder();
            WordBlocks(body, w, text);
            return new ExtractedText(Tidy(text.ToString()), "docx", []);
        }
        catch (InvalidDataException)
        {
            throw new DocumentReadException("This Word document could not be opened. It may be damaged, or protected with a password.");
        }
        catch (XmlException e)
        {
            throw new DocumentReadException("This Word document could not be read (" + e.Message + ").");
        }
    }

    /// <summary>Paragraphs become lines (headings as "#" lines, list items as "- "), tables become rows of cells joined by " | ".</summary>
    private static void WordBlocks(XElement parent, XNamespace w, StringBuilder text)
    {
        foreach (var block in parent.Elements())
        {
            if (block.Name == w + "p")
            {
                var line = WordParagraph(block, w);
                var style = block.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
                var level = HeadingLevel(style);
                if (line.Trim().Length == 0) { text.Append('\n'); continue; }
                if (level > 0) text.Append('\n').Append(new string('#', level)).Append(' ').Append(line.Trim()).Append("\n\n");
                else if (block.Element(w + "pPr")?.Element(w + "numPr") is not null) text.Append("- ").Append(line).Append('\n');
                else text.Append(line).Append("\n\n");
            }
            else if (block.Name == w + "tbl")
            {
                foreach (var row in block.Elements(w + "tr"))
                {
                    var cells = row.Elements(w + "tc").Select(c => string.Join(" ", c.Elements(w + "p").Select(p => WordParagraph(p, w).Trim()).Where(s => s.Length > 0)));
                    text.Append(string.Join(" | ", cells)).Append('\n');
                }
                text.Append('\n');
            }
            else if (block.Name == w + "sdt")
            {
                if (block.Element(w + "sdtContent") is { } content) WordBlocks(content, w, text);
            }
        }
    }

    private static string WordParagraph(XElement paragraph, XNamespace w)
    {
        var line = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == w + "t") line.Append(node.Value);
            else if (node.Name == w + "tab") line.Append('\t');
            else if (node.Name == w + "br" || node.Name == w + "cr") line.Append('\n');
            else if (node.Name == w + "noBreakHyphen") line.Append('-');
        }
        return line.ToString();
    }

    private static int HeadingLevel(string style)
    {
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out var n)) return Math.Clamp(n, 1, 6);
        return 0;
    }

    private static ExtractedText ReadText(byte[] bytes, string kind)
    {
        // Text files rarely contain NUL; binary files almost always do, near the start.
        var probe = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
        var utf16 = StartsWith(bytes, [0xFF, 0xFE]) || StartsWith(bytes, [0xFE, 0xFF]);
        if (!utf16 && probe.IndexOf((byte)0) >= 0)
            throw new DocumentReadException("It does not look like a text file, and DOG AI reads only PDF, Word (.docx), and text or code files.");
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        return new ExtractedText(Tidy(text), kind, []);
    }

    /// <summary>Plain line ends, no control characters, at most one blank line in a row, no trailing spaces.</summary>
    internal static string Tidy(string text)
    {
        var output = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder(raw.Length);
            foreach (var c in raw)
                if (c == '\t' || !char.IsControl(c)) line.Append(c);
            var clean = line.ToString().TrimEnd();
            if (clean.Length == 0)
            {
                if (++blank > 1 || output.Length == 0) continue;
            }
            else blank = 0;
            output.Append(clean).Append('\n');
        }
        return output.ToString().TrimEnd();
    }

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);
}
