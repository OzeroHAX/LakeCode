namespace LakeCode.Agent.Kernel

open System.Threading.Tasks

/// Why processing ended, not an execution state or a duplicate response.
[<RequireQualifiedAccess>]
type FinishReason =
    | Completed
    | Canceled
    | Stopped of reason: string
    | Failed of stage: string * message: string

[<RequireQualifiedAccess>]
type RequestEvent =
    | Started
    | Finished of FinishReason

[<RequireQualifiedAccess>]
type ToolEvent =
    | Proposed of name: string
    | ArgumentsDelta of string
    | Ready of ToolCall
    | Started
    | Progress of Progress

[<RequireQualifiedAccess>]
type HistoryEvent =
    | Added of Message
    | Rewound of RewindPoint

[<RequireQualifiedAccess>]
type KernelEvent =
    | Request of RequestId * RequestEvent
    | Output of RequestId * blockId: string * OutputEvent
    | Tool of RequestId * CallId * ToolEvent
    | History of HistoryEvent

[<RequireQualifiedAccess>]
type SessionError =
    | Busy
    | Closed
    | InvalidHistory of HistoryError

/// Result of starting a request: its identity and its final reason.
type RequestHandle =
    { Id: RequestId
      Completion: Task<FinishReason> }
