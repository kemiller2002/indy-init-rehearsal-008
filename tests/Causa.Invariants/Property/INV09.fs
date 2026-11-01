// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-09 (PERSIST-013-O3): after any sequence of saves, rebases, reconciliations and conflict resolutions by any number
/// of tabs, every other writer's commit remains reachable from the branch head. Random interleavings of three
/// simulated tabs and an external writer against the stateful fake.
module INV09 =
    type Action =
        | Edit of tab: int * command: int
        | Save of tab: int
        | Deliver of tab: int
        | External

    type private Tab = { Session: InvestigationSession; Pending: SaveEffect list }

    let private actions: Gen<Action list> =
        let tab = Gen.intRange 0 2

        Gen.frequency
            [ 3, Gen.map2 (fun t c -> Edit(t, c)) tab (Gen.intRange 1 50)
              3, Gen.map Save tab
              6, Gen.map Deliver tab
              1, Gen.constant External ]
        |> Gen.listOf 40

    /// Delivers one pending effect of one tab: execute a request against the fake and step its interpretation, or fire
    /// a reconciliation timer (virtual clock). The engine's MutationSent precedes every mutation it executes.
    let private deliverOne (machine: SaveMachine) (fake: FakeGitHub) (tab: Tab, n: int64) : Tab * int64 =
        let context = Fixture.context

        let stepWith (session: InvestigationSession) n input rest =
            match machine.Step (context n) session input with
            | Ok(next, effects) -> { Session = next; Pending = rest @ effects }, n + 1L
            | Error _ -> { Session = session; Pending = rest }, n + 1L

        match tab.Pending with
        | [] -> tab, n
        | Send request :: rest ->
            let session, n =
                if Requests.isMutation request then
                    match machine.Step (context n) tab.Session (SaveInput.MutationSent request.Id) with
                    | Ok(next, _) -> next, n + 1L
                    | Error _ -> tab.Session, n + 1L
                else
                    tab.Session, n

            match machine.Interpret (context n) session (fake.Execute request) with
            | NextRequest(next, nextRequest) -> { Session = next; Pending = Send nextRequest :: rest }, n + 1L
            | MachineEvent input -> stepWith session n input rest
        | ScheduleReconcileRead _ :: rest ->
            match machine.ReconcileDue (context n) tab.Session with
            | Ok(next, effects) -> { Session = next; Pending = rest @ effects }, n + 1L
            | Error _ -> { tab with Pending = rest }, n + 1L
        | (Announce _ | ExportCommands _) :: rest -> { tab with Pending = rest }, n

    let private apply machine fake (tabs: Map<int, Tab>, n: int64) (action: Action) =
        let update t (tab, n) = Map.add t tab tabs, n

        let input t i =
            let tab = tabs[t]

            match machine.Step (Fixture.context n) tab.Session i with
            | Ok(next, effects) -> update t ({ Session = next; Pending = tab.Pending @ effects }, n + 1L)
            | Error _ -> tabs, n + 1L

        match action with
        | Edit(t, c) -> input t (SaveInput.Edit(Fixture.command c))
        | Save t -> input t SaveInput.SaveRequested
        | Deliver t -> update t (deliverOne machine fake (tabs[t], n))
        | External ->
            fake.ExternalCommit() |> ignore
            tabs, n

    let private drain machine fake (tabs: Map<int, Tab>, n) =
        [ 1..200 ]
        |> List.fold (fun state _ -> [ 0..2 ] |> List.fold (fun s t -> apply machine fake s (Deliver t)) state) (tabs, n)

    [<Fact>]
    let AllCommitsReachable () =
        let machine = Require.saveMachine ()
        let newFake = Require.statefulFake ()

        Check.check 100 actions (fun script ->
            let fake = newFake ()
            let opened = fake.Open()
            let tabs = [ 0..2 ] |> List.map (fun t -> t, { Session = machine.Initial opened; Pending = [] }) |> Map.ofList
            script |> List.fold (apply machine fake) (tabs, 1L) |> drain machine fake |> ignore

            fake.Created()
            |> List.filter (fake.ReachableFromHead >> not)
            |> function
                | [] -> Ok()
                | lost -> Error(sprintf "commits no longer reachable from the head: %A" lost))
