using System.Text;
using System.Text.RegularExpressions;

namespace LakeCode.Core.Agent.Tools.Bash;

/// <summary>Conservative direct-command filter, not a shell parser or OS sandbox.</summary>
internal static class BashCommandPolicy
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "egrep", "fgrep", "rgrep", "zgrep", "zegrep", "zfgrep", "bzgrep", "xzgrep", "rg", "ripgrep", "ugrep", "ag", "ack",
        "find", "fd", "fdfind", "locate", "mlocate", "plocate", "ls", "dir", "vdir", "tree",
        "awk", "gawk", "mawk", "nawk", "sed", "gsed", "cat", "tac", "head", "tail", "less", "more", "bat", "batcat",
        "strings", "od", "xxd", "hexdump", "dd", "cut", "paste", "join", "comm", "sort", "uniq", "tr", "wc", "nl", "fold", "fmt", "tee",
        "sh", "bash", "dash", "zsh", "ksh", "fish", "csh", "tcsh", "ash", "pwsh", "powershell",
        "node", "nodejs", "bun", "deno", "qjs", "quickjs", "js", "perl", "ruby", "irb", "php", "lua", "luajit",
        "r", "rscript", "julia", "csi", "csharp", "fsi", "fsharpi", "tclsh", "wish", "gjs", "scala", "groovy",
        "eval", "exec", "source", ".", "command", "builtin", "env", "sudo", "doas", "xargs", "parallel", "busybox", "toybox",
        "nohup", "timeout", "time", "watch", "npx", "read", "readarray", "mapfile", "alias", "unalias", "enable", "trap",
        "export", "declare", "typeset", "local", "set", "shopt", "let", "test", "[", "[[",
        "if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case", "esac", "function", "select", "coproc", "!"
    };
    private static readonly Regex Interpreter = new(
        @"^(?:pythonw?(?:[0-9]+(?:\.[0-9]+)*)?|pypy[0-9.]*|ipython[0-9.]*|perl[0-9.]*|ruby[0-9.]*|php[0-9.]*|lua[0-9.]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly HashSet<string> Scripts = new(StringComparer.OrdinalIgnoreCase)
    { ".sh", ".bash", ".zsh", ".fish", ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".ps1", ".pl", ".rb", ".php", ".lua", ".csx", ".fsx" };

    public static string? Check(string command)
    {
        if (command.Length > 32_000) return Error("command is too long");
        var words = new List<string>();
        var word = new StringBuilder();
        var started = false;
        char quote = '\0';
        string? FlushCommand()
        {
            if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
            var error = CheckWords(words);
            words.Clear();
            return error;
        }
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else word.Append(c);
                continue;
            }
            if (c is '$' or '`') return Error("shell expansions and substitutions are not allowed");
            if (quote == '"')
            {
                if (c == '"') { quote = '\0'; continue; }
                if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\') c = command[++i];
                word.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (c == '\\')
            {
                if (++i >= command.Length) return Error("unfinished escape");
                if (command[i] != '\n') { word.Append(command[i]); started = true; }
                continue;
            }
            if (c == '#' && !started)
            {
                while (i < command.Length && command[i] != '\n') i++;
                if (i == command.Length) break;
                c = '\n';
            }
            if (c is '*' or '?' or '[' or ']' || c == '~' && !started)
                return Error("unquoted pathname expansions are not allowed; pass explicit paths");
            if (c is '<' or '>' or '(' or ')' or '{' or '}') return Error("redirections, process substitutions and shell compound constructs are not allowed");
            if (c is ';' or '|' or '&' or '\n')
            {
                if (c == '&' && (i + 1 >= command.Length || command[i + 1] != '&')) return Error("background execution is not allowed");
                if (c is '|' or '&' && i + 1 < command.Length && command[i + 1] == c) i++;
                var error = FlushCommand();
                if (error is not null) return error;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
                continue;
            }
            word.Append(c);
            started = true;
        }
        if (quote != '\0') return Error("unclosed quote");
        return FlushCommand();
    }

    private static string? CheckWords(List<string> words)
    {
        if (words.Count == 0) return null;
        var executable = words[0];
        var name = Path.GetFileName(executable.Replace('\\', '/'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.ToLowerInvariant();
        if (name.Length == 0 || name.Contains('=') || name.IndexOfAny(['*', '?', '[', ']', '~']) >= 0)
            return Error("dynamic command names and environment assignments are not allowed");
        if (Forbidden.Contains(name) || Interpreter.IsMatch(name) || Scripts.Contains(Path.GetExtension(executable)))
            return Error($"'{name}' is prohibited");
        if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 1; i < words.Count; i++)
            {
                var arg = words[i];
                if (arg == "-c" || arg.StartsWith("-c", StringComparison.Ordinal) || arg.StartsWith("--config-env", StringComparison.Ordinal) ||
                    arg.StartsWith("--exec-path", StringComparison.Ordinal)) return Error("git command/configuration wrappers are not allowed");
                if (arg is "-C" or "--git-dir" or "--work-tree" or "--namespace") { i++; continue; }
                if (arg.StartsWith('-')) continue;
                if (arg is "grep" or "show" or "cat-file" or "ls-files" or "ls-tree") return Error($"'git {arg}' must use the retrieval tools instead");
                break;
            }
        }
        if (name is "npm" or "pnpm" or "yarn" or "dotnet")
        {
            var subcommand = words.Skip(1).FirstOrDefault(w => !w.StartsWith('-'));
            if (subcommand is "exec" or "dlx" or "script" or "fsi") return Error("interpreter/package execution wrappers are not allowed");
        }
        return null;
    }

    private static string Error(string reason) => $"Error: Bash command policy: {reason}. Use search_text, find_files, outline_file, trace_code, read_file or list_directory for retrieval. Do not bypass through wrappers, scripts or eval. This filter is not an OS sandbox.";
}
