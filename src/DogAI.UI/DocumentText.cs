using DogAI.Core.Documents;
using DogAI.Core.Models;

namespace DogAI.UI;

/// <summary>Wording for documents in lists.</summary>
public static class DocumentText
{
    /// <summary>"Ready · 12 passages", "Indexing 4 of 12", "Waiting for a search model", or the problem.</summary>
    public static string Status(DocumentSummary doc, IndexProgress? progress) => doc.Status switch
    {
        DocumentStatus.Ready => Details(doc),
        DocumentStatus.Indexing when progress is { } p && p.DocumentId == doc.Id => $"Indexing {p.Done} of {p.Total} passages",
        DocumentStatus.Indexing => "Indexing…",
        DocumentStatus.Failed => doc.Error ?? "It could not be indexed.",
        _ => "Waiting to be indexed",
    };

    /// <summary>"PDF · 12 pages · 1.2 MB · 40 passages".</summary>
    public static string Details(DocumentSummary doc) => string.Join(" · ", new[]
    {
        Kind(doc.Kind),
        doc.PageCount > 0 ? $"{doc.PageCount} page{(doc.PageCount == 1 ? "" : "s")}" : null,
        Size(doc.SizeBytes),
        $"{doc.ChunkCount} passage{(doc.ChunkCount == 1 ? "" : "s")}",
    }.Where(s => s is not null));

    public static string Kind(string kind) => kind switch
    {
        "pdf" => "PDF",
        "docx" => "Word",
        "markdown" => "Markdown",
        "code" => "Code",
        _ => "Text",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
