using System.Text;
using LakeCode.Core.Agent;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal static class FileDiffPreview
{
    private const int ContextLines = 3;
    private const int MaxCharacters = 32_000;

    public static string Format(FileChange change)
    {
        if (change.Before is null)
            return MarkupParser.Escape($"{change.Path}\nDiff unavailable: {change.PreviewError}");
        if (change.Before == change.After)
            return MarkupParser.Escape($"{change.Path}\nNo content changes.");

        var before = Lines(change.Before);
        var after = Lines(change.After);
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && before[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < before.Count - prefix && suffix < after.Count - prefix &&
               before[before.Count - suffix - 1] == after[after.Count - suffix - 1])
            suffix++;

        var removed = before.Count - prefix - suffix;
        var added = after.Count - prefix - suffix;
        var start = Math.Max(0, prefix - ContextLines);
        var tail = Math.Min(ContextLines, suffix);
        var width = Math.Max(before.Count, after.Count).ToString().Length;
        var output = new StringBuilder();
        output.AppendLine($"[bold]{MarkupParser.Escape(change.Path)}[/]  [green]+{added}[/] [red]−{removed}[/]");
        output.AppendLine($"[grey]@@ -{(before.Count == 0 ? 0 : start + 1)},{prefix - start + removed + tail} +{(after.Count == 0 ? 0 : start + 1)},{prefix - start + added + tail} @@[/]");
        var truncated = false;

        void Append(string text, int? oldLine, int? newLine, char sign, string? color)
        {
            if (truncated) return;
            var oldNumber = oldLine?.ToString().PadLeft(width) ?? new string(' ', width);
            var newNumber = newLine?.ToString().PadLeft(width) ?? new string(' ', width);
            var line = $"{oldNumber} {newNumber} {sign} {text.TrimEnd('\r', '\n')}";
            var escaped = MarkupParser.Escape(line);
            if (output.Length + escaped.Length + 100 > MaxCharacters)
            {
                output.AppendLine("[grey]… Diff preview truncated; the file change is complete.[/]");
                truncated = true;
                return;
            }
            output.AppendLine(color is null ? escaped : $"[{color}]{escaped}[/]");
            if (!text.EndsWith('\n'))
                output.AppendLine("[grey]\\ No newline at end of file[/]");
        }

        for (var i = start; i < prefix && !truncated; i++)
            Append(before[i], i + 1, i + 1, ' ', "grey");
        for (var i = prefix; i < before.Count - suffix && !truncated; i++)
            Append(before[i], i + 1, null, '-', "red");
        for (var i = prefix; i < after.Count - suffix && !truncated; i++)
            Append(after[i], null, i + 1, '+', "green");
        for (var i = 0; i < tail && !truncated; i++)
            Append(after[after.Count - suffix + i], before.Count - suffix + i + 1,
                after.Count - suffix + i + 1, ' ', "grey");
        return output.ToString();
    }

    // Keep terminators in comparisons so newline-only edits are not reported as unchanged.
    private static List<string> Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }
}
