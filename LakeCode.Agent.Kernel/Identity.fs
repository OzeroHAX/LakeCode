namespace LakeCode.Agent.Kernel

open System

/// Identifies one accepted request, including its late progress reports.
[<Struct>]
type RequestId = private RequestId of Guid

[<RequireQualifiedAccess>]
module RequestId =
    let create () = RequestId(Guid.NewGuid())
    let value (RequestId id) = id

/// A provider call id. Its uniqueness is checked within the retained conversation.
[<Struct>]
type CallId = private CallId of string

[<RequireQualifiedAccess>]
module CallId =
    let create value =
        if String.IsNullOrWhiteSpace value then Error "A tool call id is required."
        else Ok(CallId value)

    let value (CallId id) = id
