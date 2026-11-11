namespace LakeCode.Agent.Kernel

open System
open System.Collections.Immutable
open System.Text.Json

/// Original image bytes. Adapters validate decoding, size limits and model support.
type Image =
    { MediaType: string
      Data: ImmutableArray<byte>
      Width: int
      Height: int
      SourcePath: string option }

type ToolCall =
    { Id: CallId
      Name: string
      Arguments: JsonElement }

[<RequireQualifiedAccess>]
module ToolCall =
    /// Copies JSON so the call does not borrow its producer's JsonDocument.
    let capture call =
        if String.IsNullOrWhiteSpace(CallId.value call.Id) then
            Error "A tool call id is required."
        elif String.IsNullOrWhiteSpace call.Name then
            Error "A tool name is required."
        elif call.Arguments.ValueKind <> JsonValueKind.Object then
            Error "Tool arguments must be a JSON object."
        else
            Ok { call with Arguments = call.Arguments.Clone() }

type ToolOutput =
    { Text: string
      Images: Image list }

[<RequireQualifiedAccess>]
type ToolResult =
    | Success of ToolOutput
    | Failure of ToolOutput
    | Skipped of reason: string
    | Interrupted of reason: string

[<RequireQualifiedAccess>]
module ToolResult =
    let success text = ToolResult.Success { Text = text; Images = [] }
    let failure text = ToolResult.Failure { Text = text; Images = [] }

    let text = function
        | ToolResult.Success output | ToolResult.Failure output -> output.Text
        | ToolResult.Skipped reason | ToolResult.Interrupted reason -> reason

    let images = function
        | ToolResult.Success output | ToolResult.Failure output -> output.Images
        | ToolResult.Skipped _ | ToolResult.Interrupted _ -> []

    let isError = function
        | ToolResult.Success _ -> false
        | ToolResult.Failure _ | ToolResult.Skipped _ | ToolResult.Interrupted _ -> true

[<RequireQualifiedAccess>]
type ReplyStatus =
    | Complete
    | Interrupted

type AssistantReply =
    { Text: string
      Calls: ToolCall list
      Status: ReplyStatus }

/// Reasoning is an observation, not provider-specific persisted message data.
[<RequireQualifiedAccess>]
type Message =
    | System of id: string * text: string
    | User of id: string * text: string
    | Assistant of id: string * reply: AssistantReply
    | Tool of id: string * call: ToolCall * result: ToolResult

[<RequireQualifiedAccess>]
module Message =
    let id = function
        | Message.System(id, _) | Message.User(id, _)
        | Message.Assistant(id, _) | Message.Tool(id, _, _) -> id
