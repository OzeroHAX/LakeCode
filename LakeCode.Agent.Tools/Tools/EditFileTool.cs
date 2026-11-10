using LakeCode.Agent.Tools.Files;

namespace LakeCode.Agent.Tools;

/// <summary>Replaces exactly one occurrence of oldText, leaving the file untouched otherwise.</summary>
public sealed class EditFileTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "edit_file";

    public override string Description =>
        "Edit a UTF-8 text file by replacing exactly one occurrence of oldText with newText. " +
        "The match is case-sensitive and includes whitespace and line endings. No match or multiple matches " +
        "leave the file unchanged. Read the file first and include enough context for a unique match. " +
        "Provide all seven rationale fields. Maximum size is 1 MiB. Path access is decided by pre-tool policy.";

    public override AgentToolMetadata Metadata { get; } =
        new(AgentToolEffects.ReadsWorkspace | AgentToolEffects.WritesWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("path", "File path relative to the workspace root, or an absolute path.")
        .Text("oldText", "Exact nonempty text to replace; must occur once in the file.")
        .Text("newText", "Replacement text; may be empty to delete the matched fragment.")
        .Text("intent", "What is being changed.")
        .Text("whyThisFile", "Why this file is the correct target.")
        .Text("evidence", "What was inspected or observed.")
        .Text("expectedChange", "Concrete result expected from the change.")
        .Text("alternativesConsidered", "Why a narrower or different approach was not chosen.")
        .Text("risksAndSideEffects", "Relevant risks and side effects, or none.")
        .Text("verification", "How the change will be verified."));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var path = arguments.RequireString("path", allowEmpty: true);
            var oldText = arguments.RequireString("oldText");
            var newText = arguments.RequireString("newText", allowEmpty: true);
            RequireAll(
                arguments,
                "intent",
                "whyThisFile",
                "evidence",
                "expectedChange",
                "alternativesConsidered",
                "risksAndSideEffects",
                "verification");

            var content = await files.ReadTextAsync(path, cancellationToken, preserveBom: true)
                .ConfigureAwait(false);

            var start = content.IndexOf(oldText, StringComparison.Ordinal);

            if (start < 0)
            {
                throw new ArgumentException("oldText was not found. Read the file again and use its exact text.");
            }

            if (content.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException("oldText occurs more than once. Include more surrounding context.");
            }

            var updated = string.Concat(
                content.AsSpan(0, start),
                newText.AsSpan(),
                content.AsSpan(start + oldText.Length));

            await files.WriteTextAsync(path, updated, overwrite: true, cancellationToken).ConfigureAwait(false);

            var display = files.Display(path);
            context.PublishProgress(new FileChange(display, content, updated));

            return Success($"Edited: {display}");
        });
}
