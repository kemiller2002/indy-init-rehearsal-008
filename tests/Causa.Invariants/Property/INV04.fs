// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-04 (PERSIST-016-O5): at most one mutation in flight per tab; per save request at most four mutations and at most
/// three automatic reconciliation reads at least 2, 4 and 8 seconds apart (plus one per offline-to-online change); a
/// save that failed before sending is not retried. Delays are checked on the effects (virtual clock).
module INV04 =
    let private mutationInFlight (s: InvestigationSession) =
        match s.Outstanding with
        | Some(_, MutationAnswer) -> true
        | _ -> false

    /// Splits a run into save requests: a new request starts where a step enters Saving from outside Saving/UnknownOutcome.
    let private saveRequests (steps: Step list) =
        let starts (step: Step) =
            match step.Before.State, (Drive.after step).State with
            | (SaveState.Saving | SaveState.UnknownOutcome _), _ -> false
            | _, SaveState.Saving -> true
            | _ -> false

        steps
        |> List.fold (fun acc step -> if starts step then [ step ] :: acc else match acc with current :: rest -> (step :: current) :: rest | [] -> []) []
        |> List.map List.rev

    let private minimumDelays = [ 2000; 4000; 8000 ]

    let bounded (steps: Step list) =
        let perStep =
            steps
            |> List.map (fun step ->
                let effects = Drive.effects step
                let after = Drive.after step

                Check.all
                    [ Check.expect (List.length (Requests.mutations effects) <= 1) "more than one mutation in one step"
                      Check.expect (after.Mutations <= 4) (sprintf "%d mutations for one save request" after.Mutations)
                      (match step.Input with
                       | SaveInput.FailedBeforeSending -> Check.expect (List.isEmpty (Requests.sends effects)) "a save that failed before sending was retried"
                       | SaveInput.Edit _
                       | SaveInput.SaveRequested
                       | SaveInput.QueuedSaveStarted
                       | SaveInput.MutationSent _
                       | SaveInput.WentOffline
                       | SaveInput.WentOnline
                       | SaveInput.CheckAgain when mutationInFlight step.Before ->
                           Check.expect (List.isEmpty (Requests.mutations effects)) "a second mutation while one is in flight"
                       | _ -> Ok()) ])

        let perRequest =
            saveRequests steps
            |> List.map (fun request ->
                let mutations = request |> List.sumBy (Drive.effects >> Requests.mutations >> List.length)
                let delays = request |> List.collect (Drive.effects >> Requests.schedules)
                let onlineChanges = request |> List.filter (fun s -> s.Input = SaveInput.WentOnline) |> List.length

                Check.all
                    [ Check.expect (mutations <= 4) (sprintf "%d mutations in one save request" mutations)
                      Check.expect (List.length delays <= 3 + onlineChanges) (sprintf "%d automatic reconciliation reads" (List.length delays))
                      Check.expect
                          (List.zip (List.truncate 3 delays) (List.truncate (min 3 (List.length delays)) minimumDelays)
                           |> List.forall (fun (actual, minimum) -> actual >= minimum))
                          (sprintf "reconciliation reads scheduled after %A ms (minimum 2000, 4000, 8000)" delays) ])

        Check.all (perStep @ perRequest)

    [<Fact>]
    let RequestBounds () =
        let machine = Require.saveMachine ()
        Check.check 400 Moves.sequence (Drive.run machine >> bounded)

        // Another investigation in this tab has a mutation in flight: the save waits; nothing is sent.
        let busy n = { Fixture.context n with MutationInFlightElsewhere = true }
        let steps = Drive.runFrom busy machine (Drive.start machine) [ Raw(SaveInput.Edit(Fixture.command 1)); Raw SaveInput.SaveRequested ]
        Assert.Empty(steps |> List.collect (Drive.effects >> Requests.sends))
        Assert.Equal("Unsaved", Names.save (Drive.finalSession machine steps).State)
