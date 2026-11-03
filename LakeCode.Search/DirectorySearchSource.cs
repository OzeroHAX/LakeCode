using System.Runtime.CompilerServices;

namespace LakeCode.Search;

public enum SourceAccess { EnumerateDirectory, ReadFile }

/// <summary>
/// Optional disk adapter. The host must authorize each access. Does not follow links or escape its root.
/// For other policies (virtual files, external permissions, links), implement ISearchSource in the host.
/// </summary>
public sealed class DirectorySearchSource : ISearchSource
{
    private readonly string _root;
    private readonly int _maxEntries;
    private readonly Func<string, SourceAccess, CancellationToken, ValueTask> _authorize;

    public DirectorySearchSource(string root, Func<string, SourceAccess, CancellationToken, ValueTask> authorize, int maxEntries = 100_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _maxEntries = maxEntries;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
    }

    public async IAsyncEnumerable<string> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(_root);
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _authorize(directory, SourceAccess.EnumerateDirectory, cancellationToken);
            ValidateLinks(directory);
            // Streaming: no materialization of an arbitrarily large directory.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > _maxEntries) throw new SearchLimitException("Directory entry budget exceeded.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return Path.GetRelativePath(_root, entry).Replace('\\', '/');
            }
        }
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path, _root);
        var relative = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the source root.");
        await _authorize(full, SourceAccess.ReadFile, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLinks(full);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void ValidateLinks(string full)
    {
        var current = Path.GetPathRoot(full)!;
        foreach (var part in Path.GetRelativePath(current, full).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links are not supported by DirectorySearchSource.");
        }
    }
}
