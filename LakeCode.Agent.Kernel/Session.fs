namespace LakeCode.Agent.Kernel

open System
open System.Threading
open System.Threading.Tasks

/// Runtime handles never enter the pure machine.
type private RunResources =
    { Id: RunId
      Cancellation: CancellationTokenSource
      Completion: TaskCompletionSource<RunResult>
      CancellationFinished: TaskCompletionSource<unit>
      Operations: ResizeArray<Task> }

/// Reply channels belong to transport, not to the machine's inputs or effects.
type private SessionRequest =
    | StartRequest of string * CancellationToken * AsyncReplyChannel<Result<RunHandle, SessionError>>
    | CancelRequest of RunId * AsyncReplyChannel<bool>
    | RewindRequest of RewindPoint * AsyncReplyChannel<Result<unit, SessionError>>
    | CloseRequest
    | Notice of SessionState.Input
    | ModelUpdateRequest of RunId * ModelUpdate * AsyncReplyChannel<bool>
    | WorkerFailed of RunId * exn
    | AbortComplete of exn

/// One owner processes inputs; external operations only send results back to it.
type Session(model: Model, tools: Tool list, ?policy: Policy, ?options: RunOptions, ?history: History) =
    let gate = obj()
    let policy = defaultArg policy Policy.none
    let options = defaultArg options RunOptions.defaults
    let tools = tools |> List.map Tool.capture
    let definitions = tools |> List.map _.Definition
    let catalog = tools |> List.map (fun tool -> tool.Definition.Name, tool) |> Map.ofList
    let initial = SessionState.create (defaultArg history History.empty)
    let mutable snapshot = SessionState.snapshot initial
    let journal = ResizeArray<JournalEntry>()
    let faults = ResizeArray<Fault>()
    let closed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable changed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable subscribers: Action<JournalEntry> option = None
    let mutable callbackThread = 0

    do
        if catalog.Count <> tools.Length then invalidArg (nameof tools) "Tool names must be unique."
        if options.MaxTurns |> Option.exists (fun limit -> limit <= 0) then
            invalidArg (nameof options) "A finite turn limit must be positive."
        if not (List.isEmpty (History.pending initial.History)) then
            invalidArg (nameof history) "A new session requires a settled history."
        if String.IsNullOrWhiteSpace model.Provider || String.IsNullOrWhiteSpace model.Name then
            invalidArg (nameof model) "Model and provider names are required."

    let recordFault run stage (error: exn) =
        lock gate (fun () -> faults.Add { Run = run; Stage = stage; Exception = error })
        { Stage = stage; Message = error.Message }

    let wakeReaders () =
        let previous = changed
        changed <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        previous.TrySetResult() |> ignore

    // Commit the projected snapshot and the whole publication batch before invoking user code.
    let commit (state: SessionState.State) effects =
        let publications = effects |> List.choose (function SessionState.Publish(run, event) -> Some(run, event) | _ -> None)
        let first = state.Revision - int64 publications.Length + 1L
        let entries = publications |> List.mapi (fun index (run, event) ->
            { Sequence = first + int64 index; Run = run; Event = event })
        lock gate (fun () ->
            journal.AddRange entries
            snapshot <- SessionState.snapshot state
            if not (List.isEmpty entries) || snapshot.Status = SessionStatus.Closed then wakeReaders ())
        entries

    // This is the only place invoking subscribers. The input owner guarantees ordering.
    let publish entry =
        let handlers = lock gate (fun () ->
            subscribers |> Option.map (fun handler -> handler.GetInvocationList()) |> Option.defaultValue [||])
        Volatile.Write(&callbackThread, Environment.CurrentManagedThreadId)
        try
            for handler in handlers do
                try (handler :?> Action<JournalEntry>).Invoke entry
                with error -> recordFault entry.Run "event subscriber" error |> ignore
        finally Volatile.Write(&callbackThread, 0)

    let eventOccurred =
        { new IDelegateEvent<Action<JournalEntry>> with
            member _.AddHandler handler =
                ArgumentNullException.ThrowIfNull handler
                lock gate (fun () ->
                    subscribers <- Some(match subscribers with
                                        | None -> handler
                                        | Some previous -> Delegate.Combine(previous, handler) :?> Action<JournalEntry>))
            member _.RemoveHandler handler =
                lock gate (fun () ->
                    subscribers <- subscribers |> Option.bind (fun previous ->
                        Delegate.Remove(previous, handler) :?> Action<JournalEntry> |> Option.ofObj)) }

    let ensureNotInHandler () =
        if Volatile.Read(&callbackThread) = Environment.CurrentManagedThreadId then
            invalidOp "Event handlers must enqueue commands, not synchronously call Start, Cancel or Rewind."

    let agent = MailboxProcessor.Start((fun inbox ->
        // This reference has one owner. All values stored in it are immutable.
        let mutable state = initial
        let mutable resources: RunResources option = None

        let requireResources id =
            match resources with
            | Some current when current.Id = id -> current
            | _ -> invalidOp "The effect does not own the run resources."

        let launch current operation =
            let work () = task {
                try do! operation ()
                with error -> inbox.Post(WorkerFailed(current.Id, error))
            }
            current.Operations.RemoveAll(fun operation -> operation.IsCompleted) |> ignore
            current.Operations.Add(Task.Run(Func<Task>(fun () -> work () :> Task)))

        let invoke current stage operation = task {
            try
                let! result = operation ()
                return Ok result
            with
            | :? OperationCanceledException when current.Cancellation.IsCancellationRequested ->
                return Error RunOutcome.Canceled
            | error -> return Error(RunOutcome.Failed(recordFault (Some current.Id) stage error))
        }

        let readModel current request = task {
            // Clone before crossing the input boundary; producer-owned documents may be disposed later.
            let send update =
                let captured =
                    match update with
                    | ModelUpdate.ToolReady call -> ModelUpdate.ToolReady { call with Arguments = call.Arguments.Clone() }
                    | other -> other
                inbox.PostAndAsyncReply(fun reply -> ModelUpdateRequest(current.Id, captured, reply))
                |> fun pending -> Async.StartAsTask(pending, cancellationToken = CancellationToken.None)
            let token = current.Cancellation.Token
            let! result = invoke current "model" (fun () -> task {
                token.ThrowIfCancellationRequested()
                match model.Respond with
                | ModelAccess.Reply respond ->
                    let! reply = respond request token
                    let! accepted = send (ModelUpdate.Text reply.Text)
                    let mutable reading = accepted
                    for call in reply.Calls do
                        if reading then
                            let! accepted = send (ModelUpdate.ToolReady call)
                            reading <- accepted
                | ModelAccess.Stream stream ->
                    use iterator = (stream request token).GetAsyncEnumerator(token)
                    let mutable reading = true
                    while reading do
                        let! available = iterator.MoveNextAsync()
                        if not available then reading <- false
                        else
                            let! accepted = send iterator.Current
                            reading <- accepted })
            inbox.Post(Notice(SessionState.ModelEnded(current.Id, result)))
        }

        let callTool current (call: ToolCall) = task {
            let! result = invoke current "tool" (fun () -> task {
                match Map.tryFind call.Name catalog with
                | None -> return ToolResult.failure $"Tool '{call.Name}' is not registered."
                | Some tool ->
                    let lifetime = obj()
                    let mutable reporting = true
                    let report progress = lock lifetime (fun () ->
                        if reporting then
                            let captured = Tool.captureProgress progress
                            inbox.Post(Notice(SessionState.ToolProgress(current.Id, call.Id, captured))))
                    try return! tool.Execute call report current.Cancellation.Token
                    finally lock lifetime (fun () -> reporting <- false) })
            inbox.Post(Notice(SessionState.ToolReturned(current.Id, result)))
        }

        let finalize current context result waitForCancellation = task {
            let diagnostics = ResizeArray<Failure>()
            let observe stage (operation: unit -> Task) = task {
                try do! operation ()
                with error -> diagnostics.Add(recordFault (Some current.Id) stage error)
            }
            do! observe "after run" (fun () -> policy.AfterRun context result CancellationToken.None)
            if waitForCancellation then
                do! observe "cancellation callbacks" (fun () -> current.CancellationFinished.Task)
            try current.Cancellation.Dispose()
            with error -> diagnostics.Add(recordFault (Some current.Id) "cancellation cleanup" error)
            inbox.Post(Notice(SessionState.Finalized(current.Id, List.ofSeq diagnostics)))
        }

        let execute effects entries =
            let mutable publications = entries
            for effect in effects do
                match effect with
                | SessionState.Publish _ ->
                    match publications with
                    | entry :: rest ->
                        publish entry
                        publications <- rest
                    | [] -> invalidOp "A publication is missing its journal entry."
                | SessionState.Prepare context ->
                    let current = requireResources context.Id
                    launch current (fun () -> task {
                        let! result = invoke current "before run" (fun () -> task {
                            current.Cancellation.Token.ThrowIfCancellationRequested()
                            do! policy.BeforeRun context current.Cancellation.Token })
                        inbox.Post(Notice(SessionState.Prepared(current.Id, result))) })
                | SessionState.ReadModel(context, request) ->
                    let current = requireResources context.Id
                    launch current (fun () -> readModel current request)
                | SessionState.DecideTool(context, call) ->
                    let current = requireResources context.Id
                    launch current (fun () -> task {
                        let! result = invoke current "tool policy" (fun () -> policy.DecideTool context call current.Cancellation.Token)
                        inbox.Post(Notice(SessionState.ToolDecided(current.Id, result))) })
                | SessionState.CallTool(id, call) ->
                    let current = requireResources id
                    launch current (fun () -> callTool current call)
                | SessionState.CancelRun id ->
                    let current = requireResources id
                    launch current (fun () -> task {
                        try
                            do! current.Cancellation.CancelAsync()
                            current.CancellationFinished.TrySetResult() |> ignore
                        with error -> current.CancellationFinished.TrySetException error |> ignore })
                | SessionState.Finalize(context, result, canceled) ->
                    let current = requireResources context.Id
                    launch current (fun () -> finalize current context result canceled)
                | SessionState.CompleteRun result ->
                    let current = requireResources result.Id
                    resources <- None
                    current.Completion.TrySetResult result |> ignore
                | SessionState.CompleteClose -> closed.TrySetResult() |> ignore

        let install next effects =
            state <- next
            commit next effects

        let accept (next, effects) =
            let entries = install next effects
            execute effects entries

        let apply input =
            SessionState.update options definitions input state |> Result.map accept

        let applyInternal input =
            match apply input with
            | Ok() -> ()
            | Error error -> invalidOp $"An internal input was rejected: {error}."

        let rejectPending = function
            | StartRequest(_, _, reply) -> reply.Reply(Error SessionError.Closed)
            | CancelRequest(_, reply) -> reply.Reply false
            | RewindRequest(_, reply) -> reply.Reply(Error SessionError.Closed)
            | ModelUpdateRequest(_, _, reply) -> reply.Reply false
            | CloseRequest | Notice _ | WorkerFailed _ | AbortComplete _ -> ()

        let failClosed message error =
            let current = resources
            recordFault (current |> Option.map _.Id) "runtime" error |> ignore
            applyInternal SessionState.Aborted
            rejectPending message
            let operations = current |> Option.map (fun current -> current.Operations.ToArray()) |> Option.defaultValue [||]
            let cleanup () = task {
                match current with
                | None -> ()
                | Some current ->
                    try do! current.Cancellation.CancelAsync()
                    with callbacks -> recordFault (Some current.Id) "runtime cancellation callbacks" callbacks |> ignore
                    try do! Task.WhenAll operations
                    with worker -> recordFault (Some current.Id) "runtime worker cleanup" worker |> ignore
                    if current.CancellationFinished.Task.IsCompleted then
                        try do! current.CancellationFinished.Task
                        with callbacks -> recordFault (Some current.Id) "runtime cancellation result" callbacks |> ignore
                    try current.Cancellation.Dispose()
                    with cleanup -> recordFault (Some current.Id) "runtime cleanup" cleanup |> ignore
                inbox.Post(AbortComplete error)
            }
            Task.Run(Func<Task>(fun () -> cleanup () :> Task)) |> ignore

        let handle message =
            // A linked token may be set before its queued cancellation notification is processed.
            match resources with
            | Some current when current.Cancellation.IsCancellationRequested -> applyInternal (SessionState.Cancel current.Id)
            | _ -> ()
            match message with
            | StartRequest(prompt, token, reply) ->
                let id = RunId.create ()
                match SessionState.update options definitions (SessionState.Start(id, prompt)) state with
                | Error error -> reply.Reply(Error error)
                | Ok transition ->
                    let current =
                        { Id = id
                          Cancellation = CancellationTokenSource.CreateLinkedTokenSource token
                          Completion = TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously)
                          CancellationFinished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                          Operations = ResizeArray() }
                    resources <- Some current
                    current.Cancellation.Token.Register(fun () -> inbox.Post(Notice(SessionState.Cancel id))) |> ignore
                    accept transition
                    reply.Reply(Ok { Id = id; Completion = current.Completion.Task })
            | CancelRequest(id, reply) ->
                let accepted = SessionState.canCancel id state
                applyInternal (SessionState.Cancel id)
                reply.Reply accepted
            | RewindRequest(point, reply) -> reply.Reply(apply (SessionState.Rewind point))
            | CloseRequest -> applyInternal SessionState.Close
            | Notice input -> applyInternal input
            | ModelUpdateRequest(id, update, reply) ->
                applyInternal (SessionState.ModelUpdated(id, update))
                reply.Reply(SessionState.acceptsModelUpdates id state)
            | WorkerFailed(id, error) ->
                match SessionState.active state with
                | Some run when run.Id = id -> raise error
                | _ -> recordFault (Some id) "late worker" error |> ignore
            | AbortComplete error ->
                match SessionState.update options definitions SessionState.AbortFinished state with
                | Error rejected -> invalidOp $"Runtime cleanup was rejected: {rejected}."
                | Ok(next, effects) ->
                    let entries = install next effects
                    resources |> Option.iter (fun current -> current.Completion.TrySetException error |> ignore)
                    resources <- None
                    closed.TrySetException error |> ignore
                    execute effects entries

        let rec loop () = async {
            let! message = inbox.Receive()
            try handle message
            with error -> failClosed message error
            return! loop ()
        }
        loop ()), cancellationToken = CancellationToken.None)

    /// Synchronous ordered notifications on the input owner, outside the snapshot lock.
    /// Handlers only enqueue UI updates; their exceptions are isolated in Faults.
    [<CLIEvent>]
    member _.EventOccurred = eventOccurred

    member _.Snapshot = lock gate (fun () -> snapshot)
    member _.Faults = lock gate (fun () -> List.ofSeq faults)

    member _.Start(prompt: string, ?cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull prompt
        ensureNotInHandler ()
        agent.PostAndReply(fun reply -> StartRequest(prompt, defaultArg cancellationToken CancellationToken.None, reply))

    member _.Cancel(runId: RunId) =
        ensureNotInHandler ()
        agent.PostAndReply(fun reply -> CancelRequest(runId, reply))

    member _.Rewind(point: RewindPoint) =
        ensureNotInHandler ()
        agent.PostAndReply(fun reply -> RewindRequest(point, reply))

    member _.Close() =
        agent.Post CloseRequest
        closed.Task :> Task

    member _.ReadEvents(after: int64) =
        if after < 0L then invalidArg (nameof after) "An event sequence cannot be negative."
        lock gate (fun () -> journal |> Seq.filter (fun entry -> entry.Sequence > after) |> Seq.toList)

    member _.WaitForEvents(after: int64, cancellationToken: CancellationToken) =
        if after < 0L then invalidArg (nameof after) "An event sequence cannot be negative."
        task {
            let mutable ready = None
            while ready.IsNone do
                let entries, signal, finished = lock gate (fun () ->
                    let entries = journal |> Seq.filter (fun entry -> entry.Sequence > after) |> Seq.toList
                    entries, changed.Task, snapshot.Status = SessionStatus.Closed)
                if not (List.isEmpty entries) || finished then ready <- Some entries
                else do! signal.WaitAsync(cancellationToken)
            match ready with
            | Some entries -> return entries
            | None -> return invalidOp "The event wait ended without a result."
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.Close())
