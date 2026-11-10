using System.Collections.Immutable;

namespace LakeCode.Agent.Tools.Files;

/// <summary>Live session settings. Publish whole immutable snapshots; tools retain the same file context.</summary>
public sealed record FileSettingsSnapshot(ImmutableHashSet<string> HiddenDirectoryNames);

public sealed class FileSettings
{
    private readonly object _gate = new();

    private FileSettingsSnapshot _current = new(
        ImmutableHashSet.Create(StringComparer.Ordinal, ".git", "bin", "obj"));

    public FileSettingsSnapshot Current => Volatile.Read(ref _current);

    public void SetHiddenDirectories(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var hidden = names.ToImmutableHashSet(StringComparer.Ordinal);

        if (hidden.Any(name => string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                               name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
        {
            throw new ArgumentException("Hidden directories must be nonempty directory names.", nameof(names));
        }

        lock (_gate)
        {
            Volatile.Write(ref _current, _current with { HiddenDirectoryNames = hidden });
        }
    }
}
