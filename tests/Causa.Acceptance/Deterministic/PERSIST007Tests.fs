// Owner: persistence. WI-R8-PTESTS: PERSIST-007 a save is confirmed only by a read-back, against the stateful fake.
// This file also holds the save driver the persistence acceptance tests after it share (SaveHarness): one tab's save
// machine (IF41-SAVE) driven against the stateful GitHub fake, effects executed at the test edge.
namespace Causa.Acceptance.Deterministic

open System
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

/// One tab against one fake: the machine, the session, and what the driver saw.
type Tab =
    { Machine: SaveMachine
      Marker: Guid
      Session: InvestigationSession
      Pending: SaveEffect list
      Announced: string list
      Delays: int list
      /// Fail-closed answers (state kept unchanged, as the engine keeps it, IF41-APP).
      Faults: SaveFault list }

module SaveHarness =
    let investigation = InvestigationId(Guid "00000000-0000-4000-8000-000000000001")

    let connection: ConnectionContext =
        { Repository = { Owner = "kemiller2002"; Name = "causa-data"; Branch = "main" }
          Credential = Credential.create "fake-credential" }

    let mutable private nextId = 1L

    let context () =
        { Connection = connection
          NextId = RequestId(Threading.Interlocked.Increment(&nextId))
          MutationInFlightElsewhere = false }

    let command (n: int) : CommandEnvelope =
        { CommandId = Guid(sprintf "00000000-0000-4000-8000-%012d" (5000 + n))
          Investigation = investigation
          ExpectedVersion = None }

    let newFake () = InvariantFake.create "kemiller2002" "causa-data" "main" investigation

    /// A tab with a known session identifier, so a test can recognise (or forge) its write marker.
    let openTabAt (verified: VerifiedInvestigation) : Tab =
        let tab = Guid.NewGuid()

        let machine =
            SaveProcess.create
                { Codec = StorageV1.codec
                  FileSetOf = SaveProcess.pendingCommandFiles
                  Tab = tab
                  NextMarker = Markers.forTab tab
                  KnownOffline = false }

        { Machine = machine
          Marker = tab
          Session = machine.Initial verified
          Pending = []
          Announced = []
          Delays = []
          Faults = [] }

    let openTab (fake: InvariantFake.Handle) : Tab = openTabAt (InvariantFake.openAtHead fake)

    /// A tab driven by a given save machine (for instance SaveProcess.forNewTab, whose session identifier is private).
    let openTabWith (machine: SaveMachine) (verified: VerifiedInvestigation) : Tab =
        { openTabAt verified with Machine = machine; Session = machine.Initial verified; Marker = Guid.Empty }

    let private absorb (tab: Tab) (session: InvestigationSession) (effects: SaveEffect list) =
        { tab with Session = session; Pending = tab.Pending @ effects }

    /// Step one App input; an illegal pair fails the test (callers that expect a fault use the machine directly).
    let inputWith (connection: ConnectionContext) (i: SaveInput) (tab: Tab) : Tab =
        match tab.Machine.Step { context () with Connection = connection } tab.Session i with
        | Ok(session, effects) -> absorb tab session effects
        | Error fault -> failwithf "unexpected fault: %A" fault

    let input (i: SaveInput) (tab: Tab) : Tab = inputWith connection i tab

    let edit n tab = input (SaveInput.Edit(command n)) tab

    let isMutationRequest (request: HttpRequest) =
        request.Body |> Option.exists (fun b -> b.Contains("createCommitOnBranch", StringComparison.Ordinal))

    /// Delivers the next pending effect. Requests go to the fake; a mutation is preceded by MutationSent (as the
    /// engine does). A reconciliation timer fires at once (virtual clock) and its delay is recorded.
    let deliverWith (connection: ConnectionContext) (execute: Execute) (tab: Tab) : Tab =
        let context () = { context () with Connection = connection }

        match tab.Pending with
        | [] -> tab
        | Send request :: rest ->
            let tab = { tab with Pending = rest }

            match tab.Session.State with
            | SaveState.Conflict view ->
                match tab.Machine.InterpretConflict (context ()) view (execute request) with
                | ConflictNextRequest(next, r) ->
                    { tab with
                        Session = { tab.Session with State = SaveState.Conflict next }
                        Pending = Send r :: tab.Pending }
                | ConflictEvent e ->
                    match tab.Machine.StepConflict (context ()) view e with
                    | Ok(next, effects) -> absorb tab { tab.Session with State = SaveState.Conflict next } effects
                    | Error fault -> { tab with Faults = tab.Faults @ [ fault ] }
            | _ ->
                let session =
                    if isMutationRequest request then
                        match tab.Machine.Step (context ()) tab.Session (SaveInput.MutationSent request.Id) with
                        | Ok(s, _) -> s
                        | Error fault -> failwithf "MutationSent refused: %A" fault
                    else
                        tab.Session

                match tab.Machine.Interpret (context ()) session (execute request) with
                | NextRequest(next, r) -> { tab with Session = next; Pending = Send r :: tab.Pending }
                | MachineEvent e ->
                    match tab.Machine.Step (context ()) session e with
                    | Ok(next, effects) -> absorb tab next effects
                    | Error fault -> { tab with Session = session; Faults = tab.Faults @ [ fault ] }
        | ScheduleReconcileRead(_, delay) :: rest ->
            let tab = { tab with Pending = rest; Delays = tab.Delays @ [ delay ] }

            match tab.Machine.ReconcileDue (context ()) tab.Session with
            | Ok(next, effects) -> absorb tab next effects
            | Error fault -> { tab with Faults = tab.Faults @ [ fault ] }
        | Announce(_, text) :: rest -> { tab with Pending = rest; Announced = tab.Announced @ [ text ] }
        | ExportCommands _ :: rest -> { tab with Pending = rest }

    /// Delivers effects until none is pending (bounded, so a runaway loop fails instead of hanging).
    let drainWith (connection: ConnectionContext) (execute: Execute) (tab: Tab) : Tab =
        let rec go n (t: Tab) =
            if List.isEmpty t.Pending then t
            elif n > 500 then failwith "the save did not settle within 500 effects"
            else go (n + 1) (deliverWith connection execute t)

        go 0 tab

    let deliverOne (fake: InvariantFake.Handle) (tab: Tab) : Tab = deliverWith connection fake.Server.Execute tab

    let drain (fake: InvariantFake.Handle) (tab: Tab) : Tab = drainWith connection fake.Server.Execute tab

    /// Every request the fake received, oldest first.
    let requests (fake: InvariantFake.Handle) = fake.Server.State.Log |> List.map fst

    let mutations (fake: InvariantFake.Handle) = requests fake |> List.filter isMutationRequest

    let isRefRead (request: HttpRequest) = request.Url.Contains("/git/ref/", StringComparison.Ordinal)

    let isContentsRead (request: HttpRequest) = request.Url.Contains("/contents/", StringComparison.Ordinal)

    let stateName (tab: Tab) = SaveProcess.stateName tab.Session.State

    /// Another writer changes this investigation's snapshot (same investigation, different bytes).
    let otherWriterChangesInvestigation (fake: InvariantFake.Handle) =
        let (RepoPath p) = StoragePaths.snapshot investigation
        fake.Server.Mutate(StatefulFake.commitOnBranch fake.Branch "other writer, same investigation" [ p, Encoding.UTF8.GetBytes "{\"other\":true}" ])

module PERSIST007Tests =
    open SaveHarness

    /// O1: Saved is reached only after GitHub returned the new commit and a re-read at that commit returned the
    /// written bytes: the mutation is followed by contents reads at exactly the returned commit, before Saved.
    [<Fact>]
    let ``PERSIST-007-O1`` () =
        let fake = newFake ()
        let saved = openTab fake |> edit 1 |> input SaveInput.SaveRequested |> drain fake

        match saved.Session.State with
        | SaveState.Saved(Confirmed c) ->
            let log = requests fake
            let mutationAt = log |> List.findIndex isMutationRequest
            let (CommitOid oid) = c.CommitOid
            let readsAfter = log |> List.skip (mutationAt + 1)
            Assert.Equal(3, readsAfter.Length)
            Assert.All(readsAfter, fun r -> Assert.True(isContentsRead r && r.Url.Contains(oid, StringComparison.Ordinal), r.Url))
            Assert.Equal(InvariantFake.head fake, c.CommitOid)
        | other -> failwithf "expected Saved by confirmation, got %A" other

        // When the re-read cannot be made, the save is not confirmed even though the commit exists.
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        fake.Server.Script [ Deliver; LoseBeforeApplying LostAfterSending ]
        let tab = tab |> deliverOne fake |> deliverOne fake
        Assert.Equal(1, mutations fake |> List.length)
        Assert.NotEqual<string>("Saved", stateName tab)

    /// O2: only a confirmed save moves to Saved and advances the base head; an unconfirmed outcome keeps the old base.
    [<Fact>]
    let ``PERSIST-007-O2`` () =
        let fake = newFake ()
        let h0 = InvariantFake.head fake
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        // The mutation applies; the read-back answer is lost: the outcome is not confirmed.
        fake.Server.Script [ Deliver; LoseBeforeApplying LostAfterSending ]
        let tab = tab |> deliverOne fake |> deliverOne fake
        Assert.Equal("UnknownOutcome", stateName tab)
        Assert.Equal(h0, tab.Session.BaseHead)
        Assert.NotEqual(h0, InvariantFake.head fake)

        // A confirmed save advances the base head to the confirmed commit.
        let fake = newFake ()
        let saved = openTab fake |> edit 1 |> input SaveInput.SaveRequested |> drain fake
        Assert.Equal("Saved", stateName saved)
        Assert.Equal(InvariantFake.head fake, saved.Session.BaseHead)
        Assert.NotEqual(h0, saved.Session.BaseHead)

    /// O3: changes made while the save was in flight stay unsaved after it is confirmed.
    [<Fact>]
    let ``PERSIST-007-O3`` () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        let tab = tab |> deliverOne fake |> edit 2 |> drain fake
        Assert.Equal("Unsaved", stateName tab)
        Assert.Equal<CommandEnvelope list>([ command 2 ], tab.Session.Unsaved)
        Assert.Equal(InvariantFake.head fake, tab.Session.BaseHead)

    let private anyFile = { Manifest = [| 1uy |]; Events = [| 2uy |]; Snapshot = [| 3uy |] }

    let private everyOtherState (verified: VerifiedInvestigation) : SaveState list =
        [ SaveState.Unsaved
          SaveState.Saving
          SaveState.Offline
          SaveState.Conflict(Reviewable verified)
          SaveState.Conflict(Reapplied(verified, []))
          SaveState.Conflict(Discarded verified)
          SaveState.UnknownOutcome 0
          SaveState.UnknownOutcome 2
          SaveState.SaveFailed RepositoryKeptChanging ]

    /// O4 (PAT-02, PAT-10, PAT-12): Saved appears only after the read-back. From every state that is not Saved, no
    /// input other than a matching read-back (or a reconciliation that re-read the bytes, from Saving or
    /// UnknownOutcome) reaches Saved. The one other entry, a confirmed discard to the verified GitHub version, is
    /// Saved(VerifiedAt) by owner decision ODR-V4-08, never Saved(Confirmed).
    [<Fact>]
    let ``PERSIST-007-O4`` () =
        let fake = newFake ()
        let tab = openTab fake
        let verified = InvariantFake.openAtHead fake
        let c = Confirmation.ofReadBack investigation (CommitOid(String('c', 40))) DateTimeOffset.UnixEpoch anyFile anyFile |> Option.get
        let oid ch = CommitOid(String(ch, 40))

        let inputs =
            [ SaveInput.Edit(command 1)
              SaveInput.SaveRequested
              SaveInput.MutationSent(RequestId 1L)
              SaveInput.ReadBackMismatched(oid 'd')
              SaveInput.StaleHeadUnrelated(oid 'e')
              SaveInput.StaleHeadSameInvestigation(oid 'e')
              SaveInput.CredentialRefused
              SaveInput.AnswerLost
              SaveInput.FailedBeforeSending
              SaveInput.ReconcileMarkerBytesDiffer(oid 'd')
              SaveInput.ReconcileNothingWrittenHeadUnchanged
              SaveInput.ReconcileNothingWrittenHeadMoved(oid 'e')
              SaveInput.ReconcileHistoryRewritten
              SaveInput.ReconcileReadFailed
              SaveInput.ReconcileReadsSpent
              SaveInput.CheckAgain
              SaveInput.WentOffline
              SaveInput.WentOnline
              SaveInput.ConflictResolvedReapplied [ command 1 ]
              SaveInput.SupersededReply(RequestId 1L)
              SaveInput.QueuedSaveStarted
              SaveInput.RebaseReadFailed
              SaveInput.ReadBackReadFailed ]

        for state in everyOtherState verified do
            let session = { tab.Session with State = state; Unsaved = [ command 9 ]; Outstanding = Some(RequestId 1L, MutationAnswer) }

            for i in inputs do
                match tab.Machine.Step (context ()) session i with
                | Ok({ State = SaveState.Saved _ }, _) -> failwithf "%A + %A reached Saved without a read-back" state i
                | _ -> ()

            for i in [ SaveInput.ReadBackMatched c; SaveInput.ReconcileMarkerBytesMatch c ] do
                match tab.Machine.Step (context ()) session i, state with
                | Ok({ State = SaveState.Saved _ }, _), (SaveState.Saving | SaveState.UnknownOutcome _) -> ()
                | Ok({ State = SaveState.Saved _ }, _), _ -> failwithf "%A + %A reached Saved" state i
                | _ -> ()

        // Discard reaches Saved only as VerifiedAt the GitHub version (ODR-V4-08).
        match tab.Machine.Step (context ()) { tab.Session with State = SaveState.Conflict(Discarded verified) } (SaveInput.ConflictResolvedDiscarded verified) with
        | Ok({ State = SaveState.Saved(VerifiedAt v) }, _) -> Assert.Equal(verified, v)
        | other -> failwithf "expected Saved(VerifiedAt), got %A" other
