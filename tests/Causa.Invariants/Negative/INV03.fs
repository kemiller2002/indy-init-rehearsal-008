// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-03 (PERSIST-015-O5): an uncertain remote outcome is never classified as failure or conflict automatically.
/// Rehearsal 7 KD-018, written from the invariant: the mutation was applied, its answer was lost, and reconciliation
/// does not yet see the commit.
module INV03 =
    [<Fact>]
    let AppliedButNotYetVisible () =
        let machine = Require.saveMachine ()

        let steps =
            Drive.run
                machine
                [ Raw(SaveInput.Edit(Fixture.command 1))
                  Raw SaveInput.SaveRequested
                  SendOutstanding
                  Raw SaveInput.AnswerLost // the fake applied the commit; the answer never arrived
                  Raw SaveInput.ReconcileReadFailed // the first reconciliation read failed
                  Raw SaveInput.ReconcileNothingWrittenHeadUnchanged ] // the next does not see the commit yet

        steps
        |> List.iter (fun step ->
            match step.Outcome with
            | Error fault -> Assert.Fail(sprintf "legal input %A refused: %A" step.Input fault)
            | Ok(session, _) ->
                match session.State with
                | SaveState.Conflict _
                | SaveState.SaveFailed _ ->
                    Assert.Fail(sprintf "%A turned an uncertain outcome into %s" step.Input (Names.save session.State))
                | _ -> ())

        let states = steps |> List.map (Drive.after >> fun s -> Names.save s.State)
        Assert.Equal<string list>([ "Unsaved"; "Saving"; "Saving"; "UnknownOutcome"; "UnknownOutcome"; "Saving" ], states)

        // The resend keeps the same expected head: the tab's own unseen commit is never treated as another writer's.
        let final = Drive.finalSession machine steps
        Assert.Equal(Fixture.head 1, final.BaseHead)
        Assert.NotEmpty(final.Unsaved @ final.AfterInFlight)
