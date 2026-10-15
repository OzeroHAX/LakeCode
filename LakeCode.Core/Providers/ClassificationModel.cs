using System.Text.RegularExpressions;

namespace LakeCode.Core.Providers;

/// <summary>System One model identity; not a chat model and has no reasoning or generation settings.</summary>
public sealed record ClassificationModel(string Id)
{
    public bool IsVersioned => IsVersionedId(Id);
    public static bool IsVersionedId(string? id) => id is { Length: > 0 and <= 128 } &&
        Regex.IsMatch(id, @"\Ajev-\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
