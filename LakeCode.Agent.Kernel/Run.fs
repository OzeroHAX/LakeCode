namespace LakeCode.Agent.Kernel

open System

/// None means unlimited; a finite limit must be positive.
type RunOptions = { MaxTurns: int option }

[<RequireQualifiedAccess>]
module RunOptions =
    let defaults = { MaxTurns = None }

/// Observed work, not a second protocol for scheduling the run.
[<RequireQualifiedAccess>]
type RunPhase =
    | Preparing
    | ReadingModel
    | CheckingTool of CallId
    | RunningTool of CallId
    | Finishing

type RunView =
    { Id: RunId
      Turn: int
      Phase: RunPhase
      CancelRequested: bool
      Text: string }

[<RequireQualifiedAccess>]
type SessionEvent =
    | RunStarted
    | CancelRequested
    | TurnStarted of turn: int
    | ModelUpdated of ModelUpdate
    | ModelFinished of ModelReply
    | MessageAdded of Message
    | ToolDecided of ToolCall * ToolDecision
    | ToolStarted of ToolCall
    | ToolProgress of CallId * ToolProgress
    | ToolFinished of ToolCall * ToolResult
    | TurnFinished of turn: int
    | RunFinishing of RunOutcome
    | RunFinished of RunResult
    | Rewound of RewindPoint

/// Pure accumulation and validation of a model response. Session owns execution.
module internal Run =
    type Draft =
        { TextParts: string list
          Calls: ToolCall list
          Started: Map<CallId, string>
          Order: CallId list }

    let emptyDraft = { TextParts = []; Calls = []; Started = Map.empty; Order = [] }

    let text draft = draft.TextParts |> List.rev |> String.concat ""
    let runText parts = parts |> List.rev |> String.concat "\n"

    let reply draft : ModelReply =
        let ready = draft.Calls |> List.map (fun call -> call.Id, call) |> Map.ofList
        { Text = text draft
          Calls = draft.Order |> List.rev |> List.choose (fun id -> Map.tryFind id ready) }

    let complete draft =
        if Map.count draft.Started <> draft.Calls.Length then
            Error "The model stream ended with unfinished tool calls."
        else Ok(reply draft)

    let update history incoming draft =
        let accepted next captured = Ok(next, captured)
        match incoming with
        | ModelUpdate.Text delta ->
            if isNull delta then Error "A text delta cannot be null."
            else accepted { draft with TextParts = delta :: draft.TextParts } incoming
        | ModelUpdate.Thinking delta ->
            if isNull delta then Error "A thinking delta cannot be null."
            else accepted draft incoming
        | ModelUpdate.ToolStarted(id, name) ->
            if String.IsNullOrWhiteSpace(CallId.value id) || String.IsNullOrWhiteSpace name ||
               Map.containsKey id draft.Started || History.containsCall id history then
                Error "A tool start needs a name and a unique call id."
            else
                accepted { draft with Started = Map.add id name draft.Started; Order = id :: draft.Order } incoming
        | ModelUpdate.Arguments(id, delta) ->
            if isNull delta || not (Map.containsKey id draft.Started) || draft.Calls |> List.exists (fun call -> call.Id = id) then
                Error "An argument delta must belong to an open tool call."
            else accepted draft incoming
        | ModelUpdate.ToolReady call ->
            if History.containsCall call.Id history || draft.Calls |> List.exists (fun previous -> previous.Id = call.Id) then
                Error "A completed tool call id cannot be reused."
            elif Map.tryFind call.Id draft.Started |> Option.exists ((<>) call.Name) then
                Error "A tool call changed its name while streaming."
            else
                ToolCall.capture call
                |> Result.map (fun captured ->
                    let next =
                        { draft with Calls = captured :: draft.Calls
                                     Started = Map.add captured.Id captured.Name draft.Started
                                     Order = if Map.containsKey captured.Id draft.Started then draft.Order else captured.Id :: draft.Order }
                    next, ModelUpdate.ToolReady captured)
