using System.ComponentModel;
using System.Text;

namespace LakeCode.Core.Agent.Tools.ListDirectory;

internal sealed class ListDirectoryTool(WorkspaceFiles files)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(ListAsync, name: "list_directory");

    [Description("List immediate files and directories. External paths require user confirmation for read-only session access. Does not recurse or follow symbolic links. Skips .git, bin and obj directories.")]
    public Task<string> ListAsync(
        [Description("Directory path relative to the workspace root, or an absolute path requiring confirmation outside it.")] string path = ".",
        [Description("Number of entries to skip to retrieve the next page.")] int offset = 0,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(() =>
    {
        if (offset < 0) throw new ArgumentException("offset must be nonnegative.");
        var directory = files.ResolveRead(path);
        var output = new StringBuilder();
        var entries = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal);
        var skipped = 0;
        var emitted = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory && files.SkipDirectory(entry)) continue;
            if (skipped++ < offset) continue;
            var type = (attributes & FileAttributes.ReparsePoint) != 0 ? "link" : isDirectory ? "directory" : "file";
            var line = $"{type}: {files.Relative(entry)}";
            if (emitted >= 500 || output.Length + line.Length + 1 > WorkspaceFiles.MaxOutputCharacters)
                return Task.FromResult(output + $"[Output truncated; continue with offset={offset + emitted}.]");
            output.AppendLine(line);
            emitted++;
        }
        return Task.FromResult(output.Length == 0 ? "No entries in the requested page." : output.ToString());
    });
}
