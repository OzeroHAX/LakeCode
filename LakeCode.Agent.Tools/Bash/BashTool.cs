using System.Diagnostics;
using System.Text;
using LakeCode.Agent.Tools.Files;

namespace LakeCode.Agent.Tools.Bash;

/// <summary>Runs one permitted non-interactive command in the workspace and returns exit code and output.</summary>
public sealed class BashTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "bash";

    public override string Description =>
        "Run a permitted non-interactive command and return exit code/stdout/stderr. No background jobs or interactive " +
        "input. Search/file-reading commands (grep/rg/find/awk/sed/cat and equivalents), interpreters, nested shells, " +
        "wrappers, expansions and redirections are blocked. Command filtering is NOT an OS sandbox. " +
        "Output is limited to 32000 characters.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ExecutesProcess);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("command", "Command to execute. No retrieval commands, interpreters, wrappers, substitutions, redirections or background execution.")
        .Text("action", "What the command does.")
        .Text("purpose", "Why the command is useful for the current task.")
        .Text("whyNeededNow", "Why this command is needed now.")
        .Text("alternativesConsidered", "Why a safer or narrower alternative was not chosen.")
        .Text("expectedResult", "Concrete result expected from the command.")
        .Text("sideEffects", "Relevant files, network, data, processes, and persistence side effects, or none.")
        .Text("workingDirectory", "Working directory relative to the workspace root.", "."));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var command = arguments.RequireString("command");
        RequireAll(
            arguments,
            "action",
            "purpose",
            "whyNeededNow",
            "alternativesConsidered",
            "expectedResult",
            "sideEffects");
        var workingDirectory = arguments.String("workingDirectory", ".");

        return GuardAsync(async () =>
        {
            if (BashCommandPolicy.Check(command) is { } policyError)
            {
                return new AgentToolResult(true, policyError);
            }

            var directory = files.Absolute(workingDirectory);

            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The working directory does not exist.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("bash")
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            // Do not execute startup files or imported shell functions before the checked command.
            process.StartInfo.Environment.Remove("BASH_ENV");
            process.StartInfo.Environment.Remove("ENV");
            process.StartInfo.Environment.Remove("SHELLOPTS");
            process.StartInfo.Environment.Remove("BASHOPTS");

            foreach (var key in process.StartInfo.Environment.Keys
                         .Where(key => key.StartsWith("BASH_FUNC_", StringComparison.Ordinal))
                         .ToArray())
            {
                process.StartInfo.Environment.Remove(key);
            }

            process.StartInfo.ArgumentList.Add("--noprofile");
            process.StartInfo.ArgumentList.Add("--norc");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(command);

            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                return Failure($"Could not start bash: {error.Message}");
            }

            process.StandardInput.Close();

            void Publish(BashExecution execution) => context.PublishProgress(execution);

            var capture = new BashOutputCapture(Publish);
            capture.Publish();
            using var outputUpdates = new CancellationTokenSource();
            var pump = capture.PumpAsync(outputUpdates.Token);
            var stdout = ReadOutputAsync(process.StandardOutput, capture, cancellationToken);
            var stderr = ReadOutputAsync(process.StandardError, capture, cancellationToken);

            try
            {
                await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation stops the command instead of leaving it running after the request.
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process can exit between HasExited and Kill.
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await outputUpdates.CancelAsync().ConfigureAwait(false);
                await pump.ConfigureAwait(false);
                capture.Publish(force: true, exitCode: process.HasExited ? process.ExitCode : null);
            }

            var status = process.ExitCode == 0 ? "Exit code: 0" : $"Error: Exit code: {process.ExitCode}";

            return process.ExitCode == 0
                ? Success($"{status}\n\nstdout:\n{await stdout}\n\nstderr:\n{await stderr}")
                : new AgentToolResult(true, $"{status}\n\nstdout:\n{await stdout}\n\nstderr:\n{await stderr}");
        });
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        BashOutputCapture capture,
        CancellationToken cancellationToken)
    {
        const int limit = WorkspaceFiles.MaxOutputCharacters / 2;
        var output = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;

        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            capture.Append(buffer.AsSpan(0, count));
            var retained = Math.Min(count, limit - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained < count;
            // Keep draining even at the limit so a full pipe cannot block the command.
        }

        if (truncated)
        {
            output.Append("\n[Output truncated.]");
        }

        return output.ToString();
    }
}
