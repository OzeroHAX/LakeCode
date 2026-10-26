using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LakeCode.Core.Attachments;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.ReadFile;

internal sealed class ReadFileTool(WorkspaceFiles files)
{
    public AIFunction AgentTool() =>
        AIFunctionFactory.Create(ReadAsync, new AIFunctionFactoryOptions
        {
            Name = "read_file",
            // Do not turn DataContent into JSON/base64 text. It must remain a model content item.
            MarshalResult = (result, _, _) => new ValueTask<object?>(MarshalResult(result))
        });

    [Description("Read a UTF-8 text file with line numbers, or read a PNG/JPEG image as image content when the model supports images. External paths require user confirmation for read-only session access. Output is limited to 32000 characters; files to 1 MiB; images to 8 MiB. Symbolic links are not supported.")]
    public async Task<string> ReadAsync(
        [Description("File path relative to the workspace root, or an absolute path requiring user confirmation outside it.")] string path,
        [Description("First line, numbered from 1; ignored for images.")] int startLine = 1,
        [Description("Maximum lines to read; 0 means all remaining lines; ignored for images.")] int lineCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (startLine < 1 || lineCount < 0) return "Error: startLine must be positive and lineCount nonnegative.";
        if (IsImagePath(path))
        {
            try
            {
                var bytes = await files.ReadImageBytesAsync(path, cancellationToken);
                var image = ImageAttachments.Decode(bytes, Path.GetFileName(path));
                return ImageResult.Prefix + JsonSerializer.Serialize(new ImageResult(
                    files.DisplayPath(files.ResolveRead(path)), image.Width, image.Height, image.MediaType,
                    Convert.ToBase64String(image.Data.Span)));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return $"Error: {error.Message}";
            }
        }

        try
        {
            var text = await files.ReadTextAsync(path, cancellationToken, allowExternalRead: true);
            using var reader = new StringReader(text);
            var output = new StringBuilder();
            var number = 0;
            var emitted = 0;
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                number++;
                if (number < startLine) continue;
                if (lineCount != 0 && emitted >= lineCount) return output + "[More lines remain; request the next range.]";
                var prefix = $"{number}: ";
                if (output.Length + prefix.Length + line.Length + 1 > WorkspaceFiles.MaxOutputCharacters)
                {
                    var remaining = Math.Max(0, WorkspaceFiles.MaxOutputCharacters - output.Length - prefix.Length - 1);
                    output.Append(prefix).Append(line.AsSpan(0, Math.Min(line.Length, remaining))).AppendLine();
                    return output + "[Output truncated; request a narrower range.]";
                }
                output.Append(prefix).AppendLine(line);
                emitted++;
            }
            return output.Length == 0 ? "No lines in the requested range." : output.ToString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Error: {error.Message}";
        }
    }

    private static object? MarshalResult(object? result)
    {
        if (result is not string text || !text.StartsWith(ImageResult.Prefix, StringComparison.Ordinal)) return result;
        var image = JsonSerializer.Deserialize<ImageResult>(text[ImageResult.Prefix.Length..]);
        if (image is null) return result;
        var data = Convert.FromBase64String(image.Data);
        return new AIContent[]
        {
            new TextContent($"Image file: {image.Path} ({image.Width}×{image.Height}, {image.MediaType})."),
            new DataContent(data, image.MediaType)
        };
    }

    private sealed record ImageResult(string Path, int Width, int Height, string MediaType, string Data)
    {
        public const string Prefix = "__lakecode_image__";
    }

    private static bool IsImagePath(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
