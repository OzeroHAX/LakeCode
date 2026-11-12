namespace LakeCode.Agent.Kernel

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading

[<RequireQualifiedAccess>]
type OutputKind =
    | Text
    | Thinking

[<RequireQualifiedAccess>]
type OutputEvent =
    | Started of OutputKind
    | Delta of string
    | Finished

/// Block ids are unique within one model response. The kernel scopes them to the request.
[<RequireQualifiedAccess>]
type ModelUpdate =
    | Output of blockId: string * OutputEvent
    | ToolProposed of CallId * name: string
    | Arguments of CallId * delta: string
    | ToolReady of ToolCall

type ModelRequest =
    { Messages: Message list
      Tools: ToolDescription list }

/// The host captures provider configuration in this function.
type Model = ModelRequest -> CancellationToken -> IAsyncEnumerable<ModelUpdate>

/// One response, owned only by the sequential reader; no immutable draft protocol.
type internal ModelResponse(history: History) =
    let text = StringBuilder()
    let mutable hasText = false
    let mutable blocks: (string * OutputKind) list = []
    let mutable seenBlocks = Set.empty
    let mutable calls: (CallId * string * ToolCall option) list = []

    let tryCall id = calls |> List.tryFind (fun (key, _, _) -> key = id)

    member _.HasText = hasText

    member _.OpenBlocks = blocks |> List.map fst |> List.rev

    member _.Reply status =
        { Content = { Text = text.ToString(); Images = [] }
          Calls = calls |> List.rev |> List.choose (fun (_, _, call) -> call)
          Status = status }

    member this.Complete() =
        if not (List.isEmpty blocks) then
            Error "The model stream ended with unfinished output blocks."
        elif calls |> List.exists (fun (_, _, call) -> call.IsNone) then
            Error "The model stream ended with unfinished tool calls."
        else Ok(this.Reply ReplyStatus.Complete)

    member _.Accept incoming =
        let accepted () = Ok incoming
        match incoming with
        | ModelUpdate.Output(id, _) when String.IsNullOrWhiteSpace id ->
            Error "An output block needs a non-empty id."
        | ModelUpdate.Output(id, OutputEvent.Started kind) ->
            if seenBlocks.Contains id then Error "An output block needs a unique id."
            else
                seenBlocks <- seenBlocks.Add id
                blocks <- (id, kind) :: blocks
                accepted ()
        | ModelUpdate.Output(id, OutputEvent.Delta delta) ->
            match blocks |> List.tryFind (fun (key, _) -> key = id) with
            | Some(_, OutputKind.Text) when not (isNull delta) ->
                text.Append delta |> ignore
                hasText <- true
                accepted ()
            | Some(_, OutputKind.Thinking) when not (isNull delta) -> accepted ()
            | _ -> Error "An output delta must be non-null and belong to an open block."
        | ModelUpdate.Output(id, OutputEvent.Finished) ->
            if blocks |> List.exists (fun (key, _) -> key = id) then
                blocks <- blocks |> List.filter (fun (key, _) -> key <> id)
                accepted ()
            else Error "Only an open output block can finish."
        | ModelUpdate.ToolProposed(id, name) ->
            if String.IsNullOrWhiteSpace(CallId.value id) || String.IsNullOrWhiteSpace name ||
               (tryCall id).IsSome || History.containsCall id history then
                Error "A tool proposal needs a name and a unique call id."
            else
                calls <- (id, name, None) :: calls
                accepted ()
        | ModelUpdate.Arguments(id, delta) ->
            match tryCall id with
            | Some(_, _, None) when not (isNull delta) -> accepted ()
            | _ -> Error "An argument delta must be non-null and belong to an open tool call."
        | ModelUpdate.ToolReady call ->
            if History.containsCall call.Id history then Error "A completed tool call id cannot be reused."
            else
                match tryCall call.Id with
                | Some(_, _, Some _) -> Error "A completed tool call id cannot be reused."
                | Some(_, name, _) when name <> call.Name -> Error "A tool call changed its name while streaming."
                | Some _ ->
                    ToolCall.capture call
                    |> Result.map (fun captured ->
                        calls <-
                            calls
                            |> List.map (fun (key, name, existing) ->
                                if key = captured.Id then key, name, Some captured else key, name, existing)
                        ModelUpdate.ToolReady captured)
                | None ->
                    ToolCall.capture call
                    |> Result.map (fun captured ->
                        calls <- (captured.Id, captured.Name, Some captured) :: calls
                        ModelUpdate.ToolReady captured)
