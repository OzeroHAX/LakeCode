using System.Text;

namespace LakeCode.Cli.Agent.Tools;

internal sealed class WorkspaceFiles
{
    public const int MaxOutputCharacters = 32_000;
    public const int MaxFileBytes = 1_048_576;
    public string Root { get; } = Path.GetFullPath(Environment.CurrentDirectory);

    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path, Root);
        var relative = Path.GetRelativePath(Root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new UnauthorizedAccessException("Paths outside the workspace are not allowed.");

        // Reject links rather than allowing a checked path to traverse outside the workspace.
        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links are not supported.");
        }
        return fullPath;
    }

    public string Relative(string path) => Path.GetRelativePath(Root, path);

    public static bool SkipDirectory(string path) => Path.GetFileName(path) is ".git" or "bin" or "obj";

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read,
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
        return text.TrimStart('\uFEFF');
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
