namespace LakeCode.Core.Agent.Tools;

internal sealed class ExternalReadPermissions
{
    private readonly Dictionary<string, bool> _decisions = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private readonly Func<ExternalReadRequest, CancellationToken, Task<ExternalReadDecision>>? _confirm;

    public ExternalReadPermissions(Func<ExternalReadRequest, CancellationToken, Task<ExternalReadDecision>>? confirm)
    {
        _confirm = confirm;
    }

    public static bool Contains(string directory, string path)
    {
        var relative = System.IO.Path.GetRelativePath(directory, path);
        return !System.IO.Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private bool? FindDecision(string path)
    {
        lock (_decisions)
        {
            string? closest = null;
            bool? decision = null;
            foreach (var entry in _decisions)
            {
                if ((closest is null || entry.Key.Length > closest.Length) && Contains(entry.Key, path))
                {
                    closest = entry.Key;
                    decision = entry.Value;
                }
            }
            return decision;
        }
    }

    public bool IsAllowed(string path) => FindDecision(path) == true;

    public async Task AuthorizeAsync(ExternalReadRequest request, CancellationToken cancellationToken)
    {
        await _promptGate.WaitAsync(cancellationToken);
        try
        {
            var allowed = FindDecision(request.Path);
            if (allowed is null)
            {
                var choice = _confirm is null ? ExternalReadDecision.DenyOnce
                    : await _confirm(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                allowed = choice == ExternalReadDecision.AllowSession;
                if (choice is ExternalReadDecision.AllowSession or ExternalReadDecision.DenySession)
                {
                    lock (_decisions) _decisions[request.Directory] = allowed.Value;
                }
            }
            if (allowed != true)
                throw new UnauthorizedAccessException($"External read access denied: {request.Directory}");
        }
        finally
        {
            _promptGate.Release();
        }
    }
}
