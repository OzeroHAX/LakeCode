using System.ComponentModel;

namespace LakeCode.Cli.Agent.Tools.WriteFile;

internal sealed class WriteFileTool(WorkspaceFiles files)
{
    [Description("Create a UTF-8 text file in the workspace, creating parent directories if needed. Existing files are not overwritten unless overwrite is true; prefer edit_file for changes to existing files. Maximum size is 1 MiB. Symbolic links are not supported.")]
    public Task<string> WriteAsync(
        [Description("File path relative to the workspace root.")] string path,
        [Description("Complete file content.")] string content,
        [Description("Explicitly allow replacing an existing file. Read it first; use edit_file for small changes.")] bool overwrite = false,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        await files.WriteTextAsync(path, content, overwrite, cancellationToken);
        return $"Written: {files.Relative(files.Resolve(path))}";
    });
}
