using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace LakeCode.Cli.Agent.Tools.Grep;

internal sealed class GrepTool(WorkspaceFiles files)
{
    [Description("Search UTF-8 text files in the workspace. Directories are searched recursively, excluding .git, bin, obj and symbolic links. Reports skipped unreadable, binary or oversized files. Results include paths and line numbers.")]
    public Task<string> SearchAsync(
        [Description("Text or regular expression to search for.")] string pattern,
        [Description("Workspace-relative file or directory path.")] string path = ".",
        [Description("Filename wildcard, for example *.cs.")] string filePattern = "*",
        [Description("Interpret pattern as a regular expression instead of literal text.")] bool useRegex = false,
        [Description("Match without regard to case.")] bool ignoreCase = false,
        [Description("Maximum matching lines, from 1 to 500.")] int maxResults = 100,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePattern);
        if (maxResults is < 1 or > 500) throw new ArgumentException("maxResults must be between 1 and 500.");
        var regex = useRegex ? new Regex(pattern, RegexOptions.CultureInvariant |
            (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(250)) : null;
        var target = files.Resolve(path);
        var output = new StringBuilder();
        var results = 0;
        var skipped = 0;
        foreach (var file in EnumerateFiles(target, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(filePattern, Path.GetFileName(file), ignoreCase)) continue;
            string text;
            try { text = await files.ReadTextAsync(file, cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                skipped++;
                continue;
            }
            using var reader = new StringReader(text);
            var number = 0;
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                number++;
                if (!(regex?.IsMatch(line) ?? line.Contains(pattern,
                        ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
                var prefix = $"{files.Relative(file)}:{number}: ";
                if (results >= maxResults || output.Length + prefix.Length + line.Length + 1 > WorkspaceFiles.MaxOutputCharacters)
                {
                    if (results < maxResults)
                    {
                        var remaining = Math.Max(0, WorkspaceFiles.MaxOutputCharacters - output.Length - prefix.Length - 1);
                        output.Append(prefix).Append(line.AsSpan(0, Math.Min(remaining, line.Length))).AppendLine();
                    }
                    return output + $"[Output truncated; narrow the path or pattern. Skipped files: {skipped}.]";
                }
                output.Append(prefix).AppendLine(line);
                results++;
            }
        }
        if (results == 0) output.AppendLine("No matches.");
        if (skipped != 0) output.AppendLine($"[Skipped {skipped} unreadable, binary or oversized files; search is incomplete.]");
        return output.ToString();
    });

    private IEnumerable<string> EnumerateFiles(string target, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target))
        {
            yield return target;
            yield break;
        }
        var directories = new Stack<string>();
        directories.Push(target);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(files.Resolve(directory)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!WorkspaceFiles.SkipDirectory(entry)) directories.Push(entry);
                }
                else yield return entry;
            }
        }
    }
}
