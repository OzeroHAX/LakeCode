using System.ComponentModel;

namespace LakeCode.Core.Agent.Tools.WriteFile;

internal sealed class WriteFileTool(WorkspaceFiles files, Action<FileChange> onChanged)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(WriteAsync, name: "write_file");

    [Description("Create a UTF-8 text file in the workspace, creating parent directories if needed. Existing files are not overwritten unless overwrite is true; prefer edit_file for changes to existing files. Provide all seven rationale fields. Maximum size is 1 MiB. Symbolic links are not supported.")]
    public Task<string> WriteAsync(
        [Description("File path relative to the workspace root.")] string path,
        [Description("Complete file content.")] string content,
        [Description("What is being changed.")] string intent,
        [Description("Why this file is the correct target.")] string whyThisFile,
        [Description("What was inspected or observed.")] string evidence,
        [Description("Concrete result expected from the change.")] string expectedChange,
        [Description("Why a narrower or different approach was not chosen.")] string alternativesConsidered,
        [Description("Relevant risks and side effects, or none.")] string risksAndSideEffects,
        [Description("How the change will be verified.")] string verification,
        [Description("Explicitly allow replacing an existing file. Read it first; use edit_file for small changes.")] bool overwrite = false,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(whyThisFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChange);
        ArgumentException.ThrowIfNullOrWhiteSpace(alternativesConsidered);
        ArgumentException.ThrowIfNullOrWhiteSpace(risksAndSideEffects);
        ArgumentException.ThrowIfNullOrWhiteSpace(verification);
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
