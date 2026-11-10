using System.Text;
using LakeCode.Agent.Tools.Files;

namespace LakeCode.Agent.Tools;

/// <summary>Lists one directory page without recursing or following links.</summary>
public sealed class ListDirectoryTool(WorkspaceFiles files) : AgentTool
{
    private const int PageSize = 500;

    public override string Name => "list_directory";

    public override string Description =>
        "List immediate files and directories. Any path is accepted; access is decided by policy before the call. " +
        "Does not recurse or follow symbolic links. Skips .git, bin and obj directories.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("path", "Directory path relative to the workspace root, or an absolute path.", ".")
        .Integer("offset", "Number of entries to skip to retrieve the next page.", minimum: 0, defaultValue: 0));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var path = arguments.String("path", ".");
            var offset = arguments.Integer("offset", 0);

            if (offset < 0)
            {
                throw new ArgumentException("offset must be nonnegative.");
            }

            var directory = files.Absolute(path);
            var output = new StringBuilder();
            var entries = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal);
            var skipped = 0;
            var emitted = 0;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;

                if (isDirectory && files.SkipDirectory(entry))
                {
                    continue;
                }

                if (skipped++ < offset)
                {
                    continue;
                }

                var type = (attributes & FileAttributes.ReparsePoint) != 0
                    ? "link"
                    : isDirectory
                        ? "directory"
                        : "file";

                var line = $"{type}: {files.Display(entry)}";

                if (emitted >= PageSize || output.Length + line.Length + 1 > WorkspaceFiles.MaxOutputCharacters)
                {
                    return Success(output + $"[Output truncated; continue with offset={offset + emitted}.]");
                }

                output.AppendLine(line);
                emitted++;
            }

            return Success(output.Length == 0 ? "No entries in the requested page." : output.ToString());
        });
}
