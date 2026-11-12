namespace LakeCode.Agent.Kernel

open System
open System.Threading
open System.Threading.Tasks

/// Resources of the one sequential request.
type internal Request =
    { Id: RequestId
      Cancellation: CancellationTokenSource
      Completion: TaskCompletionSource<FinishReason>
      mutable CancelRequested: bool
      mutable CancelTask: Task option
      mutable Finishing: bool }

/// The services one request needs; no other session state crosses into execution.
type internal RequestEnvironment =
    { Model: Model
      Tools: Map<string, Tool>
      Policy: ToolPolicy
      MaxModelCalls: int option
      History: unit -> History
      Append: (string -> Message) -> unit
      Publish: KernelEvent -> unit }

module internal Execution =
    let canceled (request: Request) =
        request.CancelRequested || request.Cancellation.IsCancellationRequested

    /// Called under the session lock: records the intent once and schedules callbacks.
    let cancel (request: Request) =
        if not request.CancelRequested then
            request.CancelRequested <- true
            request.CancelTask <- Some(Task.Run(Func<Task>(fun () -> request.Cancellation.CancelAsync())))

    let invoke (request: Request) stage operation = task {
        try
            let! result = operation ()
            return Ok result
        with
        | :? OperationCanceledException when canceled request -> return Error FinishReason.Canceled
        | error -> return Error(FinishReason.Failed(stage, error.Message))
    }

    let private event (request: Request) turn = function
        | ModelUpdate.Output(id, value) -> KernelEvent.Output(request.Id, $"{turn}:{id}", value)
        | ModelUpdate.ToolProposed(id, name) -> KernelEvent.Tool(request.Id, id, ToolEvent.Proposed name)
        | ModelUpdate.Arguments(id, delta) -> KernelEvent.Tool(request.Id, id, ToolEvent.ArgumentsDelta delta)
        | ModelUpdate.ToolReady call -> KernelEvent.Tool(request.Id, call.Id, ToolEvent.Ready call)

    /// Reads one model response; a partial reply is retained as Interrupted.
    let private read env request turn = task {
        let conversation = env.History()
        let descriptions = env.Tools |> Map.toList |> List.map (fun (_, tool) -> Tool.description tool)
        let response = ModelResponse(conversation)
        let mutable protocolFailure = None
        let! streamed = invoke request "model" (fun () -> task {
            let token = request.Cancellation.Token
            token.ThrowIfCancellationRequested()
            use iterator =
                (env.Model { Messages = History.messages conversation; Tools = descriptions } token)
                    .GetAsyncEnumerator(token)
            let mutable reading = true
            while reading do
                let! available = iterator.MoveNextAsync()
                if not available then reading <- false
                else
                    match response.Accept iterator.Current with
                    | Error message -> protocolFailure <- Some message; reading <- false
                    | Ok captured -> env.Publish(event request turn captured) })
        let result =
            match protocolFailure, streamed with
            | Some message, _ -> Error(FinishReason.Failed("model protocol", message))
            | None, Error reason -> Error reason
            | None, Ok() when canceled request -> Error FinishReason.Canceled
            | None, Ok() ->
                response.Complete()
                |> Result.mapError (fun message -> FinishReason.Failed("model protocol", message))
        let reply =
            match result with
            | Ok reply -> reply
            | Error _ ->
                for id in response.OpenBlocks do
                    env.Publish(KernelEvent.Output(request.Id, $"{turn}:{id}", OutputEvent.Finished))
                response.Reply ReplyStatus.Interrupted
        if Result.isOk result || response.HasText || not (List.isEmpty reply.Calls) then
            env.Append(fun id -> Message.Assistant(id, reply))
        return result
    }

    let private runTool env request (call: ToolCall) =
        invoke request "tool" (fun () -> task {
            match Map.tryFind call.Name env.Tools with
            | None -> return ToolResult.failure $"Tool '{call.Name}' is not registered."
            | Some tool ->
                let progressGate = obj()
                let mutable reporting = true
                let report progress =
                    lock progressGate (fun () ->
                        if reporting then
                            env.Publish(
                                KernelEvent.Tool(request.Id, call.Id, ToolEvent.Progress(Tool.captureProgress progress))))
                try return! tool.Execute call report request.Cancellation.Token
                finally lock progressGate (fun () -> reporting <- false) })

    /// One sequential model/tool loop. Returns why it ended and the call left unfinished, if any.
    let run (env: RequestEnvironment) (request: Request) = task {
        let mutable reason = FinishReason.Completed
        let mutable started: CallId option = None
        let execute (call: ToolCall) = task {
            if canceled request then return Error FinishReason.Canceled
            else
                let! decision =
                    invoke request "tool policy" (fun () ->
                        env.Policy (History.messages (env.History())) call request.Cancellation.Token)
                match decision with
                | Error failure -> return Error failure
                | Ok _ when canceled request -> return Error FinishReason.Canceled
                | Ok(ToolDecision.Stop message) -> return Error(FinishReason.Stopped message)
                | Ok(ToolDecision.Deny message) ->
                    env.Append(fun id -> Message.Tool(id, call, ToolResult.Skipped message))
                    return Ok()
                | Ok ToolDecision.Allow ->
                    started <- Some call.Id
                    env.Publish(KernelEvent.Tool(request.Id, call.Id, ToolEvent.Started))
                    let! result = runTool env request call
                    return result |> Result.map (fun output ->
                        env.Append(fun id -> Message.Tool(id, call, output))
                        started <- None)
        }
        let mutable reading = true
        let mutable turn = 0
        while reading do
            if canceled request then
                reason <- FinishReason.Canceled
                reading <- false
            elif env.MaxModelCalls |> Option.exists (fun maximum -> turn >= maximum) then
                reason <- FinishReason.Stopped $"The model call limit ({turn}) was reached."
                reading <- false
            else
                turn <- turn + 1
                let! response = read env request turn
                match response with
                | Error failure -> reason <- failure; reading <- false
                | Ok reply when List.isEmpty reply.Calls -> reading <- false
                | Ok reply ->
                    for call in reply.Calls do
                        if reading then
                            let! result = execute call
                            match result with
                            | Ok() -> ()
                            | Error failure -> reason <- failure; reading <- false
        if reason = FinishReason.Completed && canceled request then
            reason <- FinishReason.Canceled
        return reason, started
    }
