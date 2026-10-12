using System.ComponentModel;
using System.Text;

namespace LakeCode.Cli.Agent.Tools.ReadFile;

internal sealed class ReadFileTool(WorkspaceFiles files)
{
    [Description("Read a UTF-8 text file with line numbers. External paths require user confirmation for read-only session access. Output is limited to 32000 characters; files to 1 MiB. Symbolic links are not supported.")]
    public Task<string> ReadAsync(
        [Description("File path relative to the workspace root, or an absolute path requiring confirmation outside it.")] string path,
        [Description("First line, numbered from 1.")] int startLine = 1,
        [Description("Maximum lines to read; 0 means all remaining lines.")] int lineCount = 0,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        if (startLine < 1 || lineCount < 0) throw new ArgumentException("startLine must be positive and lineCount nonnegative.");
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
    });
}
