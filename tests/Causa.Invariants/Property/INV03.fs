// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-03 (PERSIST-015-O3): losing the answer at any point never yields Conflict or Save failed without a
/// reconciliation result that justifies it; a network failure is never a stale head or a conflict.
module INV03 =
    let private justifiedByReconciliation (input: SaveInput) =
        match input with
        | SaveInput.ReconcileMarkerBytesDiffer _
        | SaveInput.ReconcileNothingWrittenHeadUnchanged
        | SaveInput.ReconcileNothingWrittenHeadMoved _
        | SaveInput.ReconcileHistoryRewritten
        | SaveInput.StaleHeadSameInvestigation _ -> true
        | _ -> false

    let private isConflictOrFailed (state: SaveState) =
        match state with
        | SaveState.Conflict _
        | SaveState.SaveFailed _ -> true
        | _ -> false

    let lostAnswerHonest (machine: SaveMachine) (steps: Step list) =
        steps
        |> List.collect (fun step ->
            let after = Drive.after step

            [ match step.Before.State, step.Input, step.Outcome with
              | SaveState.Saving, SaveInput.AnswerLost, Ok _ ->
                  Check.expect (match after.State with SaveState.UnknownOutcome _ -> true | _ -> false) "a lost answer did not lead to UnknownOutcome"
              | SaveState.UnknownOutcome _, input, Ok _ when isConflictOrFailed after.State ->
                  Check.expect (justifiedByReconciliation input) (sprintf "UnknownOutcome became %s by %A" (Names.save after.State) input)
              | _, SaveInput.ProviderRefused(NetworkUnavailable _), Ok _ ->
                  Check.expect (match after.State with SaveState.Conflict _ -> false | _ -> true) "a network failure became a conflict"
              | _ -> Ok()

              // Interpretation: a lost mutation answer is AnswerLost; a lost reconciliation read is a failed read.
              match after.Outstanding with
              | Some(id, awaiting) ->
                  let interpretations =
                      [ LostAfterSending; TimedOut ]
                      |> List.map (fun failure -> machine.Interpret (Fixture.context 1L) after (NotAnswered(id, failure)))

                  match awaiting with
                  | MutationAnswer ->
                      Check.expect
                          (interpretations |> List.forall ((=) (MachineEvent SaveInput.AnswerLost)))
                          (sprintf "a lost mutation answer was interpreted as %A" interpretations)
                  | ReconcileHead
                  | ReconcileCommits _
                  | ReconcileFilesAt _ ->
                      Check.expect
                          (interpretations |> List.forall ((=) (MachineEvent SaveInput.ReconcileReadFailed)))
                          (sprintf "a lost reconciliation read was interpreted as %A" interpretations)
                  | ReadBackAt _
                  | FreshHead
                  | FileComparisonAt _ ->
                      Check.expect
                          (interpretations
                           |> List.forall (function
                               | MachineEvent(SaveInput.StaleHeadSameInvestigation _)
                               | MachineEvent(SaveInput.StaleHeadUnrelated _)
                               | MachineEvent(SaveInput.ProviderRefused _) -> false
                               | _ -> true))
                          (sprintf "a lost read after the mutation was interpreted as %A" interpretations)
              | None -> Ok() ])
        |> Check.all

    [<Fact>]
    let LostAnswerNeverConflictOrFailure () =
        let machine = Require.saveMachine ()
        Check.check 400 Moves.sequence (Drive.run machine >> lostAnswerHonest machine)
