namespace LakeCode.Agent.Kernel

open System

[<RequireQualifiedAccess>]
type HistoryError =
    | InvalidMessage of string
    | OpenToolBatch
    | InvalidBoundary

[<RequireQualifiedAccess>]
type RewindTarget =
    | BeforeUser of messageId: string
    | AfterAssistant of messageId: string
    | AfterTools of calls: CallId list

/// An anchored prefix: an old point cannot accidentally target a different branch.
type RewindPoint =
    private
        { TargetValue: RewindTarget
          CountValue: int
          Anchor: string option }
    member this.Target = this.TargetValue
    member this.MessageCount = this.CountValue

/// Immutable conversation. Only the kernel can append an open tool batch.
type internal History =
    private
        { Entries: Message list
          Size: int
          Pending: ToolCall list
          CallIds: Set<CallId>
          MessageIds: Set<string> }

[<RequireQualifiedAccess>]
module internal History =
    let empty = { Entries = []; Size = 0; Pending = []; CallIds = Set.empty; MessageIds = Set.empty }
    let messages history = List.rev history.Entries
    let pending history = history.Pending
    let containsCall id history = Set.contains id history.CallIds

    let private captureCalls calls =
        calls
        |> List.fold (fun state call ->
            state |> Result.bind (fun captured ->
                ToolCall.capture call |> Result.map (fun item -> item :: captured))) (Ok [])
        |> Result.map List.rev

    let private validateOutput result =
        match result with
        | ToolResult.Success content | ToolResult.Failure content ->
            Content.validate content |> Result.map (fun _ -> result)
        | ToolResult.Skipped reason | ToolResult.Interrupted reason ->
            if isNull reason then Error "Tool result reason cannot be null." else Ok result

    let internal append message history =
        let id = Message.id message
        let invalid text = Error(HistoryError.InvalidMessage text)
        let commit entries pending callIds =
            Ok { Entries = entries :: history.Entries
                 Size = history.Size + 1
                 Pending = pending
                 CallIds = callIds
                 MessageIds = Set.add id history.MessageIds }

        if String.IsNullOrWhiteSpace id || Set.contains id history.MessageIds then
            invalid "Message ids must be non-empty and unique."
        else
            match message with
            | Message.System(_, text) ->
                if not (List.isEmpty history.Pending) then Error HistoryError.OpenToolBatch
                elif isNull text then invalid "Message text cannot be null."
                else commit message [] history.CallIds
            | Message.User(_, content) ->
                if not (List.isEmpty history.Pending) then Error HistoryError.OpenToolBatch
                else
                    Content.validate content
                    |> Result.mapError HistoryError.InvalidMessage
                    |> Result.bind (fun _ -> commit message [] history.CallIds)
            | Message.Assistant(_, reply) ->
                if not (List.isEmpty history.Pending) then Error HistoryError.OpenToolBatch
                else
                    Content.validate reply.Content
                    |> Result.mapError HistoryError.InvalidMessage
                    |> Result.bind (fun _ ->
                        captureCalls reply.Calls |> Result.mapError HistoryError.InvalidMessage)
                    |> Result.bind (fun calls ->
                        let ids = calls |> List.map _.Id
                        if Set.count (Set.ofList ids) <> ids.Length ||
                           ids |> List.exists (fun callId -> Set.contains callId history.CallIds) then
                            invalid "Tool call ids must be unique in the retained history."
                        else
                            commit (Message.Assistant(id, { reply with Calls = calls }))
                                calls (Set.union history.CallIds (Set.ofList ids)))
            | Message.Tool(_, call, result) ->
                match history.Pending |> List.tryFind (fun pending -> pending.Id = call.Id) with
                | None -> invalid "A tool result must match an unanswered call."
                | Some expected when expected.Name <> call.Name -> invalid "A tool result changed the tool name."
                | Some expected ->
                    validateOutput result
                    |> Result.mapError HistoryError.InvalidMessage
                    |> Result.bind (fun output ->
                        commit (Message.Tool(id, expected, output))
                            (history.Pending |> List.filter (fun pending -> pending.Id <> call.Id))
                            history.CallIds)

    /// Restores a settled history, never a live model or tool operation.
    let ofMessages entries =
        entries
        |> List.fold (fun state message -> state |> Result.bind (append message)) (Ok empty)
        |> Result.bind (fun history ->
            if List.isEmpty history.Pending then Ok history else Error HistoryError.OpenToolBatch)

    /// One linear pass: the open batch and the current tool group are carried forward.
    let rewindPoints history =
        let point target count anchor =
            { TargetValue = target; CountValue = count; Anchor = anchor }

        let rec collect index previous batch group points remaining =
            match remaining with
            | [] -> List.rev points
            | message :: rest ->
                let after = index + 1
                let anchor = Some(Message.id message)
                match message with
                | Message.User(id, _) ->
                    collect after anchor [] [] (point (RewindTarget.BeforeUser id) index previous :: points) rest
                | Message.Assistant(id, reply) when List.isEmpty reply.Calls ->
                    collect after anchor [] [] (point (RewindTarget.AfterAssistant id) after anchor :: points) rest
                | Message.Assistant(_, reply) ->
                    collect after anchor (reply.Calls |> List.map _.Id) [] points rest
                | Message.Tool(_, call, _) ->
                    let openCalls = batch |> List.filter ((<>) call.Id)
                    let group = call.Id :: group
                    if List.isEmpty openCalls then
                        let calls = List.rev group
                        collect after anchor [] [] (point (RewindTarget.AfterTools calls) after anchor :: points) rest
                    else collect after anchor openCalls group points rest
                | Message.System _ ->
                    collect after anchor batch [] points rest

        collect 0 None [] [] [] (messages history)

    let rewind point history =
        if point.CountValue < 0 || point.CountValue > history.Size then
            Error HistoryError.InvalidBoundary
        else
            let prefix = messages history |> List.take point.CountValue
            if (prefix |> List.tryLast |> Option.map Message.id) <> point.Anchor then
                Error HistoryError.InvalidBoundary
            else ofMessages prefix
