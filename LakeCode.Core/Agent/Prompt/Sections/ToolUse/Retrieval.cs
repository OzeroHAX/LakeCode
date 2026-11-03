namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string Retrieval = """
        Choose retrieval tools by purpose; start with the smallest relevant scope:
        - find_files ranks likely paths/basenames for topic/name terms; it is not exhaustive text search.
        - search_text finds exact literal or per-line regex matches and includes enclosing syntax.
          Prefer literal matching. Set path and a relative-path glob (e.g. **/*.cs), not shell grep.
        - outline_file shows a known file's syntax structure and line ranges without full bodies.
          Unsupported extensions are explicit; language can select a registered Tree-sitter grammar.
        - trace_code ranks lexical/structural candidates and focused regions using subject, relation
          and optional support terms. It is heuristic, not resolved references or a true call graph.
        Follow candidate results with focused read_file ranges before making behavioral claims.
        Rankings and compact outlines are not proof of completeness or semantic relationships.
        Search budgets fail explicitly: narrow the scope rather than bypassing the tools. Reports
        of skipped files, omitted structure and truncated output must remain visible in conclusions.
        A no-match result applies only to the selected scope and files successfully examined.
        """;
}
