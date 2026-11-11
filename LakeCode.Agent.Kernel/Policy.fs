namespace LakeCode.Agent.Kernel

open System.Threading
open System.Threading.Tasks

type Failure =
    { Stage: string
      Message: string }

[<RequireQualifiedAccess>]
type RunOutcome =
    | Completed
    | Canceled
    | Stopped of reason: string
    | Failed of Failure

type RunResult =
    { Id: RunId
      Outcome: RunOutcome
      Text: string
      Turns: int
      Diagnostics: Failure list }

type RunContext =
    { Id: RunId
      Turn: int
      History: History }

[<RequireQualifiedAccess>]
type ToolDecision =
    | Allow
    | Deny of reason: string
    | Stop of reason: string

/// Preparation, tool admission and cleanup; none receives mutable session state.
type Policy =
    { BeforeRun: RunContext -> CancellationToken -> Task<unit>
      DecideTool: RunContext -> ToolCall -> CancellationToken -> Task<ToolDecision>
      AfterRun: RunContext -> RunResult -> CancellationToken -> Task<unit> }

[<RequireQualifiedAccess>]
module Policy =
    let none =
        { BeforeRun = fun _ _ -> Task.FromResult()
          DecideTool = fun _ _ _ -> Task.FromResult ToolDecision.Allow
          AfterRun = fun _ _ _ -> Task.FromResult() }
