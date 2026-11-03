using System.Runtime.CompilerServices;
using System.Text;
using LakeCode.Search;

namespace LakeCode.Core.Agent.Tools.Search;

/// <summary>Per-call scope; all reads remain subject to the shared, live WorkspaceFiles policy.</summary>
internal sealed class WorkspaceSearchSource : ISearchSource
{
    private readonly WorkspaceFiles _files;
    private readonly string _target;
    private readonly string _root;
    private readonly bool _directory;

    public WorkspaceSearchSource(WorkspaceFiles files, string path)
    {
        _files = files;
        _target = files.ResolveRead(path);
        _directory = Directory.Exists(_target);
        _root = _directory ? _target : Path.GetDirectoryName(_target)!;
    }

    public string FileId => Path.GetFileName(_target);
    public string Display(string id) => _files.DisplayPath(FullPath(id));

    private string Resolve(string id) => _files.ResolveRead(FullPath(id));

    private string FullPath(string id)
    {
        var full = Path.GetFullPath(id, _root);
        if (!ExternalReadPermissions.Contains(_root, full) ||
            !_directory && !string.Equals(full, _target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Search path escapes the selected scope.");
        return full;
    }

    public async IAsyncEnumerable<string> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_directory)
        {
            yield return FileId;
            yield break;
        }
        var pending = new Stack<string>();
        pending.Push(_root);
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory != _root && _files.SkipDirectory(directory)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(_files.ResolveRead(directory)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > 100_000) throw new SearchLimitException("Directory entry budget exceeded; narrow the search path.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!_files.SkipDirectory(entry)) pending.Push(entry);
                }
                else yield return Path.GetRelativePath(_root, entry).Replace('\\', '/');
            }
        }
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        // Central bounded UTF-8 reader also preserves oversized/binary/skipped-file behavior.
        var text = await _files.ReadTextAsync(Resolve(path), cancellationToken, preserveBom: true, allowExternalRead: true);
        return new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
    }
}
