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
    /// A Limen data-each list: one flat record of string fields per row (WI-R8-T23 graph lists).
    | Rows of (string * string) list list

/// One complete Limen ViewState: every bound key, every time (never a patch).
type ShellView = (string * ViewValue) list

/// The tab-local investigation: the events the kernel allowed, in order, and the last answer. Session-local: it is
/// not saved anywhere (persistence is wired in later tasks), and the page says so.
type Workspace =
    { History: EventEnvelope list
      Submissions: int
      Last: SubmitOutcome option }

/// What the wire layer knows: has Limen initialized us, where are we, and the tab's workspace.
type ShellState =
    { Booted: bool
      Path: string
      Hash: string
      Workspace: Workspace
      /// WI-R8-T28: the version the history region shows (read-only time travel; it never changes Workspace).
      History: Views.History.Selection }

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

module View =
    /// The engine identity the page and the smoke tests read (DEP-CURRENT-004 Limen startup).
    [<Literal>]
    let EngineName = "causa"

    let initial: ShellState =
        { Booted = false
          Path = "/"
          Hash = ""
          Workspace = Workspace.empty
          History = Views.History.Latest }

    /// The page link of the tab-local investigation (it lives on the start screen until it is saved).
    let private tabInvestigationHref = Routes.format AppRoute.InvestigationStart

    let private tabOf (hash: string) =
        match Routes.parse hash with
        | AppRoute.Investigation(_, _, tab) -> Some tab
        | _ -> None

    /// Pure: the workspace's keys (Views/Investigation.fs, WI-R8-T22), all from one projection of one history, with
    /// the save indicator and header of a tab that has no repository connected (its changes exist only in this tab).
    let renderWorkspace (hash: string) (workspace: Workspace) : Views.ShellKeys =
        let changes = workspace.History.Length
        let notSaved = if changes = 0 then [] else [ Workspace.investigation, tabInvestigationHref ]

        Views.Investigation.keys Workspace.policy Workspace.actor (tabOf hash) workspace.History workspace.Last
        @ Views.SaveIndicator.keys (Views.SaveIndicator.forTab Workspace.investigation changes)
        @ Views.WorkspaceHeader.keys (Views.WorkspaceHeader.forTab Disconnected false notSaved)
        @ Views.ConflictReview.keys None

    let private flat (keys: Views.ShellKeys) : ShellView =
        keys
        |> List.map (fun (name, value) ->
            match value with
            | Views.Str s -> name, Text s
            | Views.Bit b -> name, Flag b)

    let private shellKeys (hash: string) : ShellView = flat (Views.Shell.projectHash hash)

    /// The causal graph's keys and lists (Views/Graph.fs, ui-2), from the same workspace history the investigation
    /// keys replay (Workspace.History, unchanged by WI-R8-T22's restructuring).
    let private graphKeys (workspace: Workspace) : ShellView =
        let graph = Views.Graph.project workspace.History
        flat graph.Keys @ (graph.Lists |> List.map (fun (name, rows) -> name, Rows rows))

    /// The report preview's keys and lists (Views/Report.fs, ui-2, WI-R8-T26), from the same history the workspace replays.
    let private reportKeys (workspace: Workspace) : ShellView =
        let report = Views.Report.view Workspace.policy Workspace.actor workspace.History
        flat report.Keys @ (report.Lists |> List.map (fun (name, rows) -> name, Rows rows))
    /// History, time travel and semantic diff (Views/History.fs, ui-2, WI-R8-T28): the same workspace history replayed
    /// to the selected version; read-only.
    let private historyKeys (state: ShellState) : ShellView =
        let history = Views.History.project Workspace.policy Workspace.actor state.Workspace.History state.History
        flat history.Keys @ (history.Lists |> List.map (fun (name, rows) -> name, Rows rows))

    /// Pure: state to the complete view.
    let render (state: ShellState) : ShellView =
        [ "engine", Text EngineName
          "engineReady", Flag state.Booted
          "engineLoading", Flag(not state.Booted)
          "status", Text(if state.Booted then "Causa engine ready" else "Loading Causa")
          "path", Text state.Path ]
        @ shellKeys state.Hash
        @ flat (renderWorkspace state.Hash state.Workspace)
        @ graphKeys state.Workspace
        @ reportKeys state.Workspace
        @ historyKeys state
