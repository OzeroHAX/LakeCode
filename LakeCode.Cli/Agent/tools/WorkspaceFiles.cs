using System.Text;

namespace LakeCode.Cli.Agent.Tools;

internal sealed class WorkspaceFiles
{
    public const int MaxOutputCharacters = 32_000;
    public const int MaxFileBytes = 1_048_576;
    public string Root { get; }
    private readonly ExternalReadPermissions _externalReads;

    public WorkspaceFiles(string? workspaceRoot = null,
        Func<ExternalReadRequest, CancellationToken, Task<ExternalReadDecision>>? confirmExternalRead = null)
    {
        Root = workspaceRoot is null ? ResolveRoot() : Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {Root}");
        _externalReads = new ExternalReadPermissions(confirmExternalRead);
    }

    public static string ResolveRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("LAKECODE_WORKSPACE_ROOT");
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Environment.CurrentDirectory
            : configuredRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {root}");
        return root;
    }

    public string Resolve(string path, bool allowMissing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path, Root);
        if (!ExternalReadPermissions.Contains(Root, fullPath))
            throw new UnauthorizedAccessException("Paths outside the workspace are not allowed.");
        ValidateLinks(fullPath, Root, allowMissing);
        return fullPath;
    }

    public async Task AuthorizeReadAsync(string path, string toolName, string callId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path, Root);
        if (ExternalReadPermissions.Contains(Root, fullPath)) return;
        ValidateLinks(fullPath, Path.GetPathRoot(fullPath)!, allowMissing: false);
        var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!;
        directory = Path.TrimEndingDirectorySeparator(directory);
        await _externalReads.AuthorizeAsync(new ExternalReadRequest(callId, toolName, fullPath, directory),
            cancellationToken);
    }

    public string ResolveRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path, Root);
        if (ExternalReadPermissions.Contains(Root, fullPath)) return Resolve(path);
        if (!_externalReads.IsAllowed(fullPath))
            throw new UnauthorizedAccessException("External read access has not been granted.");
        ValidateLinks(fullPath, Path.GetPathRoot(fullPath)!, allowMissing: false);
        return fullPath;
    }

    private static void ValidateLinks(string fullPath, string root, bool allowMissing)
    {
        var relative = Path.GetRelativePath(root, fullPath);

        // Reject links rather than allowing a checked path to traverse outside the workspace.
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Symbolic links are not supported.");
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }

    public string Relative(string path) => Path.GetRelativePath(Root, path);

    public static bool SkipDirectory(string path) => Path.GetFileName(path) is ".git" or "bin" or "obj";

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken,
        bool preserveBom = false, bool allowExternalRead = false)
    {
        await using var stream = new FileStream(allowExternalRead ? ResolveRead(path) : Resolve(path),
            FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxFileBytes)
            throw new IOException($"File exceeds the {MaxFileBytes}-byte text file limit.");
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken)) != 0)
        {
            if (buffer.Length + count > MaxFileBytes)
                throw new IOException($"File exceeds the {MaxFileBytes}-byte text file limit.");
            buffer.Write(block, 0, count);
        }
        var text = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (text.Contains('\0')) throw new IOException("Binary files are not supported.");
        return preserveBom ? text : text.TrimStart('\uFEFF');
    }

    public async Task WriteTextAsync(string path, string content, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Contains('\0')) throw new ArgumentException("Binary files are not supported.");
        var encoding = new UTF8Encoding(false, true);
        if (encoding.GetByteCount(content) > MaxFileBytes)
            throw new IOException($"File exceeds the {MaxFileBytes}-byte text file limit.");
        var destination = Resolve(path, allowMissing: true);
        var parent = Path.GetDirectoryName(destination)!;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        destination = Resolve(path, allowMissing: true);

        // Commit a complete sibling file rather than truncating the destination on cancellation.
        var temporary = Path.Combine(parent, $".lakecode-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(encoding.GetBytes(content), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(destination))
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
            cancellationToken.ThrowIfCancellationRequested();
            Resolve(path, allowMissing: true);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<string> ExecuteAsync(Func<Task<string>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                         or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return $"Error: {exception.Message}";
        }
    }
}
