using TreeSitter;

namespace LakeCode.Search;

internal static class StructureReader
{
    // Inventory of native grammars shipped in TreeSitter.DotNet 1.3.0, not the upstream README's aspirational list.
    internal static readonly IReadOnlyList<Grammar> Grammars = Array.AsReadOnly(new[]
    {
        G("agda", ".agda"), G("bash", ".sh", ".bash"), G("c", ".c", ".h"), G("cpp", ".cpp", ".hpp", ".cc", ".cxx", ".hxx"),
        G("c-sharp", ".cs"), G("css", ".css"), G("embedded-template", ".erb", ".ejs"), G("go", ".go"),
        G("haskell", ".hs"), G("html", ".html", ".htm"), G("java", ".java"), G("javascript", ".js", ".jsx", ".mjs", ".cjs"),
        G("jsdoc"), G("json", ".json"), G("julia", ".jl"), G("ocaml", ".ml"), G("ocaml-type", ".mli"),
        G("php", ".php"), G("python", ".py", ".pyi"), G("ql", ".ql", ".qll"), G("razor", ".cshtml", ".razor"),
        G("ruby", ".rb"), G("rust", ".rs"), G("scala", ".scala", ".sc"), G("tsx", ".tsx"),
        G("typescript", ".ts", ".mts", ".cts"), G("verilog", ".v", ".sv", ".svh")
    });
    private static Grammar G(string id, params string[] extensions) => new(id, Array.AsReadOnly(extensions));

    internal static FileOutline Read(string path, string text, string? languageId, Action check, int maxNodes)
    {
        var grammar = languageId is null
            ? Grammars.FirstOrDefault(g => g.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            : Grammars.FirstOrDefault(g => g.Id.Equals(languageId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown grammar '{languageId}'. See SearchEngine.Grammars.");
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
        if (grammar is null) return new(path, null, lines, false, []);
        check();
        using var language = new Language(grammar.Id);
        using var parser = new Parser(language);
        using var tree = parser.Parse(text) ?? throw new InvalidOperationException($"Parser returned no tree for {path}.");
        check();
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
        int LineAt(int index)
        {
            var found = lineStarts.BinarySearch(index);
            return found >= 0 ? found + 1 : ~found;
        }
        var items = new List<StructureItem>();
        var stack = new Stack<(Node Node, int Depth)>();
        stack.Push((tree.RootNode, -1));
        var visited = 0;
        while (stack.TryPop(out var current))
        {
            check();
            if (++visited > maxNodes) throw new SearchLimitException($"Syntax node budget exceeded for {path}.");
            var node = current.Node;
            if (current.Depth >= 0 && !node.IsExtra && !node.IsError && node.NamedChildren.Count > 0 &&
                (current.Depth == 0 || IsUnit(node.Type)))
            {
                var start = LineAt(node.StartIndex);
                var endIndex = Math.Max(node.StartIndex, node.EndIndex - 1);
                var end = LineAt(endIndex);
                var name = node.Fields.FirstOrDefault(f => f.Key is "name" or "key" or "declarator").Value;
                var label = name is null ? SliceLabel(text, node.StartIndex, node.EndIndex) : SliceLabel(text, name.StartIndex, name.EndIndex);
                items.Add(new(node.Type, label, start, end, current.Depth));
            }
            for (var i = node.NamedChildren.Count - 1; i >= 0; i--)
                stack.Push((node.NamedChildren[i], current.Depth + 1));
        }
        return new(path, grammar.Id, lines, tree.RootNode.HasError, items);
    }

    private static bool IsUnit(string type) => type.Contains("declaration", StringComparison.Ordinal) ||
        type.Contains("definition", StringComparison.Ordinal) || type.EndsWith("_item", StringComparison.Ordinal) ||
        type is "class" or "method" or "function" or "module" or "singleton_method" or "pair" or "element" or "rule_set";
    private static string SliceLabel(string text, int start, int end)
    {
        var length = Math.Min(160, end - start);
        var label = text.Substring(start, length);
        var newline = label.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? label : label[..newline]).Trim();
    }
}
