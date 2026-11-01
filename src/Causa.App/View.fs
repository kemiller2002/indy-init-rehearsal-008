// Owner: integration. The composition of the shell view the Limen kernel binds (data-text, data-bind-*, data-if).
// A Limen ViewState is flat: string, number or boolean per key. Views (ui, ui-2) contribute their keys through the
// ScreenView projection once the engine (IF41-APP) is wired; this file only composes and never decides legality.
//
// WI-R8-T17 (integration checkpoint 1): the tab-local workspace. A command the page sends is decided by the domain
// kernel (Causa.Domain.Kernel.decideOn), its events stamped and appended; the workspace view below is one projection
// of that history (one replay, one version) that every dependent region of the page reads (ADRP-004). Legality and
// blocker reasons come from the kernel's capabilities; nothing here re-derives them.
namespace Causa.App

open System
open Causa.Domain

type ViewValue =
    | Text of string
    | Number of float
    | Flag of bool

/// One complete Limen ViewState: every bound key, every time (never a patch).
type ShellView = (string * ViewValue) list

/// The kernel's answer to the last command this tab submitted.
type Outcome =
    | Accepted of command: Command * events: EventEnvelope list
    | Refused of command: Command * decision: Decision

/// The tab-local investigation: the events the kernel allowed, in order, and the last answer. Session-local: it is
/// not saved anywhere (persistence is wired in later tasks), and the page says so.
type Workspace =
    { History: EventEnvelope list
      Submissions: int
      Last: Outcome option }

/// What the wire layer knows: has Limen initialized us, where are we, and the tab's workspace.
type ShellState =
    { Booted: bool
      Path: string
      Hash: string
      Workspace: Workspace }

/// The one projection of a workspace history every dependent view reads.
type WorkspaceProjection =
    { State: InvestigationState option
      Capabilities: Capability list
      Fault: string option }

[<RequireQualifiedAccess>]
module Workspace =
    let policy: PolicySet = { Id = Policies.defaultPolicyId }

    /// The person acting in this tab. Identity comes with the connection (later tasks); until then the tab acts.
    let actor: ActorContext = { ActorId = "this-tab" }

    /// The tab-local investigation's identity. Deterministic because the engine is pure (no randomness).
    let investigation = InvestigationId(Guid.Parse "00000000-0000-4000-8000-00000000ca05")

    /// The identity of the n-th command this tab submits (deterministic, unique within the tab).
    let commandId (n: int) = Guid.Parse("00000000-0000-4000-8000-" + n.ToString("D12"))

    let empty: Workspace = { History = []; Submissions = 0; Last = None }

    /// The commands the page offers, in page order. Each is sent as the Limen event "submit.<CommandName>".
    let offered =
        [ StartInvestigation
          RecordObservation
          ActivateInvestigation
          RecordConclusion
          ResolveInvestigation ]

    let eventName (command: Command) = "submit." + Command.name command

    /// Pure: a Limen event name to the typed command it carries, or None for any other event.
    let commandOfEvent (name: string) : Command option =
        Command.all |> List.tryFind (fun c -> eventName c = name)

    /// Pure: the one projection of a history. An empty history is "no investigation yet".
    let project (history: EventEnvelope list) : WorkspaceProjection =
        match history with
        | [] ->
            { State = None
              Capabilities = Capabilities.derive policy actor None
              Fault = None }
        | _ ->
            match Kernel.replayState history with
            | Ok state ->
                { State = Some state
                  Capabilities = Capabilities.derive policy actor (Some state)
                  Fault = None }
            | Error error ->
                { State = None
                  Capabilities = []
                  Fault = Some(sprintf "The history does not replay: %A" error) }

module View =
    /// The engine identity the page and the smoke tests read (DEP-CURRENT-004 Limen startup).
    [<Literal>]
    let EngineName = "causa"

    let initial: ShellState =
        { Booted = false
          Path = "/"
          Hash = ""
          Workspace = Workspace.empty }

    let private lifecycleName (lifecycle: InvestigationLifecycle) =
        match lifecycle with
        | Draft -> "Draft"
        | Active -> "Active"
        | AwaitingEvidence -> "Awaiting evidence"
        | AwaitingVerification -> "Awaiting verification"
        | Resolved -> "Resolved"
        | Closed -> "Closed"
        | Archived -> "Archived"

    let private kindOf (event: EventEnvelope) =
        let (EventPayload kind) = event.Payload
        kind

    let private versionOf (state: InvestigationState option) =
        match state with
        | Some s ->
            let (AggregateVersion v) = s.Version
            float v
        | None -> 0.0

    let private blockerText (blockers: Blocker list) =
        blockers |> List.map (fun b -> $"{b.Message} ({b.Code})") |> String.concat " "

    /// Pure: a refused decision as the sentence the page shows, with the kernel's reason.
    let refusalText (decision: Decision) =
        match decision with
        | Allowed _ -> "Allowed."
        | Blocked blockers -> blockerText blockers
        | RequiresReview reasons -> "Needs review: " + String.concat " " reasons
        | UnsupportedSemantics names -> "Unsupported semantics: " + String.concat ", " names
        | Conflict(AggregateVersion e, AggregateVersion a) ->
            $"The investigation changed: expected version {e}, found version {a} (version-conflict)."
        | Duplicate _ -> "This command was already applied (duplicate)."

    let private outcomeKeys (last: Outcome option) : ShellView =
        let state, text =
            match last with
            | None -> "none", "No command sent yet."
            | Some(Accepted(command, events)) ->
                let kinds = events |> List.map kindOf |> String.concat ", "
                "accepted", $"{Command.name command} accepted: {kinds}."
            | Some(Refused(command, decision)) -> "refused", $"{Command.name command} refused: {refusalText decision}"
        [ "outcomeState", Text state; "outcomeText", Text text ]

    let private stateKeys (projection: WorkspaceProjection) (history: EventEnvelope list) : ShellView =
        let count f =
            projection.State |> Option.map f |> Option.defaultValue 0 |> float

        let lifecycle =
            projection.State
            |> Option.map (fun s -> lifecycleName s.Lifecycle)
            |> Option.defaultValue "No investigation"

        [ "projectionVersion", Number(versionOf projection.State)
          "investigationLifecycle", Text lifecycle
          "timelineCount", Number(float history.Length)
          "timelineLatest", Text(history |> List.tryLast |> Option.map kindOf |> Option.defaultValue "No events")
          "observationCount", Number(count _.Observations)
          "conclusionCount", Number(count _.Conclusions)
          "projectionFault", Text(projection.Fault |> Option.defaultValue "")
          "projectionFaultHidden", Flag projection.Fault.IsNone ]

    let private capabilityText (projection: WorkspaceProjection) (command: Command) =
        match projection.Capabilities |> List.tryFind (fun c -> c.Command = command) with
        | Some { Availability = Available } -> "Available"
        | Some { Availability = Unavailable blockers } -> "Unavailable: " + blockerText blockers
        | None -> "Unavailable: the investigation could not be read."

    /// One key per offered command: "Available", or the kernel's reason it is not.
    let private capabilityKeys (projection: WorkspaceProjection) : ShellView =
        Workspace.offered
        |> List.map (fun command -> "capability" + Command.name command, Text(capabilityText projection command))

    /// Pure: the workspace's keys, all from one projection of one history.
    let renderWorkspace (workspace: Workspace) : ShellView =
        let projection = Workspace.project workspace.History
        stateKeys projection workspace.History
        @ capabilityKeys projection
        @ outcomeKeys workspace.Last

    let private shellKeys (hash: string) : ShellView =
        Views.Shell.projectHash hash
        |> List.map (fun (name, value) ->
            match value with
            | Views.Str s -> name, Text s
            | Views.Bit b -> name, Flag b)

    /// Pure: state to the complete view.
    let render (state: ShellState) : ShellView =
        [ "engine", Text EngineName
          "engineReady", Flag state.Booted
          "engineLoading", Flag(not state.Booted)
          "status", Text(if state.Booted then "Causa engine ready" else "Loading Causa")
          "path", Text state.Path ]
        @ shellKeys state.Hash
        @ renderWorkspace state.Workspace
