using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LakeCode.Core.Agent.Tools.Bash;

internal sealed class BashTool(WorkspaceFiles files, Action<BashExecution> reportOutput)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(RunAsync, name: "bash");

    [Description("Run a non-interactive bash command and return its exit code, stdout and stderr after completion. No background jobs, polling or interactive input. Output is limited to 32000 characters. Inline Python and JavaScript execution is prohibited; rewrite inline code in C# and use eval. Running script files and modules is allowed. Bash is not sandboxed and has the application's OS permissions.")]
    public Task<string> RunAsync(
        [Description("Bash command to execute. Do not start background processes or interactive programs.")] string command,
        [Description("Required: explain the action, its purpose for this task, and expected side effects (files, network, data). Shown to the user before execution.")] string description,
        [Description("Working directory relative to the workspace root.")] string workingDirectory = ".",
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (ContainsInlineInterpreterInvocation(command))
            return "Error: Inline Python and JavaScript execution through bash is prohibited. Running script files and modules is allowed. " +
                   "Rewrite the operation in C# and use eval instead. eval supports C# only. " +
                   "Do not bypass this restriction with shell wrappers or indirect process launches.";
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

    // A direct-command policy, not a shell security sandbox. Inspect command words rather
    // than all text, so echo/grep arguments mentioning Python or JS remain usable.
    private static bool ContainsInlineInterpreterInvocation(string command)
    {
        const string tokenPattern = "(?:'[^']*'|\"(?:\\\\.|[^\"\\\\])*\"|\\\\.|[^\\s;&|()<>])+|[;&|()<>]|\\n";
        var expectCommand = true;
        var redirectTarget = false;
        var wrapper = false;
        var comment = false;
        string? interpreter = null;
        var optionValue = false;
        foreach (Match match in Regex.Matches(command, tokenPattern, RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(250)))
        {
            var token = match.Value;
            if (comment && token != "\n") continue;
            if (token is ";" or "&" or "|" or "(" or ")" or "\n")
            {
                expectCommand = true;
                redirectTarget = false;
                wrapper = false;
                comment = false;
                interpreter = null;
                optionValue = false;
                continue;
            }
            if (token == "<" && interpreter is not null &&
                match.Index + 1 < command.Length && command[match.Index + 1] == '<') return true;
            if (token is "<" or ">") { redirectTarget = true; continue; }
            if (redirectTarget) { redirectTarget = false; continue; }
            if (token.StartsWith('#')) { comment = true; continue; }
            if (!expectCommand && interpreter is null) continue;
            token = Regex.Replace(token, "['\"]|\\\\(.)", "$1", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
            if (interpreter is not null)
            {
                if (optionValue) { optionValue = false; continue; }
                var python = interpreter.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                             interpreter.StartsWith("pypy", StringComparison.OrdinalIgnoreCase) ||
                             interpreter.StartsWith("ipython", StringComparison.OrdinalIgnoreCase);
                if (python && token.StartsWith('-') && !token.StartsWith("--", StringComparison.Ordinal) && token.Contains('c') ||
                    !python && (token is "eval" or "--eval" or "--print" ||
                                token.StartsWith("--eval=", StringComparison.Ordinal) ||
                                token.StartsWith("--print=", StringComparison.Ordinal) ||
                                Regex.IsMatch(token, "^-[^-]*[ep]", RegexOptions.CultureInvariant,
                                    TimeSpan.FromMilliseconds(250))))
                    return true;
                // Module and file arguments belong to the invoked program, not the interpreter.
                if (python && token is "-m" || python && token.StartsWith("-m", StringComparison.Ordinal))
                { interpreter = null; continue; }
                if (token is "-W" or "-X" or "--require" or "-r" or "--import" or "--loader")
                { optionValue = true; continue; }
                if (token.StartsWith('-') && token != "-") continue;
                if (interpreter is "bun" or "deno" && token == "run") continue;
                interpreter = null;
                continue;
            }
            if (Regex.IsMatch(token, "^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250))) continue;
            if (wrapper && token.StartsWith('-')) continue;
            var name = Path.GetFileName(token);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (Regex.IsMatch(name, "^(?:python(?:[0-9]+(?:\\.[0-9]+)*)?|pypy[0-9]*|ipython[0-9]*|node(?:js)?|bun|deno|qjs|quickjs|js)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
            {
                interpreter = name;
                expectCommand = false;
                continue;
            }
            if (name is "env" or "command" or "exec" or "sudo" or "nohup" or "time" or "timeout")
            {
                wrapper = true;
                continue;
            }
            if (name is "if" or "then" or "elif" or "else" or "do" or "!" ||
                wrapper && double.TryParse(token.TrimEnd('s', 'm', 'h', 'd'),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                continue;
            expectCommand = false;
        }
        return false;
    }

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
