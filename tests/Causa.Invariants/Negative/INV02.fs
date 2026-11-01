// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-02 (PERSIST-051-O2): a reply to a request the tab has superseded never changes the state, the base head or the
/// unsaved changes. Rehearsal 7 third-review E1 and E2, written from the invariant.
module INV02 =
    let private answer (RequestId n) = Answered(RequestId n, 200, [], "{}")

    let private stepConflict (machine: SaveMachine) n view input =
        match machine.StepConflict (Fixture.context n) view input with
        | Ok(next, effects) -> next, effects
        | Error fault -> failwithf "SM-CONFLICT refused a legal input: %A" fault

    [<Fact>]
    let LateConflictReReadKeepsOtherWriter () =
        let machine = Require.saveMachine ()
        let atH1 = Fixture.verifiedAt 31
        let atH2 = Fixture.verifiedAt 32

        // The first read (r1) answered h1; the user asked to read again (r2) because another writer moved to h2.
        let reviewH1, _ = stepConflict machine 1L (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteVerified atH1)
        let reading, _ = stepConflict machine 2L reviewH1 ConflictInput.ReadAgain

        let current =
            match reading with
            | ReadingRemote id -> id
            | other -> failwithf "ReadAgain did not start a read: %A" other

        Assert.NotEqual(RequestId 1L, current)

        // The re-read answers h2 first; then a late answer to the earlier read (h1) arrives.
        let reviewH2, _ = stepConflict machine 3L reading (ConflictInput.RemoteVerified atH2)

        match machine.InterpretConflict (Fixture.context 4L) reviewH2 (answer (RequestId 1L)) with
        | ConflictEvent(ConflictInput.SupersededReply _ as late) ->
            let afterLate, effects = stepConflict machine 5L reviewH2 late
            Assert.Equal<ConflictView>(reviewH2, afterLate)
            Assert.Empty(effects)
        | other -> Assert.Fail(sprintf "a late answer to a superseded conflict read was not dropped: %A" other)

        // Reapply rebases on the other writer's version (h2), so that writer's change survives.
        match stepConflict machine 6L reviewH2 ConflictInput.ReapplyChosen with
        | Reapplying remote, effects ->
            Assert.Equal(atH2.ReadAt, remote.ReadAt)
            Assert.Empty(Requests.mutations effects)
        | other, _ -> Assert.Fail(sprintf "reapply did not start on the latest GitHub version: %A" other)

    [<Fact>]
    let StaleRefreshDropped () =
        let machine = Require.saveMachine ()
        let atB2 = Fixture.verifiedAt 42

        // A refresh was requested at base b1 (request r1); a save then moved the tab to b2, so the load holds b2.
        let loadedB2 = Loaded atB2

        match machine.InterpretLoad (Fixture.context 2L) loadedB2 (answer (RequestId 1L)) with
        | LoadEvent(LoadInput.SupersededReply _ as late) ->
            match machine.StepLoad (Fixture.context 3L) loadedB2 late with
            | Ok(state, effects) ->
                Assert.Equal<LoadState>(loadedB2, state)
                Assert.Empty(effects)
            | Error fault -> Assert.Fail(sprintf "a superseded load reply must be discarded, not refused: %A" fault)
        | other -> Assert.Fail(sprintf "a refresh answer for a superseded request was not dropped: %A" other)

        // The same while a newer load is waiting: the reply to r1 never completes the load of r2.
        let loadingR2 = Loading(RequestId 2L, LoadHead)

        match machine.InterpretLoad (Fixture.context 4L) loadingR2 (answer (RequestId 1L)) with
        | LoadEvent(LoadInput.SupersededReply _) -> ()
        | other -> Assert.Fail(sprintf "a reply to r1 was interpreted for the load waiting on r2: %A" other)
