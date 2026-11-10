using System.Runtime.CompilerServices;
using System.Text;
using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>
/// Per-call scope. It keeps the search engine inside the directory the caller selected: identifiers
/// produced by <see cref="EnumerateAsync"/> must resolve back to the same tree. Path permissions are
/// not checked here; a hook decides access before the tool runs.
/// </summary>
internal sealed class WorkspaceSearchSource : ISearchSource
{
    private readonly WorkspaceFiles _files;
    private readonly string _target;
    private readonly string _root;
    private readonly bool _directory;

    public WorkspaceSearchSource(WorkspaceFiles files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);

        _files = files;
        _target = files.Absolute(path);
        _directory = Directory.Exists(_target);
        _root = _directory ? _target : Path.GetDirectoryName(_target)!;
    }

    public string FileId => Path.GetFileName(_target);

    public string Display(string id) => _files.Display(FullPath(id));

    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

            if (directory != _root && _files.SkipDirectory(directory))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++visited > 100_000)
                {
                    throw new SearchLimitException("Directory entry budget exceeded; narrow the search path.");
                }

                var attributes = File.GetAttributes(entry);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!_files.SkipDirectory(entry))
                    {
                        pending.Push(entry);
                    }
                }
                else
                {
                    yield return Path.GetRelativePath(_root, entry).Replace('\\', '/');
                }
            }
        }
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        // The central bounded UTF-8 reader also enforces oversized/binary/skipped-file behavior.
        var text = await _files
            .ReadTextAsync(FullPath(path), cancellationToken, preserveBom: true)
            .ConfigureAwait(false);

        return new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
    }

    private string FullPath(string id)
    {
        var full = Path.GetFullPath(id, _root);

        if (!IsInsideScope(full) ||
            (!_directory && !string.Equals(
                full,
                _target,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            throw new ArgumentException("Search path escapes the selected scope.");
        }

        return full;
    }

    private bool IsInsideScope(string full)
    {
        var relative = Path.GetRelativePath(_root, full);

        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
