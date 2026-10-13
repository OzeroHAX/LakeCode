using SharpConsoleUI.Helpers;

namespace LakeCode.Cli.Components;

// Adapted from SharpConsoleUI v2.6.18 Helpers/TextWrapping.cs (internal API).
// Keep the break rules identical to PromptControl's standard rendering.
// https://github.com/nickprotop/ConsoleEx/blob/v2.6.18/SharpConsoleUI/Helpers/TextWrapping.cs
// MIT License
// Copyright (c) 2024-2026 Nikolaos Protopapas
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
internal static class PromptTextWrapping
{
    internal readonly record struct Segment(int Offset, int Length);

    public static void WrapWords(List<Segment> into, string line, int width)
    {
        if (line.Length == 0) { into.Add(new Segment(0, 0)); return; }
        var pos = 0;
        while (pos < line.Length)
        {
            var (fitEnd, _) = UnicodeWidth.TakeColumns(line, pos, Math.Max(1, width));
            if (fitEnd >= line.Length) { into.Add(new Segment(pos, line.Length - pos)); break; }
            var breakAt = -1;
            for (var j = fitEnd - 1; j > pos; j--)
                if (line[j] == ' ') { breakAt = j; break; }
            var length = breakAt > pos ? breakAt - pos + 1 : fitEnd - pos;
            into.Add(new Segment(pos, length));
            pos += length;
        }
    }
}
