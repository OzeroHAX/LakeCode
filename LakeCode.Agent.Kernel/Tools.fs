namespace LakeCode.Agent.Kernel

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type ToolDefinition =
    { Name: string
      Description: string
      Parameters: JsonElement option }

[<RequireQualifiedAccess>]
type ToolProgress =
    | Text of string
    | Data of JsonElement

/// Executes exactly one attempt. Decisions, retries and history belong to the kernel.
type Tool =
    { Definition: ToolDefinition
      Execute: ToolCall -> (ToolProgress -> unit) -> CancellationToken -> Task<ToolResult> }

[<RequireQualifiedAccess>]
module Tool =
    let internal capture tool =
        if String.IsNullOrWhiteSpace tool.Definition.Name then
            invalidArg (nameof tool) "A tool name is required."

        let definition =
            { tool.Definition with
                Parameters = tool.Definition.Parameters |> Option.map (fun schema -> schema.Clone()) }

        { tool with Definition = definition }

    let internal captureProgress = function
        | ToolProgress.Text text -> ToolProgress.Text text
        | ToolProgress.Data data -> ToolProgress.Data(data.Clone())
