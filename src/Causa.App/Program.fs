// Owner: integration. The engine's wire edge (Limen protocol 1). Pure decoding, a pure step function, pure encoding;
// the one mutable cell below is the effect edge the host shim calls through.
namespace Causa.App

open System
open System.IO
open System.Text
open System.Text.Json
open Causa.Domain

/// The Limen messages the engine answers (BrowserToEngineMessage), decoded to data.
[<RequireQualifiedAccess>]
type Incoming =
    | Initialize of path: string * hash: string
    | Event of name: string * value: string option
    | LocationChanged of path: string * hash: string
    /// WI-R8-T22B: an Http effect's answer (LimenHttp.outcomeOf); None for any other effect result.
    | EffectResult of Causa.Persistence.HttpOutcome option
    | CapabilityFact
    | Unreadable of reason: string

/// The answer to one message (EngineToBrowserMessage). Effects are requested, never performed here.
type Outgoing = { View: ShellView; Effects: HostEffect list; Cancellations: string list }

/// WI-R8-T22B (CR-T22B-1): the tab beside the shell: the engine's Model (IF41-APP), an explicit create in flight and
/// the Connect / investigation form drafts. The token draft exists only here, in this tab's memory, until Connect
/// turns it into a credential; it is never rendered, logged or stored.
type Tab =
    { Shell: ShellState
      Engine: Model
      Creation: Creation option
      Active: InvestigationId option
      Owner: string
      Repository: string
      Branch: string
      Token: string
      InvestigationText: string
      Notice: string }

/// WI-R8-T17: a typed command from the page through the domain kernel, decided by Update.submit (ui, WI-R8-T22;
/// CR-T17-01): Kernel.decideOn against the tab's history, an allowed decision's events stamped and appended; a refusal
/// changes nothing but the answer the page shows.
[<RequireQualifiedAccess>]
module Submit =
    let command (workspace: Workspace) (command: Command) : Workspace =
        let envelope =
            Update.envelope (Workspace.commandId (workspace.Submissions + 1)) Workspace.investigation workspace.History

        let history, outcome = Update.submit Workspace.policy Workspace.actor workspace.History envelope command

        { History = history
          Submissions = workspace.Submissions + 1
          Last = Some outcome }

module Program =
    let private tryProp (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private tryString name e =
        tryProp name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.map (fun v -> v.GetString() |> Option.ofObj |> Option.defaultValue "")

    let private pathOf (location: JsonElement option) =
        location |> Option.bind (tryString "path") |> Option.defaultValue "/"

    let private hashOf (location: JsonElement option) =
        location |> Option.bind (tryString "hash") |> Option.defaultValue ""

    /// Pure: one JSON message to Incoming. Never throws.
    let decode (json: string) : Incoming =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            match tryString "kind" root with
            | Some "Initialize" ->
                let location = tryProp "location" root
                Incoming.Initialize(pathOf location, hashOf location)
            | Some "LocationChanged" ->
                let location = tryProp "location" root
                Incoming.LocationChanged(pathOf location, hashOf location)
            | Some "Event" ->
                let ev = tryProp "event" root
                let name = ev |> Option.bind (tryString "name") |> Option.defaultValue ""
                Incoming.Event(name, ev |> Option.bind (tryString "value"))
            | Some "EffectResult" -> Incoming.EffectResult(tryProp "result" root |> Option.bind LimenHttp.outcomeOf)
            | Some "CapabilityFact" -> Incoming.CapabilityFact
            | Some other -> Incoming.Unreadable $"unknown message kind {other}"
            | None -> Incoming.Unreadable "message has no kind"
        with :? JsonException as ex ->
            Incoming.Unreadable ex.Message

    /// Pure: the next shell state. Unknown or unreadable input leaves the state unchanged.
    let stepShell (state: ShellState) (message: Incoming) : ShellState =
        match message with
        | Incoming.Initialize(path, hash)
        | Incoming.LocationChanged(path, hash) ->
            { state with
                Booted = true
                Path = path
                Hash = if Routes.isRouteHash hash then hash else state.Hash }
        | Incoming.Event(name, _) ->
            match Workspace.commandOfEvent name with
            | Some command -> { state with Workspace = Submit.command state.Workspace command }
            | None -> state
        | Incoming.EffectResult _
        | Incoming.CapabilityFact
        | Incoming.Unreadable _ -> state

    /// Pure: the next shell state and its answer (the tab-local shell alone; the wired tab is stepTab).
    let step (state: ShellState) (message: Incoming) : ShellState * Outgoing =
        let next = stepShell state message
        next, { View = View.render next; Effects = []; Cancellations = [] }

    /// The engine of this tab (a fresh tab identifier for its write markers, PERSIST-014).
    let private config = AppEngine.configFor (Guid.NewGuid())
    let private engine = AppEngine.create config

    let initialTab: Tab =
        { Shell = View.initial
          Engine = fst (engine.Init(Route "/"))
          Creation = None
          Active = None
          Owner = ""
          Repository = ""
          Branch = "main"
          Token = ""
          InvestigationText = ""
          Notice = "" }

    let private investigationOf (text: string) =
        match Guid.TryParse(text.Trim()) with
        | true, g -> Some(InvestigationId g)
        | _ -> None

    let private run (tab: Tab) (msg: Msg) =
        let model, effects = engine.Update msg tab.Engine
        { tab with Engine = model }, effects

    let private onActive (tab: Tab) (f: InvestigationId -> Msg) =
        match tab.Active with
        | Some id -> run tab (f id)
        | None -> tab, []

    /// The page's persistence events (index.html, CR-T22B-2) to engine messages. `newCommandId` is the edge's
    /// identity source for a command sent to a wired investigation.
    let private onEvent (newCommandId: unit -> Guid) (tab: Tab) (name: string) (value: string option) : Tab * HostEffect list =
        let v = value |> Option.defaultValue ""

        match name with
        | "connect.owner" -> { tab with Owner = v.Trim() }, []
        | "connect.repository" -> { tab with Repository = v.Trim() }, []
        | "connect.branch" -> { tab with Branch = v.Trim() }, []
        | "connect.token" -> { tab with Token = v }, []
        | "connect.submit" ->
            match ConnectionMachine.credentialOf tab.Token, tab.Owner, tab.Repository with
            | Some credential, owner, repo when owner <> "" && repo <> "" ->
                let repository: Causa.Persistence.Repository = { Owner = owner; Name = repo; Branch = (if tab.Branch = "" then "main" else tab.Branch) }
                run { tab with Token = ""; Notice = "" } (Connection(ConnectionInput.ConnectSubmitted(repository, credential)))
            | _ -> { tab with Notice = "Enter the repository owner, name and a token to connect." }, []
        | "reconnect.submit" ->
            match ConnectionMachine.credentialOf tab.Token with
            | Some credential -> run { tab with Token = "" } (Connection(ConnectionInput.ReconnectSubmitted credential))
            | None -> tab, []
        | "connect.disconnect" -> run { tab with Active = None; Creation = None } (Connection ConnectionInput.DisconnectConfirmed)
        | "investigation.id" -> { tab with InvestigationText = v }, []
        | "investigation.open" ->
            match investigationOf tab.InvestigationText with
            | Some id -> run { tab with Active = Some id; Notice = "" } (LoadStep(id, Causa.Persistence.LoadInput.OpenRequested id))
            | None -> { tab with Notice = "Enter the investigation's identifier (a UUID)." }, []
        | "investigation.create" ->
            let id = investigationOf tab.InvestigationText |> Option.defaultWith (fun () -> InvestigationId(newCommandId ()))
            let model, creation, effects = Create.start id tab.Engine

            { tab with
                Engine = model
                Creation = Some creation
                Active = Some id
                InvestigationText = (let (InvestigationId g) = id in g.ToString "D")
                Notice = "Creating the investigation on GitHub." },
            effects
        | "save.Save"
        | "save.Retry" -> onActive tab (fun id -> SessionInput(id, Causa.Persistence.SaveInput.SaveRequested))
        | "save.CheckAgain" -> onActive tab (fun id -> SessionInput(id, Causa.Persistence.SaveInput.CheckAgain))
        | "save.Reconnect" -> { tab with Notice = "Enter a new token and choose Reconnect." }, []
        | "save.Review" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.ReviewChosen))
        | "save.ReapplyMine"
        | "conflict.Reapply" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.ReapplyChosen))
        | "save.DiscardMine"
        | "conflict.Discard" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.DiscardChosen))
        | "conflict.DiscardConfirm" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.DiscardConfirmed))
        | "conflict.DiscardCancel" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.DiscardCancelled))
        | "save.ExportMine"
        | "conflict.Export" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.ExportChosen))
        | "conflict.Retry" -> onActive tab (fun id -> ConflictStep(id, Causa.Persistence.ConflictInput.ReadAgain))
        | _ ->
            // A command for a wired investigation goes to its save session (DomainCommand, CR-T17-01: the frozen
            // envelope carries no body, so the kernel decides it as Kernel.bareCommand, DEV-R8-09).
            match Workspace.commandOfEvent name, tab.Active with
            | Some _, Some id when tab.Engine.Sessions.ContainsKey id ->
                let envelope: CommandEnvelope = { CommandId = newCommandId (); Investigation = id; ExpectedVersion = None }
                run tab (DomainCommand envelope)
            | _ -> tab, []

    let private onOutcome (tab: Tab) (outcome: Causa.Persistence.HttpOutcome) : Tab * HostEffect list =
        match tab.Creation with
        | Some creation ->
            let model, next, effects = Create.answer config creation outcome tab.Engine

            if next <> creation then
                let notice =
                    match next with
                    | CreateRefused(_, reason) -> reason
                    | CreateOpened _ -> "Created on GitHub; opening it."
                    | _ -> tab.Notice

                { tab with Engine = model; Creation = Some next; Notice = notice }, effects
            else
                run tab (Browser(HttpCompleted outcome))
        | None -> run tab (Browser(HttpCompleted outcome))

    /// The next tab and the effects the engine asks for. Impure only through `newCommandId` (the edge's identities).
    let stepTab (newCommandId: unit -> Guid) (tab: Tab) (message: Incoming) : Tab * HostEffect list =
        // A command for a wired investigation is decided by its save session alone, never also by the tab-local
        // workspace (CRT22B review).
        let wired =
            match message with
            | Incoming.Event(name, _) ->
                (Workspace.commandOfEvent name).IsSome
                && (tab.Active |> Option.exists tab.Engine.Sessions.ContainsKey)
            | _ -> false

        let tab = if wired then tab else { tab with Shell = stepShell tab.Shell message }

        match message with
        | Incoming.Event(name, value) -> onEvent newCommandId tab name value
        | Incoming.EffectResult(Some outcome) -> onOutcome tab outcome
        | _ -> tab, []

    let private flat (keys: Views.ShellKeys) : ShellView =
        keys
        |> List.map (fun (name, value) ->
            match value with
            | Views.Str s -> name, Text s
            | Views.Bit b -> name, Flag b)

    /// The history the page shows for the open investigation: the verified base read from GitHub plus the kernel's
    /// events for the commands not yet confirmed (AppEngine.decideAll). None until the engine has loaded it.
    let private wiredHistory (tab: Tab) : EventEnvelope list option =
        tab.Active
        |> Option.bind (fun id ->
            AppEngine.baseOf tab.Engine id
            |> Option.map (fun opened ->
                match Map.tryFind id tab.Engine.Sessions with
                | Some session ->
                    let _, history, _ =
                        AppEngine.decideAll config (opened.History, Some opened.Investigation) (AppEngine.unconfirmed session)

                    history
                | None -> opened.History))

    /// Pure: the complete view. The engine's keys replace the tab-local indicator, header and review once an
    /// investigation is open in the engine; the Connect form keys are always the engine's.
    let renderTab (tab: Tab) (announcements: string list) : ShellView =
        let engineKeys = flat (Views.EngineView.keys tab.Engine tab.Active tab.Notice)
        // The connection and network states are the engine's even before an investigation is open (CRT22B review:
        // the header said "Not connected" beside a connected Connect form).
        let alwaysEngine = set [ "headerConnection"; "headerOfflineHidden"; "headerOfflineText" ]
        let replaced = if tab.Active.IsSome then engineKeys |> List.map fst |> Set.ofList else alwaysEngine
        let shellState =
            match wiredHistory tab with
            | Some history -> { tab.Shell with Workspace = { tab.Shell.Workspace with History = history; Last = None } }
            | None -> tab.Shell

        let shell = View.render shellState |> List.filter (fun (k, _) -> not (replaced.Contains k))
        let shellNames = shell |> List.map fst |> Set.ofList
        let added = engineKeys |> List.filter (fun (k, _) -> not (shellNames.Contains k))
        shell @ added @ [ "liveAnnouncement", Text(String.concat " " announcements) ]

    let private writeValue (w: Utf8JsonWriter) (name: string) (value: ViewValue) =
        match value with
        | Text s -> w.WriteString(name, s)
        | Number n -> w.WriteNumber(name, n)
        | Flag b -> w.WriteBoolean(name, b)
        | Rows rows ->
            w.WriteStartArray name
            rows
            |> List.iter (fun fields ->
                w.WriteStartObject()
                fields |> List.iter (fun (field, text) -> w.WriteString(field, text))
                w.WriteEndObject())
            w.WriteEndArray()

    /// Pure: the answer as Limen JSON.
    let encode (answer: Outgoing) : string =
        use stream = new MemoryStream()
        (
            use w = new Utf8JsonWriter(stream)
            w.WriteStartObject()
            w.WriteStartObject "view"
            answer.View |> List.iter (fun (name, value) -> writeValue w name value)
            w.WriteEndObject()
            w.WriteStartArray "effects"
            // Http effects go to the Limen kernel; Timer, Download and the leave guard have no Limen effect in
            // protocol 1 without a capability (see CR-T22B-1 notes); LiveAnnounce is rendered as a view key.
            answer.Effects
            |> List.iter (function
                | Http request -> LimenHttp.writeRequest w request
                | Timer _
                | Download _
                | SetLeaveGuard _
                | LiveAnnounce _ -> ())
            w.WriteEndArray()
            w.WriteStartArray "cancellations"
            answer.Cancellations |> List.iter w.WriteStringValue
            w.WriteEndArray()
            w.WriteEndObject()
        )
        Encoding.UTF8.GetString(stream.ToArray())

    // The effect edge: the only mutable value in the engine's wire layer. Limen dispatches one message at a time.
    let mutable private current = initialTab

    /// Called by Causa.Host.HostShim.Dispatch for every Limen message.
    let dispatch (json: string) : string =
        let next, effects = stepTab Guid.NewGuid current (decode json)
        current <- next
        let announcements = effects |> List.choose (function LiveAnnounce(_, text) -> Some text | _ -> None)
        encode { View = renderTab next announcements; Effects = effects; Cancellations = [] }
