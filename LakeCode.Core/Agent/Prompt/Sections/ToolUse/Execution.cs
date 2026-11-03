namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    /// <summary>Command execution policy shared by Build and Plan.</summary>
    internal const string Execution = """
        Every bash and eval call MUST include description: explain the action, why it is needed,
        and expected side effects (artifacts, temporary files, network, data). Never use a vague
        label instead of an explanation. The UI shows this text; it is not a safety certification.
        Nested bash calls through eval also require description and their own approval in Plan.
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
