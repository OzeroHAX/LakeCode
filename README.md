# LakeCode

A terminal coding agent built with C# and .NET 10. Powered by **Microsoft Agent Framework**, with a terminal interface built using **[SharpConsoleUI](https://github.com/nickprotop/ConsoleEx)**.

The project is under active development and is not ready for regular use yet. I'm building it for fun, through vibe coding, but with a specific goal: to make my own everyday work tool, not just release another coding agent.

## Suggestions and contributions

Suggestions, feedback, and PRs are welcome — I'll consider them all. That said, I'm building LakeCode primarily around my own tasks and workflow. I can't promise to add every proposed feature: what matters most to me is having a tool I enjoy using every day.

## Installation

Supports Linux with glibc on x64 and ARM64. Requires Bash, the .NET 10 SDK, and access to NuGet.

From the repository root:

```bash
bash scripts/install.sh
```

The command is installed in `~/.local/bin`, and the .NET Runtime is bundled with the application. If that directory isn't already in your `PATH`, add this to `~/.bashrc` and open a new terminal:

```bash
export PATH="$HOME/.local/bin:$PATH"
```

Run LakeCode from the project directory you want to work in:

```bash
cd /path/to/your/project
lakecode
```

Configure your provider using the `/providers` command in the interface.

To update, close LakeCode, update the source, and run the install script again. Your settings and sessions are preserved.

## Thanks

Special thanks to the author of **[SharpConsoleUI](https://github.com/nickprotop/ConsoleEx)** for continuing to develop the project and merging PRs so quickly. The library lets me focus on my tool instead of building a terminal interface from scratch.

## License

LakeCode is licensed under the [Apache License 2.0](LICENSE). Third-party components are distributed under their own licenses.
