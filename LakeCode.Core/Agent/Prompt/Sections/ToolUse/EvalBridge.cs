namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    /// <summary>Mechanics of the persistent eval worker and its tool bridge.</summary>
    internal const string EvalBridge = """
        Each eval call is one script in a persistent C# worker; variables survive until reset,
        cancellation, timeout, session disposal, or worker failure. Saved history does not restore
        runtime variables.
        Use Display(value) or Console.WriteLine for output. Display prints strings directly,
        objects as readable JSON and tuples as arrays; large output is explicitly truncated.
        Call session tools with await Tools.CallAsync("tool_name", new { argument = value });
        the result is a string, and tool errors throw. Use await Tools.TryCallAsync(name, arguments)
        for an Output/IsError result without throwing on tool errors (transport errors still throw).
        await Tools.ListToolsAsync() returns JSON with available tool names, descriptions and schemas.
        Bridge calls are serialized even with Task.WhenAll; this is not parallel tool execution.
        Shortcuts: await Tools.ReadFileAsync(path, startLine: 1, lineCount: 0) and
        await Tools.ListDirectoryAsync(path: ".", offset: 0). read_file returns numbered lines,
        not raw file contents. eval cannot call itself; all other bridge access follows tool registration.
        Await all work before returning; do not leave background tasks or request interactive stdin.
        Errors report their category and state status; compilation errors preserve previous state,
        runtime errors retain the executed submission but may leave partially initialized variables,
        mutations and tool side effects. Worker generation and cell number are included in script errors.
        reset starts a fresh worker before executing the supplied code. This tool has no security sandbox and code has user OS
        permissions. All project restrictions on execution and verification still apply.
        Timeout includes worker startup and tool execution, but excludes manual execution-approval waits;
        after timeout or cancellation assume
        previous runtime variables are gone. Do not automatically replay code with side effects.
        """;
}
