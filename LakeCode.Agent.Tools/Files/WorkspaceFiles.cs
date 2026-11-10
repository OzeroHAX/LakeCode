using System.Text;

namespace LakeCode.Agent.Tools.Files;

/// <summary>
/// Path arithmetic and file mechanics shared by every file tool: one root for relative paths, bounded
/// reads, atomic writes and hidden-directory filtering. Access decisions are not made here — a hook
/// sees the tool call and its arguments before the tool runs, so the tool must work with any path.
/// </summary>
public sealed class WorkspaceFiles
{
    public const int MaxOutputCharacters = 32_000;
    public const int MaxFileBytes = 1_048_576;

    public WorkspaceFiles(string? workspaceRoot = null)
    {
        Root = workspaceRoot is null ? ResolveRoot() : Path.GetFullPath(workspaceRoot);

        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {Root}");
        }
    }

    public string Root { get; }

    public FileSettings Settings { get; } = new();

    /// <summary>Report absolute paths, as needed when the root is a scratch folder outside the project.</summary>
    public bool ReportAbsolutePaths { get; init; }

    public static string ResolveRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("LAKECODE_WORKSPACE_ROOT");
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Environment.CurrentDirectory
            : configuredRoot);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {root}");
        }

        return root;
    }

    /// <summary>Makes a relative path absolute against the workspace root; absolute paths are kept as is.</summary>
    public string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path, Root);
    }

    /// <summary>True when the path is the root itself or lies below it. Used for display, not for access.</summary>
    public bool IsInside(string path)
    {
        var full = Path.GetFullPath(path, Root);
        var relative = Path.GetRelativePath(Root, full);

        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Path relative to the root when inside it, otherwise the absolute path.</summary>
    public string Display(string path)
    {
        var full = Absolute(path);
        return ReportAbsolutePaths || !IsInside(full) ? full : Path.GetRelativePath(Root, full);
    }

    public bool SkipDirectory(string path) => Settings.Current.HiddenDirectoryNames.Contains(Path.GetFileName(path));

    public async Task<string> ReadTextAsync(
        string path,
        CancellationToken cancellationToken,
        bool preserveBom = false)
    {
        await using var stream = new FileStream(
            Absolute(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > MaxFileBytes)
        {
            throw new IOException($"File exceeds the {MaxFileBytes}-byte text file limit.");
        }

        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int count;

        while ((count = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxFileBytes)
            {
                throw new IOException($"File exceeds the {MaxFileBytes}-byte text file limit.");
            }

            buffer.Write(block, 0, count);
        }

        var text = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

        if (text.Contains('\0'))
        {
            throw new IOException("Binary files are not supported.");
        }

        return preserveBom ? text : text.TrimStart('\uFEFF');
    }

    public async Task WriteTextAsync(
        string path,
        string content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Contains('\0'))
        {
            throw new ArgumentException("Binary files are not supported.");
        }

        var encoding = new UTF8Encoding(false, true);

        if (encoding.GetByteCount(content) > MaxFileBytes)
        {
            throw new IOException($"File exceeds the {MaxFileBytes}-byte text file limit.");
        }

        await WriteBytesCoreAsync(path, encoding.GetBytes(content), overwrite, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<byte[]> ReadImageBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Absolute(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        const int max = ImageContent.MaxImageBytes;

        if (stream.Length > max)
        {
            throw new IOException("Input image exceeds the image size limit.");
        }

        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;

        while ((count = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > max)
            {
                throw new IOException("Input image exceeds the image size limit.");
            }

            buffer.Write(block, 0, count);
        }

        return buffer.ToArray();
    }

    private async Task WriteBytesCoreAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = Absolute(path);
        var parent = Path.GetDirectoryName(destination)!;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        destination = Absolute(path);

        // Commit a complete sibling file rather than truncating the destination on cancellation.
        var temporary = Path.Combine(parent, $".lakecode-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows() && File.Exists(destination))
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
