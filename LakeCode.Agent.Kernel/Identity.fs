namespace LakeCode.Agent.Kernel

open System

/// Identifies one accepted run, including its late progress reports.
[<Struct>]
type RunId = private RunId of Guid

[<RequireQualifiedAccess>]
module RunId =
    let create () = RunId(Guid.NewGuid())
    let value (RunId id) = id

/// A provider call id. Its uniqueness is checked within the retained conversation.
[<Struct>]
type CallId = private CallId of string

[<RequireQualifiedAccess>]
module CallId =
    let create value =
        if String.IsNullOrWhiteSpace value then Error "A tool call id is required."
        else Ok(CallId value)

    let value (CallId id) = id
