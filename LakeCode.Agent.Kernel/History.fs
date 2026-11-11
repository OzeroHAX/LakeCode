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
type History =
    private
        { Entries: Message list
          Size: int
          Pending: ToolCall list
          CallIds: Set<CallId>
          MessageIds: Set<string> }

[<RequireQualifiedAccess>]
module History =
    let empty = { Entries = []; Size = 0; Pending = []; CallIds = Set.empty; MessageIds = Set.empty }
    let messages history = List.rev history.Entries
    let count history = history.Size
    let internal pending history = history.Pending
    let internal containsCall id history = Set.contains id history.CallIds

    let private captureCalls calls =
        let folder result call =
            result |> Result.bind (fun captured -> ToolCall.capture call |> Result.map (fun item -> item :: captured))

        List.fold folder (Ok []) calls |> Result.map List.rev

    let private validateOutput result =
        let images = ToolResult.images result
        if isNull (ToolResult.text result) then Error "Tool output cannot be null."
        elif images |> List.exists (fun image ->
            String.IsNullOrWhiteSpace image.MediaType || image.Data.IsDefaultOrEmpty || image.Width <= 0 || image.Height <= 0) then
            Error "Tool images need original bytes, a media type and positive dimensions."
        else Ok result

    let internal append message history =
        let id = Message.id message
        let invalid text = Error(HistoryError.InvalidMessage text)
        let commit captured pending callIds =
            Ok { Entries = captured :: history.Entries
                 Size = history.Size + 1
                 Pending = pending
                 CallIds = callIds
                 MessageIds = Set.add id history.MessageIds }

        if String.IsNullOrWhiteSpace id || Set.contains id history.MessageIds then
            invalid "Message ids must be non-empty and unique."
        else
            match message with
            | Message.System(_, text) | Message.User(_, text) ->
                if not (List.isEmpty history.Pending) then Error HistoryError.OpenToolBatch
                elif isNull text then invalid "Message text cannot be null."
                else commit message [] history.CallIds
            | Message.Assistant(_, reply) ->
                if not (List.isEmpty history.Pending) then Error HistoryError.OpenToolBatch
                elif isNull reply.Text then invalid "Assistant text cannot be null."
                else
                    captureCalls reply.Calls
                    |> Result.mapError HistoryError.InvalidMessage
                    |> Result.bind (fun calls ->
                        let ids = calls |> List.map _.Id
                        if Set.count (Set.ofList ids) <> ids.Length || ids |> List.exists (fun callId -> Set.contains callId history.CallIds) then
                            invalid "Tool call ids must be unique in the retained history."
                        else
                            commit (Message.Assistant(id, { reply with Calls = calls })) calls (Set.union history.CallIds (Set.ofList ids)))
            | Message.Tool(_, call, result) ->
                match history.Pending |> List.tryFind (fun pending -> pending.Id = call.Id) with
                | None -> invalid "A tool result must match an unanswered call."
                | Some expected when expected.Name <> call.Name -> invalid "A tool result changed the tool name."
                | Some expected ->
                    validateOutput result
                    |> Result.mapError HistoryError.InvalidMessage
                    |> Result.bind (fun output ->
                        commit (Message.Tool(id, expected, output))
                            (history.Pending |> List.filter (fun pending -> pending.Id <> call.Id)) history.CallIds)

    /// Restores a settled history, never a live model or tool operation.
    let ofMessages entries =
        entries
        |> List.fold (fun state message -> state |> Result.bind (append message)) (Ok empty)
        |> Result.bind (fun history ->
            if List.isEmpty history.Pending then Ok history else Error HistoryError.OpenToolBatch)

    let rewindPoints history =
        let entries = messages history
        let rec collect index anchor batch points remaining =
            let point target count last = { TargetValue = target; CountValue = count; Anchor = last }
            match remaining with
            | [] -> List.rev points
            | message :: rest ->
                let after = index + 1
                let nextAnchor = Some(Message.id message)
                match message with
                | Message.User(id, _) ->
                    collect after nextAnchor batch (point (RewindTarget.BeforeUser id) index anchor :: points) rest
                | Message.Assistant(id, reply) when List.isEmpty reply.Calls ->
                    collect after nextAnchor batch (point (RewindTarget.AfterAssistant id) after nextAnchor :: points) rest
                | Message.Assistant(_, reply) ->
                    collect after nextAnchor (reply.Calls |> List.map _.Id) points rest
                | Message.Tool(_, call, _) ->
                    let pending = batch |> List.filter ((<>) call.Id)
                    if List.isEmpty pending then
                        let calls =
                            entries |> List.take after |> List.rev
                            |> List.takeWhile (function Message.Tool _ -> true | _ -> false)
                            |> List.choose (function Message.Tool(_, completed, _) -> Some completed.Id | _ -> None)
                            |> List.rev
                        collect after nextAnchor [] (point (RewindTarget.AfterTools calls) after nextAnchor :: points) rest
                    else collect after nextAnchor pending points rest
                | Message.System _ -> collect after nextAnchor batch points rest
        collect 0 None [] [] entries

    let rewind point history =
        if point.CountValue < 0 || point.CountValue > history.Size then
            Error HistoryError.InvalidBoundary
        else
            let prefix = messages history |> List.take point.CountValue
            if (prefix |> List.tryLast |> Option.map Message.id) <> point.Anchor then
                Error HistoryError.InvalidBoundary
            else ofMessages prefix

