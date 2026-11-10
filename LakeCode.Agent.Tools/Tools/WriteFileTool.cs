using LakeCode.Agent.Tools.Files;

namespace LakeCode.Agent.Tools;

/// <summary>Creates a UTF-8 text file and reports the resulting change for preview.</summary>
public sealed class WriteFileTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "write_file";

    public override string Description =>
        "Create a UTF-8 text file, creating parent directories if needed. " +
        "Existing files are not overwritten unless overwrite is true; prefer edit_file for changes to existing files. " +
        "Provide all seven rationale fields. Maximum size is 1 MiB. Path access is decided by pre-tool policy.";

    public override AgentToolMetadata Metadata { get; } =
        new(AgentToolEffects.ReadsWorkspace | AgentToolEffects.WritesWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("path", "File path relative to the workspace root, or an absolute path.")
        .Text("content", "Complete file content.")
        .Text("intent", "What is being changed.")
        .Text("whyThisFile", "Why this file is the correct target.")
        .Text("evidence", "What was inspected or observed.")
        .Text("expectedChange", "Concrete result expected from the change.")
        .Text("alternativesConsidered", "Why a narrower or different approach was not chosen.")
        .Text("risksAndSideEffects", "Relevant risks and side effects, or none.")
        .Text("verification", "How the change will be verified.")
        .Boolean("overwrite", "Explicitly allow replacing an existing file. Read it first; use edit_file for small changes.", false));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var path = arguments.RequireString("path", allowEmpty: true);
            var content = arguments.RequireString("content", allowEmpty: true);
            RequireAll(
                arguments,
                "intent",
                "whyThisFile",
                "evidence",
                "expectedChange",
                "alternativesConsidered",
                "risksAndSideEffects",
                "verification");
            var overwrite = arguments.Boolean("overwrite", false);

            string? before = string.Empty;
            string? previewError = null;

            if (overwrite && File.Exists(files.Absolute(path)))
            {
                try
                {
                    before = await files.ReadTextAsync(path, cancellationToken, preserveBom: true)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A preview must not make an otherwise permitted overwrite fail.
                    before = null;
                    previewError = error.Message;
                }
            }

            await files.WriteTextAsync(path, content, overwrite, cancellationToken).ConfigureAwait(false);

            var display = files.Display(path);
            context.PublishProgress(new FileChange(display, before, content, previewError));

            return Success($"Written: {display}");
        });
}
