// Owner: integration. The engine's wire edge (Limen protocol 1). Pure decoding, a pure step function, pure encoding;
// the one mutable cell below is the effect edge the host shim calls through.
namespace Causa.App

open System
open System.IO
open System.Text
open System.Text.Json
open Causa.Domain

/// WI-R8-CAPS: one generated Limen contract unit or capability as the handshake names it (id, version, fingerprint).
type LimenIdentity = { Id: string; Version: int; Fingerprint: string }

/// WI-R8-CAPS: what the host offered in Initialize (HostHandshake): its protocol revision, core contract and the
/// optional capabilities it implements. An offer is not permission; the engine selects what it uses.
type HostOffer =
    { Major: int
      Minor: int
      Contract: LimenIdentity
      Capabilities: LimenIdentity list }

/// WI-R8-CAPS: the engine's answer to the offer (EngineHandshake Accepted): the revision it speaks and what it selected.
type Handshake =
    { Major: int
      Minor: int
      Contract: LimenIdentity
      Selected: LimenIdentity list }

/// WI-R8-CAPS: a capability's answer, reduced to what the engine acts on.
[<RequireQualifiedAccess>]
type CapabilityAnswer =
    /// limen.schedule: the timeout elapsed.
    | Fired
    /// limen.files: the browser was handed the file.
    | Downloaded
    /// Anything else (Cancelled, NeedsGesture, Unsupported, Rejected, ...), by its kind.
    | NotPerformed of kind: string

/// The Limen messages the engine answers (BrowserToEngineMessage), decoded to data.
[<RequireQualifiedAccess>]
type Incoming =
    | Initialize of path: string * hash: string * offer: HostOffer option
    | Event of name: string * value: string option
    | LocationChanged of path: string * hash: string
    /// WI-R8-T22B: an Http effect's answer (LimenHttp.outcomeOf); None for any other effect result.
    | EffectResult of Causa.Persistence.HttpOutcome option
    /// WI-R8-CAPS: an optional capability's answer (EffectResult of kind CapabilityResult).
    | CapabilityResult of correlationId: string * capability: string * answer: CapabilityAnswer
    | CapabilityFact
    | Unreadable of reason: string

/// WI-R8-CAPS: an effect as Limen performs it: an Http effect, or a request to a negotiated capability.
[<RequireQualifiedAccess>]
type WireEffect =
    | Http of Causa.Persistence.HttpRequest
    /// limen.schedule `timeout`.
    | Timeout of correlationId: string * delayMs: int
    /// limen.files `download` (UTF-8 text).
    | Download of correlationId: string * fileName: string * mimeType: string * content: string

/// The answer to one message (EngineToBrowserMessage). Effects are requested, never performed here. The handshake is
/// present only in the answer to Initialize.
type Outgoing =
    { View: ShellView
      Effects: WireEffect list
      Cancellations: string list
      Handshake: Handshake option }

/// WI-R8-CAPS: the Limen identities this engine was built against (Limen 0.9.1) and the pure handshake answer.
[<RequireQualifiedAccess>]
module LimenHandshake =
    /// The newest protocol minor this engine decodes (1.4: OutcomeUnknown connection-lost, read as LostAfterSending).
    [<Literal>]
    let ProtocolMinor = 4

    let core =
        { Id = "limen.core"; Version = 1; Fingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c" }

    let schedule =
        { Id = "limen.schedule"; Version = 1; Fingerprint = "sha256:627657798f0fa735ac6f955eb4f413e7c397f0aef6942b340a87683a30db54d0" }

    let files =
        { Id = "limen.files"; Version = 1; Fingerprint = "sha256:2cf28b1016994b11955c412282ec6038a6c8c205a056b14c6039ef6fd5bfabdf" }

    /// The capabilities this engine uses, when the host offers exactly these identities.
    let wanted = [ schedule; files ]

    /// Pure: Accepted with the capabilities offered exactly as this engine knows them, when the host speaks protocol 1
    /// and the same core contract. Any other host gets no handshake (Limen's legacy mode: Http only, no capability),
    /// so the page still works and the edge drops what it cannot request.
    let answer (offer: HostOffer option) : Handshake option =
        offer
        |> Option.filter (fun o -> o.Major = 1 && o.Contract = core)
        |> Option.map (fun o ->
            { Major = 1
              Minor = min o.Minor ProtocolMinor
              Contract = core
              Selected = wanted |> List.filter (fun c -> List.contains c o.Capabilities) })

    let selectedIds (handshake: Handshake option) : Set<string> =
        handshake |> Option.map (fun h -> h.Selected |> List.map _.Id |> Set.ofList) |> Option.defaultValue Set.empty

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
      Notice: string
      /// WI-R8-CAPS: the capabilities the handshake selected (empty with a legacy host).
      Capabilities: Set<string>
      /// WI-R8-CAPS: the next capability correlation number of this tab.
      NextCorrelation: int64
      /// WI-R8-CAPS: the engine timer each pending limen.schedule request stands for.
      Timers: Map<string, TimerKey>
      /// WI-R8-T32W: the body of each domain command this tab sent to a wired investigation, by its command identity.
      /// The frozen CommandEnvelope (Msg.DomainCommand) carries no body (DEV-R8-09), so the tab keeps it here and the
      /// engine decides with it (AppEngine.Config.CommandBody) instead of as Kernel.bareCommand.
      Commands: Map<Guid, Command> }

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

    let private tryInt name e =
        tryProp name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Number)
        |> Option.bind (fun v ->
            match v.TryGetInt32() with
            | true, n -> Some n
            | _ -> None)

    let private identityOf (idName: string) (e: JsonElement) : LimenIdentity option =
        match tryString idName e, tryInt "version" e, tryString "fingerprint" e with
        | Some id, Some version, Some fingerprint -> Some { Id = id; Version = version; Fingerprint = fingerprint }
        | _ -> None

    /// Pure: the HostHandshake of an Initialize; None when absent or not readable.
    let private offerOf (root: JsonElement) : HostOffer option =
        tryProp "handshake" root
        |> Option.bind (fun h ->
            let protocol = tryProp "protocol" h
            let capabilities =
                match tryProp "capabilities" h with
                | Some list when list.ValueKind = JsonValueKind.Array -> [ for c in list.EnumerateArray() -> identityOf "id" c ] |> List.choose id
                | _ -> []

            match protocol |> Option.bind (tryInt "major"), protocol |> Option.bind (tryInt "minor"), tryProp "contract" h |> Option.bind (identityOf "unit") with
            | Some major, Some minor, Some contract ->
                Some { Major = major; Minor = minor; Contract = contract; Capabilities = capabilities }
            | _ -> None)

    /// Pure: an EffectResult of kind CapabilityResult; None for any other result.
    let private capabilityResultOf (result: JsonElement) : Incoming option =
        match tryString "kind" result, tryString "correlationId" result, tryString "capability" result, tryProp "outcome" result with
        | Some "CapabilityResult", Some correlation, Some capability, Some outcome ->
            let answer =
                match tryString "kind" outcome with
                | Some "Completed" ->
                    match tryProp "result" outcome |> Option.bind (tryString "kind") with
                    | Some "Fired" -> CapabilityAnswer.Fired
                    | Some "Downloaded" -> CapabilityAnswer.Downloaded
                    | Some other -> CapabilityAnswer.NotPerformed other
                    | None -> CapabilityAnswer.NotPerformed "Completed"
                | Some other -> CapabilityAnswer.NotPerformed other
                | None -> CapabilityAnswer.NotPerformed "unknown"

            Some(Incoming.CapabilityResult(correlation, capability, answer))
        | _ -> None

    /// Pure: one JSON message to Incoming. Never throws.
    let decode (json: string) : Incoming =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            match tryString "kind" root with
            | Some "Initialize" ->
                let location = tryProp "location" root
                Incoming.Initialize(pathOf location, hashOf location, offerOf root)
            | Some "LocationChanged" ->
                let location = tryProp "location" root
                Incoming.LocationChanged(pathOf location, hashOf location)
            | Some "Event" ->
                let ev = tryProp "event" root
                let name = ev |> Option.bind (tryString "name") |> Option.defaultValue ""
                Incoming.Event(name, ev |> Option.bind (tryString "value"))
            | Some "EffectResult" ->
                let result = tryProp "result" root

                match result |> Option.bind capabilityResultOf with
                | Some capability -> capability
                | None -> Incoming.EffectResult(result |> Option.bind LimenHttp.outcomeOf)
            | Some "CapabilityFact" -> Incoming.CapabilityFact
            | Some other -> Incoming.Unreadable $"unknown message kind {other}"
            | None -> Incoming.Unreadable "message has no kind"
        with :? JsonException as ex ->
            Incoming.Unreadable ex.Message

    /// Pure: the next shell state. Unknown or unreadable input leaves the state unchanged.
    let stepShell (state: ShellState) (message: Incoming) : ShellState =
        match message with
        | Incoming.Initialize(path, hash, _)
        | Incoming.LocationChanged(path, hash) ->
            { state with
                Booted = true
                Path = path
                Hash = if Routes.isRouteHash hash then hash else state.Hash }
        | Incoming.Event((Views.History.SelectEvent | Views.History.LatestEvent) as name, value) ->
            { state with History = Views.History.select state.Workspace.History state.History name value }
        | Incoming.Event(name, value) when name = Views.Administration.ReportEvent ->
            { state with Loaded = Views.Administration.report value }
        | Incoming.Event(name, _) ->
            match Workspace.commandOfEvent name with
            | Some command -> { state with Workspace = Submit.command state.Workspace command }
            | None -> state
        | Incoming.EffectResult _
        | Incoming.CapabilityResult _
        | Incoming.CapabilityFact
        | Incoming.Unreadable _ -> state

    /// Pure: the next shell state and its answer (the tab-local shell alone; the wired tab is stepTab).
    let step (state: ShellState) (message: Incoming) : ShellState * Outgoing =
        let next = stepShell state message
        next,
        { View = View.render next
          Effects = []
          Cancellations = []
          Handshake =
            match message with
            | Incoming.Initialize(_, _, offer) -> LimenHandshake.answer offer
            | _ -> None }

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
          Notice = ""
          Capabilities = Set.empty
          NextCorrelation = 1L
          Timers = Map.empty
          Commands = Map.empty }

    let private investigationOf (text: string) =
        match Guid.TryParse(text.Trim()) with
        | true, g -> Some(InvestigationId g)
        | _ -> None

    /// WI-R8-T32W: the engine's config with the bodies of the commands this tab sent (pure lookup into Tab.Commands).
    let configOf (tab: Tab) : AppEngine.Config =
        { config with CommandBody = fun id -> Map.tryFind id tab.Commands }

    /// WI-R8-T32W / T33-F-07: the bodies of the commands the engine's sessions still hold (unconfirmed), nothing else.
    let retained (tab: Tab) : Map<Guid, Command> =
        let held =
            tab.Engine.Sessions |> Map.toList |> List.collect (snd >> AppEngine.unconfirmed) |> List.map _.CommandId |> Set.ofList

        tab.Commands |> Map.filter (fun id _ -> held.Contains id)

    let private run (tab: Tab) (msg: Msg) =
        let model, effects = AppEngine.update (configOf tab) msg tab.Engine
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
        | "connect.disconnect" ->
            // T33-F-07: the engine decides the disconnect first; only bodies of commands no remaining session holds
            // are dropped, so a disconnect that fails closed (Connecting, Reauthenticating) keeps every body.
            let disconnected, effects = run tab (Connection ConnectionInput.DisconnectConfirmed)

            match disconnected.Engine.Context with
            | None -> { disconnected with Active = None; Creation = None; Commands = retained disconnected }, effects
            | Some _ -> disconnected, effects
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
            // A command for a wired investigation goes to its save session (DomainCommand, CR-T17-01). The frozen
            // envelope carries no body (DEV-R8-09), so the tab keeps the body by the command's identity and the engine
            // decides it in full (WI-R8-T32W), never as Kernel.bareCommand.
            match Workspace.commandOfEvent name, tab.Active with
            | Some command, Some id when tab.Engine.Sessions.ContainsKey id ->
                let envelope: CommandEnvelope = { CommandId = newCommandId (); Investigation = id; ExpectedVersion = None }
                run { tab with Commands = Map.add envelope.CommandId command tab.Commands } (DomainCommand envelope)
            | _ -> tab, []

    let private onOutcome (tab: Tab) (outcome: Causa.Persistence.HttpOutcome) : Tab * HostEffect list =
        match tab.Creation with
        | Some creation ->
            let model, next, effects = Create.answer (configOf tab) creation outcome tab.Engine

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

    /// WI-R8-CAPS: a capability's answer. A fired reconcile timer is the engine's TimerFired; any other end of a timer
    /// (Cancelled, Unsupported) changes nothing. A download's answer is told on the page.
    let private onCapability (tab: Tab) (correlation: string) (capability: string) (answer: CapabilityAnswer) : Tab * HostEffect list =
        match Map.tryFind correlation tab.Timers, answer with
        | Some key, CapabilityAnswer.Fired -> run { tab with Timers = Map.remove correlation tab.Timers } (Browser(TimerFired key))
        | Some _, _ -> { tab with Timers = Map.remove correlation tab.Timers }, []
        | None, _ when capability = LimenHandshake.files.Id ->
            match answer with
            | CapabilityAnswer.Downloaded -> { tab with Notice = "Your unsaved changes were downloaded." }, []
            | CapabilityAnswer.NotPerformed kind -> { tab with Notice = $"The export could not be downloaded ({kind})." }, []
            | CapabilityAnswer.Fired -> tab, []
        | None, _ -> tab, []

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
                        AppEngine.decideAllWith (configOf tab).CommandBody (configOf tab) (opened.History, Some opened.Investigation) (AppEngine.unconfirmed session)

                    history
                | None -> opened.History))

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

        let tab =
            match message, wiredHistory tab with
            | _ when wired -> tab
            // T28 history selection reads the history the page shows: the open investigation's, once wired.
            | Incoming.Event((Views.History.SelectEvent | Views.History.LatestEvent) as name, value), Some history ->
                { tab with Shell = { tab.Shell with History = Views.History.select history tab.Shell.History name value } }
            | _ -> { tab with Shell = stepShell tab.Shell message }

        match message with
        | Incoming.Initialize(_, _, offer) -> { tab with Capabilities = LimenHandshake.selectedIds (LimenHandshake.answer offer) }, []
        | Incoming.Event(name, value) -> onEvent newCommandId tab name value
        | Incoming.EffectResult(Some outcome) -> onOutcome tab outcome
        | Incoming.CapabilityResult(correlation, capability, answer) -> onCapability tab correlation capability answer
        | _ -> tab, []

    /// Pure (WI-R8-CAPS): the engine's host effects as Limen effects. A Timer becomes a limen.schedule timeout whose
    /// correlation the tab remembers, so its Fired answer comes back as the engine's TimerFired (the save machine's
    /// automatic reconciliation read, PERSIST-016); a Download becomes a limen.files download. Without the capability
    /// (a legacy host) a timer is dropped (the user still has Check again) and a download says it cannot be offered.
    /// The leave guard has no Limen mechanism (lifecycle has no beforeunload) and LiveAnnounce is a view key.
    let toWire (tab: Tab) (effects: HostEffect list) : Tab * WireEffect list =
        let correlation (t: Tab) (prefix: string) =
            $"{prefix}-{t.NextCorrelation}", { t with NextCorrelation = t.NextCorrelation + 1L }

        effects
        |> List.fold
            (fun (t: Tab, wire) effect ->
                match effect with
                | Http request -> t, wire @ [ WireEffect.Http request ]
                | Timer(key, ms) when t.Capabilities.Contains LimenHandshake.schedule.Id ->
                    let id, t = correlation t "schedule"
                    { t with Timers = Map.add id key t.Timers }, wire @ [ WireEffect.Timeout(id, max 0 ms) ]
                | Download(fileName, content) when t.Capabilities.Contains LimenHandshake.files.Id ->
                    let id, t = correlation t "files"
                    t, wire @ [ WireEffect.Download(id, fileName, "application/json", content) ]
                | Download _ -> { t with Notice = "This browser cannot download the export." }, wire
                | Timer _
                | SetLeaveGuard _
                | LiveAnnounce _ -> t, wire)
            (tab, [])

    let private flat (keys: Views.ShellKeys) : ShellView =
        keys
        |> List.map (fun (name, value) ->
            match value with
            | Views.Str s -> name, Text s
            | Views.Bit b -> name, Flag b)

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

        // WI-R8-T29: Administration and Settings report the engine's connection state, never the shell's default.
        let admin = View.administrationKeys tab.Engine.Connection shellState
        let adminNames = admin |> List.map fst |> Set.ofList
        let shell =
            View.render shellState |> List.filter (fun (k, _) -> not (replaced.Contains k || adminNames.Contains k))
        let shellNames = shell |> List.map fst |> Set.ofList
        let added = engineKeys |> List.filter (fun (k, _) -> not (shellNames.Contains k || adminNames.Contains k))
        // WI-R8-CAPS: the token fields are mounted (data-if) only while a token is being typed for Connect
        // (Disconnected) or Reconnect (CredentialRefused). Once Connect or Reconnect has made the credential the field
        // leaves the DOM, so the typed token does not linger there, and it returns empty.
        let tokenFields =
            [ "connectTokenShown", Flag(tab.Engine.Connection = Disconnected)
              "reconnectTokenShown", Flag(tab.Engine.Connection = CredentialRefused) ]

        shell @ admin @ added @ tokenFields @ [ "liveAnnouncement", Text(String.concat " " announcements) ]

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

    let private identity (w: Utf8JsonWriter) (idName: string) (i: LimenIdentity) =
        w.WriteString(idName, i.Id)
        w.WriteNumber("version", i.Version)
        w.WriteString("fingerprint", i.Fingerprint)

    /// One Capability effect request: correlation, capability identity and the pack's request object.
    let private capability (w: Utf8JsonWriter) (correlation: string) (c: LimenIdentity) (request: unit -> unit) =
        w.WriteStartObject()
        w.WriteString("kind", "Capability")
        w.WriteString("correlationId", correlation)
        w.WriteString("capability", c.Id)
        w.WriteNumber("version", c.Version)
        w.WriteStartObject "request"
        request ()
        w.WriteEndObject()
        w.WriteEndObject()

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
            // Http effects are Limen Core; timers and downloads are requests to the negotiated capabilities (toWire).
            answer.Effects
            |> List.iter (function
                | WireEffect.Http request -> LimenHttp.writeRequest w request
                | WireEffect.Timeout(id, ms) ->
                    capability w id LimenHandshake.schedule (fun () ->
                        w.WriteString("operation", "timeout")
                        w.WriteNumber("delayMs", ms))
                | WireEffect.Download(id, fileName, mimeType, content) ->
                    capability w id LimenHandshake.files (fun () ->
                        w.WriteString("operation", "download")
                        w.WriteString("fileName", fileName)
                        w.WriteString("mimeType", mimeType)
                        w.WriteString("format", "text")
                        w.WriteString("data", content)))
            w.WriteEndArray()
            w.WriteStartArray "cancellations"
            answer.Cancellations |> List.iter w.WriteStringValue
            w.WriteEndArray()
            answer.Handshake
            |> Option.iter (fun h ->
                w.WriteStartObject "handshake"
                w.WriteString("kind", "Accepted")
                w.WriteStartObject "protocol"
                w.WriteNumber("major", h.Major)
                w.WriteNumber("minor", h.Minor)
                w.WriteEndObject()
                w.WriteStartObject "contract"
                identity w "unit" h.Contract
                w.WriteEndObject()
                w.WriteStartArray "capabilities"
                h.Selected
                |> List.iter (fun c ->
                    w.WriteStartObject()
                    identity w "id" c
                    w.WriteEndObject())
                w.WriteEndArray()
                w.WriteEndObject())
            w.WriteEndObject()
        )
        Encoding.UTF8.GetString(stream.ToArray())

    // The effect edge: the only mutable value in the engine's wire layer. Limen dispatches one message at a time.
    let mutable private current = initialTab

    /// Called by Causa.Host.HostShim.Dispatch for every Limen message.
    let dispatch (json: string) : string =
        let message = decode json
        let stepped, effects = stepTab Guid.NewGuid current message
        let next, wire = toWire stepped effects
        current <- next
        let announcements = effects |> List.choose (function LiveAnnounce(_, text) -> Some text | _ -> None)

        let handshake =
            match message with
            | Incoming.Initialize(_, _, offer) -> LimenHandshake.answer offer
            | _ -> None

        encode
            { View = renderTab next announcements
              Effects = wire
              Cancellations = []
              Handshake = handshake }
