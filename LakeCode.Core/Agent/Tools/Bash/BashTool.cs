using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LakeCode.Core.Agent.Tools.Bash;

internal sealed class BashTool(WorkspaceFiles files, Action<BashExecution> reportOutput)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(RunAsync, name: "bash");

    [Description("Run a permitted non-interactive command and return exit code/stdout/stderr. No background jobs or interactive input. Search/file-reading commands (grep/rg/find/awk/sed/cat and equivalents), Python/JS/other interpreters, nested shells, wrappers, expansions and redirections are blocked. Use retrieval tools for source investigation. Build/package scripts may run when otherwise permitted, but must not be used to bypass restrictions. Command filtering is NOT an OS sandbox. Output is limited to 32000 characters.")]
    public Task<string> RunAsync(
        [Description("Command to execute. No retrieval commands, interpreters, wrappers, substitutions, redirections or background execution.")] string command,
        [Description("Required structured justification shown to the user before execution. Include: Action, Purpose, Why needed now, Alternatives considered, Expected result, and Side effects (files, network, data, processes, and persistence). Explain why safer available alternatives are insufficient and keep it specific to this call.")] string description,
        [Description("Working directory relative to the workspace root.")] string workingDirectory = ".",
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (BashCommandPolicy.Check(command) is { } policyError) return policyError;
        var directory = files.Resolve(workingDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The working directory does not exist.");
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
                CreateNoWindow = true
            }
        };
        // Do not execute startup files or imported shell functions before the checked command.
        process.StartInfo.Environment.Remove("BASH_ENV");
        process.StartInfo.Environment.Remove("ENV");
        process.StartInfo.Environment.Remove("SHELLOPTS");
        process.StartInfo.Environment.Remove("BASHOPTS");
        foreach (var key in process.StartInfo.Environment.Keys.Where(k => k.StartsWith("BASH_FUNC_", StringComparison.Ordinal)).ToArray())
            process.StartInfo.Environment.Remove(key);
        process.StartInfo.ArgumentList.Add("--noprofile");
        process.StartInfo.ArgumentList.Add("--norc");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            return $"Error: Could not start bash: {error.Message}";
        }
        process.StandardInput.Close();

        var capture = new BashOutputCapture(reportOutput);
        capture.Publish();
        using var outputUpdates = new CancellationTokenSource();
        var pump = capture.PumpAsync(outputUpdates.Token);
        var stdout = ReadOutputAsync(process.StandardOutput, capture, cancellationToken);
        var stderr = ReadOutputAsync(process.StandardError, capture, cancellationToken);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr);
        }
        catch
        {
            // Cancellation stops the command instead of leaving it running after the request.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process can exit between HasExited and Kill.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await outputUpdates.CancelAsync();
            await pump;
            capture.Publish(force: true, exitCode: process.HasExited ? process.ExitCode : null);
        }

        var status = process.ExitCode == 0 ? "Exit code: 0" : $"Error: Exit code: {process.ExitCode}";
        return $"{status}\n\nstdout:\n{await stdout}\n\nstderr:\n{await stderr}";
    });

    private static async Task<string> ReadOutputAsync(StreamReader reader, BashOutputCapture capture,
        CancellationToken cancellationToken)
    {
        const int limit = WorkspaceFiles.MaxOutputCharacters / 2;
        var output = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            capture.Append(buffer.AsSpan(0, count));
            var retained = Math.Min(count, limit - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained < count;
            // Keep draining even at the limit so a full pipe cannot block the command.
        }
        if (truncated) output.Append("\n[Output truncated.]");
        return output.ToString();
    }
}
