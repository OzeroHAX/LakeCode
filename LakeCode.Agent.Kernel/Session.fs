namespace LakeCode.Agent.Kernel

open System
open System.Threading
open System.Threading.Tasks

/// One owner per session: accepts requests, serializes notifications and owns history.
type Session(model: Model, tools: Tool list, ?policy: ToolPolicy, ?maxModelCalls: int, ?messages: Message list) =
    let gate = obj()
    let policy = defaultArg policy (fun _ _ _ -> Task.FromResult ToolDecision.Allow)
    let tools = tools |> List.map Tool.capture
    let catalog = tools |> List.map (fun tool -> tool.Name, tool) |> Map.ofList
    let mutable history =
        match History.ofMessages (defaultArg messages []) with
        | Ok history -> history
        | Error error -> invalidArg (nameof messages) $"Invalid initial messages: {error}."
    let mutable current: Request option = None
    let mutable closing = false
    let mutable nextMessage = 0
    let closed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable subscribers: Action<KernelEvent> option = None
    let mutable callbackThread = 0

    do
        if catalog.Count <> tools.Length then invalidArg (nameof tools) "Tool names must be unique."
        if maxModelCalls |> Option.exists (fun limit -> limit <= 0) then
            invalidArg (nameof maxModelCalls) "A finite model call limit must be positive."

    let publish event =
        let handlers =
            lock gate (fun () ->
                subscribers |> Option.map (fun handler -> handler.GetInvocationList()) |> Option.defaultValue [||])
        Volatile.Write(&callbackThread, Environment.CurrentManagedThreadId)
        try
            for handler in handlers do
                try (handler :?> Action<KernelEvent>).Invoke event
                with _ -> ()
        finally Volatile.Write(&callbackThread, 0)

    let ensureNotInHandler () =
        if Volatile.Read(&callbackThread) = Environment.CurrentManagedThreadId then
            invalidOp "Event handlers must enqueue commands, not synchronously call Start, Cancel or Rewind."

    // Ids stay unique for the session lifetime, including after a rewind.
    let append (request: Request) (create: string -> Message) =
        let message =
            lock gate (fun () ->
                let message = create $"{RequestId.value request.Id}:{nextMessage}"
                nextMessage <- nextMessage + 1
                match History.append message history with
                | Ok next -> history <- next
                | Error error -> invalidOp $"Invalid history transition: {error}."
                message)
        publish (KernelEvent.History(HistoryEvent.Added message))

    let complete (request: Request) (registration: CancellationTokenRegistration) reason = task {
        lock gate (fun () -> request.Finishing <- true)
        try registration.Dispose()
        with _ -> ()
        match lock gate (fun () -> request.CancelTask) with
        | Some cancellation ->
            try do! cancellation
            with _ -> ()
        | None -> ()
        try request.Cancellation.Dispose()
        with _ -> ()
        publish (KernelEvent.Request(request.Id, RequestEvent.Finished reason))
        lock gate (fun () ->
            current <- None
            request.Completion.TrySetResult reason |> ignore
            if closing then closed.TrySetResult() |> ignore)
    }

    /// One request; the model/tool loop itself lives in Execution.
    let execute (request: Request) (registration: CancellationTokenRegistration) = task {
        let environment =
            { Model = model
              Tools = catalog
              Policy = policy
              MaxModelCalls = maxModelCalls
              History = (fun () -> lock gate (fun () -> history))
              Append = append request
              Publish = publish }
        let! reason =
            task {
                try
                    let! reason, started = Execution.run environment request
                    let pending = lock gate (fun () -> History.pending history)
                    for call in pending do
                        let result =
                            if started = Some call.Id then
                                ToolResult.Interrupted "Call interrupted; no result was received. Side effects may have happened. Do not retry automatically."
                            else ToolResult.Skipped "The request ended before this call was executed."
                        append request (fun id -> Message.Tool(id, call, result))
                    return reason
                with error ->
                    return FinishReason.Failed("runtime", error.Message)
            }
        do! complete request registration reason
    }

    [<CLIEvent>]
    member _.EventOccurred =
        { new IDelegateEvent<Action<KernelEvent>> with
            member _.AddHandler handler =
                ArgumentNullException.ThrowIfNull handler
                lock gate (fun () ->
                    let combined =
                        match subscribers with
                        | None -> handler
                        | Some previous -> Delegate.Combine(previous, handler) :?> Action<KernelEvent>
                    subscribers <- Some combined)
            member _.RemoveHandler handler =
                lock gate (fun () ->
                    subscribers <-
                        subscribers
                        |> Option.bind (fun previous ->
                            Delegate.Remove(previous, handler) :?> Action<KernelEvent> |> Option.ofObj)) }

    member _.Messages = lock gate (fun () -> History.messages history)
    member _.RewindPoints = lock gate (fun () -> History.rewindPoints history)

    member _.Start(message: Message, ?cancellationToken: CancellationToken) =
        match message with
        | Message.User _ -> ()
        | _ -> invalidArg (nameof message) "Only a user message can start a request."
        ensureNotInHandler ()
        let accepted =
            lock gate (fun () ->
                if closing then Error SessionError.Closed
                elif current.IsSome then Error SessionError.Busy
                else
                    History.append message history
                    |> Result.mapError SessionError.InvalidHistory
                    |> Result.map (fun next ->
                        let request =
                            { Id = RequestId.create ()
                              Cancellation = new CancellationTokenSource()
                              Completion = TaskCompletionSource<FinishReason>(TaskCreationOptions.RunContinuationsAsynchronously)
                              CancelRequested = false
                              CancelTask = None
                              Finishing = false }
                        let registration =
                            try
                                (defaultArg cancellationToken CancellationToken.None)
                                    .Register(fun () ->
                                        lock gate (fun () ->
                                            if not request.Finishing then Execution.cancel request))
                            with _ ->
                                request.Cancellation.Dispose()
                                reraise ()
                        current <- Some request
                        history <- next
                        request, registration))
        accepted |> Result.map (fun (request, registration) ->
            publish (KernelEvent.Request(request.Id, RequestEvent.Started))
            publish (KernelEvent.History(HistoryEvent.Added message))
            Task.Run(Func<Task>(fun () -> execute request registration)) |> ignore
            { Id = request.Id; Completion = request.Completion.Task })

    member this.Start(text: string, ?cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull text
        this.Start(Message.User(Guid.NewGuid().ToString("N"), Content.text text), ?cancellationToken = cancellationToken)

    member _.Cancel(requestId: RequestId) =
        ensureNotInHandler ()
        lock gate (fun () ->
            match current with
            | Some request when request.Id = requestId && not request.Finishing ->
                Execution.cancel request
                true
            | _ -> false)

    member _.Rewind(point: RewindPoint) =
        ensureNotInHandler ()
        let result =
            lock gate (fun () ->
                if closing then Error SessionError.Closed
                elif current.IsSome then Error SessionError.Busy
                else
                    History.rewind point history
                    |> Result.mapError SessionError.InvalidHistory
                    |> Result.map (fun next -> history <- next))
        result |> Result.map (fun () -> publish (KernelEvent.History(HistoryEvent.Rewound point)))

    member _.Close() =
        lock gate (fun () ->
            closing <- true
            match current with
            | Some request when not request.Finishing -> Execution.cancel request
            | Some _ -> ()
            | None -> closed.TrySetResult() |> ignore)
        closed.Task :> Task

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.Close())
