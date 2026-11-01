// Owner: ui-2. WI-R8-T28: history, time travel and semantic diff (S14, S15; in scope by DEC-R7-005).
// Pure. Time travel replays a prefix of the kernel's history (Kernel.replayState up to a chosen aggregate version) and
// derives that version's capabilities (Capabilities.derive); it never touches the live history, so a historical view is
// read-only by construction. The semantic diff compares two replayed states in the terms the kernel records: lifecycle,
// recorded entries, capability/blocker changes and the derived next legal actions. Nothing is inferred (ADRP-007-O4):
// a change appears only because an accepted event made it. The timeline is in canonical version order, never wall-clock
// (ADRP-007-O2). Frozen causa-storage v1 histories are diffed the same way over StorageV1Replay prefixes (diffV1).
namespace Causa.App.Views

open Causa.Domain

module History =
    /// Which version the history screen shows: the live head, or an earlier aggregate version (read-only).
    type Selection =
        | Latest
        | At of int64

    /// One timeline entry: one accepted event at its canonical version.
    type TimelineEntry =
        { Version: int64
          EventId: string
          Kind: string
          Summary: string }

    /// The investigation as the kernel replays it at one version.
    type Snapshot =
        { Version: int64
          State: InvestigationState option
          Capabilities: Capability list
          Fault: string option }

    /// One semantic change between two replayed versions, in the kernel's own terms.
    type Change =
        | LifecycleChange of before: InvestigationLifecycle * after: InvestigationLifecycle
        | EntryAdded of role: Graph.NodeRole * ordinal: int * kind: string * version: int64
        | CapabilityChange of command: Command * before: Availability * after: Availability
        | PostureChange of before: Command list * after: Command list

    type SemanticDiff =
        { From: int64
          To: int64
          Changes: Change list
          Fault: string option }

    /// The history's contribution to the view: flat keys and the row lists the page repeats with data-each.
    type HistoryView = { Keys: ShellKeys; Lists: (string * Graph.Row list) list }

    /// The Limen events the history screen sends: the scrubber (value: a version) and "return to the latest version".
    [<Literal>]
    let SelectEvent = "history.at"

    [<Literal>]
    let LatestEvent = "history.latest"

    let private versionOf (AggregateVersion v) = v

    /// The history in canonical version order (ADRP-007-O2).
    let canonical (history: EventEnvelope list) = history |> List.sortBy (fun e -> versionOf e.Version)

    let latestVersion (history: EventEnvelope list) =
        history |> List.map (fun e -> versionOf e.Version) |> List.fold max 0L

    /// Pure: the selection after a page event. A version at or past the head is the head; anything unreadable or
    /// before the first version leaves the selection unchanged (never guessed).
    let select (history: EventEnvelope list) (current: Selection) (name: string) (value: string option) : Selection =
        let latest = latestVersion history
        let parsed =
            value
            |> Option.bind (fun v ->
                match System.Int64.TryParse(v.Trim()) with
                | true, n -> Some n
                | false, _ -> None)
        match name, parsed with
        | LatestEvent, _ -> Latest
        | SelectEvent, Some v when v >= latest -> Latest
        | SelectEvent, Some v when v >= 1L -> At v
        | _ -> current

    /// Pure: the version a selection resolves to against this history (0 when there is no history).
    let resolve (history: EventEnvelope list) (selection: Selection) : int64 =
        let latest = latestVersion history
        match selection with
        | Latest -> latest
        | At v -> min v latest

    let private sentence = Report.eventSentence

    /// Pure: the aggregate-version timeline with event summaries.
    let timeline (history: EventEnvelope list) : TimelineEntry list =
        canonical history
        |> List.map (fun e ->
            let (EventId id) = e.EventId
            let (EventPayload kind) = e.Payload
            { Version = versionOf e.Version
              EventId = id.ToString "D"
              Kind = kind
              Summary = DomainEvent.ofPayload e.Payload |> Option.map sentence |> Option.defaultValue "An event this version of Causa does not describe" })

    /// The events up to and including a version: the history as it stood then.
    let prefix (version: int64) (history: EventEnvelope list) =
        canonical history |> List.filter (fun e -> versionOf e.Version <= version)

    /// Pure: state-at-version. Replays the prefix; the live history is not changed (it is an immutable list).
    let stateAt (policy: PolicySet) (actor: ActorContext) (version: int64) (history: EventEnvelope list) : Snapshot =
        match prefix version history with
        | [] ->
            { Version = 0L
              State = None
              Capabilities = Capabilities.derive policy actor None
              Fault = None }
        | events ->
            match Kernel.replayState events with
            | Ok state ->
                { Version = versionOf state.Version
                  State = Some state
                  Capabilities = Capabilities.derive policy actor (Some state)
                  Fault = None }
            | Error error ->
                { Version = version
                  State = None
                  Capabilities = []
                  Fault = Some(sprintf "The history cannot be shown at version %d: %A" version error) }

    let private codes (availability: Availability) =
        match availability with
        | Available -> []
        | Unavailable blockers -> blockers |> List.map _.Code

    let private sameAvailability (a: Availability) (b: Availability) =
        match a, b with
        | Available, Available -> true
        | Unavailable x, Unavailable y -> (x |> List.map _.Code) = (y |> List.map _.Code)
        | Available, Unavailable _
        | Unavailable _, Available -> false

    let private availableCommands (capabilities: Capability list) =
        capabilities
        |> List.choose (fun c ->
            match c.Availability with
            | Available -> Some c.Command
            | Unavailable _ -> None)

    /// Every recorded entry with its 1-based ordinal within its role, in canonical order.
    let private numberedEntries (history: EventEnvelope list) =
        canonical history
        |> List.choose (fun e ->
            DomainEvent.ofPayload e.Payload
            |> Option.bind Graph.roleOf
            |> Option.map (fun role -> e, role))
        |> List.mapFold
            (fun (counts: Map<Graph.NodeRole, int>) (e, role) ->
                let n = (counts |> Map.tryFind role |> Option.defaultValue 0) + 1
                let (EventPayload kind) = e.Payload
                (role, n, kind, versionOf e.Version), Map.add role n counts)
            Map.empty
        |> fst

    let private lifecycleChanges (before: InvestigationState) (after: InvestigationState) =
        if before.Lifecycle = after.Lifecycle then [] else [ LifecycleChange(before.Lifecycle, after.Lifecycle) ]

    let private additions (fromV: int64) (toV: int64) (history: EventEnvelope list) =
        numberedEntries history
        |> List.filter (fun (_, _, _, v) -> v > fromV && v <= toV)
        |> List.map EntryAdded

    let private capabilityChanges (before: Capability list) (after: Capability list) =
        before
        |> List.choose (fun b ->
            after
            |> List.tryFind (fun a -> a.Command = b.Command)
            |> Option.filter (fun a -> not (sameAvailability b.Availability a.Availability))
            |> Option.map (fun a -> CapabilityChange(b.Command, b.Availability, a.Availability)))

    let private postureChanges (before: Capability list) (after: Capability list) =
        let b, a = availableCommands before, availableCommands after
        if b = a then [] else [ PostureChange(b, a) ]

    /// Pure: the semantic diff from one version to a later one (the order is normalised), from two replays.
    let diff (policy: PolicySet) (actor: ActorContext) (history: EventEnvelope list) (a: int64) (b: int64) : SemanticDiff =
        let fromV, toV = min a b, max a b
        let before, after = stateAt policy actor fromV history, stateAt policy actor toV history
        match before.State, after.State, before.Fault |> Option.orElse after.Fault with
        | Some s0, Some s1, None ->
            { From = fromV
              To = toV
              Changes =
                lifecycleChanges s0 s1
                @ additions fromV toV history
                @ capabilityChanges before.Capabilities after.Capabilities
                @ postureChanges before.Capabilities after.Capabilities
              Fault = None }
        | _, _, fault ->
            { From = fromV
              To = toV
              Changes = []
              Fault = fault |> Option.orElse (Some "There is no investigation to compare at one of these versions.") }

    // ---- Words: every sentence is built from kernel values. ----

    /// A command in the words a person reads (UI-024-O5). The same wording as ui's PresentationLanguage.commandLabel
    /// (PR #37); switch to it once that module is on main. The command's name stays as data (row key) only.
    let commandLabel (command: Command) =
        match command with
        | StartInvestigation -> "Start investigation"
        | RenameInvestigation -> "Rename"
        | ActivateInvestigation -> "Activate"
        | MarkAwaitingEvidence -> "Mark awaiting evidence"
        | MarkAwaitingVerification -> "Mark awaiting verification"
        | ResolveInvestigation -> "Resolve"
        | CloseInvestigation -> "Close"
        | ReopenInvestigation -> "Reopen"
        | ArchiveInvestigation -> "Archive"
        | RecordObservation -> "Record an observation"
        | Command.RecordHypothesis -> "Record a hypothesis"
        | RecordConclusion -> "Record a conclusion"

    /// Availability in words; the kernel's blocker codes stay as data (the capability row's "codes" field).
    let private availabilityText (availability: Availability) =
        match availability with
        | Available -> "available"
        | Unavailable _ -> "not available"

    let private commandsText (commands: Command list) =
        match commands with
        | [] -> "none"
        | cs -> cs |> List.map commandLabel |> String.concat ", "

    let changeCategory (change: Change) =
        match change with
        | LifecycleChange _ -> "lifecycle"
        | EntryAdded _ -> "addition"
        | CapabilityChange _ -> "capability"
        | PostureChange _ -> "posture"

    let changeText (change: Change) =
        match change with
        | LifecycleChange(b, a) -> sprintf "Lifecycle: %s to %s." (Graph.lifecycleName b) (Graph.lifecycleName a)
        | EntryAdded(role, n, _, v) -> sprintf "Added %s %d (version %d)." (Graph.roleName role) n v
        | CapabilityChange(c, b, a) -> sprintf "%s: %s to %s." (commandLabel c) (availabilityText b) (availabilityText a)
        | PostureChange(b, a) -> sprintf "Next possible actions: %s to %s." (commandsText b) (commandsText a)

    /// The kernel's history carries no policy or domain-version event, and every version replays under one policy.
    let policyText (policy: PolicySet) =
        sprintf "No policy or domain-version change: the history records none; every version is read under policy %s." policy.Id

    let supersessionText =
        "No entry removed by supersession: this investigation's kernel records no supersession event."

    // ---- Frozen causa-storage v1 histories (StorageV1Replay). ----

    /// One semantic change between two replayed v1 states (CONTRACT.md §8.2 entity arrays).
    type V1Change =
        | V1LifecycleChange of before: string * after: string
        | V1EntityAdded of array: string * id: string
        | V1Superseded of array: string * id: string * replacement: string option
        | V1FieldChange of array: string * id: string * field: string * before: string * after: string

    let private v1Text (value: V1Value) =
        match value with
        | VText s -> s
        | VNull -> "none"
        | VBool _
        | VInt _
        | VArray _
        | VObject _ -> StorageV1Replay.canonicalText value

    /// The fields a v1 entity's lifecycle lives in (§8.3): its state, and evidence verification.
    let private trackedFields = [ "state"; "verification" ]

    let private entityField (e: V1Entity) k = e |> Map.tryFind k |> Option.defaultValue VNull

    let private entityChanges (array: string) (id: string) (before: V1Entity) (after: V1Entity) : V1Change list =
        let supersededNow = entityField after "state" = VText "superseded" && entityField before "state" <> VText "superseded"
        let superseded =
            match entityField before "supersededBy", entityField after "supersededBy" with
            | VNull, VText replacement -> [ V1Superseded(array, id, Some replacement) ]
            | _ when supersededNow -> [ V1Superseded(array, id, None) ]
            | _ -> []
        let fields =
            trackedFields
            |> List.filter (fun k -> entityField before k <> entityField after k && not (k = "state" && supersededNow))
            |> List.map (fun k -> V1FieldChange(array, id, k, v1Text (entityField before k), v1Text (entityField after k)))
        superseded @ fields

    let private arrayChanges (s0: V1State) (s1: V1State) (array: string) : V1Change list =
        let entitiesOf (s: V1State) = s.Arrays |> Map.tryFind array |> Option.defaultValue Map.empty
        let before, after = entitiesOf s0, entitiesOf s1
        after
        |> Map.toList
        |> List.sortBy fst
        |> List.collect (fun (id, entity) ->
            match before |> Map.tryFind id with
            | None -> [ V1EntityAdded(array, id) ]
            | Some old -> entityChanges array id old entity)

    /// The events of a v1 history up to and including a version (file order is canonical order, §5).
    let v1Prefix (version: int64) (history: V1Event list) =
        history |> List.filter (fun e -> versionOf e.Version <= version)

    /// Pure: the semantic diff of a frozen v1 history between two versions, from two StorageV1Replay prefixes.
    let diffV1 (history: V1Event list) (a: int64) (b: int64) : Result<V1Change list, V1Refusal> =
        let fromV, toV = min a b, max a b
        match StorageV1Replay.replay (v1Prefix fromV history), StorageV1Replay.replay (v1Prefix toV history) with
        | Ok s0, Ok s1 ->
            let lifecycle = if s0.Lifecycle = s1.Lifecycle then [] else [ V1LifecycleChange(s0.Lifecycle, s1.Lifecycle) ]
            Ok(lifecycle @ (StorageV1Replay.entityArrays |> List.collect (arrayChanges s0 s1)))
        | Error refusal, _
        | _, Error refusal -> Error refusal

    let v1ChangeText (change: V1Change) =
        match change with
        | V1LifecycleChange(b, a) -> sprintf "Lifecycle: %s to %s." b a
        | V1EntityAdded(array, id) -> sprintf "Added %s %s." array id
        | V1Superseded(array, id, Some r) -> sprintf "Removed by supersession: %s %s, superseded by %s." array id r
        | V1Superseded(array, id, None) -> sprintf "Removed by supersession: %s %s." array id
        | V1FieldChange(array, id, k, b, a) -> sprintf "%s %s %s: %s to %s." array id k b a

    // ---- The view. ----

    let private timelineRow (selected: int64) (entry: TimelineEntry) : Graph.Row =
        [ "key", entry.EventId
          "version", string entry.Version
          "kind", entry.Kind
          "summary", entry.Summary
          "current", (if entry.Version = selected then "true" else "false")
          "text", sprintf "Version %d: %s." entry.Version entry.Summary ]

    let private capabilityRow (c: Capability) : Graph.Row =
        [ "key", Command.name c.Command
          "command", commandLabel c.Command
          "availability", availabilityText c.Availability
          "codes", codes c.Availability |> String.concat " " ]

    let private changeRow (i: int) (change: Change) : Graph.Row =
        [ "key", sprintf "change-%d" i; "category", changeCategory change; "text", changeText change ]

    let private count (snapshot: Snapshot) f =
        snapshot.State |> Option.map f |> Option.defaultValue 0 |> string

    let private diffSummary (latest: int64) (selected: int64) (d: SemanticDiff) =
        match d.Fault with
        | Some fault -> fault
        | None when selected = latest -> "Showing the latest version. Choose an earlier version to compare it with the latest."
        | None when d.Changes.IsEmpty -> sprintf "From version %d to version %d: no semantic change." d.From d.To
        | None -> sprintf "From version %d to version %d: %d changes." d.From d.To d.Changes.Length

    let private stateKeys (latest: int64) (selected: int64) (snapshot: Snapshot) : ShellKeys =
        let historical = selected < latest
        let lifecycle =
            snapshot.State |> Option.map (fun s -> Graph.lifecycleName s.Lifecycle) |> Option.defaultValue "No investigation"
        [ "historyLatestVersion", Str(string latest)
          "historyAtVersion", Str(string selected)
          "historyMode", Str(if historical then "historical" else "latest")
          "historyReadOnlyHidden", Bit(not historical)
          "historyReadOnlyText", Str(sprintf "Viewing version %d of %d. Historical view: read-only; the investigation is unchanged." selected latest)
          "historyEmptyHidden", Bit(latest > 0L)
          "historyBodyHidden", Bit(latest = 0L)
          "historyStateLifecycle", Str lifecycle
          "historyStateObservations", Str(count snapshot _.Observations)
          "historyStateHypotheses", Str(count snapshot _.Hypotheses)
          "historyStateConclusions", Str(count snapshot _.Conclusions)
          "historyFault", Str(snapshot.Fault |> Option.defaultValue "")
          "historyFaultHidden", Bit snapshot.Fault.IsNone ]

    /// Pure: the history screen for one history and selection: every key and list, every time.
    let project (policy: PolicySet) (actor: ActorContext) (history: EventEnvelope list) (selection: Selection) : HistoryView =
        let latest = latestVersion history
        let selected = resolve history selection
        let snapshot = stateAt policy actor selected history
        let d = diff policy actor history selected latest
        { Keys =
            stateKeys latest selected snapshot
            @ [ "historyDiffSummary", Str(diffSummary latest selected d)
                "historyDiffFrom", Str(string d.From)
                "historyDiffTo", Str(string d.To)
                "historyPolicyText", Str(policyText policy)
                "historySupersessionText", Str supersessionText ]
          Lists =
            [ "historyTimeline", timeline history |> List.map (timelineRow selected)
              "historyCapabilities", (if latest > 0L then snapshot.Capabilities |> List.map capabilityRow else [])
              "historyDiff", d.Changes |> List.mapi changeRow ] }
