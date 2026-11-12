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

/// Text and images: one content shape for user, assistant and tool messages.
type Content =
    { Text: string
      Images: Image list }

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

[<RequireQualifiedAccess>]
type ToolResult =
    | Success of Content
    | Failure of Content
    | Skipped of reason: string
    | Interrupted of reason: string

[<RequireQualifiedAccess>]
module ToolResult =
    let success text = ToolResult.Success { Text = text; Images = [] }
    let failure text = ToolResult.Failure { Text = text; Images = [] }

[<RequireQualifiedAccess>]
type ReplyStatus =
    | Complete
    | Interrupted

type AssistantReply =
    { Content: Content
      Calls: ToolCall list
      Status: ReplyStatus }

/// Reasoning is an observation, not provider-specific persisted message data.
[<RequireQualifiedAccess>]
type Message =
    | System of id: string * text: string
    | User of id: string * content: Content
    | Assistant of id: string * reply: AssistantReply
    | Tool of id: string * call: ToolCall * result: ToolResult

[<RequireQualifiedAccess>]
module Message =
    let id = function
        | Message.System(id, _) | Message.User(id, _)
        | Message.Assistant(id, _) | Message.Tool(id, _, _) -> id

[<RequireQualifiedAccess>]
module Content =
    let text value = { Text = value; Images = [] }

    let internal validate content =
        if isNull content.Text then Error "Message text cannot be null."
        elif content.Images |> List.exists (fun image ->
            String.IsNullOrWhiteSpace image.MediaType || image.Data.IsDefaultOrEmpty || image.Width <= 0 || image.Height <= 0) then
            Error "Images need original bytes, a media type and positive dimensions."
        else Ok content
