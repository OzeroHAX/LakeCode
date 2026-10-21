using System.Collections.Immutable;

namespace LakeCode.Core.Agent.Tools;

internal sealed record FileSettingsSnapshot(
    bool AllowAllExternalReads, ImmutableHashSet<string> HiddenDirectoryNames, long AccessRevision);

/// <summary>Live session settings. Publish whole immutable snapshots; tools retain the same file context.</summary>
internal sealed class FileSettings
{
    // Permission decisions and access revisions use this gate so stale prompt answers cannot be remembered.
    internal object SyncRoot { get; } = new();
    private FileSettingsSnapshot _current = new(false,
        ImmutableHashSet.Create(StringComparer.Ordinal, ".git", "bin", "obj"), 0);
    public FileSettingsSnapshot Current => Volatile.Read(ref _current);

    public void SetAllowAllExternalReads(bool allowed)
    {
        lock (SyncRoot)
            Volatile.Write(ref _current, _current with
            {
                AllowAllExternalReads = allowed,
                AccessRevision = _current.AccessRevision + 1
            });
    }

    public void SetHiddenDirectories(IEnumerable<string> names)
    {
        var hidden = names.ToImmutableHashSet(StringComparer.Ordinal);
        if (hidden.Any(name => string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                               name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
            throw new ArgumentException("Hidden directories must be nonempty directory names.", nameof(names));
        lock (SyncRoot)
            Volatile.Write(ref _current, _current with { HiddenDirectoryNames = hidden });
    }
}
