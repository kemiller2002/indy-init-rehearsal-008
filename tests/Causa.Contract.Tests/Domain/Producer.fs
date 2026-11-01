// Owner: domain. IF41-DOMAIN producer contract tests (T14). DecideIsTotalOverCommands and FrozenFixturesReplay
// need the kernel implementation and are completed in T13 and T15.
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
