using System.Collections.Immutable;
using System.Text;
using System.Xml;
using LakeCode.Core.Agent.Tools;

namespace LakeCode.Core.Attachments;

/// <summary>A selected workspace file. Content is a send-time snapshot, not the current disk version.</summary>
public sealed record FileAttachment(string Path, string? Content, int EstimatedTokens, string? ReferenceReason);
public sealed record FileSearchResult(ImmutableArray<string> Paths, bool Truncated, int Skipped);

public static class FileAttachments
{
    public const int MaxFiles = 16;
    public const int MaxFileTokens = 4_000;
    public const int MaxTotalTokens = 12_000;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".razor", ".xaml",
        ".txt", ".md", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".config",
        ".js", ".jsx", ".ts", ".tsx", ".html", ".css", ".scss", ".vue", ".svelte",
        ".py", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".rb", ".php",
        ".sh", ".bash", ".ps1", ".sql", ".graphql", ".proto", ".csv", ".gitignore", ".editorconfig"
    };

    public static int Budget(int contextWindowTokens) => Math.Min(MaxTotalTokens, Math.Max(0, contextWindowTokens / 10));
    public static int Estimate(string text) => (Encoding.UTF8.GetByteCount(text) + 2) / 3;

    public static FileSearchResult Search(string root, string query, CancellationToken cancellationToken)
    {
        var files = new WorkspaceFiles(root);
        var directories = new Stack<string>();
        directories.Push(files.Resolve("."));
        var matches = new List<string>();
        var visited = 0;
        var skipped = 0;
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                files.Resolve(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > 20_000) return Result(true);
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!WorkspaceFiles.SkipDirectory(entry) && Path.GetFileName(entry) != "node_modules")
                                directories.Push(entry);
                            continue;
                        }
                        var path = files.Relative(entry);
                        if (IsTextCandidate(path) && path.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(path);
                            if (matches.Count >= 100) return Result(true);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return Result(false);

        FileSearchResult Result(bool truncated) => new(matches.OrderBy(path => path, StringComparer.Ordinal)
            .Take(10).ToImmutableArray(), truncated || matches.Count > 10, skipped);
    }

    private static bool IsTextCandidate(string path) =>
        string.IsNullOrEmpty(Path.GetExtension(path)) || TextExtensions.Contains(Path.GetExtension(path)) ||
        Path.GetFileName(path) is ".gitignore" or ".editorconfig";

    public static async Task<ImmutableArray<FileAttachment>> CaptureAsync(
        string root, IReadOnlyList<string> paths, int contextWindowTokens, CancellationToken cancellationToken)
    {
        if (paths.Count > MaxFiles) throw new InvalidDataException($"Можно прикрепить не больше {MaxFiles} файлов.");
        var files = new WorkspaceFiles(root);
        var result = new List<FileAttachment>();
        // Reserve one estimated token per separator; rounded per-entry estimates are conservative.
        var remaining = Budget(contextWindowTokens) - Estimate("<attachments>\n</attachments>") - paths.Count;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = files.Resolve(path);
            var relative = files.Relative(fullPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException($"Файл недоступен: {relative}");
            string? content = null;
            string? reason = null;
            // A UTF-8 byte consumes at least as much space in our serialized estimate.
            if (new FileInfo(fullPath).Length > MaxFileTokens * 3)
                reason = "Размер превышает бюджет содержимого одного файла.";
            else
            {
                content = await files.ReadTextAsync(relative, cancellationToken);
                try { XmlConvert.VerifyXmlChars(content); }
                catch (XmlException) { content = null; reason = "Текст содержит символы, недопустимые в XML."; }
            }
            var candidate = new FileAttachment(relative, content, 0, reason);
            var tokens = Estimate(SerializeFile(candidate));
            if (content is not null && (tokens > MaxFileTokens || tokens > remaining))
            {
                reason = tokens > MaxFileTokens ? "Превышен лимит ≈4 000 токенов на файл."
                    : "Не хватает общего бюджета содержимого вложений.";
                candidate = candidate with { Content = null, ReferenceReason = reason };
                tokens = Estimate(SerializeFile(candidate));
            }
            // Every entry, including references, counts against the total serialized budget.
            if (tokens > remaining)
            {
                // Demote earlier snapshots if necessary to leave room for all selected references.
                for (var i = result.Count - 1; i >= 0 && tokens > remaining; i--)
                {
                    if (result[i].Content is null) continue;
                    var reference = result[i] with { Content = null, ReferenceReason = "Оставлен бюджет для ссылок на остальные файлы." };
                    var referenceTokens = Estimate(SerializeFile(reference));
                    remaining += result[i].EstimatedTokens - referenceTokens;
                    result[i] = reference with { EstimatedTokens = referenceTokens };
                }
                if (tokens > remaining) throw new InvalidDataException("Ссылки на файлы превышают бюджет вложений этой модели.");
            }
            result.Add(candidate with { EstimatedTokens = tokens });
            remaining -= tokens;
        }
        Validate(result, contextWindowTokens);
        return result.ToImmutableArray();
    }

    public static string Serialize(IEnumerable<FileAttachment> attachments)
    {
        var entries = attachments.ToArray();
        return entries.Length == 0 ? "" : "<attachments>\n" + string.Join("\n", entries.Select(SerializeFile)) + "\n</attachments>";
    }

    private static string SerializeFile(FileAttachment attachment)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.None }))
        {
            writer.WriteStartElement("file");
            writer.WriteAttributeString("path", attachment.Path);
            writer.WriteAttributeString("mode", attachment.Content is null ? "reference" : "snapshot");
            if (attachment.Content is not null) writer.WriteElementString("content", attachment.Content);
            writer.WriteEndElement();
        }
        return builder.ToString();
    }

    public static void Validate(IEnumerable<FileAttachment> attachments, int? contextWindowTokens = null)
    {
        var entries = attachments.ToArray();
        if (entries.Length > MaxFiles || entries.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path) ||
            Path.IsPathRooted(item.Path) || item.EstimatedTokens != Estimate(SerializeFile(item)) ||
            (item.Content is not null && item.EstimatedTokens > MaxFileTokens)))
            throw new InvalidDataException("Некорректные файловые вложения.");
        if (entries.Length > 0 && Estimate(Serialize(entries)) > (contextWindowTokens is { } window ? Budget(window) : MaxTotalTokens))
            throw new InvalidDataException("Превышен общий бюджет файловых вложений.");
    }
}
