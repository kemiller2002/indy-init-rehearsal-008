// Owner: ui (WI-R8-T22). SM-NETWORK (state-machine-contracts.json): T-W01, T-W02 and the two illegal pairs. The
// network step sends WentOffline / WentOnline to every investigation's SM-SAVE; a recording save machine shows what
// each session received (the save machine itself is persistence's and is tested there).
namespace Causa.App.Tests.StateMachines

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App

module SMNETWORK =
    let private id n = InvestigationId(Guid(sprintf "00000000-0000-4000-8000-%012d" n))

    let private session n state : InvestigationSession =
        { Id = id n
          BaseHead = CommitOid(String('a', 40))
          State = state
          Unsaved = []
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0 }

    let private unused name : 'a = failwithf "%s is not used by the network step" name

    /// A save machine that applies WentOffline / WentOnline the way SM-SAVE's table names them for these states,
    /// and sends nothing (T-W02: no save is sent automatically).
    let private recording: SaveMachine =
        { Initial = fun _ -> unused "Initial"
          Interpret = fun _ _ _ -> unused "Interpret"
          Step =
            fun _ s input ->
                match s.State, input with
                | SaveState.Unsaved, SaveInput.WentOffline -> Ok({ s with State = SaveState.Offline }, [])
                | SaveState.Offline, SaveInput.WentOnline -> Ok({ s with State = SaveState.Unsaved }, [])
                | _ -> Error { Machine = "SM-SAVE"; State = sprintf "%A" s.State; Input = sprintf "%A" input; Reason = "not legal" }
          ReconcileDue = fun _ _ -> unused "ReconcileDue"
          InterpretConflict = fun _ _ _ -> unused "InterpretConflict"
          StepConflict = fun _ _ _ -> unused "StepConflict"
          InterpretLoad = fun _ _ _ -> unused "InterpretLoad"
          StepLoad = fun _ _ _ -> unused "StepLoad" }

    let private model network sessions : Model =
        { Connection = Connected true
          Context =
            Some
                { Repository = { Owner = "kemiller2002"; Name = "causa-data"; Branch = "main" }
                  Credential = Credential.create "ghp_SmNetworkTestToken" }
          NextRequestId = RequestId 1L
          Network = network
          Route = Route "#/"
          Loads = Map.empty
          Sessions = sessions |> List.map (fun (s: InvestigationSession) -> s.Id, s) |> Map.ofList }

    let private saved n =
        session n (SaveState.Saved(VerifiedAt { Investigation = { Id = id n; Version = AggregateVersion 1L }; History = []; ReadAt = CommitOid(String('b', 40)); Bytes = { Manifest = [||]; Events = [||]; Snapshot = [||] } }))

    [<Fact>]
    let ``T-W01`` () =
        let before = model NetworkState.Online [ session 1 SaveState.Unsaved; session 2 SaveState.Unsaved; saved 3 ]

        match Update.network recording NetworkInput.OfflineObserved before with
        | Ok(after, effects) ->
            Assert.Equal(NetworkState.Offline, after.Network)
            Assert.Equal(SaveState.Offline, after.Sessions[id 1].State)
            Assert.Equal(SaveState.Offline, after.Sessions[id 2].State)
            // PERSIST-031-O23: an investigation with no unsaved change stays Saved; the header shows offline.
            Assert.Equal(before.Sessions[id 3].State, after.Sessions[id 3].State)
            Assert.Empty(effects)
            let header = Projection.value.Header after
            Assert.True(header.Offline)
            Assert.Equal(2, header.NotSaved.Length)
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let ``T-W02`` () =
        let before = model NetworkState.Offline [ session 1 SaveState.Offline ]

        match Update.network recording NetworkInput.OnlineObserved before with
        | Ok(after, effects) ->
            Assert.Equal(NetworkState.Online, after.Network)
            Assert.Equal(SaveState.Unsaved, after.Sessions[id 1].State)
            // No save is sent automatically.
            Assert.DoesNotContain(effects, fun e -> match e with Http _ -> true | _ -> false)
            Assert.Equal(before.NextRequestId, after.NextRequestId)
            Assert.False((Projection.value.Header after).Offline)
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        for state, input in [ NetworkState.Online, NetworkInput.OnlineObserved; NetworkState.Offline, NetworkInput.OfflineObserved ] do
            match NetworkMachine.step state input with
            | Ok result -> failwithf "%A on %A should fail closed, got %A" input state result
            | Error fault ->
                Assert.Equal("SM-NETWORK", fault.Machine)
                Assert.Equal(NetworkMachine.stateName state, fault.State)
                Assert.Equal(NetworkMachine.inputName input, fault.Input)

            let before = model state [ session 1 SaveState.Unsaved ]

            match Update.network recording input before with
            | Ok _ -> failwithf "%A on %A should fail closed" input state
            | Error _ -> ()

        // A connectivity signal that tells the tab nothing new is not an event at all.
        Assert.Equal(None, NetworkMachine.observe NetworkState.Online true)
        Assert.Equal(Some NetworkInput.OfflineObserved, NetworkMachine.observe NetworkState.Online false)
