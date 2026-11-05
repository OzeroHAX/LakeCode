namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    /// <summary>Command execution policy shared by Build and Plan.</summary>
    internal const string Execution = """
        Every bash call MUST include these six separate fields:
        action, purpose, whyNeededNow, alternativesConsidered, expectedResult, and sideEffects.
        Every eval call MUST include these same six separate fields:
        action, purpose, whyNeededNow, alternativesConsidered, expectedResult, and sideEffects.
        Explain why the call is needed for the current task and why a safer available alternative
        is insufficient. Do not claim that no alternatives exist without identifying the relevant
        alternatives. Prefer the narrowest read-only operation that can answer the question.
        These values must be specific to the call, not generic justification. The UI shows this
        text; it is not a safety certification.
        Nested bash calls through eval also require the same separate bash fields and their own approval in Plan.
        Use eval for ad-hoc C# execution instead of shelling out to compile temporary programs.
        Repository discovery, text search, structure inspection and source reading MUST use
        find_files, search_text, outline_file, trace_code, list_directory and read_file.
        Never replace these operations with bash commands or direct filesystem/process APIs in eval.
        Bash rejects grep/rg and equivalents, find/fd/ls/tree, awk/sed, cat/head/tail and similar
        readers, Python/JavaScript and other interpreters (including script files and modules),
        nested shells and typical command wrappers. Substitutions, expansions, redirections,
        compound shell constructs and background execution are not accepted. Simple command
        chains such as dotnet build ... && dotnet build ... remain available.
        Do not bypass these rules with aliases, renamed executables, generated scripts, build/npm
        scripts, MCP tools, shell wrappers or indirect launches from eval. Ordinary project
        build/package scripts may run only for their legitimate task, subject to project rules;
        this does not authorize using them for forbidden retrieval or interpreter operations.
        eval supports C# only; use it for permitted computation or calling the registered tools.
        Bash filtering is a direct-command policy, not an OS sandbox; that limitation is not permission.
        """;
}
