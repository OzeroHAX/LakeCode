namespace LakeCode.Core.Agent.Tools;

internal sealed class ExternalReadPermissions
{
    private readonly Dictionary<string, bool> _decisions = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    public bool AllowAll
    {
        get => Volatile.Read(ref _allowAll);
        set
        {
            lock (_decisions)
            {
                _modeRevision++;
                Volatile.Write(ref _allowAll, value);
            }
        }
    }
    private bool _allowAll;
    private int _modeRevision;
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

    public bool IsAllowed(string path) => AllowAll || FindDecision(path) == true;

    public async Task AuthorizeAsync(ExternalReadRequest request, CancellationToken cancellationToken)
    {
        await _promptGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AllowAll) return;
            var allowed = FindDecision(request.Path);
            if (allowed is null)
            {
                int modeRevision;
                lock (_decisions) modeRevision = _modeRevision;
                var choice = _confirm is null ? ExternalReadDecision.DenyOnce
                    : await _confirm(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Automatic approval must not become a remembered directory decision.
                if (AllowAll) return;
                allowed = choice == ExternalReadDecision.AllowSession;
                if (choice is ExternalReadDecision.AllowSession or ExternalReadDecision.DenySession)
                {
                    lock (_decisions)
                        if (_modeRevision == modeRevision)
                            _decisions[request.Directory] = allowed.Value;
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
