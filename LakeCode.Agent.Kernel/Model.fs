namespace LakeCode.Agent.Kernel

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type ModelRequest =
    { Messages: Message list
      Tools: ToolDefinition list }

type ModelReply =
    { Text: string
      Calls: ToolCall list }

[<RequireQualifiedAccess>]
type ModelUpdate =
    | Text of string
    | Thinking of string
    | ToolStarted of CallId * name: string
    | Arguments of CallId * delta: string
    | ToolReady of ToolCall

/// Both transports feed the same run model; no SDK types cross this boundary.
[<RequireQualifiedAccess>]
type ModelAccess =
    | Reply of (ModelRequest -> CancellationToken -> Task<ModelReply>)
    | Stream of (ModelRequest -> CancellationToken -> IAsyncEnumerable<ModelUpdate>)

type Model =
    { Provider: string
      Name: string
      Respond: ModelAccess }
