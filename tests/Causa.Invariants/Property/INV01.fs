// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-01 (PERSIST-023-O2): Saved occurs only immediately after a matching read-back or marker reconciliation.
module INV01 =
    /// The named check over one run: every entry into Saved is justified by the step that made it.
    let noSavedWithoutConfirmation (steps: Step list) : Result<unit, string> =
        steps
        |> List.map (fun step ->
            match step.Outcome with
            | Error _ -> Ok()
            | Ok(after, _) ->
                let before = step.Before.State

                match before, after.State with
                | SaveState.Saved _, SaveState.Saved _ ->
                    Check.expect (after.State = before && after.BaseHead = step.Before.BaseHead) (sprintf "%A changed what Saved rests on" step.Input)
                | _, SaveState.Saved(Confirmed c) ->
                    match step.Input, before with
                    | SaveInput.ReadBackMatched c', SaveState.Saving
                    | SaveInput.ReconcileMarkerBytesMatch c', SaveState.UnknownOutcome _ when c' = c ->
                        Check.all
                            [ Check.expect (after.BaseHead = c.CommitOid) "the base head is not the confirmed commit"
                              Check.expect (List.isEmpty after.AfterInFlight) "Saved while changes made during the save are unsaved" ]
                    | input, _ -> Error(sprintf "Saved from %s by %A without a matching read-back" (Names.save before) input)
                | _, SaveState.Saved(VerifiedAt v) ->
                    match step.Input, before with
                    | SaveInput.ConflictResolvedDiscarded v', SaveState.Conflict _ when v' = v ->
                        Check.expect (after.BaseHead = v.ReadAt) "the base head is not the verified GitHub version"
                    | input, _ -> Error(sprintf "Saved (verified load) from %s by %A" (Names.save before) input)
                | _ -> Ok())
        |> Check.all

    [<Fact>]
    let NoSavedWithoutMatchingReadBack () =
        // The proof itself: a confirmation exists only for identical bytes (shared with the mutation check).
        Check.check 200 Causa.Invariants.Mutation.INV01.cases (Causa.Invariants.Mutation.INV01.readBackGuardHolds Confirmation.ofReadBack)

        // The machine: random edits, saves and replies, including forged commits and mismatched read-backs.
        let machine = Require.saveMachine ()
        Check.check 400 Moves.sequence (Drive.run machine >> noSavedWithoutConfirmation)
