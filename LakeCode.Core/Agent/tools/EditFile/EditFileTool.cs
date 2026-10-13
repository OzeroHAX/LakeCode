using System.ComponentModel;

namespace LakeCode.Core.Agent.Tools.EditFile;

internal sealed class EditFileTool(WorkspaceFiles files, Action<FileChange> onChanged)
{
    [Description("Edit a workspace UTF-8 text file by replacing exactly one occurrence of oldText with newText. The match is case-sensitive and includes whitespace and line endings. No match or multiple matches leave the file unchanged. Read the file first and include enough context for a unique match. Maximum size is 1 MiB. Symbolic links are not supported.")]
    public Task<string> EditAsync(
        [Description("File path relative to the workspace root.")] string path,
        [Description("Exact nonempty text to replace; must occur once in the file.")] string oldText,
        [Description("Replacement text; may be empty to delete the matched fragment.")] string newText,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var content = await files.ReadTextAsync(path, cancellationToken, preserveBom: true);
        var start = content.IndexOf(oldText, StringComparison.Ordinal);
        if (start < 0)
            throw new ArgumentException("oldText was not found. Read the file again and use its exact text.");
        if (content.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
            throw new ArgumentException("oldText occurs more than once. Include more surrounding context.");
        var updated = string.Concat(content.AsSpan(0, start), newText.AsSpan(), content.AsSpan(start + oldText.Length));
        await files.WriteTextAsync(path, updated, overwrite: true, cancellationToken);
        onChanged(new FileChange(files.Relative(files.Resolve(path)), content, updated));
        return $"Edited: {files.Relative(files.Resolve(path))}";
    });
}
