using System.ComponentModel;

namespace LakeCode.Core.Agent.Tools.WriteFile;

internal sealed class WriteFileTool(WorkspaceFiles files, Action<FileChange> onChanged)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(WriteAsync, name: "write_file");

    [Description("Create a UTF-8 text file in the workspace, creating parent directories if needed. Existing files are not overwritten unless overwrite is true; prefer edit_file for changes to existing files. Maximum size is 1 MiB. Symbolic links are not supported.")]
    public Task<string> WriteAsync(
        [Description("File path relative to the workspace root.")] string path,
        [Description("Complete file content.")] string content,
        [Description("Explicitly allow replacing an existing file. Read it first; use edit_file for small changes.")] bool overwrite = false,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        string? before = "";
        string? previewError = null;
        if (overwrite && File.Exists(files.Resolve(path, allowMissing: true)))
        {
            try
            {
                before = await files.ReadTextAsync(path, cancellationToken, preserveBom: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A preview must not make an otherwise permitted overwrite fail.
                before = null;
                previewError = error.Message;
            }
        }
        await files.WriteTextAsync(path, content, overwrite, cancellationToken);
        onChanged(new FileChange(files.DisplayPath(path), before, content, previewError));
        return $"Written: {files.DisplayPath(path)}";
    });
}
