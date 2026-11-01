// Owner: domain. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.Domain

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
