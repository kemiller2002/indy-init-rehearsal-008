// Owner: domain. IF41-DOMAIN producer contract tests (T14, T13). FrozenFixturesReplay is completed in T15.
module Causa.Contract.Tests.Domain.Producer

open System
open Xunit
open Causa.Domain

let private investigation = InvestigationId(Guid.Parse "00000000-0000-0000-0000-0000000000a1")

let private eventAt (version: int64) : EventEnvelope =
    { EventId = EventId(Guid(int version, 0s, 0s, Array.zeroCreate 8))
      Investigation = investigation
      Version = AggregateVersion version
      Payload = EventPayload "contract-fixture" }

let private historyOf (versions: int64 list) = versions |> List.map eventAt

let private genesis = AggregateVersion 1L

/// The law every Replay must satisfy for the replay preconditions: a refused history names the first version
/// that breaks the run. Shared with T13, which applies it to the kernel's Replay as well.
let replayRefusesGapsLaw (refuse: EventEnvelope list -> Result<'a, ReplayError>) =
    let refusedWith versions =
        match refuse (historyOf versions) with
        | Ok _ -> None
        | Error error -> Some error

    // Negative cases: forged/illegal histories (REHEARSAL-AGENT-INTERFACE-FREEZE, mandatory negative cases).
    Assert.Equal(Some EmptyHistory, refusedWith [])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 3L, AggregateVersion 4L)), refusedWith [ 1L; 2L; 4L ])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 1L, AggregateVersion 2L)), refusedWith [ 2L; 3L ])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 3L, AggregateVersion 2L)), refusedWith [ 1L; 2L; 2L ])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 2L, AggregateVersion 3L)), refusedWith [ 1L; 3L; 2L ])

[<Fact>]
let ReplayRefusesGaps () =
    replayRefusesGapsLaw (ReplayOrder.contiguousFrom genesis)

    // Positive control: a contiguous history passes through unchanged, so the refusals above are not vacuous.
    let contiguous = historyOf [ 1L; 2L; 3L ]
    Assert.Equal(Ok contiguous, ReplayOrder.contiguousFrom genesis contiguous)

[<Fact>]
let KernelReplayRefusesGaps () =
    // T13: the same law applied to the kernel's Replay (genesis = version 1; contiguity is checked before any event).
    replayRefusesGapsLaw Kernel.domain.Replay

let private policy: PolicySet = { Id = Policies.defaultPolicyId }
let private actor: ActorContext = { ActorId = "contract-tests" }
let private commandId (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)

let private envelope (n: int) (expected: AggregateVersion option) : CommandEnvelope =
    { CommandId = commandId n
      Investigation = investigation
      ExpectedVersion = expected }

let private isDecision (decision: Decision) =
    match decision with
    | Allowed _
    | Blocked _
    | RequiresReview _
    | UnsupportedSemantics _
    | Conflict _
    | Duplicate _ -> true

/// Histories at every lifecycle state, built only from allowed decisions.
let private historyAfter (commands: Command list) =
    commands
    |> List.indexed
    |> List.fold
        (fun history (i, command) ->
            let summary = Kernel.replay history |> Result.toOption
            let env = envelope (200 + i) (summary |> Option.map _.Version)

            match Kernel.decideOn policy actor history { Envelope = env; Command = command } with
            | Allowed payloads -> history @ Kernel.stamp env summary payloads
            | _ -> history)
        []

let private open' = [ StartInvestigation; RecordObservation; ActivateInvestigation ]

let private histories =
    [ []
      [ StartInvestigation ]
      open'
      open' @ [ MarkAwaitingEvidence ]
      open' @ [ MarkAwaitingVerification ]
      open' @ [ RecordConclusion; ResolveInvestigation ]
      open' @ [ RecordConclusion; ResolveInvestigation; CloseInvestigation ]
      open' @ [ ArchiveInvestigation ] ]
    |> List.map historyAfter

[<Fact>]
let DecideIsTotalOverCommands () =
    let actors = [ actor; { ActorId = "" } ]
    let policies = [ policy; { Id = "unknown-pack" } ]

    for history in histories do
        let summary = Kernel.replay history |> Result.toOption
        let versions = [ None; Some(AggregateVersion 0L); summary |> Option.map _.Version; Some(AggregateVersion 99L) ]

        for p in policies do
            for a in actors do
                for expected in versions do
                    // The frozen Decide over a bare envelope, and the full decision for every command.
                    Assert.True(isDecision (Kernel.domain.Decide p a summary (envelope 1 expected)))

                    for command in Command.all do
                        let decision = Kernel.decideOn p a history { Envelope = envelope 1 expected; Command = command }
                        Assert.True(isDecision decision)

                        // Every Allowed decision evolves: decide never proposes an event Evolve refuses.
                        match decision with
                        | Allowed payloads ->
                            let after = history @ Kernel.stamp (envelope 1 expected) summary payloads
                            Assert.True(Result.isOk (Kernel.domain.Replay after), sprintf "%A allowed but does not replay" command)
                        | _ -> ()

[<Fact>]
let DecideNegatives () =
    // G2 negatives: stale aggregate version, duplicate submission, unauthorized command.
    let history = historyAfter open' // versions 1..3 from commands 200..202
    let current = AggregateVersion 3L

    Assert.Equal(
        Conflict(AggregateVersion 2L, current),
        Kernel.decideOn policy actor history { Envelope = envelope 300 (Some(AggregateVersion 2L)); Command = RecordObservation }
    )

    Assert.Equal(Conflict(AggregateVersion 2L, current), Kernel.domain.Decide policy actor (Some { Id = investigation; Version = current }) (envelope 300 (Some(AggregateVersion 2L))))

    Assert.Equal(
        Duplicate [ EventIds.forCommand (commandId 201) 0 ],
        Kernel.decideOn policy actor history { Envelope = envelope 201 (Some(AggregateVersion 1L)); Command = RecordObservation }
    )

    let unauthorized =
        [ Kernel.decideOn policy { ActorId = "" } history { Envelope = envelope 301 (Some current); Command = RecordObservation }
          Kernel.domain.Decide policy { ActorId = "" } None (envelope 302 None) ]

    unauthorized
    |> List.iter (fun decision ->
        match decision with
        | Blocked blockers -> Assert.Contains(blockers, fun b -> b.Code = "unauthorized")
        | other -> Assert.Fail(sprintf "unauthorized command decided %A" other))
