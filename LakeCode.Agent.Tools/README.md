# LakeCode.Agent.Tools

Capability layer built on `LakeCode.Agent`. A tool performs one operation and reports its own outcome;
it never decides whether the call is allowed.

## Dependency direction

```
LakeCode.Agent          contracts, runner, hooks, events
        ↑
LakeCode.Agent.Tools    capabilities: files, search, bash, ask, web, eval
        ↑
orchestration layer     composition, modes, approvals, session services
```

The project references only `LakeCode.Agent`, `LakeCode.Eval.Contracts`, `LakeCode.Search` and
`StbImageSharp` and `StbImageWriteSharp`. It must not reference the legacy `LakeCode.Core` project or any UI project.

## Responsibilities

| Concern | Where it lives |
|---|---|
| Executing one operation | tool implementation |
| Declared side effects | `AgentTool.Metadata` (`AgentToolMetadata`) |
| Allow / deny / stop | `BeforeToolExecution` hook |
| Approval prompt | hook composition |
| Which tools a run gets | orchestration layer |
| Rendering, previews, prompts | host / CLI |
| Event stream and history | `LakeCode.Agent` |

## Tools

`read_file`, `list_directory`, `search_text`, `find_files`, `outline_file`, `trace_code`,
`find_duplicate_units`, `write_file`, `edit_file`, `bash`, `ask_tool`, `web_search`, `read_web`,
`eval` (opt-in, owns a worker process).

History projection, image generation and delegation tools are intentionally absent: they depend on
session-scoped services that belong to the orchestration layer, not to capabilities.

## Policy through hooks

`ToolPolicyHook.Create(catalog, mode, confirmations)` builds a pre-tool hook that reads declared effects
instead of tool names. A tool that declares no effects is denied unless the mode explicitly allows
uncovered tools, so a new capability is covered by declaring effects rather than by editing policy code.

Effects are `ReadsWorkspace`, `WritesWorkspace`, `ExecutesProcess`, `CallsExternalService` and
`PromptsUser`. Modes with `RequireApprovalForProcess` or `RequireApprovalForWrites` need a matching
delegate in `ToolPolicyConfirmations`; without one the call is denied rather than silently allowed.

```csharp
var set = AgentToolSet.Create(files, sessionId, ask, invokeTool, describeTools);
var hooks = new AgentHooks
{
    BeforeToolExecution = ToolPolicyHook.Create(
        set.Catalog,
        ToolPolicyMode.Interactive,
        new ToolPolicyConfirmations(Process: ConfirmAsync)),
};
var session = new AgentSession(model, set.Tools, hooks);
```

## Limits that belong to a tool

Some limits are capabilities, not permissions: the bash command filter, output budgets, file size
caps, image format checks and the public-URL restriction in `read_web`.

## Paths and hook composition

`WorkspaceFiles.Absolute` resolves relative paths against the fixed session root, but does not restrict
access, reject symlinks or prompt. Reads and writes can address any path available to the process.
Atomic writes and permission preservation remain file mechanics. `Display` reports root-relative paths
inside the root and absolute paths elsewhere, independent of CWD. Search identifiers stay within the
selected search scope; recursive search skips symlink entries.

Compose the effect hook with host-owned path decisions:

```csharp
var effects = ToolPolicyHook.Create(set.Catalog, mode, confirmations);
var paths = WorkspacePathHook.Create(files, DecidePathsAsync);
var hooks = new AgentHooks
{
    BeforeToolExecution = async (context, call, token) =>
    {
        var decision = await effects(context, call, token);
        return decision.Kind == AgentToolDecisionKind.Allow
            ? await paths(context, call, token)
            : decision;
    },
};
```

The host supplies `DecidePathsAsync(call, absolutePaths, token)` and owns grants, approvals, link/path
validation and custom tools. The helper extracts explicit `path`, `workingDirectory`, `output_path` and
`input_paths` arguments. Defaults/omitted scopes and paths embedded in commands or scripts require host
handling or the optional extractor. Empty lists still reach the policy. This is not a sandbox and cannot
eliminate races between checks and filesystem use. Eval bridge calls must enter the same policy pipeline.

## Tool images

`AgentToolResult.Images` and `ToolResultMessage.Images` carry prepared bytes, never base64 text in the
text result. `read_file` checks the PNG/JPEG header before decoding, fits to 2048 pixels per side and
2500 32-pixel patches, then tries PNG and JPEG quality 85 within 1 MiB. Alpha is composited over white
for JPEG. Three encoding sizes are attempted; failing the byte budget is an error, not an oversized
fallback. Original bytes are retained only after encoding if no resize was needed and the original is
no larger. Source files are not modified; the original user-attachment pipeline is unaffected.

Set `supportsImageInput` when composing the tool set for the accepted model; false returns a path notice.
Providers emit all outputs of a tool batch before a user message containing the images. History keeps
images on the logical tool result for rewind/replay. The host owns aggregate history/persistence budgets;
the per-image normalization limit does not cap a long conversation.

