// Owner: ui (WI-R8-T22). SM-NETWORK (state-machine-contracts.json): T-W01, T-W02 and the two illegal pairs. The
// network step sends WentOffline / WentOnline to every investigation's SM-SAVE; a recording save machine shows what
// each session received (the save machine itself is persistence's and is tested there). WI-R8-UIP0 (T30 review): T-W01
// also runs against the real save machine (T-S41 for a Saved session), a save-machine fault is surfaced (F-05), the
// tab-wide mutation guard is computed from the sessions (F-04, T-S53) and Export carries whole envelopes (F-03).
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

    /// The real SM-SAVE (persistence's SaveProcess), as the engine builds it.
    let private tab = Guid "00000000-0000-4000-8000-0000000000ab"

    let private real: SaveMachine =
        SaveProcess.create
            { Codec = StorageV1.codec
              FileSetOf = SaveProcess.pendingCommandFiles
              Tab = tab
              NextMarker = Markers.forTab tab
              KnownOffline = false }

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

        match Update.network real NetworkInput.OfflineObserved before with
        | Ok(after, effects, faults) ->
            Assert.Empty(faults)
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
        | Ok(after, effects, faults) ->
            Assert.Empty(faults)
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

    [<Fact>]
    let ``T-W01 with the real save machine keeps a Saved investigation Saved (T-S41)`` () =
        let before = model NetworkState.Online [ session 1 SaveState.Unsaved; saved 3 ]

        match Update.network real NetworkInput.OfflineObserved before with
        | Ok(after, effects, faults) ->
            Assert.Empty(faults)
            Assert.Empty(effects)
            Assert.Equal(SaveState.Offline, after.Sessions[id 1].State)
            Assert.Equal(before.Sessions[id 3].State, after.Sessions[id 3].State)
            Assert.True((Projection.value.Header after).Offline)
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let ``F-05 a save-machine fault on a network step is surfaced, not dropped`` () =
        // The recording machine refuses WentOffline on Saved: the session keeps its state and the fault is returned.
        let before = model NetworkState.Online [ session 1 SaveState.Unsaved; saved 3 ]

        match Update.network recording NetworkInput.OfflineObserved before with
        | Ok(after, _, faults) ->
            Assert.Equal(SaveState.Offline, after.Sessions[id 1].State)
            Assert.Equal(before.Sessions[id 3].State, after.Sessions[id 3].State)
            let faulted, fault = Assert.Single faults
            Assert.Equal(id 3, faulted)
            Assert.Equal("SM-SAVE", fault.Machine)
        | Error fault -> failwithf "%A" fault

    let private command n : CommandEnvelope =
        { CommandId = Guid(sprintf "00000000-0000-4000-8000-%012d" (100 + n))
          Investigation = id 2
          ExpectedVersion = if n = 1 then None else Some(AggregateVersion(int64 n)) }

    [<Fact>]
    let ``F-04 a mutation in flight elsewhere in the tab blocks a second save (T-S53)`` () =
        let busy = { session 1 SaveState.Saving with Outstanding = Some(RequestId 7L, MutationAnswer); Mutations = 1 }
        let waiting = { session 2 SaveState.Unsaved with Unsaved = [ command 1 ] }
        let before = model NetworkState.Online [ busy; waiting ]
        Assert.True(Update.mutationInFlightElsewhere before (id 2))
        Assert.False(Update.mutationInFlightElsewhere before (id 1))
        let after, effects = AppEngine.sessionStep (AppEngine.configFor tab) (id 2) SaveInput.SaveRequested before
        Assert.Equal(SaveState.Unsaved, after.Sessions[id 2].State)
        Assert.DoesNotContain(effects, fun e -> match e with Http _ -> true | _ -> false)
        // With nothing in flight elsewhere the same save is sent.
        let idle = model NetworkState.Online [ session 1 SaveState.Unsaved; waiting ]
        Assert.False(Update.mutationInFlightElsewhere idle (id 2))
        let saving, sent = AppEngine.sessionStep (AppEngine.configFor tab) (id 2) SaveInput.SaveRequested idle
        Assert.Equal(SaveState.Saving, saving.Sessions[id 2].State)
        Assert.Contains(sent, fun e -> match e with Http _ -> true | _ -> false)

    [<Fact>]
    let ``F-03 Export writes every unconfirmed command envelope in order`` () =
        let commands = [ command 1; command 2; command 3 ]

        match Update.hostEffect (ExportCommands commands) with
        | Download(name, body) ->
            Assert.Equal("causa-unsaved-commands.json", name)
            use doc = System.Text.Json.JsonDocument.Parse body
            Assert.Equal("causa-unsaved-commands/1", doc.RootElement.GetProperty("format").GetString())
            let exported = doc.RootElement.GetProperty("commands").EnumerateArray() |> List.ofSeq
            Assert.Equal(3, exported.Length)

            for c, e in List.zip commands exported do
                Assert.Equal(c.CommandId.ToString "D", e.GetProperty("commandId").GetString())
                Assert.Equal("00000000-0000-4000-8000-000000000002", e.GetProperty("investigation").GetString())

                match c.ExpectedVersion with
                | None -> Assert.Equal(System.Text.Json.JsonValueKind.Null, e.GetProperty("expectedVersion").ValueKind)
                | Some(AggregateVersion v) -> Assert.Equal(v, e.GetProperty("expectedVersion").GetInt64())
        | other -> failwithf "Export should download, got %A" other

    // WI-R8-EXPVER (T32, review F-03): a command recorded on a saved/opened investigation is held, and exported, with
    // the aggregate version it was decided on: the confirmed base evolved by the commands before it, never null.
    let private opened (config: AppEngine.Config) n : VerifiedInvestigation =
        let history = Create.firstHistory config (id n) (Guid "00000000-0000-4000-8000-0000000000c1")

        match Kernel.replay history with
        | Ok investigation ->
            { Investigation = investigation
              History = history
              ReadAt = CommitOid(String('c', 40))
              Bytes = { Manifest = [||]; Events = [||]; Snapshot = [||] } }
        | Error error -> failwithf "the first history should replay: %A" error

    [<Fact>]
    let ``F-03 a command on a saved investigation carries the version it was decided on, and Export writes it`` () =
        let config = AppEngine.configFor tab
        let verified = opened config 4
        let engine = AppEngine.create config

        let before =
            { model NetworkState.Online [ session 4 (SaveState.Saved(VerifiedAt verified)) ] with
                Loads = Map.ofList [ id 4, Loaded verified ] }

        let bare n : CommandEnvelope =
            { CommandId = Guid(sprintf "00000000-0000-4000-8000-%012d" (400 + n)); Investigation = id 4; ExpectedVersion = None }

        let afterFirst, _ = engine.Update (DomainCommand(bare 1)) before
        let afterSecond, _ = engine.Update (DomainCommand(bare 2)) afterFirst
        let held = AppEngine.unconfirmed afterSecond.Sessions[id 4]
        Assert.Equal(2, held.Length)

        // The first is decided on the confirmed base; the second on the base evolved by the first (the kernel's own
        // decision of the held commands, in order, is the reference).
        Assert.Equal(Some verified.Investigation.Version, held[0].ExpectedVersion)
        let _, _, evolved = AppEngine.decideAll config (verified.History, Some verified.Investigation) [ held[0] ]
        Assert.Equal(evolved |> Option.map _.Version, held[1].ExpectedVersion)
        Assert.NotEqual(held[0].ExpectedVersion, held[1].ExpectedVersion)

        // Both are accepted by the kernel at that base exactly as held (no version conflict).
        let reapplied, _, _ = AppEngine.decideAll config (verified.History, Some verified.Investigation) held
        Assert.Equal(2, reapplied.Accepted.Length)
        Assert.Empty(reapplied.Refused)

        match Update.hostEffect (ExportCommands held) with
        | Download(_, body) ->
            use doc = System.Text.Json.JsonDocument.Parse body
            let exported = doc.RootElement.GetProperty("commands").EnumerateArray() |> List.ofSeq

            for c, e in List.zip held exported do
                Assert.Equal(System.Text.Json.JsonValueKind.Number, e.GetProperty("expectedVersion").ValueKind)
                let (AggregateVersion v) = Option.get c.ExpectedVersion
                Assert.Equal(v, e.GetProperty("expectedVersion").GetInt64())
        | other -> failwithf "Export should download, got %A" other
