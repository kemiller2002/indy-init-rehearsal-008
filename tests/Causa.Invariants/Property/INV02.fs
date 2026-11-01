// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-02 (PERSIST-051-O2): replies to superseded loads, refreshes, conflict re-reads and reconciliation reads are inert,
/// whatever order they arrive in.
module INV02 =
    let private stale = RequestId -99L
    let private staleAnswer = Answered(stale, 200, [], "{}")

    let private essence (s: InvestigationSession) =
        Names.save s.State, s.BaseHead, s.Unsaved, s.AfterInFlight, s.Mutations

    let private inertSession (machine: SaveMachine) (steps: Step list) =
        steps
        |> List.collect (fun step ->
            let after = Drive.after step

            [ match step.Input with
              | SaveInput.SupersededReply _ ->
                  Check.all
                      [ Check.expect (after = step.Before) "a superseded reply changed the session"
                        Check.expect (List.isEmpty (Drive.effects step)) "a superseded reply produced effects" ]
              | _ -> Ok()
              match machine.Interpret (Fixture.context 1L) after staleAnswer with
              | MachineEvent(SaveInput.SupersededReply id) when id = stale -> Ok()
              | other -> Error(sprintf "an answer to a request that is not outstanding was interpreted as %A in %s" other (Names.save after.State)) ])
        |> Check.all

    /// Delivering the superseded replies anywhere in the sequence leaves the same final session as not delivering them.
    let private orderIndependent (machine: SaveMachine) (moves: Move list) =
        let withStale = Drive.run machine moves |> Drive.finalSession machine
        let withoutStale = moves |> List.filter ((<>) Supersede) |> Drive.run machine |> Drive.finalSession machine
        Check.expect (essence withStale = essence withoutStale) "superseded replies changed the final state"

    [<Fact>]
    let ReorderedRepliesAreInert () =
        let machine = Require.saveMachine ()

        Check.check 300 Moves.sequence (fun moves ->
            Check.all [ inertSession machine (Drive.run machine moves); orderIndependent machine moves ])

        // Conflict re-reads: every reachable SM-CONFLICT view drops an answer to a superseded read.
        ConflictModel.explore machine 4
        |> List.choose (fun (_, _, outcome) -> outcome |> Result.toOption |> Option.map fst)
        |> List.iter (fun view ->
            match machine.InterpretConflict (Fixture.context 2L) view staleAnswer with
            | ConflictEvent(ConflictInput.SupersededReply _ as input) ->
                match machine.StepConflict (Fixture.context 3L) view input with
                | Ok(next, effects) ->
                    Assert.Equal<ConflictView>(view, next)
                    Assert.Empty(effects)
                | Error _ -> () // fail closed: the view is unchanged
            | other -> Assert.Fail(sprintf "SM-CONFLICT %A interpreted a superseded answer as %A" view other))

        // Loads and refreshes.
        [ NotLoaded
          Loading(RequestId 5L, LoadHead)
          Loading(RequestId 5L, LoadFilesAt(Fixture.head 2))
          Loaded(Fixture.verifiedAt 1)
          LoadFailed(ProviderFailed(NetworkUnavailable TimedOut))
          UnavailableOffline ]
        |> List.iter (fun state ->
            match machine.InterpretLoad (Fixture.context 4L) state staleAnswer with
            | LoadEvent(LoadInput.SupersededReply _ as input) ->
                match machine.StepLoad (Fixture.context 5L) state input with
                | Ok(next, effects) ->
                    Assert.Equal<LoadState>(state, next)
                    Assert.Empty(effects)
                | Error _ -> ()
            | other -> Assert.Fail(sprintf "SM-LOAD %A interpreted a superseded answer as %A" state other))
