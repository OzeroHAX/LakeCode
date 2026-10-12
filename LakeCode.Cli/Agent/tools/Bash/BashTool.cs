using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LakeCode.Cli.Agent.Tools.Bash;

internal sealed class BashTool(WorkspaceFiles files)
{
    [Description("Run a non-interactive bash command and return its exit code, stdout and stderr after completion. No background jobs, polling or interactive input. Output is limited to 32000 characters. Bash is not sandboxed and has the application's OS permissions.")]
    public Task<string> RunAsync(
        [Description("Bash command to execute. Do not start background processes or interactive programs.")] string command,
        [Description("Working directory relative to the workspace root.")] string workingDirectory = ".",
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
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

        var stdout = ReadOutputAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadOutputAsync(process.StandardError, cancellationToken);
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

        var status = process.ExitCode == 0 ? "Exit code: 0" : $"Error: Exit code: {process.ExitCode}";
        return $"{status}\n\nstdout:\n{await stdout}\n\nstderr:\n{await stderr}";
    });

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int limit = WorkspaceFiles.MaxOutputCharacters / 2;
        var output = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            var retained = Math.Min(count, limit - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained < count;
            // Keep draining even at the limit so a full pipe cannot block the command.
        }
        if (truncated) output.Append("\n[Output truncated.]");
        return output.ToString();
    }
}
