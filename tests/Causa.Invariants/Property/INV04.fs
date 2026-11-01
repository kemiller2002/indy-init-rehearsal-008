// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
// WI-R8-T15 (T35 R-09): save requests delimited by the user's request, and a 60-second window under total failure.
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-04 (PERSIST-016-O5): at most one mutation in flight per tab; per save request at most four mutations and at most
/// three automatic reconciliation reads at least 2, 4 and 8 seconds apart (plus one per offline-to-online change); under
/// total network failure at most four mutations and four reconciliation reads in 60 seconds; a save that failed before
/// sending is not retried. Delays and the window are checked on the effects, with a virtual clock.
module INV04 =
    let private mutationInFlight (s: InvestigationSession) =
        match s.Outstanding with
        | Some(_, MutationAnswer) -> true
        | _ -> false

    /// A save request begins with the user's SaveRequested, or with QueuedSaveStarted (the held request of T-S53 sent
    /// as in T-S04); SM-SAVE gives "fresh bounds" only there (T-S04, T-S35, T-S54). Everything up to the next one,
    /// including Offline and WentOnline, belongs to the same request, so a machine that resumes sending after a
    /// reconnect cannot reset its count (R-09, T35). Steps before the first request carry no request.
    let private saveRequests (steps: Step list) =
        let starts (step: Step) =
            match step.Input with
            | SaveInput.SaveRequested
            | SaveInput.QueuedSaveStarted -> true
            | _ -> false

        steps
        |> List.fold
            (fun acc step ->
                if starts step then
                    [ step ] :: acc
                else
                    match acc with
                    | current :: rest -> (step :: current) :: rest
                    | [] -> [])
            []
        |> List.rev
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
                       // T-S09, T-S32, T-S42..T-S51: going online sends no mutation (no replay, no queue).
                       | SaveInput.WentOnline -> Check.expect (List.isEmpty (Requests.mutations effects)) "going online sent a mutation"
                       | SaveInput.Edit _
                       | SaveInput.SaveRequested
                       | SaveInput.QueuedSaveStarted
                       | SaveInput.MutationSent _
                       | SaveInput.WentOffline
                       | SaveInput.CheckAgain when mutationInFlight step.Before ->
                           Check.expect (List.isEmpty (Requests.mutations effects)) "a second mutation while one is in flight"
                       | _ -> Ok()) ])

        let perRequest =
            saveRequests steps
            |> List.map (fun request ->
                // Counted from the effects, never from the machine's own Mutations counter.
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

        // Before any save request nothing may be sent.
        let beforeFirst =
            steps
            |> List.takeWhile (fun s -> s.Input <> SaveInput.SaveRequested && s.Input <> SaveInput.QueuedSaveStarted)
            |> List.collect (Drive.effects >> Requests.mutations)
            |> List.isEmpty

        Check.all ((Check.expect beforeFirst "a mutation was sent before any save request" :: perStep) @ perRequest)

    // ----- Total network failure on a virtual clock: every request fails, timers fire when the clock reaches them.

    type private Sent =
        | MutationAt of int64
        | ReadAt of int64

    type private World =
        { Session: InvestigationSession
          Clock: int64
          Timers: int64 list
          Sent: Sent list
          Next: int64
          Faults: string list }

    let private window = 60_000L

    /// The answer total failure gives to what the session awaits: no answer for a mutation (lost after sending, or not
    /// sent at all), a failed read otherwise.
    let private failureFor (lostBeforeSending: bool) (awaiting: Awaiting) : SaveInput =
        match awaiting with
        | MutationAnswer -> if lostBeforeSending then SaveInput.FailedBeforeSending else SaveInput.AnswerLost
        | ReadBackAt _ -> SaveInput.ReadBackReadFailed
        | FreshHead
        | FileComparisonAt _ -> SaveInput.RebaseReadFailed
        | ReconcileHead
        | ReconcileCommits _
        | ReconcileFilesAt _ -> SaveInput.ReconcileReadFailed

    let private record (world: World) (outcome: Result<InvestigationSession * SaveEffect list, SaveFault>) =
        match outcome with
        | Error fault -> { world with Next = world.Next + 1L; Faults = sprintf "%s/%s/%s" fault.State fault.Input fault.Reason :: world.Faults }
        | Ok(session, effects) ->
            let sent =
                Requests.sends effects
                |> List.map (fun r -> if Requests.isMutation r then MutationAt world.Clock else ReadAt world.Clock)

            let timers = Requests.schedules effects |> List.map (fun ms -> world.Clock + int64 ms)

            { world with
                Session = session
                Sent = world.Sent @ sent
                Timers = List.sort (world.Timers @ timers)
                Next = world.Next + 1L }

    /// Fails every outstanding request (MutationSent first for a mutation, as Engine.Update does), at most a bounded
    /// number of times so a machine that never stops asking still terminates the test.
    let rec private failOutstanding (machine: SaveMachine) (lostBeforeSending: bool) (budget: int) (world: World) =
        match world.Session.Outstanding with
        | Some(requestId, awaiting) when budget > 0 ->
            let world =
                match awaiting with
                | MutationAnswer -> record world (machine.Step (Fixture.context world.Next) world.Session (SaveInput.MutationSent requestId))
                | _ -> world

            let failed = record world (machine.Step (Fixture.context world.Next) world.Session (failureFor lostBeforeSending awaiting))
            failOutstanding machine lostBeforeSending (budget - 1) failed
        | _ -> world

    /// Advances the clock to `until`, firing every timer due on the way (ReconcileDue) and failing what it sends.
    let rec private advance (machine: SaveMachine) (until: int64) (world: World) =
        match world.Timers with
        | due :: later when due <= until ->
            { world with Clock = max world.Clock due; Timers = later }
            |> fun w -> record w (machine.ReconcileDue (Fixture.context w.Next) w.Session)
            |> failOutstanding machine false 20
            |> advance machine until
        | _ -> { world with Clock = max world.Clock until }

    /// A user action after a pause (milliseconds), under total failure.
    let private userAction: Gen<int * SaveInput * bool> =
        Gen.map3
            (fun pause input beforeSending -> pause, input, beforeSending)
            (Gen.intRange 0 20_000)
            (Gen.frequency
                [ 2, Gen.intRange 1 30 |> Gen.map (fun n -> SaveInput.Edit(Fixture.command n))
                  4, Gen.constant SaveInput.SaveRequested
                  3, Gen.constant SaveInput.CheckAgain
                  1, Gen.constant SaveInput.WentOffline
                  2, Gen.constant SaveInput.WentOnline
                  1, Gen.constant SaveInput.QueuedSaveStarted ])
            (Gen.intRange 0 3 |> Gen.map (fun n -> n = 0))

    let private failureSchedule =
        Gen.listOf 30 userAction
        |> Gen.map (fun rest -> (0, SaveInput.Edit(Fixture.command 0), false) :: (0, SaveInput.SaveRequested, false) :: rest)

    let private runUnderTotalFailure (machine: SaveMachine) (actions: (int * SaveInput * bool) list) =
        let start =
            { Session = Drive.start machine
              Clock = 0L
              Timers = []
              Sent = []
              Next = 100L
              Faults = [] }

        let afterActions =
            actions
            |> List.fold
                (fun world (pause, input, beforeSending) ->
                    let world = advance machine (world.Clock + int64 pause) world
                    record world (machine.Step (Fixture.context world.Next) world.Session input)
                    |> failOutstanding machine beforeSending 20)
                start

        // Let every remaining timer fire (bounded: two windows past the last action).
        advance machine (afterActions.Clock + 2L * window) afterActions

    /// At most `limit` of the timestamps fall in any 60-second window [t, t + 60 s).
    let private withinWindow (limit: int) (times: int64 list) =
        times
        |> List.forall (fun t -> times |> List.filter (fun u -> u >= t && u < t + window) |> List.length <= limit)

    let private totalFailureBounded (world: World) =
        let mutations = world.Sent |> List.choose (function MutationAt t -> Some t | ReadAt _ -> None)
        let reads = world.Sent |> List.choose (function ReadAt t -> Some t | MutationAt _ -> None)

        Check.all
            [ Check.expect (withinWindow 4 mutations) (sprintf "more than four mutations in 60 s under total network failure: %A" mutations)
              Check.expect (withinWindow 4 reads) (sprintf "more than four reconciliation reads in 60 s under total network failure: %A" reads) ]

    [<Fact>]
    let RequestBounds () =
        let machine = Require.saveMachine ()
        Check.check 400 Moves.sequence (Drive.run machine >> bounded)

        // Total network failure, virtual clock: the 60-second bound across save requests (R-09, T35).
        Check.check 300 failureSchedule (runUnderTotalFailure machine >> totalFailureBounded)

        // Another investigation in this tab has a mutation in flight: the save waits; nothing is sent.
        let busy n = { Fixture.context n with MutationInFlightElsewhere = true }
        let steps = Drive.runFrom busy machine (Drive.start machine) [ Raw(SaveInput.Edit(Fixture.command 1)); Raw SaveInput.SaveRequested ]
        Assert.Empty(steps |> List.collect (Drive.effects >> Requests.sends))
        Assert.Equal("Unsaved", Names.save (Drive.finalSession machine steps).State)
