// Owner: domain. WI-R8-T13: evolve, the only way an investigation state changes. Pure and total.
namespace Causa.Domain

[<RequireQualifiedAccess>]
module Evolve =
    /// The version of an investigation's first event (InvestigationStarted). Replay starts its contiguity check here.
    let genesis = AggregateVersion 1L

    let successor (AggregateVersion v) = AggregateVersion(v + 1L)

    let private fail (envelope: EventEnvelope) reason : Result<'a, ProjectionError> =
        Error { At = envelope.Version; Reason = reason }

    let private count (target: DomainEvent) (event: DomainEvent) = if event = target then 1 else 0

    /// Applies one event to the full state, refusing a wrong version, a foreign investigation, a repeated event id,
    /// an unknown kind, or a transition the lifecycle table does not allow.
    let evolve (state: InvestigationState option) (envelope: EventEnvelope) : Result<InvestigationState, ProjectionError> =
        let expected = state |> Option.map (fun s -> successor s.Version) |> Option.defaultValue genesis
        let (EventPayload kind) = envelope.Payload

        match DomainEvent.ofPayload envelope.Payload with
        | None -> fail envelope (sprintf "unknown event kind '%s'" kind)
        | Some _ when envelope.Version <> expected -> fail envelope (sprintf "expected version %A, found %A" expected envelope.Version)
        | Some _ when state |> Option.exists (fun s -> s.Id <> envelope.Investigation) -> fail envelope "event belongs to another investigation"
        | Some _ when state |> Option.exists (fun s -> Set.contains envelope.EventId s.EventIds) -> fail envelope "event id already in the history"
        | Some event ->
            match Lifecycle.transition (state |> Option.map _.Lifecycle) (DomainEvent.command event) with
            | Error blocker -> fail envelope blocker.Message
            | Ok lifecycle ->
                let before =
                    state
                    |> Option.defaultValue
                        { Id = envelope.Investigation
                          Version = envelope.Version
                          Lifecycle = lifecycle
                          Observations = 0
                          Hypotheses = 0
                          Conclusions = 0
                          EventIds = Set.empty }

                Ok
                    { before with
                        Version = envelope.Version
                        Lifecycle = lifecycle
                        Observations = before.Observations + count ObservationRecorded event
                        Hypotheses = before.Hypotheses + count HypothesisRecorded event
                        Conclusions = before.Conclusions + count ConclusionRecorded event
                        EventIds = Set.add envelope.EventId before.EventIds }

    /// Summary-level evolve over the frozen Investigation (id and version only): version, identity, known kind, and
    /// "InvestigationStarted first and only first". The full lifecycle table is applied by Replay over the history.
    let evolveSummary (state: Investigation option) (envelope: EventEnvelope) : Result<Investigation, ProjectionError> =
        let expected = state |> Option.map (fun s -> successor s.Version) |> Option.defaultValue genesis
        let (EventPayload kind) = envelope.Payload

        match DomainEvent.ofPayload envelope.Payload, state with
        | None, _ -> fail envelope (sprintf "unknown event kind '%s'" kind)
        | Some _, _ when envelope.Version <> expected -> fail envelope (sprintf "expected version %A, found %A" expected envelope.Version)
        | Some _, Some s when s.Id <> envelope.Investigation -> fail envelope "event belongs to another investigation"
        | Some InvestigationStarted, None -> Ok { Id = envelope.Investigation; Version = envelope.Version }
        | Some _, None -> fail envelope "the first event of an investigation must be InvestigationStarted"
        | Some InvestigationStarted, Some _ -> fail envelope "InvestigationStarted on an existing investigation"
        | Some _, Some s -> Ok { s with Version = envelope.Version }
