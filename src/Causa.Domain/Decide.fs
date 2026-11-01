// Owner: domain. WI-R8-T13: decide, in the evaluation order of STATE-TRANSITION-TABLES.md section 16.
// Pure and total: every command against every state yields a Decision; no exceptions for domain failures.
namespace Causa.Domain

[<RequireQualifiedAccess>]
module Decide =
    /// The aggregate version of "no investigation yet", reported in a Conflict when a create expected a version.
    let noAggregate = AggregateVersion 0L

    let private blockedUnless (blockers: Blocker list) : Result<unit, Decision> =
        match blockers with
        | [] -> Ok()
        | some -> Error(Blocked some)

    /// Step 1: the command targets this investigation.
    let identity (state: Investigation option) (envelope: CommandEnvelope) : Result<unit, Decision> =
        match state with
        | Some s when s.Id <> envelope.Investigation ->
            Error(Blocked [ Blockers.make "investigation-mismatch" "The command targets a different investigation." ])
        | Some _
        | None -> Ok()

    /// Step 2: the policy set's semantics are ones this kernel evaluates.
    let semantics (policy: PolicySet) : Result<unit, Decision> =
        if Policies.supports policy then Ok() else Error(UnsupportedSemantics [ policy.Id ])

    /// Before the version check: a resubmitted command (its first event id is already in the history) is a
    /// Duplicate naming the events it already produced, not a stale-version Conflict.
    let duplicate (state: InvestigationState option) (envelope: CommandEnvelope) : Result<unit, Decision> =
        match state with
        | None -> Ok()
        | Some s ->
            let produced =
                List.init 256 (EventIds.forCommand envelope.CommandId)
                |> List.takeWhile (fun id -> Set.contains id s.EventIds)

            match produced with
            | [] -> Ok()
            | ids -> Error(Duplicate ids)

    /// Step 3: the expected aggregate version matches; every command on an existing investigation carries one.
    let version (state: Investigation option) (envelope: CommandEnvelope) : Result<unit, Decision> =
        match state, envelope.ExpectedVersion with
        | None, None -> Ok()
        | None, Some expected -> Error(Conflict(expected, noAggregate))
        | Some s, Some expected when expected = s.Version -> Ok()
        | Some s, Some expected -> Error(Conflict(expected, s.Version))
        | Some _, None ->
            Error(Blocked [ Blockers.make "expected-version-required" "A command on an existing investigation must name the version it expects." ])

    /// Steps 4-9 for a command against a full state, without an envelope: structural transition, policy gates,
    /// authorization, then the proposed events. Capabilities are derived from this.
    let evaluate (policy: PolicySet) (actor: ActorContext) (state: InvestigationState option) (command: Command) : Decision =
        let structural () =
            Lifecycle.transition (state |> Option.map _.Lifecycle) command
            |> Result.map ignore
            |> Result.mapError (fun blocker -> Blocked [ blocker ])

        let gates () =
            match state with
            | Some s -> blockedUnless (Policies.gates s command)
            | None -> Ok()

        semantics policy
        |> Result.bind structural
        |> Result.bind gates
        |> Result.bind (fun () -> blockedUnless (Policies.authorize actor))
        |> Result.map (fun () -> Allowed [ DomainEvent.toPayload (DomainEvent.ofCommand command) ])
        |> Result.defaultWith id

    /// The full decision for a submission against the state folded from the whole history.
    let decide (policy: PolicySet) (actor: ActorContext) (state: InvestigationState option) (submission: Submission) : Decision =
        let summary = state |> Option.map InvestigationState.summary
        let envelope = submission.Envelope

        identity summary envelope
        |> Result.bind (fun () -> semantics policy)
        |> Result.bind (fun () -> duplicate state envelope)
        |> Result.bind (fun () -> version summary envelope)
        |> Result.map (fun () -> evaluate policy actor state submission.Command)
        |> Result.defaultWith id

    /// The decision when only the frozen summary (id and version) is known. The lifecycle is not in the summary, so
    /// the structural check is reduced to "Start only when there is no investigation, anything else only when there
    /// is one"; Replay applies the full table to the history.
    let decideOnSummary (policy: PolicySet) (actor: ActorContext) (state: Investigation option) (submission: Submission) : Decision =
        let envelope = submission.Envelope

        let structural () =
            match state, submission.Command with
            | None, StartInvestigation
            | Some _, (RenameInvestigation | ActivateInvestigation | MarkAwaitingEvidence | MarkAwaitingVerification | ResolveInvestigation | CloseInvestigation | ReopenInvestigation | ArchiveInvestigation | RecordObservation | RecordHypothesis | RecordConclusion) -> Ok()
            | None, command -> Error(Blocked [ Blockers.illegalTransition command None ])
            | Some _, StartInvestigation ->
                Error(Blocked [ Blockers.make "illegal-transition" "StartInvestigation is not legal on an existing investigation." ])

        identity state envelope
        |> Result.bind (fun () -> semantics policy)
        |> Result.bind (fun () -> version state envelope)
        |> Result.bind structural
        |> Result.bind (fun () -> blockedUnless (Policies.authorize actor))
        |> Result.map (fun () -> Allowed [ DomainEvent.toPayload (DomainEvent.ofCommand submission.Command) ])
        |> Result.defaultWith id
