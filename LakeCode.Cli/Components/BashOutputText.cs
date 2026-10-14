using System.Text;

namespace LakeCode.Cli.Components;

/// <summary>Plain log presentation, not a terminal emulator. Raw output stays in Core.</summary>
internal static class BashOutputText
{
    public static string Normalize(string text)
    {
        var output = new StringBuilder();
        var line = new StringBuilder();
        var column = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\u001b')
            {
                if (++index >= text.Length) break;
                if (text[index] == '[')
                {
                    while (++index < text.Length && text[index] is not (>= '@' and <= '~')) { }
                }
                else if (text[index] == ']')
                {
                    while (++index < text.Length)
                    {
                        if (text[index] == '\a') break;
                        if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\')
                        {
                            index++;
                            break;
                        }
                    }
                }
                continue;
            }
            if (c == '\r') { column = 0; continue; }
            if (c == '\n')
            {
                output.Append(line).Append('\n');
                line.Clear();
                column = 0;
                continue;
            }
            if (c == '\b') { column = Math.Max(0, column - 1); continue; }
            if (c == '\t')
            {
                var spaces = 4 - column % 4;
                for (var n = 0; n < spaces; n++) Put(' ');
            }
            else if (!char.IsControl(c)) Put(c);
        }
        output.Append(line);
        return output.ToString();

        void Put(char value)
        {
            if (column < line.Length) line[column] = value;
            else line.Append(value);
            column++;
        }
    }
}
