// Owner: domain. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.Domain

open System

/// IF41-DOMAIN: the F# kernel (Boundaries A-C). Pure: no network, clock, randomness, GitHub or browser types.
type ReplayError =
    | EmptyHistory
    | NonContiguousVersion of expected: AggregateVersion * found: AggregateVersion
    | IllegalEvent of ProjectionError
    | UnknownEventKind of string

type InvestigationProjection =
    { Investigation: Investigation
      Timeline: EventEnvelope list
      Capabilities: string list }

type DomainKernel =
    { Decide: PolicySet -> ActorContext -> Investigation option -> CommandEnvelope -> Decision
      Evolve: Investigation option -> EventEnvelope -> Result<Investigation, ProjectionError>
      Replay: EventEnvelope list -> Result<Investigation, ReplayError>
      Project: Investigation -> InvestigationProjection }

/// Replay preconditions (Boundary C), owned by domain and pure. Not part of the frozen IF41-DOMAIN shapes above:
/// the kernel's Replay (T13) composes this before folding Evolve, so a history with a gap, a duplicate or a
/// reordered version is refused with a typed ReplayError and is never folded.
[<RequireQualifiedAccess>]
module ReplayOrder =
    let private successor (AggregateVersion v) = AggregateVersion(v + 1L)

    /// Ok with the history unchanged when its versions run genesis, genesis+1, ... without a gap or repeat;
    /// EmptyHistory for no events; otherwise NonContiguousVersion naming the first version that breaks the run.
    let contiguousFrom (genesis: AggregateVersion) (history: EventEnvelope list) : Result<EventEnvelope list, ReplayError> =
        let rec check expected remaining =
            match remaining with
            | [] -> Ok history
            | (event: EventEnvelope) :: rest when event.Version = expected -> check (successor expected) rest
            | event :: _ -> Error(NonContiguousVersion(expected, event.Version))

        match history with
        | [] -> Error EmptyHistory
        | _ -> check genesis history

/// IF41-DOMAIN implementation (T13). `Kernel.domain` is the frozen DomainKernel record; the functions beside it take
/// the full history or an explicit Command where the frozen shapes cannot carry one.
[<RequireQualifiedAccess>]
module Kernel =
    /// Replay genesis: an investigation's history starts at version 1 (its InvestigationStarted event).
    let genesis = Evolve.genesis

    /// The full investigation state of a history: contiguity first (ReplayOrder), then Evolve for each event.
    let replayState (history: EventEnvelope list) : Result<InvestigationState, ReplayError> =
        let step (state: Result<InvestigationState option, ReplayError>) (envelope: EventEnvelope) =
            state
            |> Result.bind (fun current ->
                match DomainEvent.ofPayload envelope.Payload with
                | None ->
                    let (EventPayload kind) = envelope.Payload
                    Error(UnknownEventKind kind)
                | Some _ -> Evolve.evolve current envelope |> Result.map Some |> Result.mapError IllegalEvent)

        ReplayOrder.contiguousFrom genesis history
        |> Result.bind (List.fold step (Ok None))
        |> Result.bind (function
            | Some state -> Ok state
            | None -> Error EmptyHistory)

    let replay (history: EventEnvelope list) : Result<Investigation, ReplayError> =
        replayState history |> Result.map InvestigationState.summary

    /// The command a bare frozen CommandEnvelope stands for (it has no body): StartInvestigation when there is no
    /// investigation, RecordObservation on an existing one.
    let bareCommand (state: Investigation option) : Command =
        match state with
        | None -> StartInvestigation
        | Some _ -> RecordObservation

    let decide (policy: PolicySet) (actor: ActorContext) (state: Investigation option) (envelope: CommandEnvelope) : Decision =
        Decide.decideOnSummary policy actor state { Envelope = envelope; Command = bareCommand state }

    /// The full decision against a history ([] for a new investigation): lifecycle, policy gates and duplicates.
    /// A history that does not replay is refused as forged.
    let decideOn (policy: PolicySet) (actor: ActorContext) (history: EventEnvelope list) (submission: Submission) : Decision =
        let decideWith state = Decide.decide policy actor state submission

        match history with
        | [] -> decideWith None
        | _ ->
            match replayState history with
            | Ok state -> decideWith (Some state)
            | Error error -> Blocked [ Blockers.make "forged-history" (sprintf "The history does not replay: %A" error) ]

    /// Envelopes for allowed payloads: ids derived from the command (EventIds.forCommand), versions after the state.
    let stamp (command: CommandEnvelope) (state: Investigation option) (payloads: EventPayload list) : EventEnvelope list =
        let first = state |> Option.map (fun s -> Evolve.successor s.Version) |> Option.defaultValue genesis
        let (AggregateVersion v0) = first

        payloads
        |> List.mapi (fun i payload ->
            { EventId = EventIds.forCommand command.CommandId i
              Investigation = command.Investigation
              Version = AggregateVersion(v0 + int64 i)
              Payload = payload })

    /// The projection of a summary: no history is available, so the timeline is empty and the capabilities are the
    /// commands a bare envelope can decide on an existing investigation.
    let project (investigation: Investigation) : InvestigationProjection =
        { Investigation = investigation
          Timeline = []
          Capabilities = [ Command.name (bareCommand (Some investigation)) ] }

    /// The projection of a history, with every capability and the reasons for the blocked ones.
    let projectHistory (policy: PolicySet) (actor: ActorContext) (history: EventEnvelope list) : Result<InvestigationProjection * Capability list, ReplayError> =
        replayState history
        |> Result.map (fun state ->
            let capabilities = Capabilities.derive policy actor (Some state)

            { Investigation = InvestigationState.summary state
              Timeline = history
              Capabilities = Capabilities.availableNames capabilities },
            capabilities)

    let domain: DomainKernel =
        { Decide = decide
          Evolve = Evolve.evolveSummary
          Replay = replay
          Project = project }

/// A JSON value as the frozen causa-storage v1 contract stores it (CONTRACT.md §2.3: no floats, integers only). Not a
/// frozen IF41-DOMAIN shape: the frozen EventPayload carries only the event kind, so the v1 fold below takes the
/// decoded envelope with its payload (decoding bytes stays the storage codec's job).
type V1Value =
    | VNull
    | VBool of bool
    | VInt of int64
    | VText of string
    | VArray of V1Value list
    | VObject of (string * V1Value) list

/// One decoded events.ndjson line (CONTRACT.md §5).
type V1Event =
    { EventId: EventId
      Investigation: InvestigationId
      Version: AggregateVersion
      EventType: string
      OccurredAt: string
      Actor: V1Value
      CorrelationId: Guid
      Payload: V1Value }

/// The kernel's refusals of a v1 history, in the contract's check order (§11.1 stages 6, 10, 11, 12, 15).
type V1Refusal =
    | V1UnknownEventType of eventType: string
    | V1InvestigationMismatch of at: AggregateVersion
    | V1EventIdDuplicate of EventId
    | V1SequenceInvalid of expected: AggregateVersion * found: AggregateVersion
    | V1HistoryInadmissible of domainCode: string * at: AggregateVersion * detail: string

type V1Entity = Map<string, V1Value>

/// The §8.2 state object as folded by §8.3.
type V1State =
    { InvestigationId: InvestigationId
      Title: V1Value
      Lifecycle: string
      Version: AggregateVersion
      CreatedAt: string
      /// entity array name -> id -> entity
      Arrays: Map<string, Map<string, V1Entity>> }

/// IF41-DOMAIN companion (T15B): replay of the frozen causa-storage v1 histories (kemiller2002/causa-data,
/// causa-storage-v1.0.0): the 31-type event registry (§7), the normative projection (§8.3), its canonical text
/// (§2.2 RFC 8785) and the kernel's refusals (§11.1, §11.3). Pure.
[<RequireQualifiedAccess>]
module StorageV1Replay =
    /// §7: exactly the engine's EventBody cases, in order.
    let registry =
        [ "InvestigationStarted"; "LifecycleChanged"; "ObservationRecorded"; "HypothesisRecorded"; "HypothesisTransitioned"
          "EvidenceAdmitted"; "EvidenceSuperseded"; "EvidenceVerificationAssessed"; "EvidenceRelated"; "UnknownIdentified"
          "UnknownTransitioned"; "AssumptionRecorded"; "AssumptionTransitioned"; "ContradictionRecorded"
          "ContradictionTransitioned"; "PredictionRecorded"; "ActionRecorded"; "ActionTransitioned"; "FindingRecorded"
          "NegativeKnowledgeEstablished"; "CausalAssertionRecorded"; "CausalAssertionConfirmed"; "CausalAssertionReopened"
          "CorrectiveActionRecorded"; "CorrectiveActionTransitioned"; "CriterionRecorded"; "VerificationRecorded"
          "ConclusionRecorded"; "ProposalRecorded"; "ProposalWasAccepted"; "ProposalWasRejected" ]

    let private registered = Set.ofList registry

    /// §8.2: the 17 entity arrays.
    let entityArrays =
        [ "observations"; "hypotheses"; "evidence"; "relationships"; "unknowns"; "assumptions"; "contradictions"
          "predictions"; "actions"; "findings"; "negativeKnowledge"; "causes"; "correctiveActions"; "criteria"
          "verifications"; "conclusions"; "proposals" ]

    let refusalCode (refusal: V1Refusal) =
        match refusal with
        | V1UnknownEventType _ -> "CAUSA.UNKNOWN_EVENT_TYPE"
        | V1InvestigationMismatch _ -> "CAUSA.INVESTIGATION_MISMATCH"
        | V1EventIdDuplicate _ -> "CAUSA.EVENT_ID_DUPLICATE"
        | V1SequenceInvalid _ -> "CAUSA.SEQUENCE_INVALID"
        | V1HistoryInadmissible _ -> "CAUSA.HISTORY_INADMISSIBLE"

    // ----- canonical text (RFC 8785 for the v1 value space: no floats)

    let private escape (s: string) =
        let sb = Text.StringBuilder(s.Length + 2)
        sb.Append '"' |> ignore

        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\012' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore

        sb.Append('"').ToString()

    /// §2.2: keys sorted by UTF-16 code units (ordinal), no insignificant whitespace.
    let rec canonicalText (value: V1Value) : string =
        match value with
        | VNull -> "null"
        | VBool true -> "true"
        | VBool false -> "false"
        | VInt n -> string n
        | VText s -> escape s
        | VArray items -> "[" + (items |> List.map canonicalText |> String.concat ",") + "]"
        | VObject fields ->
            let body =
                fields
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                |> List.map (fun (k, v) -> escape k + ":" + canonicalText v)
                |> String.concat ","

            "{" + body + "}"

    /// §2.11: SHA-256 of the canonical text (no trailing LF), lowercase hex.
    let sha256 (value: V1Value) =
        Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(canonicalText value))).ToLowerInvariant()

    // ----- accessors

    let field (name: string) (value: V1Value) : V1Value =
        match value with
        | VObject fields -> fields |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.defaultValue VNull
        | _ -> VNull

    let private text name value =
        match field name value with
        | VText s -> Some s
        | _ -> None

    let private texts name value =
        match field name value with
        | VArray items -> items |> List.choose (function VText s -> Some s | _ -> None)
        | _ -> []

    let private intOf name (value: V1Value) =
        match field name value with
        | VInt n -> n
        | _ -> 0L

    let private entities (state: V1State) name =
        state.Arrays |> Map.tryFind name |> Option.defaultValue Map.empty

    let private entity (state: V1State) name id = entities state name |> Map.tryFind id
    let private exists state name id = entity state name id |> Option.isSome

    let private ent (e: V1Entity) k = e |> Map.tryFind k |> Option.defaultValue VNull

    let private entText (e: V1Entity) k =
        match ent e k with
        | VText s -> Some s
        | _ -> None

    let private entTexts (e: V1Entity) k =
        match ent e k with
        | VArray items -> items |> List.choose (function VText s -> Some s | _ -> None)
        | _ -> []

    let private entInt (e: V1Entity) k =
        match ent e k with
        | VInt n -> n
        | _ -> 0L

    // ----- projection (§8.3)

    let private add name (fields: (string * V1Value) list) (provenance: V1Value) (state: V1State) =
        let e = Map.ofList (("provenance", provenance) :: fields)

        match ent e "id" with
        | VText id -> { state with Arrays = state.Arrays |> Map.add name (entities state name |> Map.add id e) }
        | _ -> state

    let private update name (id: V1Value) (f: V1Entity -> V1Entity) (state: V1State) =
        match id with
        | VText key ->
            match entity state name key with
            | Some e -> { state with Arrays = state.Arrays |> Map.add name (entities state name |> Map.add key (f e)) }
            | None -> state
        | _ -> state

    let private set k v (e: V1Entity) = Map.add k v e

    let private versionValue (AggregateVersion v) = VInt v

    let private provenanceOf (event: V1Event) =
        VObject [ "actor", event.Actor; "recordedAt", VText event.OccurredAt ]

    /// InvestigationStarted creates the state (§8.3, first row).
    let start (event: V1Event) : V1State =
        let p = event.Payload

        { InvestigationId = event.Investigation
          Title = field "title" p
          Lifecycle = "draft"
          Version = event.Version
          CreatedAt = event.OccurredAt
          Arrays = entityArrays |> List.map (fun a -> a, Map.empty) |> Map.ofList }
        |> add "observations" [ "id", field "observationId" p; "original", field "original" p; "observedAt", VNull ] (provenanceOf event)

    /// One §8.3 row for an event after the first.
    let apply (state: V1State) (event: V1Event) : V1State =
        let p = event.Payload
        let pf k = field k p
        let version = versionValue event.Version
        let state = { state with Version = event.Version }
        let addE name fields = add name fields (provenanceOf event) state

        match event.EventType with
        | "LifecycleChanged" ->
            match pf "to" with
            | VText target -> { state with Lifecycle = target }
            | _ -> state
        | "ObservationRecorded" -> addE "observations" [ "id", pf "observationId"; "original", pf "original"; "observedAt", pf "observedAt" ]
        | "HypothesisRecorded" ->
            addE "hypotheses" [ "id", pf "hypothesisId"; "statement", pf "statement"; "mechanism", pf "mechanism"; "state", VText "proposed"; "history", VArray [] ]
        | "HypothesisTransitioned" ->
            let change =
                VObject
                    [ "from", pf "from"; "to", pf "to"; "command", pf "command"; "reason", pf "reason"; "evidenceIds", pf "evidenceIds"
                      "at", VText event.OccurredAt; "aggregateVersion", version ]

            state
            |> update "hypotheses" (pf "hypothesisId") (fun h ->
                let history =
                    match ent h "history" with
                    | VArray xs -> xs
                    | _ -> []

                h |> set "state" (pf "to") |> set "history" (VArray(history @ [ change ])))
        | "EvidenceAdmitted" ->
            addE
                "evidence"
                [ "id", pf "evidenceId"; "contentKind", pf "contentKind"; "content", pf "content"; "category", pf "category"
                  "source", pf "source"; "capturedAt", pf "capturedAt"; "verification", VText "unverified"; "supersededBy", VNull
                  "admittedAtVersion", version ]
        | "EvidenceSuperseded" -> state |> update "evidence" (pf "evidenceId") (set "supersededBy" (pf "replacementEvidenceId"))
        | "EvidenceVerificationAssessed" -> state |> update "evidence" (pf "evidenceId") (set "verification" (pf "verification"))
        | "EvidenceRelated" ->
            addE "relationships" [ "id", pf "relationshipId"; "evidenceId", pf "evidenceId"; "hypothesisId", pf "hypothesisId"; "kind", pf "kind"; "rationale", pf "rationale" ]
        | "UnknownIdentified" ->
            addE "unknowns" [ "id", pf "unknownId"; "question", pf "question"; "materiality", pf "materiality"; "state", VText "identified"; "basis", VNull ]
        | "UnknownTransitioned" -> state |> update "unknowns" (pf "unknownId") (set "state" (pf "state") >> set "basis" (pf "basis"))
        | "AssumptionRecorded" ->
            addE "assumptions" [ "id", pf "assumptionId"; "proposition", pf "proposition"; "materiality", pf "materiality"; "state", VText "explicitUnverified" ]
        | "AssumptionTransitioned" -> state |> update "assumptions" (pf "assumptionId") (set "state" (pf "state"))
        | "ContradictionRecorded" ->
            addE
                "contradictions"
                [ "id", pf "contradictionId"; "statement", pf "statement"; "evidenceIds", pf "evidenceIds"; "materiality", pf "materiality"
                  "state", VText "detected"; "basis", VNull ]
        | "ContradictionTransitioned" -> state |> update "contradictions" (pf "contradictionId") (set "state" (pf "state") >> set "basis" (pf "basis"))
        | "PredictionRecorded" ->
            addE "predictions" [ "id", pf "predictionId"; "hypothesisId", pf "hypothesisId"; "statement", pf "statement"; "strength", pf "strength"; "timing", pf "timing" ]
        | "ActionRecorded" ->
            addE "actions" [ "id", pf "actionId"; "kind", pf "kind"; "purpose", pf "purpose"; "targetHypothesisIds", pf "targetHypothesisIds"; "state", VText "proposed" ]
        | "ActionTransitioned" -> state |> update "actions" (pf "actionId") (set "state" (pf "state"))
        | "FindingRecorded" ->
            addE "findings" [ "id", pf "findingId"; "actionId", pf "actionId"; "originalResult", pf "originalResult"; "interpretation", pf "interpretation" ]
        | "NegativeKnowledgeEstablished" ->
            addE "negativeKnowledge" [ "id", pf "negativeKnowledgeId"; "statement", pf "statement"; "scope", pf "scope"; "basisEvidenceIds", pf "basisEvidenceIds" ]
        | "CausalAssertionRecorded" ->
            addE "causes" [ "id", pf "causeId"; "outcome", pf "outcome"; "members", pf "members"; "mechanism", pf "mechanism"; "state", VText "proposed" ]
        | "CausalAssertionConfirmed" -> state |> update "causes" (pf "causeId") (set "state" (VText "confirmed"))
        | "CausalAssertionReopened" -> state |> update "causes" (pf "causeId") (set "state" (VText "reopened"))
        | "CorrectiveActionRecorded" ->
            addE
                "correctiveActions"
                [ "id", pf "correctiveActionId"; "kind", pf "kind"; "description", pf "description"; "targetCauseIds", pf "targetCauseIds"
                  "state", VText "proposed"; "awaitingSinceVersion", VInt 0L ]
        | "CorrectiveActionTransitioned" ->
            state
            |> update "correctiveActions" (pf "correctiveActionId") (fun ca ->
                let ca = ca |> set "state" (pf "state")
                if pf "state" = VText "awaitingVerificationOfEffect" then ca |> set "awaitingSinceVersion" version else ca)
        | "CriterionRecorded" ->
            addE "criteria" [ "id", pf "criterionId"; "correctiveActionId", pf "correctiveActionId"; "expectedOutcome", pf "expectedOutcome"; "method", pf "method" ]
        | "VerificationRecorded" ->
            addE "verifications" [ "id", pf "verificationId"; "criterionId", pf "criterionId"; "outcome", pf "outcome"; "evidenceIds", pf "evidenceIds"; "recordedAtVersion", version ]
        | "ConclusionRecorded" ->
            addE
                "conclusions"
                [ "id", pf "conclusionId"; "statement", pf "statement"; "strength", pf "strength"; "causeIds", pf "causeIds"
                  "residualUncertainty", pf "residualUncertainty"; "recordedAtVersion", version ]
        | "ProposalRecorded" ->
            addE "proposals" [ "id", pf "proposalId"; "kind", pf "kind"; "text", pf "text"; "rationale", pf "rationale"; "state", VText "active"; "decision", VNull ]
        | "ProposalWasAccepted" -> state |> update "proposals" (pf "proposalId") (set "state" (VText "accepted"))
        | "ProposalWasRejected" -> state |> update "proposals" (pf "proposalId") (set "state" (VText "rejected") >> set "decision" (pf "reason"))
        | _ -> state

    /// The §8.2 state object: every entity array sorted by id, ordinal comparison of the UUID text (§8.3).
    let stateObject (state: V1State) : V1Value =
        let (InvestigationId id) = state.InvestigationId
        let (AggregateVersion version) = state.Version

        let arrays =
            entityArrays
            |> List.map (fun name ->
                let items =
                    entities state name
                    |> Map.toList
                    |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                    |> List.map (fun (_, e) -> VObject(Map.toList e))

                name, VArray items)

        VObject(
            [ "investigationId", VText(id.ToString "D")
              "title", state.Title
              "lifecycle", VText state.Lifecycle
              "version", VInt version
              "createdAt", VText state.CreatedAt ]
            @ arrays
        )

    /// expectedStateSha256 (§8.4).
    let stateSha256 (state: V1State) = sha256 (stateObject state)

    // ----- admissibility (§11.3): the engine rules this kernel enforces, decided against the prior state.

    let private isOpen lifecycle =
        match lifecycle with
        | "draft"
        | "active"
        | "awaitingEvidence"
        | "awaitingVerification" -> true
        | _ -> false

    let private isMaterial (e: V1Entity) =
        match entText e "materiality" with
        | Some "blocking"
        | Some "material" -> true
        | _ -> false

    let private lifecycleAllowed (command: string) (source: string) (target: string) =
        match command, target with
        | "activateInvestigation", "active" -> source = "draft" || source = "awaitingEvidence" || source = "awaitingVerification"
        | "markAwaitingEvidence", "awaitingEvidence" -> source = "active"
        | "markAwaitingVerification", "awaitingVerification" -> source = "active" || source = "awaitingEvidence"
        | "resolveInvestigation", "resolved" -> source = "active" || source = "awaitingEvidence" || source = "awaitingVerification"
        | "closeInvestigation", "closed" -> source = "resolved"
        | "reopenInvestigation", "active" -> source = "resolved" || source = "closed"
        | "verificationFailed", "active" -> source = "awaitingVerification"
        | _ -> false

    let private hypothesisAllowed (command: string) (source: string) (target: string) =
        let live = [ "proposed"; "plausible"; "supported"; "weakened" ]

        match command, target with
        | "markPlausible", "plausible" -> source = "proposed" || source = "weakened"
        | "markSupported", "supported" -> source = "plausible" || source = "weakened"
        | "markWeakened", "weakened" -> source = "plausible" || source = "supported"
        | "falsify", "falsified" -> List.contains source live
        | "supersede", "superseded" -> List.contains source live
        | "reopen", "plausible" -> source = "falsified" || source = "superseded"
        | _ -> false

    /// The confirmation gate of a cause (§15): codes of every unmet condition, empty when the gate holds.
    let private causeGate (state: V1State) (cause: V1Entity) : string list =
        let members =
            match ent cause "members" with
            | VArray ms -> ms
            | _ -> []

        let memberIds = members |> List.choose (text "hypothesisId") |> Set.ofList
        let roles = members |> List.choose (text "role")

        let memberNotSupported =
            memberIds
            |> Set.exists (fun id -> entity state "hypotheses" id |> Option.bind (fun h -> entText h "state") <> Some "supported")

        let openContradiction =
            entities state "contradictions"
            |> Map.exists (fun _ c -> isMaterial c && List.contains (entText c "state" |> Option.defaultValue "") [ "detected"; "triaged"; "investigating" ])

        let openUnknown =
            entities state "unknowns"
            |> Map.exists (fun _ u -> isMaterial u && List.contains (entText u "state" |> Option.defaultValue "") [ "identified"; "investigating" ])

        let unverifiedAssumption =
            entities state "assumptions"
            |> Map.exists (fun _ a -> isMaterial a && entText a "state" = Some "explicitUnverified")

        let liveAlternative =
            entities state "hypotheses"
            |> Map.exists (fun id h ->
                not (Set.contains id memberIds)
                && List.contains (entText h "state" |> Option.defaultValue "") [ "proposed"; "plausible"; "supported" ])

        [ not (List.contains "rootCause" roles), "CAUSE.NO_ROOT"
          memberNotSupported, "CAUSE.MEMBER_NOT_SUPPORTED"
          openContradiction, "CAUSE.OPEN_CONTRADICTION"
          openUnknown, "CAUSE.OPEN_UNKNOWN"
          unverifiedAssumption, "CAUSE.UNVERIFIED_ASSUMPTION"
          liveAlternative, "CAUSE.ALTERNATIVE_NOT_ADDRESSED"
          ent cause "mechanism" = VNull, "CAUSE.NO_MECHANISM" ]
        |> List.filter fst
        |> List.map snd

    /// The resolution gate (R6.4, R7.2-R7.4): codes of every unmet condition.
    let private resolutionGate (state: V1State) : string list =
        let confirmed = entities state "causes" |> Map.filter (fun _ c -> entText c "state" = Some "confirmed")

        let corrections =
            entities state "correctiveActions"
            |> Map.filter (fun _ ca -> entText ca "kind" = Some "correction" && entTexts ca "targetCauseIds" |> List.exists (fun id -> Map.containsKey id confirmed))

        let criterionSatisfied criterionId =
            entities state "verifications"
            |> Map.toList
            |> List.map snd
            |> List.filter (fun v -> entText v "criterionId" = Some criterionId)
            |> List.sortBy (fun v -> entInt v "recordedAtVersion")
            |> List.tryLast
            |> Option.bind (fun v -> entText v "outcome")
            |> (=) (Some "satisfied")

        let criteriaOf caId =
            entities state "criteria" |> Map.filter (fun _ c -> entText c "correctiveActionId" = Some caId) |> Map.toList |> List.map fst

        let conclusions =
            entities state "conclusions" |> Map.toList |> List.map snd |> List.sortBy (fun c -> entInt c "recordedAtVersion")

        let residualAccepted =
            [ "unknowns"; "contradictions" ]
            |> List.exists (fun name -> entities state name |> Map.exists (fun _ e -> entText e "state" = Some "acceptedResidual"))

        [ Map.isEmpty confirmed, "RESOLVE.NO_CONFIRMED_CAUSE"
          confirmed |> Map.exists (fun _ c -> not (List.isEmpty (causeGate state c |> List.filter ((<>) "CAUSE.ALTERNATIVE_NOT_ADDRESSED")))),
          "RESOLVE.CAUSE_NO_LONGER_HOLDS"
          not (Map.isEmpty confirmed) && Map.isEmpty corrections, "RESOLVE.NO_CORRECTION"
          corrections |> Map.exists (fun id ca -> entText ca "state" <> Some "verifiedEffective" || List.isEmpty (criteriaOf id)),
          "RESOLVE.CORRECTION_NOT_VERIFIED"
          corrections |> Map.exists (fun id _ -> criteriaOf id |> List.exists (criterionSatisfied >> not)), "RESOLVE.CRITERION_UNSATISFIED"
          List.isEmpty conclusions, "RESOLVE.NO_CONCLUSION"
          residualAccepted && (conclusions |> List.tryLast |> Option.exists (fun c -> ent c "residualUncertainty" = VNull)),
          "RESOLVE.RESIDUAL_NOT_STATED" ]
        |> List.filter fst
        |> List.map snd

    /// The array and payload field a creating event's new id lives in.
    let private creates =
        Map.ofList
            [ "ObservationRecorded", ("observations", "observationId")
              "HypothesisRecorded", ("hypotheses", "hypothesisId")
              "EvidenceAdmitted", ("evidence", "evidenceId")
              "EvidenceRelated", ("relationships", "relationshipId")
              "UnknownIdentified", ("unknowns", "unknownId")
              "AssumptionRecorded", ("assumptions", "assumptionId")
              "ContradictionRecorded", ("contradictions", "contradictionId")
              "PredictionRecorded", ("predictions", "predictionId")
              "ActionRecorded", ("actions", "actionId")
              "FindingRecorded", ("findings", "findingId")
              "NegativeKnowledgeEstablished", ("negativeKnowledge", "negativeKnowledgeId")
              "CausalAssertionRecorded", ("causes", "causeId")
              "CorrectiveActionRecorded", ("correctiveActions", "correctiveActionId")
              "CriterionRecorded", ("criteria", "criterionId")
              "VerificationRecorded", ("verifications", "verificationId")
              "ConclusionRecorded", ("conclusions", "conclusionId")
              "ProposalRecorded", ("proposals", "proposalId") ]

    /// Every (array, id) an event refers to and that must already exist.
    let private references (event: V1Event) : (string * string) list =
        let p = event.Payload
        let one name k = text k p |> Option.map (fun id -> name, id) |> Option.toList
        let many name k = texts k p |> List.map (fun id -> name, id)

        match event.EventType with
        | "HypothesisTransitioned" -> one "hypotheses" "hypothesisId" @ many "evidence" "evidenceIds"
        | "EvidenceSuperseded" -> one "evidence" "evidenceId" @ one "evidence" "replacementEvidenceId"
        | "EvidenceVerificationAssessed" -> one "evidence" "evidenceId"
        | "EvidenceRelated" -> one "evidence" "evidenceId" @ one "hypotheses" "hypothesisId"
        | "UnknownTransitioned" -> one "unknowns" "unknownId"
        | "AssumptionTransitioned" -> one "assumptions" "assumptionId"
        | "ContradictionRecorded" -> many "evidence" "evidenceIds"
        | "ContradictionTransitioned" -> one "contradictions" "contradictionId"
        | "PredictionRecorded" -> one "hypotheses" "hypothesisId"
        | "ActionRecorded" -> many "hypotheses" "targetHypothesisIds"
        | "ActionTransitioned" -> one "actions" "actionId"
        | "FindingRecorded" -> one "actions" "actionId"
        | "NegativeKnowledgeEstablished" -> many "evidence" "basisEvidenceIds"
        | "CausalAssertionRecorded" ->
            match field "members" p with
            | VArray ms -> ms |> List.choose (text "hypothesisId") |> List.map (fun id -> "hypotheses", id)
            | _ -> []
        | "CausalAssertionConfirmed"
        | "CausalAssertionReopened" -> one "causes" "causeId"
        | "CorrectiveActionRecorded" -> many "causes" "targetCauseIds"
        | "CorrectiveActionTransitioned" -> one "correctiveActions" "correctiveActionId"
        | "CriterionRecorded" -> one "correctiveActions" "correctiveActionId"
        | "VerificationRecorded" -> one "criteria" "criterionId" @ many "evidence" "evidenceIds"
        | "ConclusionRecorded" -> many "causes" "causeIds"
        | "ProposalWasAccepted"
        | "ProposalWasRejected" -> one "proposals" "proposalId"
        | _ -> []

    let private wellFormedArtifact (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, NonNull uri -> (uri.Scheme = "http" || uri.Scheme = "https") && uri.Host <> "" && not (url |> Seq.exists Char.IsWhiteSpace)
        | _ -> false

    /// The first rule the event breaks against the state before it, with its engine code.
    let private verdict (state: V1State) (event: V1Event) : (string * string) option =
        let p = event.Payload
        let pText k = text k p |> Option.defaultValue ""
        let first conditions = conditions |> List.tryFind fst |> Option.map snd

        let creation =
            creates
            |> Map.tryFind event.EventType
            |> Option.bind (fun (name, k) -> text k p |> Option.filter (exists state name) |> Option.map (fun id -> "DUPLICATE_ID", sprintf "%s %s already exists" name id))

        let dangling =
            references event
            |> List.tryFind (fun (name, id) -> not (exists state name id))
            |> Option.map (fun (name, id) -> "NOT_FOUND", sprintf "%s %s does not exist" name id)

        let specific () =
            match event.EventType with
            | "InvestigationStarted" -> Some("HISTORY.INADMISSIBLE_EVENT", "InvestigationStarted on an existing investigation")
            | "LifecycleChanged" ->
                let command = pText "command"
                let target = pText "to"

                first
                    [ pText "from" <> state.Lifecycle || not (lifecycleAllowed command state.Lifecycle target),
                      ("LIFECYCLE.TRANSITION", sprintf "%s from %s to %s" command state.Lifecycle target)
                      command = "markAwaitingVerification"
                      && not (
                          entities state "correctiveActions"
                          |> Map.exists (fun _ ca -> List.contains (entText ca "state" |> Option.defaultValue "") [ "implemented"; "awaitingVerificationOfEffect"; "verifiedEffective" ])
                      ),
                      ("LIFECYCLE.NO_VERIFICATION_WORK", "no implemented corrective action")
                      command = "resolveInvestigation" && not (List.isEmpty (resolutionGate state)),
                      ("HISTORY.UNGATED_RESOLUTION", String.concat "; " (resolutionGate state)) ]
            | _ when not (isOpen state.Lifecycle) -> Some("INVESTIGATION.NOT_OPEN", sprintf "%s in a %s investigation" event.EventType state.Lifecycle)
            | "HypothesisTransitioned" ->
                let current = entity state "hypotheses" (pText "hypothesisId")
                let source = current |> Option.bind (fun h -> entText h "state") |> Option.defaultValue ""
                let command = pText "command"

                let lastFalsifiedAt =
                    match current |> Option.map (fun h -> ent h "history") with
                    | Some(VArray changes) ->
                        changes |> List.filter (fun c -> text "to" c = Some "falsified" || text "to" c = Some "superseded") |> List.map (intOf "aggregateVersion") |> List.fold max 0L
                    | _ -> 0L

                let newEvidence =
                    texts "evidenceIds" p
                    |> List.exists (fun id -> entity state "evidence" id |> Option.exists (fun e -> entInt e "admittedAtVersion" > lastFalsifiedAt))

                first
                    [ pText "from" <> source || not (hypothesisAllowed command source (pText "to")),
                      ("HYPOTHESIS.TRANSITION", sprintf "%s from %s to %s" command source (pText "to"))
                      command = "reopen" && not newEvidence, ("HYPOTHESIS.REOPEN_NEEDS_NEW_EVIDENCE", "no evidence admitted after the falsification") ]
            | "EvidenceAdmitted" ->
                first [ pText "contentKind" = "artifact" && not (wellFormedArtifact (pText "content")), ("EVIDENCE.URL", "malformed artifact URL") ]
            | "EvidenceSuperseded" ->
                first
                    [ pText "evidenceId" = pText "replacementEvidenceId", ("EVIDENCE.SELF", "evidence superseded by itself")
                      entity state "evidence" (pText "evidenceId") |> Option.exists (fun e -> ent e "supersededBy" <> VNull),
                      ("EVIDENCE.ALREADY_SUPERSEDED", "evidence already superseded") ]
            | "PredictionRecorded" ->
                let hypothesis = pText "hypothesisId"

                let relevantFinding =
                    entities state "findings"
                    |> Map.exists (fun _ f ->
                        entText f "actionId"
                        |> Option.bind (entity state "actions")
                        |> Option.exists (fun a -> entTexts a "targetHypothesisIds" |> List.contains hypothesis))

                first [ pText "timing" = "prospective" && relevantFinding, ("PREDICTION.TIMING", "prospective after a relevant finding") ]
            | "FindingRecorded" ->
                let actionState = entity state "actions" (pText "actionId") |> Option.bind (fun a -> entText a "state")
                first [ actionState = Some "rejected" || actionState = Some "cancelled", ("ACTION.NOT_EXECUTABLE", "finding for a rejected or cancelled action") ]
            | "CausalAssertionConfirmed" ->
                match entity state "causes" (pText "causeId") with
                | Some cause when entText cause "state" = Some "confirmed" -> Some("HISTORY.UNGATED_CONFIRMATION", "CAUSE.STATE")
                | Some cause ->
                    match causeGate state cause with
                    | [] -> None
                    | codes -> Some("HISTORY.UNGATED_CONFIRMATION", String.concat "; " codes)
                | None -> None
            | "CorrectiveActionTransitioned" ->
                let ca = pText "correctiveActionId"
                let hasCriteria = entities state "criteria" |> Map.exists (fun _ c -> entText c "correctiveActionId" = Some ca)
                first [ pText "state" = "awaitingVerificationOfEffect" && not hasCriteria, ("CORRECTION.NO_CRITERIA", "verification of effect without criteria") ]
            | "VerificationRecorded" ->
                let since =
                    entity state "criteria" (pText "criterionId")
                    |> Option.bind (fun c -> entText c "correctiveActionId")
                    |> Option.bind (entity state "correctiveActions")
                    |> Option.map (fun ca -> entInt ca "awaitingSinceVersion")
                    |> Option.defaultValue 0L

                let predates =
                    texts "evidenceIds" p
                    |> List.exists (fun id -> entity state "evidence" id |> Option.exists (fun e -> entInt e "admittedAtVersion" < since))

                first [ pText "outcome" = "satisfied" && predates, ("VERIFICATION.EVIDENCE_PREDATES_ROUND", "evidence admitted before the verification round") ]
            | "ConclusionRecorded" ->
                let causes = texts "causeIds" p
                let unconfirmed = causes |> List.exists (fun id -> entity state "causes" id |> Option.bind (fun c -> entText c "state") <> Some "confirmed")

                let blockingOpen =
                    entities state "unknowns"
                    |> Map.exists (fun _ u -> entText u "materiality" = Some "blocking" && List.contains (entText u "state" |> Option.defaultValue "") [ "identified"; "investigating" ])

                first
                    [ pText "strength" = "confirmedCause" && (List.isEmpty causes || unconfirmed), ("CONCLUSION.UNSUPPORTED", "confirmed conclusion on an unconfirmed cause")
                      blockingOpen && field "residualUncertainty" p = VNull, ("CONCLUSION.RESIDUAL_REQUIRED", "residual uncertainty required") ]
            | "ProposalRecorded" ->
                let repeats =
                    pText "kind" = "hypothesis"
                    && entities state "hypotheses" |> Map.exists (fun _ h -> entText h "state" = Some "falsified" && entText h "statement" = Some(pText "text"))

                first [ repeats, ("PROPOSAL.REPEATS_NEGATIVE_KNOWLEDGE", "the proposal repeats a falsified hypothesis") ]
            | "ProposalWasAccepted"
            | "ProposalWasRejected" ->
                let proposalState = entity state "proposals" (pText "proposalId") |> Option.bind (fun pr -> entText pr "state")
                first [ proposalState <> Some "active", ("PROPOSAL.NOT_ACTIVE", "the proposal is not active") ]
            | _ -> None

        match creation, dangling with
        | Some reason, _ -> Some reason
        | None, Some reason -> Some reason
        | None, None -> specific ()

    /// §5.4: the events of one command share occurredAt and actor.
    let private groupAgrees (previous: V1Event) (event: V1Event) =
        previous.CorrelationId <> event.CorrelationId || (previous.OccurredAt = event.OccurredAt && previous.Actor = event.Actor)

    let private successor (AggregateVersion v) = AggregateVersion(v + 1L)

    /// Replays a decoded v1 history in file order, refusing it at the first failing stage (§11.1).
    let replay (history: V1Event list) : Result<V1State, V1Refusal> =
        let unknown = history |> List.tryFind (fun e -> not (Set.contains e.EventType registered))

        let mismatch =
            match history with
            | first :: rest -> rest |> List.tryFind (fun e -> e.Investigation <> first.Investigation)
            | [] -> None

        let duplicate =
            history
            |> List.countBy _.EventId
            |> List.tryFind (fun (_, n) -> n > 1)
            |> Option.map fst

        let sequence =
            history
            |> List.mapi (fun i e -> AggregateVersion(int64 i + 1L), e.Version)
            |> List.tryFind (fun (expected, found) -> expected <> found)

        let inadmissible code (event: V1Event) detail = Error(V1HistoryInadmissible(code, event.Version, detail))

        match history, unknown, mismatch, duplicate, sequence with
        | _, Some e, _, _, _ -> Error(V1UnknownEventType e.EventType)
        | _, _, Some e, _, _ -> Error(V1InvestigationMismatch e.Version)
        | _, _, _, Some id, _ -> Error(V1EventIdDuplicate id)
        | _, _, _, _, Some(expected, found) -> Error(V1SequenceInvalid(expected, found))
        | [], _, _, _, _ -> Error(V1SequenceInvalid(AggregateVersion 1L, AggregateVersion 0L))
        | first :: _, _, _, _, _ when first.EventType <> "InvestigationStarted" -> inadmissible "HISTORY.NO_START" first "the history does not begin with InvestigationStarted"
        | first :: rest, _, _, _, _ ->
            let step (acc: Result<V1State * V1Event, V1Refusal>) (event: V1Event) =
                acc
                |> Result.bind (fun (state, previous) ->
                    if not (groupAgrees previous event) then
                        inadmissible "HISTORY.INADMISSIBLE_EVENT" event "one command's events disagree on occurredAt or actor"
                    else
                        match verdict state event with
                        | Some(code, detail) when code.StartsWith "HISTORY." -> inadmissible code event detail
                        | Some(code, detail) -> inadmissible "HISTORY.INADMISSIBLE_EVENT" event (code + ": " + detail)
                        | None -> Ok(apply state event, event))

            rest |> List.fold step (Ok(start first, first)) |> Result.map fst

    /// §9.2 optimistic append: an expected version other than the state's is a version conflict (CAUSA.VERSION_STALE).
    let checkAppend (expected: AggregateVersion) (state: V1State) : Decision option =
        if expected = state.Version then None else Some(Conflict(expected, state.Version))
