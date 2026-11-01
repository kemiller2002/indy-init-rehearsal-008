// Owner: ui (WI-R8-T22). The engine's domain-command handling and its network/connection steps, as additive pure
// functions beside the frozen IF41-APP shapes (CR-T17-01: the frozen Msg.DomainCommand of CommandEnvelope cannot carry
// the command body, DEV-R8-09). Integration retires Program.fs's interim Submit by calling Update.submit.
namespace Causa.App

open Causa.Domain
open Causa.Persistence

/// The kernel's answer to one submitted command.
type SubmitOutcome =
    | CommandAccepted of command: Command * events: EventEnvelope list
    | CommandRefused of command: Command * decision: Decision

[<RequireQualifiedAccess>]
module Update =
    /// The envelope a command is sent in: the expected version is the version of the history it was decided on.
    let envelope (commandId: System.Guid) (investigation: InvestigationId) (history: EventEnvelope list) : CommandEnvelope =
        { CommandId = commandId
          Investigation = investigation
          ExpectedVersion = history |> List.tryLast |> Option.map _.Version }

    /// Pure: one domain command decided by the kernel against the history (Kernel.decideOn), and on Allowed the
    /// stamped events (Kernel.stamp) appended. A refusal returns the history unchanged with the kernel's decision.
    let submit
        (policy: PolicySet)
        (actor: ActorContext)
        (history: EventEnvelope list)
        (envelope: CommandEnvelope)
        (command: Command)
        : EventEnvelope list * SubmitOutcome =
        match Kernel.decideOn policy actor history { Envelope = envelope; Command = command } with
        | Allowed payloads ->
            let summary = Kernel.replay history |> Result.toOption
            let events = Kernel.stamp envelope summary payloads
            history @ events, CommandAccepted(command, events)
        | decision -> history, CommandRefused(command, decision)

    /// A save machine effect as the host effect that performs it (effects stay data until the host runs them).
    let hostEffect (effect: SaveEffect) : HostEffect =
        match effect with
        | Send request -> Http request
        | ScheduleReconcileRead(id, ms) -> Timer(ReconcileTimer id, ms)
        | Announce(politeness, text) -> LiveAnnounce(politeness, text)
        | ExportCommands commands ->
            let ids = commands |> List.map (fun c -> "\"" + c.CommandId.ToString "D" + "\"") |> String.concat ","
            Download("causa-unsaved-commands.json", "{\"commands\":[" + ids + "]}")

    let private requestContext (model: Model) (context: ConnectionContext) : RequestContext =
        { Connection = context
          NextId = model.NextRequestId
          MutationInFlightElsewhere = false }

    let private advance (RequestId n) = RequestId(n + 1L)

    /// T-W01 / T-W02: a network transition sends WentOffline / WentOnline to every investigation's SM-SAVE. An
    /// illegal network pair fails closed (state unchanged, no effect). A session whose step faults keeps its state.
    let network (machine: SaveMachine) (input: NetworkInput) (model: Model) : Result<Model * HostEffect list, SaveFault> =
        NetworkMachine.step model.Network input
        |> Result.map (fun (network, saveInput) ->
            let folded =
                model.Sessions
                |> Map.fold
                    (fun (current: Model, effects) id session ->
                        match current.Context with
                        | None -> current, effects
                        | Some context ->
                            match machine.Step (requestContext current context) session saveInput with
                            | Ok(next, saveEffects) ->
                                let sent = saveEffects |> List.exists (function Send _ -> true | _ -> false)

                                { current with
                                    Sessions = Map.add id next current.Sessions
                                    NextRequestId = if sent then advance current.NextRequestId else current.NextRequestId },
                                effects @ (saveEffects |> List.map hostEffect)
                            | Error _ -> current, effects)
                    ({ model with Network = network }, [])

            folded)

    /// One SM-CONNECTION step on the model: the context is kept on Connect, the credential replaced on Reconnect and
    /// dropped (with the tab state) on Disconnect. Illegal pairs fail closed.
    let connection (input: ConnectionInput) (model: Model) : Result<Model, SaveFault> =
        ConnectionMachine.step model.NextRequestId model.Connection input
        |> Result.map (fun (state, effects) ->
            effects
            |> List.fold
                (fun (m: Model) effect ->
                    match effect with
                    | ConnectionMachine.KeepContext context ->
                        { m with
                            Context = Some context
                            NextRequestId = advance m.NextRequestId }
                    | ConnectionMachine.ReplaceCredential credential ->
                        { m with
                            Context = m.Context |> Option.map (fun c -> { c with Credential = credential })
                            NextRequestId = advance m.NextRequestId }
                    | ConnectionMachine.DropCredentialAndTabState ->
                        { m with
                            Context = None
                            Loads = Map.empty
                            Sessions = Map.empty }
                    | ConnectionMachine.StateCause _
                    | ConnectionMachine.CredentialNoLongerAccepted -> m)
                { model with Connection = state })

/// WI-R8-T22B: what a reapply re-decided (T-C06/T-C07): accepted envelopes in order, refused with the kernel's reason.
type Reapplied = { Accepted: CommandEnvelope list; Refused: RefusedCommand list }

/// WI-R8-T22B: the IF41-APP Engine record. Every Msg drives the machines the contract names (SM-CONNECTION,
/// SM-NETWORK, SM-LOAD, SM-SAVE, SM-CONFLICT); every request is data (HostEffect.Http) the host executes once through
/// Limen and answers with Browser(HttpCompleted). The only impure value is the tab's marker source, injected here.
[<RequireQualifiedAccess>]
module AppEngine =
    type Config =
        { Tab: System.Guid
          NextMarker: unit -> WriteMarker
          Policy: PolicySet
          Actor: ActorContext }

    let private advance (RequestId n) = RequestId(n + 1L)

    let private outcomeId (outcome: HttpOutcome) =
        match outcome with
        | Answered(id, _, _, _)
        | NotAnswered(id, _) -> id

    /// An investigation holds work while it is not Saved or has commands not yet confirmed (INV-07).
    let holdsWork (session: InvestigationSession) =
        match session.State with
        | SaveState.Saved _ -> not (List.isEmpty session.Unsaved && List.isEmpty session.AfterInFlight)
        | _ -> true

    /// The commands of the session not confirmed on GitHub (what Export offers, T-C12).
    let unconfirmed (session: InvestigationSession) = session.Unsaved @ session.AfterInFlight

    /// The confirmed base the tab read for an investigation (SM-LOAD Loaded), if any.
    let baseOf (model: Model) (id: InvestigationId) : VerifiedInvestigation option =
        match Map.tryFind id model.Loads with
        | Some(Loaded verified) -> Some verified
        | _ -> None

    let private reason (decision: Decision) =
        match decision with
        | Allowed _ -> "allowed"
        | Blocked blockers -> blockers |> List.map _.Message |> String.concat "; " |> sprintf "Blocked: %s"
        | RequiresReview reasons -> reasons |> String.concat "; " |> sprintf "Requires review: %s"
        | UnsupportedSemantics reasons -> reasons |> String.concat "; " |> sprintf "Unsupported: %s"
        | Decision.Conflict(expected, actual) -> sprintf "Version conflict: expected %A, found %A" expected actual
        | Duplicate ids -> sprintf "Duplicate of %d recorded event(s)" (List.length ids)

    /// Pure: the unsaved commands decided in order by the kernel at a base (history and investigation), each against
    /// the state as evolved so far (T-C06). Accepted envelopes are returned unchanged; refused ones carry the reason.
    let decideAll
        (config: Config)
        (history: EventEnvelope list, state: Investigation option)
        (commands: CommandEnvelope list)
        : Reapplied * EventEnvelope list * Investigation option =
        let reapplied, (finalHistory, finalState) =
            commands
            |> List.fold
                (fun (acc: Reapplied, (history, state: Investigation option)) command ->
                    let decidedOn = { command with ExpectedVersion = state |> Option.map _.Version }

                    let refuse text =
                        { acc with Refused = acc.Refused @ [ { Command = command; Reason = text } ] }, (history, state)

                    match Kernel.domain.Decide config.Policy config.Actor state decidedOn with
                    | Allowed payloads ->
                        let events = Kernel.stamp decidedOn state payloads

                        let evolved =
                            events
                            |> List.fold
                                (fun current event -> current |> Result.bind (fun s -> Kernel.domain.Evolve s event |> Result.map Some))
                                (Ok state)

                        match evolved with
                        | Ok next -> { acc with Accepted = acc.Accepted @ [ command ] }, (history @ events, next)
                        | Error error -> refuse (sprintf "The event does not apply: %A" error)
                    | decision -> refuse (reason decision))
                ({ Accepted = []; Refused = [] }, (history, state))

        reapplied, finalHistory, finalState

    /// The file set a save writes: the confirmed base plus the kernel's events for the in-flight commands, encoded
    /// with causa-storage v1.0.0 (GitHubProvider.fileSetOf). Without a base (never in a wired tab) the stand-in.
    let private fileSetFor (config: Config) (model: Model) (session: InvestigationSession) : FileSet =
        match baseOf model session.Id with
        | Some opened ->
            let decided (s: InvestigationSession) =
                let _, history, _ = decideAll config (opened.History, Some opened.Investigation) s.Unsaved
                history |> List.skip opened.History.Length

            GitHubProvider.fileSetOf StorageV1.codec opened decided session
        | None -> SaveProcess.pendingCommandFiles session

    /// The machine of this update: built with the model's SM-NETWORK state (KnownOffline injection, SaveSetup).
    let machineFor (config: Config) (model: Model) : SaveMachine =
        SaveProcess.create (
            GitHubProvider.saveSetup config.Tab config.NextMarker (fileSetFor config model) (model.Network = NetworkState.Offline)
        )

    let private requestContext (model: Model) (id: InvestigationId) : RequestContext option =
        model.Context
        |> Option.map (fun connection ->
            { Connection = connection
              NextId = model.NextRequestId
              MutationInFlightElsewhere =
                model.Sessions
                |> Map.exists (fun other s ->
                    other <> id
                    && (match s.Outstanding with
                        | Some(_, MutationAnswer) -> true
                        | _ -> false)) })

    let private sends (effects: SaveEffect list) =
        effects |> List.exists (function Send _ -> true | _ -> false)

    /// T-C12 / review F-03(b): the conflict view does not hold the commands, so the engine fills Export from the
    /// session's unconfirmed commands; every other effect becomes the host effect that performs it.
    let private hostEffects (session: InvestigationSession) (effects: SaveEffect list) =
        effects
        |> List.map (function
            | ExportCommands [] -> Update.hostEffect (ExportCommands(unconfirmed session))
            | effect -> Update.hostEffect effect)

    /// Record a step's result on the model: the session, the request identity (advanced when a request was emitted)
    /// and, in the same update that sends a mutation, MutationSent with its identity (T-S61, ODR-V41-08).
    let private recordSession config (model: Model) (before: InvestigationSession) (session: InvestigationSession, effects: SaveEffect list) =
        let afterSend = if sends effects then advance model.NextRequestId else model.NextRequestId

        let session =
            match session.Outstanding with
            | Some(id, MutationAnswer) when effects |> List.exists (function Send r -> r.Id = id | _ -> false) ->
                match requestContext model session.Id with
                | Some context ->
                    match (machineFor config model).Step context session (SaveInput.MutationSent id) with
                    | Ok(sent, _) -> sent
                    | Error _ -> session
                | None -> session
            | _ -> session

        { model with
            Sessions = Map.add session.Id session model.Sessions
            NextRequestId = afterSend },
        hostEffects before effects

    /// After a confirmed save the confirmed history is the base of the next one: the base advances to the commit with
    /// the events of the commands just confirmed (so a later save never drops them).
    let private advanceBase config (model: Model) (before: InvestigationSession) (after: InvestigationSession) =
        let confirmedNow =
            match before.State, after.State with
            | (SaveState.Saving | SaveState.UnknownOutcome _), (SaveState.Saved(Confirmed _) | SaveState.Unsaved) ->
                after.BaseHead <> before.BaseHead
            | _ -> false

        match confirmedNow, baseOf model before.Id with
        | true, Some opened ->
            let _, history, state = decideAll config (opened.History, Some opened.Investigation) before.Unsaved

            match state with
            | Some investigation ->
                let confirmedBase =
                    { Investigation = investigation
                      History = history
                      ReadAt = after.BaseHead
                      Bytes = fileSetFor config model before }

                { model with Loads = Map.add before.Id (Loaded confirmedBase) model.Loads }
            | None -> model
        | _ -> model

    /// One SM-SAVE step on the model. An illegal pair, or no connection, fails closed: the model is unchanged.
    let sessionStep config (id: InvestigationId) (input: SaveInput) (model: Model) : Model * HostEffect list =
        match Map.tryFind id model.Sessions, requestContext model id with
        | Some session, Some context ->
            match (machineFor config model).Step context session input with
            | Ok(next, effects) ->
                let recorded, hostEffects = recordSession config model session (next, effects)
                advanceBase config recorded session next, hostEffects
            | Error _ -> model, []
        | _ -> model, []

    /// T-C04/T-C13 (review F-03(a)): a transition into ReadingRemote that sent nothing has the engine send the read of
    /// the GitHub version with the identity the transition recorded.
    let private withRemoteRead (context: RequestContext) (session: InvestigationSession) (view: ConflictView) (effects: SaveEffect list) =
        match view with
        | ReadingRemote id when not (sends effects) ->
            effects @ [ Send(ConflictMachine.readRequest { context with NextId = id } session.Id) ]
        | _ -> effects

    /// One SM-CONFLICT step, then the engine's part of the resolution: reapply re-decides the unsaved commands at the
    /// GitHub version (T-C06/T-C07, INV-12) and a confirmed discard loads it (T-C09, T-S38).
    let rec conflictStep config (id: InvestigationId) (input: ConflictInput) (model: Model) : Model * HostEffect list =
        match Map.tryFind id model.Sessions, requestContext model id with
        | Some({ State = SaveState.Conflict view } as session), Some context ->
            match (machineFor config model).StepConflict context view input with
            | Error _ -> model, []
            | Ok(next, effects) ->
                let effects = withRemoteRead context session next effects
                let stepped = { session with State = SaveState.Conflict next }
                let model, hostEffects = recordSession config model session (stepped, effects)

                match next with
                | Reapplying remote ->
                    let reapplied, _, _ = decideAll config (remote.History, Some remote.Investigation) (unconfirmed session)
                    let model', more = conflictStep config id (ConflictInput.ReapplyDone(reapplied.Accepted, reapplied.Refused)) model
                    model', hostEffects @ more
                | Reapplied(remote, []) ->
                    let accepted =
                        let reapplied, _, _ = decideAll config (remote.History, Some remote.Investigation) (unconfirmed session)
                        reapplied.Accepted

                    let model = { model with Loads = Map.add id (Loaded remote) model.Loads }
                    let model', more = sessionStep config id (SaveInput.ConflictResolvedReapplied accepted) model
                    model', hostEffects @ more
                | Discarded remote ->
                    let model = { model with Loads = Map.add id (Loaded remote) model.Loads }
                    let model', more = sessionStep config id (SaveInput.ConflictResolvedDiscarded remote) model
                    model', hostEffects @ more
                | _ -> model, hostEffects
        | _ -> model, []

    /// One SM-LOAD step. INV-07: Refresh is refused, and a completing load dropped, while the investigation holds
    /// work; otherwise a verified load starts (or restarts) the investigation's save session at the read head.
    let loadStep config (id: InvestigationId) (input: LoadInput) (model: Model) : Model * HostEffect list =
        let working = model.Sessions |> Map.tryFind id |> Option.exists holdsWork

        match input, working with
        | LoadInput.RefreshRequested, true
        | LoadInput.LoadVerified _, true -> model, []
        | _ ->
            match requestContext model id with
            | None -> model, []
            | Some context ->
                let current = model.Loads |> Map.tryFind id |> Option.defaultValue NotLoaded

                match (machineFor config model).StepLoad context current input with
                | Error _ -> model, []
                | Ok(next, effects) ->
                    let machine = machineFor config model

                    let sessions =
                        match next with
                        | Loaded verified -> Map.add id (machine.Initial verified) model.Sessions
                        | _ -> model.Sessions

                    { model with
                        Loads = Map.add id next model.Loads
                        Sessions = sessions
                        NextRequestId = if sends effects then advance model.NextRequestId else model.NextRequestId },
                    effects |> List.map Update.hostEffect

    /// SM-NETWORK, then WentOffline / WentOnline to every load and save session (T-W01, T-W02).
    let networkStep config (input: NetworkInput) (model: Model) : Model * HostEffect list =
        match NetworkMachine.step model.Network input with
        | Error _ -> model, []
        | Ok(network, saveInput) ->
            let loadInput =
                match input with
                | NetworkInput.OfflineObserved -> LoadInput.WentOffline
                | NetworkInput.OnlineObserved -> LoadInput.WentOnline

            let start = { model with Network = network }, []

            let afterLoads =
                model.Loads
                |> Map.fold
                    (fun (m, effects) id _ ->
                        let m', more = loadStep config id loadInput m
                        m', effects @ more)
                    start

            model.Sessions
            |> Map.fold
                (fun (m, effects) id _ ->
                    let m', more = sessionStep config id saveInput m
                    m', effects @ more)
                afterLoads

    /// SM-CONNECTION; Connect and Reconnect send the repository read (GitHubProvider.connector) with the identity
    /// the transition used. The credential goes only into that request's Authorization header.
    let connectionStep (input: ConnectionInput) (model: Model) : Model * HostEffect list =
        match Update.connection input model with
        | Error _ -> model, []
        | Ok next ->
            match input, next.Context with
            | ConnectionInput.ConnectSubmitted _, Some context
            | ConnectionInput.ReconnectSubmitted _, Some context ->
                next, [ Http(GitHubProvider.connector.ConnectRequest model.NextRequestId context) ]
            | _ -> next, []

    /// The answer to one request, routed by its identity to the machine waiting for it. An answer nobody waits for
    /// changes nothing.
    let httpCompleted config (outcome: HttpOutcome) (model: Model) : Model * HostEffect list =
        let id = outcomeId outcome
        let machine = machineFor config model
        let pendingLoad = model.Loads |> Map.tryFindKey (fun _ l -> match l with Loading(r, _) -> r = id | _ -> false)

        let pendingSave =
            model.Sessions |> Map.tryFindKey (fun _ s -> match s.Outstanding with Some(r, _) -> r = id | None -> false)

        let pendingConflict =
            model.Sessions
            |> Map.tryFindKey (fun _ s -> match s.State with SaveState.Conflict(ReadingRemote r) -> r = id | _ -> false)

        let connectWaiting =
            match model.Connection with
            | Connecting r -> r = id
            | Reauthenticating -> true
            | _ -> false

        match pendingLoad, pendingSave, pendingConflict with
        | Some inv, _, _ ->
            match requestContext model inv with
            | Some context ->
                match machine.InterpretLoad context model.Loads[inv] outcome with
                | LoadEvent input -> loadStep config inv input model
                | LoadNextRequest(state, request) ->
                    { model with Loads = Map.add inv state model.Loads; NextRequestId = advance model.NextRequestId },
                    [ Http request ]
            | None -> model, []
        | None, Some inv, _ ->
            match requestContext model inv with
            | Some context ->
                let session = model.Sessions[inv]

                match machine.Interpret context session outcome with
                | MachineEvent input -> sessionStep config inv input model
                | NextRequest(next, request) ->
                    { model with Sessions = Map.add inv next model.Sessions; NextRequestId = advance model.NextRequestId },
                    [ Http request ]
            | None -> model, []
        | None, None, Some inv ->
            match requestContext model inv, model.Sessions[inv].State with
            | Some context, SaveState.Conflict view ->
                match machine.InterpretConflict context view outcome with
                | ConflictEvent input -> conflictStep config inv input model
                | ConflictNextRequest(next, request) ->
                    { model with
                        Sessions = Map.add inv { model.Sessions[inv] with State = SaveState.Conflict next } model.Sessions
                        NextRequestId = advance model.NextRequestId },
                    [ Http request ]
            | _ -> model, []
        | None, None, None when connectWaiting ->
            match GitHubProvider.interpretConnect id outcome with
            | ConnectAccepted canWrite -> connectionStep (ConnectionInput.ConnectSucceeded canWrite) model
            | ConnectRejected error -> connectionStep (ConnectionInput.ConnectRefused error) model
            | ConnectSuperseded -> model, []
        | _ -> model, []

    let init (route: Route) : Model * HostEffect list =
        { Connection = Disconnected
          Context = None
          NextRequestId = RequestId 1L
          Network = NetworkState.Online
          Route = route
          Loads = Map.empty
          Sessions = Map.empty },
        []

    /// The leave guard follows whether any investigation holds work (never claims a save that did not happen).
    let private guarded (before: Model) (after: Model, effects: HostEffect list) =
        let holding (m: Model) = m.Sessions |> Map.exists (fun _ s -> holdsWork s)

        if holding before <> holding after then
            after, effects @ [ SetLeaveGuard(holding after) ]
        else
            after, effects

    let update config (msg: Msg) (model: Model) : Model * HostEffect list =
        guarded
            model
            (match msg with
             | Navigate route -> { model with Route = route }, []
             | Connection input -> connectionStep input model
             | Network input -> networkStep config input model
             | SessionInput(id, input) -> sessionStep config id input model
             | ConflictStep(id, input) -> conflictStep config id input model
             | LoadStep(id, input) -> loadStep config id input model
             | DomainCommand envelope -> sessionStep config envelope.Investigation (SaveInput.Edit envelope) model
             | Browser(ConnectivityChanged online) ->
                 match NetworkMachine.observe model.Network online with
                 | Some input -> networkStep config input model
                 | None -> model, []
             | Browser(TimerFired(ReconcileTimer id)) ->
                 match Map.tryFind id model.Sessions, requestContext model id with
                 | Some session, Some context ->
                     match (machineFor config model).ReconcileDue context session with
                     | Ok(next, effects) -> recordSession config model session (next, effects)
                     | Error _ -> model, []
                 | _ -> model, []
             | Browser(HttpCompleted outcome) -> httpCompleted config outcome model
             | Browser LeaveAttempted -> model, [])

    let configFor (tab: System.Guid) : Config =
        { Tab = tab
          NextMarker = Markers.forTab tab
          Policy = { Id = Policies.defaultPolicyId }
          Actor = { ActorId = "this-tab" } }

    /// The engine of one tab.
    let create (config: Config) : Engine = { Init = init; Update = update config }

    /// IF41-APP: the engine of a new tab (a fresh random tab identifier for its write markers, PERSIST-014).
    let engine: Engine = create (configFor (System.Guid.NewGuid()))

/// WI-R8-T22B: explicit create (EXT-DATA-006-O2) beside the engine record (the frozen Model has no field for it, so
/// the host keeps this value next to the Model). Create reads the head itself, sends the create commit only when that
/// read found the investigation absent and Connect stated write access, and then opens it through SM-LOAD: the
/// investigation shows Saved only from the verified read after the commit, never from the create answer alone.
type Creation =
    | CreateReadingHead of RequestId * InvestigationId
    | CreateCommitting of RequestId * InvestigationId * expectedHead: CommitOid
    | CreateOpened of InvestigationId
    | CreateRefused of InvestigationId * reason: string

[<RequireQualifiedAccess>]
module Create =
    let private advance (RequestId n) = RequestId(n + 1L)

    let private context (model: Model) =
        model.Context
        |> Option.map (fun connection ->
            { Connection = connection
              NextId = model.NextRequestId
              MutationInFlightElsewhere =
                model.Sessions |> Map.exists (fun _ s -> match s.Outstanding with Some(_, MutationAnswer) -> true | _ -> false) })

    let investigationOf (creation: Creation) =
        match creation with
        | CreateReadingHead(_, id)
        | CreateCommitting(_, id, _)
        | CreateOpened id
        | CreateRefused(id, _) -> id

    /// Start: read the head for this investigation (GitHubProvider.openRequest). Not connected: refused, nothing sent.
    let start (investigation: InvestigationId) (model: Model) : Model * Creation * HostEffect list =
        match model.Context, model.Connection with
        | Some connection, Connected _ ->
            { model with NextRequestId = advance model.NextRequestId },
            CreateReadingHead(model.NextRequestId, investigation),
            [ Http(GitHubProvider.openRequest model.NextRequestId connection investigation) ]
        | _ -> model, CreateRefused(investigation, "Connect to GitHub first."), []

    /// The first history of a new investigation: StartInvestigation decided by the kernel and stamped.
    let firstHistory (config: AppEngine.Config) (investigation: InvestigationId) (commandId: System.Guid) : EventEnvelope list =
        let envelope = { CommandId = commandId; Investigation = investigation; ExpectedVersion = None }

        match Kernel.decideOn config.Policy config.Actor [] { Envelope = envelope; Command = StartInvestigation } with
        | Allowed payloads -> Kernel.stamp envelope None payloads
        | _ -> []

    let private refusal (refused: CreateRefusal) =
        match refused with
        | NoWriteAccess -> "This credential cannot write to the repository."
        | HeadNotReadEmpty -> "The investigation already exists on the branch, or the branch could not be read."
        | OutsidePrefix paths -> "Refused: files outside the investigation's folder: " + String.concat ", " paths
        | MutationInFlight -> "Another save in this tab is in flight; create again when it finishes."

    /// One answer to the create flow. Answers to other requests leave it unchanged.
    let answer (config: AppEngine.Config) (creation: Creation) (outcome: HttpOutcome) (model: Model) : Model * Creation * HostEffect list =
        let id =
            match outcome with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> id

        match creation, context model with
        | CreateReadingHead(sent, investigation), Some ctx when sent = id ->
            let headRead = GitHubProvider.interpretOpen StorageV1.codec outcome
            let canWrite = (match model.Connection with Connected write -> write | _ -> false)

            match headRead with
            | AuthorizedEmpty head ->
                let (InvestigationId g) = investigation
                let files = StorageV1.encodeHistory investigation (firstHistory config investigation g)

                match GitHubProvider.createRequest ctx canWrite headRead investigation files (config.NextMarker()) with
                | Ok request ->
                    { model with NextRequestId = advance model.NextRequestId },
                    CreateCommitting(request.Id, investigation, head),
                    [ Http request ]
                | Error refused -> model, CreateRefused(investigation, refusal refused), []
            | Opened _ -> model, CreateRefused(investigation, "The investigation already exists on the branch; open it instead."), []
            | Incompatible _ -> model, CreateRefused(investigation, "Files of this investigation exist but do not verify; nothing was written."), []
            | LargeFiles _ -> model, CreateRefused(investigation, "Files of this investigation already exist; nothing was written."), []
            | OpenRefused error -> model, CreateRefused(investigation, ProviderMessage.ofError error), []
        | CreateCommitting(sent, investigation, head), Some _ when sent = id ->
            match (ErrorMatrix.classify (ErrorMatrix.Mutation head) outcome).Result with
            | Ok _ ->
                let opened, effects = AppEngine.loadStep config investigation (LoadInput.OpenRequested investigation) model
                opened, CreateOpened investigation, effects
            | Error error -> model, CreateRefused(investigation, "Not created: " + ProviderMessage.ofError error), []
        | _ -> model, creation, []

/// WI-R8-T22B: HostEffect.Http and its answer as Limen protocol 1 JSON (EffectRequest kind Http / EffectResult kind
/// HttpResult). Pure. The kernel performs the fetch and classifies transport outcomes only; persistence classifies
/// the answer. Headers (the Authorization header among them) are never reported by the Limen kernel's diagnostics.
[<RequireQualifiedAccess>]
module LimenHttp =
    open System.Text.Json

    /// Answer headers the error matrix reads (rate limits, request identity).
    let responseHeaders = [ "x-ratelimit-remaining"; "x-ratelimit-reset"; "retry-after"; "x-github-request-id" ]

    [<Literal>]
    let TimeoutMs = 30000

    let correlationId (RequestId n) = "http-" + string n

    let requestIdOf (correlation: string) : RequestId option =
        match correlation.StartsWith "http-", System.Int64.TryParse(correlation.Substring(min 5 correlation.Length)) with
        | true, (true, n) -> Some(RequestId n)
        | _ -> None

    let methodName (m: HttpMethod) =
        match m with
        | Get -> "GET"
        | Post -> "POST"
        | Put -> "PUT"
        | Patch -> "PATCH"

    /// Write one Http effect request. The body is answered as text (decoding stays in persistence).
    let writeRequest (w: Utf8JsonWriter) (request: HttpRequest) =
        w.WriteStartObject()
        w.WriteString("kind", "Http")
        w.WriteString("correlationId", correlationId request.Id)
        w.WriteString("method", methodName request.Method)
        w.WriteString("url", request.Url)
        w.WriteStartObject "headers"

        request.Headers
        |> List.filter (fun (name, _) -> not (name.Equals("User-Agent", System.StringComparison.OrdinalIgnoreCase)))
        |> List.iter (fun (name, value) -> w.WriteString(name, value))

        w.WriteEndObject()
        request.Body |> Option.iter (fun body -> w.WriteString("body", body))
        w.WriteNumber("timeoutMs", TimeoutMs)
        w.WriteString("response", "text")
        w.WriteStartArray "responseHeaders"
        responseHeaders |> List.iter w.WriteStringValue
        w.WriteEndArray()
        w.WriteString("credentials", "omit")
        w.WriteEndObject()

    let private prop (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private text name e =
        prop name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.bind (fun v -> v.GetString() |> Option.ofObj)

    let private stringOf (v: JsonElement) =
        match v.ValueKind with
        | JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> v.GetRawText()

    /// Read a Limen EffectResult (the `result` object) of kind HttpResult as an HttpOutcome. A failure that cannot have
    /// changed anything is FailedBeforeSending; any doubt (timeout, connection lost, abort, cancel) is never collapsed
    /// into a failure (LostAfterSending / TimedOut), so SM-SAVE reconciles instead of claiming an outcome.
    let outcomeOf (result: JsonElement) : HttpOutcome option =
        match text "kind" result, text "correlationId" result |> Option.bind requestIdOf, prop "outcome" result with
        | Some "HttpResult", Some id, Some outcome ->
            let status () =
                prop "status" outcome
                |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Number)
                |> Option.map (fun v -> v.GetInt32())

            match text "kind" outcome with
            | Some "Success" ->
                let headers =
                    match prop "headers" outcome with
                    | Some h when h.ValueKind = JsonValueKind.Object ->
                        [ for p in h.EnumerateObject() -> p.Name.ToLowerInvariant(), stringOf p.Value ]
                    | _ -> []

                let body =
                    match prop "body" outcome with
                    | Some b when b.ValueKind = JsonValueKind.String -> stringOf b
                    | Some b when b.ValueKind <> JsonValueKind.Null -> b.GetRawText()
                    | _ -> ""

                Some(Answered(id, status () |> Option.defaultValue 0, headers, body))
            | Some "Failure" ->
                match text "reason" outcome, status () with
                | Some "network", _ -> Some(NotAnswered(id, FailedBeforeSending))
                | _, Some code -> Some(Answered(id, code, [], ""))
                | _ -> Some(NotAnswered(id, LostAfterSending))
            | Some "OutcomeUnknown" ->
                match text "reason" outcome with
                | Some "timeout-after-dispatch" -> Some(NotAnswered(id, TimedOut))
                | _ -> Some(NotAnswered(id, LostAfterSending))
            | Some "Cancelled" -> Some(NotAnswered(id, LostAfterSending))
            | _ -> None
        | _ -> None
