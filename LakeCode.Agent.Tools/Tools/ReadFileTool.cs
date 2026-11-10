using System.Text;
using LakeCode.Agent.Tools.Files;

namespace LakeCode.Agent.Tools;

/// <summary>Reads bounded UTF-8 text with line numbers, or returns a PNG/JPEG as image content.</summary>
public sealed class ReadFileTool(WorkspaceFiles files, bool supportsImageInput = true) : AgentTool
{
    public override string Name => "read_file";

    public override string Description =>
        "Read a UTF-8 text file with line numbers, or read a PNG/JPEG image as image content when the model supports images. " +
        "Any path is accepted; access is decided by policy before the call. Output is limited to 32000 characters; " +
        "files to 1 MiB; images are downscaled to fit vision limits. Path and symlink permissions belong to policy hooks.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("path", "File path relative to the workspace root, or an absolute path.")
        .Integer("startLine", "First line, numbered from 1; ignored for images.", minimum: 1, defaultValue: 1)
        .Integer("lineCount", "Maximum lines to read; 0 means all remaining lines; ignored for images.", minimum: 0, defaultValue: 0));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var path = arguments.RequireString("path");
        var startLine = arguments.Integer("startLine", 1);
        var lineCount = arguments.Integer("lineCount", 0);

        if (startLine < 1 || lineCount < 0)
        {
            return ValueTask.FromResult(Failure("startLine must be positive and lineCount nonnegative."));
        }

        return GuardAsync(async () => IsImagePath(path)
            ? await ReadImageAsync(files, path, supportsImageInput, cancellationToken).ConfigureAwait(false)
            : await ReadTextAsync(files, path, startLine, lineCount, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<AgentToolResult> ReadImageAsync(
        WorkspaceFiles files,
        string path,
        bool supportsImageInput,
        CancellationToken cancellationToken)
    {
        var display = files.Display(path);

        if (!supportsImageInput)
        {
            return Success($"Image file: {display}. The current model does not accept image input, so only the path is returned.");
        }

        var bytes = await files.ReadImageBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var image = ImageContent.Decode(bytes);

        var size = image.Resized
            ? $"{image.OriginalWidth}×{image.OriginalHeight} → {image.Width}×{image.Height}"
            : $"{image.Width}×{image.Height}";
        var description = $"Image file: {display} ({size}, {image.MediaType}, {image.Data.Length} bytes).";

        return new AgentToolResult(false, description, [image.ToAgentImage(files.Absolute(path))]);
    }

    private static async ValueTask<AgentToolResult> ReadTextAsync(
        WorkspaceFiles files,
        string path,
        int startLine,
        int lineCount,
        CancellationToken cancellationToken)
    {
        var text = await files
            .ReadTextAsync(path, cancellationToken)
            .ConfigureAwait(false);

        using var reader = new StringReader(text);
        var output = new StringBuilder();
        var number = 0;
        var emitted = 0;

        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;

            if (number < startLine)
            {
                continue;
            }

            if (lineCount != 0 && emitted >= lineCount)
            {
                return Success(output + "[More lines remain; request the next range.]");
            }

            var prefix = $"{number}: ";

            if (output.Length + prefix.Length + line.Length + 1 > WorkspaceFiles.MaxOutputCharacters)
            {
                var remaining = Math.Max(0, WorkspaceFiles.MaxOutputCharacters - output.Length - prefix.Length - 1);
                output.Append(prefix)
                    .Append(line.AsSpan(0, Math.Min(line.Length, remaining)))
                    .AppendLine();

                return Success(output + "[Output truncated; request a narrower range.]");
            }

            output.Append(prefix).AppendLine(line);
            emitted++;
        }

        return Success(output.Length == 0 ? "No lines in the requested range." : output.ToString());
    }

    private static bool IsImagePath(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
