# Working on LakeCode

## Discovery and verification

- Do not use an indexer or create or update a project index.
- Use LSP for symbols and diagnostics when available.
- Do not create or run tests.
- Run the application only when the user explicitly requests it; otherwise, the user checks the application manually.
- Do not run formatters or other command-based checks except builds.
- After any edit, before the final response, run from the repository root:
  `dotnet build LakeCode.Cli/LakeCode.Cli.csproj`.
- Fix build errors introduced by the edit and rebuild.
- Address delayed LSP diagnostics without launching the application.

## Architecture

- The agent uses Microsoft Agent Framework (MAF); entry point: `LakeCode.Core/Agent/AgentLoop.cs`.
- `LakeCode.Core` has no CLI or SharpConsoleUI dependency. CLI references Core via ProjectReference.
- `ConversationService` owns the agent, queue, cancellation, sessions, rewind, and persistence; only this service consumes the main agent stream.
- Core contracts are separate: `ConversationCommand` commands, ordered `ConversationEvent` events, and `ConversationState` snapshots.
- Notification subscribers only enqueue view updates, never block or throw. `ChatController` dispatches updates to the UI thread.
- Compose UI components in `LakeCode.Cli/Program.cs`; keep clipboard, PixelBuffer, and previews in CLI, original attachment data in Core.
- Connection profiles and transport creation live in `LakeCode.Core/Providers`; shared logic must not depend on a single provider, endpoint, or API mode.
- `LakeCode.Eval.Contracts` and `LakeCode.Eval.Worker` support C# eval.

## Implementation invariants

- Each `AgentLoop` holds one session; run main requests sequentially and fully consume each response stream.
- Connection, model, reasoning, and message data are captured at submission; later settings changes do not affect accepted requests.
- Cancellation and rewind do not undo completed side effects.
- `SystemPromptBuilder` reads global `~/.agents/AGENTS.md`, then project `AGENTS.md`, once at session creation, before the fixed environment context. Skip missing or empty files; propagate other read errors.
- Provider settings in `~/.lakecode/providers.json` store unencrypted keys; preserve access restrictions. Never include keys in persisted sessions or message queues.
- Shared file restrictions live in `LakeCode.Core/Agent/tools/WorkspaceFiles.cs`.
- File writes must remain atomic, preserve applicable permissions, and leave no partial result if cancelled before commit.
- Send original image bytes, not previews; validate format, limits, and model support. Attachment preparation failures must preserve the draft.

## UI and documentation

- Use SharpConsoleUI and suitable standard controls instead of custom rendering; keep component styling consistent.
- Preserve text and tool-call order; display reasoning, errors, and cancellation separately. Do not display private provider-specific data.
- Do not block the UI with file reads, clipboard access, or agent streaming. Manual user actions take precedence over automatic behavior.
- Contracts, providers, persistence, and delegation: `LakeCode.Core/README.md`; UI implementation: `LakeCode.Cli/Components` and `ChatController.cs`.
- For MAF changes, read `.agents/skills/microsoft-agent-framework/SKILL.md` and its `references/dotnet.md`; use the `microsoft-docs` skill for official documentation.
- Do not duplicate general system-prompt rules, dependency versions, model catalogs, numeric limits, or UI change history here.
