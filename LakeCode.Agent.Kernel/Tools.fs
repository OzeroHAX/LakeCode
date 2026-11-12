namespace LakeCode.Agent.Kernel

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Progress is transient and does not enter the model history.
[<RequireQualifiedAccess>]
type Progress =
    | Text of string
    | Data of JsonElement

/// The model-facing part of a tool, without its implementation.
type ToolDescription =
    { Name: string
      Description: string
      Parameters: JsonElement option }

type Tool =
    { Name: string
      Description: string
      Parameters: JsonElement option
      Execute: ToolCall -> (Progress -> unit) -> CancellationToken -> Task<ToolResult> }

[<RequireQualifiedAccess>]
module Tool =
    let description tool =
        { Name = tool.Name; Description = tool.Description; Parameters = tool.Parameters }

    let internal capture tool =
        if String.IsNullOrWhiteSpace tool.Name then
            invalidArg (nameof tool) "A tool name is required."
        { tool with Parameters = tool.Parameters |> Option.map (fun schema -> schema.Clone()) }

    let internal captureProgress = function
        | Progress.Text text -> Progress.Text text
        | Progress.Data data -> Progress.Data(data.Clone())

[<RequireQualifiedAccess>]
type ToolDecision =
    | Allow
    | Deny of reason: string
    | Stop of reason: string

/// Admission only. Preparation and cleanup are owned by the host.
type ToolPolicy = Message list -> ToolCall -> CancellationToken -> Task<ToolDecision>
