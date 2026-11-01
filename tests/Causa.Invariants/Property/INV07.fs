// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-07 (PERSIST-031-O21): for random failure sequences the multiset of unsaved commands is unchanged except by a
/// confirmed discard or a confirmation that saved them.
module INV07 =
    let private work (s: InvestigationSession) = List.sort (s.Unsaved @ s.AfterInFlight)

    let preserved (steps: Step list) =
        steps
        |> List.map (fun step ->
            match step.Outcome with
            | Error _ -> Ok()
            | Ok(after, _) ->
                match step.Input with
                | SaveInput.Edit command ->
                    Check.expect (work after = List.sort (command :: work step.Before)) "an edit was not kept in tab memory"
                | SaveInput.ReadBackMatched _
                | SaveInput.ReconcileMarkerBytesMatch _ ->
                    Check.expect
                        (step.Before.AfterInFlight |> List.forall (fun c -> List.contains c (work after)))
                        "a confirmation dropped changes made during the save"
                | SaveInput.ConflictResolvedDiscarded _
                | SaveInput.ConflictResolvedReapplied _ -> Ok()
                | input -> Check.expect (work after = work step.Before) (sprintf "%A in %s changed the unsaved commands" input (Names.save step.Before.State)))
        |> Check.all

    [<Fact>]
    let WorkSurvivesRecoverableFailures () =
        let machine = Require.saveMachine ()
        Check.check 400 Moves.sequence (Drive.run machine >> preserved)
