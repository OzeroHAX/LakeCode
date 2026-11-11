namespace LakeCode.Agent.Kernel

open System
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type SessionError =
    | Busy
    | Closed
    | InvalidHistory of HistoryError

[<RequireQualifiedAccess>]
type SessionStatus =
    | Ready
    | Running of RunView
    | Closing of RunView option
    | Closed

type SessionSnapshot =
    { Revision: int64
      Status: SessionStatus
      History: History
      LastRun: RunResult option }

type JournalEntry =
    { Sequence: int64
      Run: RunId option
      Event: SessionEvent }

type RunHandle =
    { Id: RunId
      Completion: Task<RunResult> }

type Fault =
    { Run: RunId option
      Stage: string
      Exception: exn }

/// Pure machine: one work state, incoming facts, outgoing effects. No resources or callbacks.
module internal SessionState =
    type Work =
        | Preparing of prompt: string
        | Reading of Run.Draft * protocolFailure: Failure option
        | Deciding of ToolCall * remaining: ToolCall list
        | Calling of ToolCall * remaining: ToolCall list
        | Finishing of RunResult

    type RunData =
        { Id: RunId
          Turn: int
          Work: Work
          CancelRequested: bool
          TextParts: string list
          NextMessage: int
          Diagnostics: Failure list }

    type Lifecycle =
        | Ready
        | Running of RunData
        | Closing of RunData option
        | Closed

    type State =
        { Revision: int64
          History: History
          Lifecycle: Lifecycle
          LastRun: RunResult option }

    type Input =
        | Start of RunId * prompt: string
        | Cancel of RunId
        | Rewind of RewindPoint
        | Close
        | Prepared of RunId * Result<unit, RunOutcome>
        | ModelUpdated of RunId * ModelUpdate
        | ModelEnded of RunId * Result<unit, RunOutcome>
        | ToolDecided of RunId * Result<ToolDecision, RunOutcome>
        | ToolProgress of RunId * CallId * ToolProgress
        | ToolReturned of RunId * Result<ToolResult, RunOutcome>
        | Finalized of RunId * diagnostics: Failure list
        | Aborted
        | AbortFinished

    type Effect =
        | Publish of RunId option * SessionEvent
        | Prepare of RunContext
        | ReadModel of RunContext * ModelRequest
        | DecideTool of RunContext * ToolCall
        | CallTool of RunId * ToolCall
        | CancelRun of RunId
        | Finalize of RunContext * RunResult * waitForCancellation: bool
        | CompleteRun of RunResult
        | CompleteClose

    let create history =
        { Revision = 0L; History = history; Lifecycle = Ready; LastRun = None }

    let active state =
        match state.Lifecycle with
        | Running run | Closing(Some run) -> Some run
        | Ready | Closing None | Closed -> None

    let private setRun run state =
        match state.Lifecycle with
        | Running previous when previous.Id = run.Id -> { state with Lifecycle = Running run }
        | Closing(Some previous) when previous.Id = run.Id -> { state with Lifecycle = Closing(Some run) }
        | _ -> invalidOp "The run does not own this session."

    let private context run state : RunContext =
        { Id = run.Id; Turn = run.Turn; History = state.History }

    let private view run : RunView =
        let phase, draft =
            match run.Work with
            | Preparing _ -> RunPhase.Preparing, ""
            | Reading(draft, _) -> RunPhase.ReadingModel, Run.text draft
            | Deciding(call, _) -> RunPhase.CheckingTool call.Id, ""
            | Calling(call, _) -> RunPhase.RunningTool call.Id, ""
            | Finishing _ -> RunPhase.Finishing, ""
        let parts = if draft.Length = 0 then run.TextParts else draft :: run.TextParts
        { Id = run.Id; Turn = run.Turn; Phase = phase
          CancelRequested = run.CancelRequested; Text = Run.runText parts }

    let snapshot state =
        let status =
            match state.Lifecycle with
            | Ready -> SessionStatus.Ready
            | Running run -> SessionStatus.Running(view run)
            | Closing run -> SessionStatus.Closing(Option.map view run)
            | Closed -> SessionStatus.Closed
        { Revision = state.Revision; Status = status; History = state.History; LastRun = state.LastRun }

    let canCancel id state =
        match active state with
        | Some run when run.Id = id -> match run.Work with Finishing _ -> false | _ -> true
        | _ -> false

    /// Transport backpressure: invalid streams stop before the producer advances again.
    let acceptsModelUpdates id state =
        match active state with
        | Some { Id = owner; Work = Reading(_, None) } when owner = id -> true
        | _ -> false

    let private publish id event = Publish(Some id, event)

    let private append run create state =
        let message = create $"{RunId.value run.Id}:{run.NextMessage}"
        match History.append message state.History with
        | Error error -> invalidOp $"Invalid history transition: {error}."
        | Ok history ->
            let run = { run with NextMessage = run.NextMessage + 1 }
            run, setRun run { state with History = history }, [ publish run.Id (SessionEvent.MessageAdded message) ]

    let private addReply run status (reply: ModelReply) state =
        let run, next, effects =
            append run (fun id -> Message.Assistant(id, { Text = reply.Text; Calls = reply.Calls; Status = status })) state
        let parts = if reply.Text.Length = 0 then run.TextParts else reply.Text :: run.TextParts
        let run = { run with TextParts = parts }
        run, setRun run next, effects

    let private addResult run call output state =
        let run, next, effects = append run (fun id -> Message.Tool(id, call, output)) state
        run, next, effects @ [ publish run.Id (SessionEvent.ToolFinished(call, output)) ]

    let private settle run outcome state =
        let currentCall = match run.Work with Calling(call, _) -> Some call.Id | _ -> None
        let close (run, current, effects) (call: ToolCall) =
            let output =
                if currentCall = Some call.Id then
                    ToolResult.Interrupted "Call interrupted; no result was received. Side effects may have happened. Do not retry automatically."
                else ToolResult.Skipped "The run ended before this call was executed."
            let run, next, added = addResult run call output current
            run, next, effects @ added
        let run, settled, effects = History.pending state.History |> List.fold close (run, state, [])
        let result =
            { Id = run.Id; Outcome = outcome; Text = Run.runText run.TextParts
              Turns = run.Turn; Diagnostics = List.rev run.Diagnostics }
        let run = { run with Work = Finishing result }
        setRun run settled,
        effects @ [ publish run.Id (SessionEvent.RunFinishing outcome); Finalize(context run settled, result, run.CancelRequested) ]

    let private finish run outcome state =
        match run.Work with
        | Reading(draft, _) when not (List.isEmpty draft.TextParts && List.isEmpty draft.Calls) ->
            let run, next, partial = addReply run ReplyStatus.Interrupted (Run.reply draft) state
            let next, effects = settle run outcome next
            next, partial @ effects
        | _ -> settle run outcome state

    let private startTurn options definitions run state =
        if run.CancelRequested then finish run RunOutcome.Canceled state
        else
            match options.MaxTurns with
            | Some maximum when run.Turn >= maximum ->
                finish run (RunOutcome.Stopped $"The turn limit ({maximum}) was reached.") state
            | _ ->
                let run = { run with Turn = run.Turn + 1; Work = Reading(Run.emptyDraft, None) }
                let next = setRun run state
                let request = { Messages = History.messages next.History; Tools = definitions }
                next, [ publish run.Id (SessionEvent.TurnStarted run.Turn); ReadModel(context run next, request) ]

    let private nextTool options definitions calls run state =
        if run.CancelRequested then finish run RunOutcome.Canceled state
        else
            match calls with
            | call :: rest ->
                let run = { run with Work = Deciding(call, rest) }
                let next = setRun run state
                next, [ DecideTool(context run next, call) ]
            | [] ->
                let next, effects = startTurn options definitions run state
                next, publish run.Id (SessionEvent.TurnFinished run.Turn) :: effects

    let private cancel run state =
        if run.CancelRequested then state, []
        else
            match run.Work with
            | Finishing _ -> state, []
            | _ ->
                setRun { run with CancelRequested = true } state,
                [ publish run.Id SessionEvent.CancelRequested; CancelRun run.Id ]

    let private onRun id handle state =
        match active state with
        | Some run when run.Id = id -> handle run state
        | _ -> state, [] // Late results from a former run never affect a new one.

    let private transition options definitions input state =
        let accepted (next, effects) = Ok(next, effects)
        match input with
        | Start(id, prompt) ->
            match state.Lifecycle with
            | Closing _ | Closed -> Error SessionError.Closed
            | Running _ -> Error SessionError.Busy
            | Ready ->
                let run =
                    { Id = id; Turn = 0; Work = Preparing prompt; CancelRequested = false
                      TextParts = []; NextMessage = 0; Diagnostics = [] }
                let next = { state with Lifecycle = Running run }
                Ok(next, [ publish id SessionEvent.RunStarted; Prepare(context run next) ])
        | Cancel id -> onRun id cancel state |> accepted
        | Rewind point ->
            match state.Lifecycle with
            | Closing _ | Closed -> Error SessionError.Closed
            | Running _ -> Error SessionError.Busy
            | Ready ->
                History.rewind point state.History
                |> Result.mapError SessionError.InvalidHistory
                |> Result.map (fun history ->
                    { state with History = history; LastRun = None }, [ Publish(None, SessionEvent.Rewound point) ])
        | Close ->
            match state.Lifecycle with
            | Ready -> Ok({ state with Lifecycle = Closed; Revision = state.Revision + 1L }, [ CompleteClose ])
            | Running run -> cancel run { state with Lifecycle = Closing(Some run); Revision = state.Revision + 1L } |> accepted
            | Closing _ -> Ok(state, [])
            | Closed -> Ok(state, [ CompleteClose ])
        | Prepared(id, result) ->
            onRun id (fun run state ->
                match run.Work, result with
                | Preparing _, Error outcome -> finish run outcome state
                | Preparing _, Ok() when run.CancelRequested -> finish run RunOutcome.Canceled state
                | Preparing prompt, Ok() ->
                    let run, next, added = append run (fun id -> Message.User(id, prompt)) state
                    let next, effects = startTurn options definitions run next
                    next, added @ effects
                | _ -> invalidOp "Unexpected preparation result.") state |> accepted
        | ModelUpdated(id, incoming) ->
            onRun id (fun run state ->
                match run.Work with
                | Reading(draft, None) ->
                    match Run.update state.History incoming draft with
                    | Error message ->
                        setRun { run with Work = Reading(draft, Some { Stage = "model protocol"; Message = message }) } state, []
                    | Ok(draft, captured) ->
                        setRun { run with Work = Reading(draft, None) } state, [ publish id (SessionEvent.ModelUpdated captured) ]
                | Reading(_, Some _) -> state, []
                | _ -> invalidOp "A model update arrived outside a model operation.") state |> accepted
        | ModelEnded(id, result) ->
            onRun id (fun run state ->
                match run.Work, result with
                | Reading(_, Some failure), _ ->
                    let run =
                        match result with
                        | Error(RunOutcome.Failed cleanup) -> { run with Diagnostics = cleanup :: run.Diagnostics }
                        | _ -> run
                    finish run (RunOutcome.Failed failure) (setRun run state)
                | Reading _, _ when run.CancelRequested && Result.isOk result -> finish run RunOutcome.Canceled state
                | Reading _, Error outcome -> finish run outcome state
                | Reading(draft, None), Ok() ->
                    match Run.complete draft with
                    | Error message -> finish run (RunOutcome.Failed { Stage = "model protocol"; Message = message }) state
                    | Ok reply ->
                        let run, next, added = addReply run ReplyStatus.Complete reply state
                        let next, effects =
                            if List.isEmpty reply.Calls then
                                let next, effects = settle run RunOutcome.Completed next
                                next, publish id (SessionEvent.TurnFinished run.Turn) :: effects
                            else nextTool options definitions reply.Calls run next
                        next, publish id (SessionEvent.ModelFinished reply) :: (added @ effects)
                | _ -> invalidOp "Unexpected model completion.") state |> accepted
        | ToolDecided(id, result) ->
            onRun id (fun run state ->
                match run.Work with
                | Deciding(call, rest) ->
                    match result with
                    | Error outcome -> finish run outcome state
                    | Ok _ when run.CancelRequested -> finish run RunOutcome.Canceled state
                    | Ok decision ->
                        let next, effects =
                            match decision with
                            | ToolDecision.Allow ->
                                let run = { run with Work = Calling(call, rest) }
                                setRun run state, [ publish id (SessionEvent.ToolStarted call); CallTool(id, call) ]
                            | ToolDecision.Deny reason ->
                                let run, next, added = addResult run call (ToolResult.Skipped reason) state
                                let next, effects = nextTool options definitions rest run next
                                next, added @ effects
                            | ToolDecision.Stop reason -> finish run (RunOutcome.Stopped reason) state
                        next, publish id (SessionEvent.ToolDecided(call, decision)) :: effects
                | _ -> invalidOp "Unexpected tool decision.") state |> accepted
        | ToolProgress(id, call, progress) ->
            onRun id (fun run state ->
                match run.Work with
                | Calling(current, _) when current.Id = call -> state, [ publish id (SessionEvent.ToolProgress(call, progress)) ]
                | _ -> state, []) state |> accepted
        | ToolReturned(id, result) ->
            onRun id (fun run state ->
                match run.Work, result with
                | Calling _, Error outcome -> finish run outcome state
                | Calling(call, rest), Ok output ->
                    let run, next, added = addResult run call output state
                    let next, effects = nextTool options definitions rest run next
                    next, added @ effects
                | _ -> invalidOp "Unexpected tool result.") state |> accepted
        | Finalized(id, diagnostics) ->
            onRun id (fun run state ->
                match run.Work with
                | Finishing initial ->
                    let result = { initial with Diagnostics = initial.Diagnostics @ diagnostics }
                    let lifecycle, close =
                        match state.Lifecycle with
                        | Running _ -> Ready, []
                        | Closing _ -> Closed, [ CompleteClose ]
                        | _ -> invalidOp "Unexpected session lifecycle at completion."
                    { state with Lifecycle = lifecycle; LastRun = Some result },
                    [ publish id (SessionEvent.RunFinished result); CompleteRun result ] @ close
                | _ -> invalidOp "Unexpected finalization result.") state |> accepted
        | Aborted -> Ok({ state with Lifecycle = Closing None; Revision = state.Revision + 1L }, [])
        | AbortFinished -> Ok({ state with Lifecycle = Closed; Revision = state.Revision + 1L }, [ CompleteClose ])

    /// No I/O: validate one input and return the next state and ordered effects.
    let update options definitions input state =
        transition options definitions input state
        |> Result.map (fun (next, effects) ->
            let count = effects |> List.sumBy (function Publish _ -> 1L | _ -> 0L)
            { next with Revision = next.Revision + count }, effects)
