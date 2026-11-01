// Owner: persistence. T19: SM-SAVE transition tests (state-machine-contracts.json), one per transition, the illegal
// pairs, and the save process end to end against the stateful fake (Interpret: read-back, rebase, conflict,
// reconciliation).
namespace Causa.Persistence.Tests.StateMachines

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport
open Support

module SMSAVE =
    let private conf = confirmation 30
    let private remote = verifiedAt 21
    let private run (machine: SaveMachine) context session input = ok (machine.Step context session input)
    let private step session input = run online (ctx 40L) session input

    let private unknownIn = { sessionIn (SaveState.UnknownOutcome 2) with ReconcileReads = 1; Mutations = 1 }

    let private states: (string * InvestigationSession) list =
        [ "Saved", start
          "Unsaved", sessionIn SaveState.Unsaved
          "Saving", saving
          "Offline", sessionIn SaveState.Offline
          "Conflict", sessionIn (SaveState.Conflict(Reviewable remote))
          "UnknownOutcome", unknownIn
          "SaveFailed", sessionIn (SaveState.SaveFailed RepositoryKeptChanging) ]

    let private events: (string * SaveInput) list =
        [ "Edit", SaveInput.Edit(command 2)
          "SaveRequested", SaveInput.SaveRequested
          "MutationSent", SaveInput.MutationSent(RequestId 7L)
          "ReadBackMatched", SaveInput.ReadBackMatched conf
          "ReadBackMismatched", SaveInput.ReadBackMismatched(head 3)
          "StaleHeadUnrelated", SaveInput.StaleHeadUnrelated(head 3)
          "StaleHeadSameInvestigation", SaveInput.StaleHeadSameInvestigation(head 3)
          "ProviderRefused", SaveInput.ProviderRefused(PermissionDenied "write")
          "CredentialRefused", SaveInput.CredentialRefused
          "AnswerLost", SaveInput.AnswerLost
          "FailedBeforeSending", SaveInput.FailedBeforeSending
          "ReconcileMarkerBytesMatch", SaveInput.ReconcileMarkerBytesMatch conf
          "ReconcileMarkerBytesDiffer", SaveInput.ReconcileMarkerBytesDiffer(head 3)
          "ReconcileNothingWrittenHeadUnchanged", SaveInput.ReconcileNothingWrittenHeadUnchanged
          "ReconcileNothingWrittenHeadMoved", SaveInput.ReconcileNothingWrittenHeadMoved(head 3)
          "ReconcileHistoryRewritten", SaveInput.ReconcileHistoryRewritten
          "ReconcileReadFailed", SaveInput.ReconcileReadFailed
          "ReconcileReadsSpent", SaveInput.ReconcileReadsSpent
          "CheckAgain", SaveInput.CheckAgain
          "WentOffline", SaveInput.WentOffline
          "WentOnline", SaveInput.WentOnline
          "ConflictResolvedReapplied", SaveInput.ConflictResolvedReapplied [ command 1 ]
          "ConflictResolvedDiscarded", SaveInput.ConflictResolvedDiscarded remote
          "SupersededReply", SaveInput.SupersededReply(RequestId 99L)
          "QueuedSaveStarted", SaveInput.QueuedSaveStarted
          "RebaseReadFailed", SaveInput.RebaseReadFailed
          "ReadBackReadFailed", SaveInput.ReadBackReadFailed ]

    let legal =
        set
            [ "Saved", "Edit"; "Saved", "WentOffline"; "Saved", "WentOnline"
              "Unsaved", "Edit"; "Unsaved", "SaveRequested"; "Unsaved", "WentOffline"; "Unsaved", "WentOnline"; "Unsaved", "QueuedSaveStarted"
              "Saving", "Edit"; "Saving", "MutationSent"; "Saving", "ReadBackMatched"; "Saving", "ReadBackMismatched"
              "Saving", "StaleHeadUnrelated"; "Saving", "StaleHeadSameInvestigation"; "Saving", "ProviderRefused"
              "Saving", "CredentialRefused"; "Saving", "AnswerLost"; "Saving", "FailedBeforeSending"; "Saving", "WentOffline"
              "Saving", "WentOnline"; "Saving", "SupersededReply"; "Saving", "RebaseReadFailed"; "Saving", "ReadBackReadFailed"
              "Offline", "Edit"; "Offline", "SaveRequested"; "Offline", "WentOffline"; "Offline", "WentOnline"; "Offline", "QueuedSaveStarted"
              "Conflict", "Edit"; "Conflict", "WentOffline"; "Conflict", "WentOnline"; "Conflict", "ConflictResolvedReapplied"
              "Conflict", "ConflictResolvedDiscarded"
              "UnknownOutcome", "Edit"; "UnknownOutcome", "StaleHeadSameInvestigation"; "UnknownOutcome", "ReconcileMarkerBytesMatch"
              "UnknownOutcome", "ReconcileMarkerBytesDiffer"; "UnknownOutcome", "ReconcileNothingWrittenHeadUnchanged"
              "UnknownOutcome", "ReconcileNothingWrittenHeadMoved"; "UnknownOutcome", "ReconcileHistoryRewritten"
              "UnknownOutcome", "ReconcileReadFailed"; "UnknownOutcome", "ReconcileReadsSpent"; "UnknownOutcome", "CheckAgain"
              "UnknownOutcome", "WentOffline"; "UnknownOutcome", "WentOnline"; "UnknownOutcome", "SupersededReply"
              "SaveFailed", "Edit"; "SaveFailed", "SaveRequested"; "SaveFailed", "WentOffline"; "SaveFailed", "WentOnline" ]

    let private oneMutation (expected: CommitOid) (effects: SaveEffect list) =
        match mutations effects with
        | [ request ] ->
            let (CommitOid oid) = expected
            Assert.Contains(sprintf "\"expectedHeadOid\":\"%s\"" oid, defaultArg request.Body "")
            Assert.Contains(Markers.trailerKey, defaultArg request.Body "")
            request
        | other -> failwithf "expected exactly one mutation: %A" other

    let private stateName (s: InvestigationSession) = SaveProcess.stateName s.State

    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        Assert.Equal(50, legal.Count)
        let pairs = illegalPairs states events legal
        Assert.Equal(139, pairs.Length)

        for (sn, s, en, e) in pairs do
            for machine in [ online; offline ] do
                let fault = faults (machine.Step (ctx 1L) s e)
                Assert.Equal("SM-SAVE", fault.Machine)
                Assert.Equal(sn, fault.State)
                Assert.Equal(en, fault.Input)

    [<Fact>]
    let ``T-S01`` () =
        let s, effects = step start (SaveInput.Edit(command 1))
        Assert.Equal(SaveState.Unsaved, s.State)
        Assert.Equal<CommandEnvelope list>([ command 1 ], s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S02`` () =
        let s, effects = run offline (ctx 1L) start (SaveInput.Edit(command 1))
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Equal<CommandEnvelope list>([ command 1 ], s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S03`` () =
        let s, _ = step (sessionIn SaveState.Unsaved) (SaveInput.Edit(command 2))
        Assert.Equal<CommandEnvelope list>([ command 1; command 2 ], s.Unsaved)

    [<Fact>]
    let ``T-S04`` () =
        let s, effects = step (sessionIn SaveState.Unsaved) SaveInput.SaveRequested
        Assert.Equal(SaveState.Saving, s.State)
        Assert.Equal(Some(RequestId 40L, MutationAnswer), s.Outstanding)
        Assert.Equal(1, s.Mutations)
        let request = oneMutation (head 1) effects
        Assert.Equal("https://api.github.com/graphql", request.Url)
        let (RepoPath prefix) = StoragePaths.prefix investigation
        Assert.Contains(prefix + "events.ndjson", defaultArg request.Body "")

    [<Fact>]
    let ``T-S05`` () =
        let s, effects = run offline (ctx 1L) (sessionIn SaveState.Unsaved) SaveInput.SaveRequested
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S06`` () =
        let s, effects = step (sessionIn SaveState.Unsaved) SaveInput.WentOffline
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S07`` () =
        let s, _ = step (sessionIn SaveState.Offline) (SaveInput.Edit(command 2))
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Equal(2, s.Unsaved.Length)

    [<Fact>]
    let ``T-S08`` () =
        let s, effects = run offline (ctx 1L) (sessionIn SaveState.Offline) SaveInput.SaveRequested
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S09`` () =
        let s, effects = step (sessionIn SaveState.Offline) SaveInput.WentOnline
        Assert.Equal(SaveState.Unsaved, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S10`` () =
        let s, effects = step saving (SaveInput.Edit(command 2))
        Assert.Equal(SaveState.Saving, s.State)
        Assert.Equal<CommandEnvelope list>([ command 2 ], s.AfterInFlight)
        Assert.Equal<CommandEnvelope list>([ command 1 ], s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S11`` () =
        let s, _ = step saving (SaveInput.ReadBackMatched conf)
        Assert.Equal(SaveState.Saved(Confirmed conf), s.State)
        Assert.Equal(conf.CommitOid, s.BaseHead)
        Assert.Empty(s.Unsaved)

    [<Fact>]
    let ``T-S12`` () =
        let s, _ = step { saving with AfterInFlight = [ command 2 ] } (SaveInput.ReadBackMatched conf)
        Assert.Equal(SaveState.Unsaved, s.State)
        Assert.Equal(conf.CommitOid, s.BaseHead)
        Assert.Equal<CommandEnvelope list>([ command 2 ], s.Unsaved)

    [<Fact>]
    let ``T-S13`` () =
        let s, effects = step saving (SaveInput.ReadBackMismatched(head 3))
        Assert.Equal(SaveState.SaveFailed(BytesDiffer(head 3)), s.State)
        Assert.Equal<CommandEnvelope list>([ command 1 ], s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S14`` () =
        let s, effects = step saving (SaveInput.StaleHeadUnrelated(head 3))
        Assert.Equal(SaveState.Saving, s.State)
        Assert.Equal(head 3, s.BaseHead)
        Assert.Equal(1, s.RebaseAttempts)
        oneMutation (head 3) effects |> ignore

    [<Fact>]
    let ``T-S15`` () =
        let s, effects = step { saving with RebaseAttempts = 3; Mutations = 4 } (SaveInput.StaleHeadUnrelated(head 3))
        Assert.Equal(SaveState.SaveFailed RepositoryKeptChanging, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S16`` () =
        let s, effects = step { saving with AfterInFlight = [ command 2 ] } (SaveInput.StaleHeadSameInvestigation(head 3))
        Assert.Equal(SaveState.Conflict(ReadingRemote(RequestId 40L)), s.State)
        Assert.Equal<CommandEnvelope list>([ command 1; command 2 ], s.Unsaved)
        Assert.Empty(mutations effects)
        Assert.Single(sends effects) |> ignore

    [<Fact>]
    let ``T-S17`` () =
        let s, _ = step saving (SaveInput.ProviderRefused(RateLimited(Secondary(Some 60))))
        Assert.Equal(SaveState.SaveFailed(Refused(RateLimited(Secondary(Some 60)))), s.State)
        Assert.Equal<CommandEnvelope list>([ command 1 ], s.Unsaved)

    [<Fact>]
    let ``T-S18`` () =
        let s, _ = step saving SaveInput.CredentialRefused
        Assert.Equal(SaveState.SaveFailed CredentialNotAccepted, s.State)

    [<Fact>]
    let ``T-S19`` () =
        let s, effects = step saving SaveInput.AnswerLost
        Assert.Equal("UnknownOutcome", stateName s)
        Assert.Empty(mutations effects)
        Assert.Equal<int list>([ 2000 ], schedules effects)

    [<Fact>]
    let ``T-S20`` () =
        let s, effects = step saving SaveInput.FailedBeforeSending
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S21`` () =
        let s, _ = step unknownIn (SaveInput.ReconcileMarkerBytesMatch conf)
        Assert.Equal(SaveState.Saved(Confirmed conf), s.State)
        Assert.Equal(conf.CommitOid, s.BaseHead)

    [<Fact>]
    let ``T-S22`` () =
        let s, _ = step { unknownIn with AfterInFlight = [ command 2 ] } (SaveInput.ReconcileMarkerBytesMatch conf)
        Assert.Equal(SaveState.Unsaved, s.State)
        Assert.Equal<CommandEnvelope list>([ command 2 ], s.Unsaved)

    [<Fact>]
    let ``T-S23`` () =
        let s, _ = step unknownIn (SaveInput.ReconcileMarkerBytesDiffer(head 3))
        Assert.Equal(SaveState.SaveFailed(BytesDiffer(head 3)), s.State)
        Assert.Equal<CommandEnvelope list>([ command 1 ], s.Unsaved)

    [<Fact>]
    let ``T-S24`` () =
        let s, effects = step unknownIn SaveInput.ReconcileNothingWrittenHeadUnchanged
        Assert.Equal(SaveState.Saving, s.State)
        Assert.Equal(head 1, s.BaseHead)
        Assert.Equal(2, s.Mutations)
        oneMutation (head 1) effects |> ignore

    [<Fact>]
    let ``T-S25`` () =
        let s, effects = step { unknownIn with Mutations = 4 } SaveInput.ReconcileNothingWrittenHeadUnchanged
        Assert.Equal(SaveState.SaveFailed RepositoryKeptChanging, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S26`` () =
        let s, effects = step unknownIn (SaveInput.ReconcileNothingWrittenHeadMoved(head 4))
        Assert.Equal(SaveState.Saving, s.State)
        Assert.Equal(head 4, s.BaseHead)
        oneMutation (head 4) effects |> ignore

    [<Fact>]
    let ``T-S27`` () =
        let s, effects = step unknownIn (SaveInput.StaleHeadSameInvestigation(head 4))
        Assert.Equal("Conflict", stateName s)
        Assert.Empty(mutations effects)

    [<Fact>]
    let ``T-S28`` () =
        let s, _ = step unknownIn SaveInput.ReconcileHistoryRewritten
        Assert.Equal(SaveState.SaveFailed HistoryWasRewritten, s.State)

    [<Fact>]
    let ``T-S29`` () =
        let s, effects = step unknownIn SaveInput.ReconcileReadFailed
        Assert.Equal("UnknownOutcome", stateName s)
        Assert.Equal<int list>([ 4000 ], schedules effects)
        let s', effects' = step s SaveInput.ReconcileReadFailed
        Assert.Equal<int list>([ 8000 ], schedules effects')
        // Three automatic reads made: the guard fails, fail closed.
        Assert.True(Result.isError (online.Step (ctx 1L) s' SaveInput.ReconcileReadFailed))

    [<Fact>]
    let ``T-S30`` () =
        let s, effects = step unknownIn SaveInput.ReconcileReadsSpent
        Assert.Equal(SaveState.UnknownOutcome 0, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S31`` () =
        let spent = { unknownIn with State = SaveState.UnknownOutcome 0; ReconcileReads = 3 }
        let s, effects = step spent SaveInput.CheckAgain
        Assert.Equal(Some(RequestId 40L, ReconcileHead), s.Outstanding)
        Assert.Single(sends effects) |> ignore
        Assert.Empty(mutations effects)
        Assert.True(Result.isError (online.Step (ctx 1L) unknownIn SaveInput.CheckAgain), "reads left: no Check again")

    [<Fact>]
    let ``T-S32`` () =
        let s, effects = step unknownIn SaveInput.WentOnline
        Assert.Equal(Some(RequestId 40L, ReconcileHead), s.Outstanding)
        Assert.Single(sends effects) |> ignore

    [<Fact>]
    let ``T-S33`` () =
        let s, _ = step unknownIn (SaveInput.Edit(command 2))
        Assert.Equal<CommandEnvelope list>([ command 2 ], s.AfterInFlight)

    [<Fact>]
    let ``T-S34`` () =
        let s, _ = step (sessionIn (SaveState.SaveFailed RepositoryKeptChanging)) (SaveInput.Edit(command 2))
        Assert.Equal("SaveFailed", stateName s)
        Assert.Equal(2, s.Unsaved.Length)

    [<Fact>]
    let ``T-S35`` () =
        let failed = { sessionIn (SaveState.SaveFailed RepositoryKeptChanging) with Mutations = 4; RebaseAttempts = 3 }
        let s, effects = step failed SaveInput.SaveRequested
        Assert.Equal(SaveState.Saving, s.State)
        Assert.Equal(1, s.Mutations)
        Assert.Equal(0, s.RebaseAttempts)
        oneMutation (head 1) effects |> ignore

    [<Fact>]
    let ``T-S36`` () =
        let s, effects = step (sessionIn (SaveState.Conflict(Reviewable remote))) (SaveInput.Edit(command 2))
        Assert.Equal("Conflict", stateName s)
        Assert.Equal(2, s.Unsaved.Length)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S37`` () =
        let s, effects = step (sessionIn (SaveState.Conflict(Reapplied(remote, [])))) (SaveInput.ConflictResolvedReapplied [ command 3 ])
        Assert.Equal(SaveState.Unsaved, s.State)
        Assert.Equal(remote.ReadAt, s.BaseHead)
        Assert.Equal<CommandEnvelope list>([ command 3 ], s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S38`` () =
        let s, effects = step (sessionIn (SaveState.Conflict(Discarded remote))) (SaveInput.ConflictResolvedDiscarded remote)
        Assert.Equal(SaveState.Saved(VerifiedAt remote), s.State)
        Assert.Equal(remote.ReadAt, s.BaseHead)
        Assert.Empty(s.Unsaved)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S39`` () =
        let s, effects = step saving (SaveInput.SupersededReply(RequestId 3L))
        Assert.Equal(saving, s)
        Assert.Empty(effects)
        match online.Interpret (ctx 1L) saving (Answered(RequestId 3L, 200, [], "{}")) with
        | MachineEvent(SaveInput.SupersededReply(RequestId 3L)) -> ()
        | other -> Assert.Fail(sprintf "%A" other)

    [<Fact>]
    let ``T-S40`` () =
        let s, effects = step unknownIn (SaveInput.SupersededReply(RequestId 3L))
        Assert.Equal(unknownIn, s)
        Assert.Empty(effects)

    [<Fact>]
    let ``T-S41`` () = Assert.Equal(start, fst (step start SaveInput.WentOffline))

    [<Fact>]
    let ``T-S42`` () = Assert.Equal(start, fst (step start SaveInput.WentOnline))

    [<Fact>]
    let ``T-S43`` () =
        let u = sessionIn SaveState.Unsaved
        Assert.Equal(u, fst (step u SaveInput.WentOnline))

    [<Fact>]
    let ``T-S44`` () =
        let o = sessionIn SaveState.Offline
        Assert.Equal(o, fst (step o SaveInput.WentOffline))

    [<Fact>]
    let ``T-S45`` () =
        let s, effects = step saving SaveInput.WentOffline
        Assert.Equal(saving, s)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S46`` () = Assert.Equal(saving, fst (step saving SaveInput.WentOnline))

    [<Fact>]
    let ``T-S47`` () =
        let s, effects = step unknownIn SaveInput.WentOffline
        Assert.Equal(unknownIn, s)
        Assert.Empty(effects)

    [<Fact>]
    let ``T-S48`` () =
        let c = sessionIn (SaveState.Conflict(Reviewable remote))
        Assert.Equal(c, fst (step c SaveInput.WentOffline))

    [<Fact>]
    let ``T-S49`` () =
        let c = sessionIn (SaveState.Conflict(Reviewable remote))
        Assert.Equal(c, fst (step c SaveInput.WentOnline))
        let s, effects = step (sessionIn (SaveState.Conflict RemoteUnavailable)) SaveInput.WentOnline
        Assert.Equal(SaveState.Conflict(ReadingRemote(RequestId 40L)), s.State)
        Assert.Single(sends effects) |> ignore
        Assert.Empty(mutations effects)

    [<Fact>]
    let ``T-S50`` () =
        let f = sessionIn (SaveState.SaveFailed RepositoryKeptChanging)
        Assert.Equal(f, fst (step f SaveInput.WentOffline))

    [<Fact>]
    let ``T-S51`` () =
        let f = sessionIn (SaveState.SaveFailed RepositoryKeptChanging)
        Assert.Equal(f, fst (step f SaveInput.WentOnline))

    [<Fact>]
    let ``T-S52`` () =
        let s, effects = run offline (ctx 1L) (sessionIn (SaveState.SaveFailed RepositoryKeptChanging)) SaveInput.SaveRequested
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S53`` () =
        let s, effects = run online (busy 1L) (sessionIn SaveState.Unsaved) SaveInput.SaveRequested
        Assert.Equal(SaveState.Unsaved, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S54`` () =
        let s, effects = step (sessionIn SaveState.Unsaved) SaveInput.QueuedSaveStarted
        Assert.Equal(SaveState.Saving, s.State)
        oneMutation (head 1) effects |> ignore

    [<Fact>]
    let ``T-S55`` () =
        let s, effects = run offline (ctx 1L) (sessionIn SaveState.Unsaved) SaveInput.QueuedSaveStarted
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S56`` () =
        let s, effects = step { unknownIn with RebaseAttempts = 3 } (SaveInput.ReconcileNothingWrittenHeadMoved(head 4))
        Assert.Equal(SaveState.SaveFailed RepositoryKeptChanging, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S57`` () =
        let s, effects = run offline (ctx 1L) saving SaveInput.RebaseReadFailed
        Assert.Equal(SaveState.Offline, s.State)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S58`` () =
        let s, effects = step saving SaveInput.ReadBackReadFailed
        Assert.Equal("UnknownOutcome", stateName s)
        Assert.Empty(mutations effects)

    [<Fact>]
    let ``T-S59`` () =
        let s, effects = step saving SaveInput.RebaseReadFailed
        Assert.Equal("UnknownOutcome", stateName s)
        Assert.Empty(mutations effects)

    [<Fact>]
    let ``T-S60`` () =
        let o = sessionIn SaveState.Offline
        let s, effects = step o SaveInput.QueuedSaveStarted
        Assert.Equal(o, s)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-S61`` () =
        let s, effects = step saving (SaveInput.MutationSent(RequestId 7L))
        Assert.Equal(saving, s)
        Assert.Empty(effects)
        Assert.True(Result.isError (online.Step (ctx 1L) saving (SaveInput.MutationSent(RequestId 8L))), "another request id fails closed")

    // ----- The save process end to end against the stateful fake (Interpret drives every read).

    let private fakeConnection = { connection with Repository = { Owner = "fake-owner"; Name = "fake-repo"; Branch = "main" } }

    let private fctx (n: int64) = { ctx n with Connection = fakeConnection }

    /// Runs effects to quiescence: each Send is executed once; MutationSent precedes each mutation; timers fire.
    let rec private drive (machine: SaveMachine) (server: StatefulFake.Server) (session: InvestigationSession, n: int64) (effects: SaveEffect list) =
        match effects with
        | [] -> session, n
        | Send request :: rest ->
            let session =
                if isMutation request then fst (ok (machine.Step (fctx n) session (SaveInput.MutationSent request.Id))) else session

            match machine.Interpret (fctx (n + 1L)) session (server.Execute request) with
            | NextRequest(next, nextRequest) -> drive machine server (next, n + 2L) (Send nextRequest :: rest)
            | MachineEvent input ->
                match machine.Step (fctx (n + 2L)) session input with
                | Ok(next, more) -> drive machine server (next, n + 3L) (rest @ more)
                | Error _ -> drive machine server (session, n + 3L) rest
        | ScheduleReconcileRead _ :: rest ->
            match machine.ReconcileDue (fctx n) session with
            | Ok(next, more) -> drive machine server (next, n + 1L) (rest @ more)
            | Error _ -> drive machine server (session, n + 1L) rest
        | _ :: rest -> drive machine server (session, n) rest

    let private opened () =
        let handle = InvariantFake.create "fake-owner" "fake-repo" "main" investigation
        handle, online.Initial(InvariantFake.openAtHead handle)

    let private saveOnce (handle: InvariantFake.Handle) (session: InvestigationSession) =
        let edited, _ = ok (online.Step (fctx 1L) session (SaveInput.Edit(command 1)))
        let saving, effects = ok (online.Step (fctx 2L) edited SaveInput.SaveRequested)
        fst (drive online handle.Server (saving, 10L) effects)

    [<Fact>]
    let SaveConfirmsOnlyAfterReadBack () =
        let handle, session = opened ()
        let saved = saveOnce handle session

        match saved.State with
        | SaveState.Saved(Confirmed c) ->
            Assert.Equal(InvariantFake.head handle, c.CommitOid)
            Assert.Equal(c.CommitOid, saved.BaseHead)
        | other -> Assert.Fail(sprintf "not confirmed: %A" other)

        // One mutation, then three file reads at the returned commit.
        let log = handle.Server.State.Log |> List.map fst
        Assert.Equal(1, log |> List.filter isMutation |> List.length)
        Assert.Equal(3, log |> List.filter (fun r -> r.Url.Contains "/contents/") |> List.length)

    [<Fact>]
    let RebaseOverUnrelatedCommitKeepsIt () =
        let handle, session = opened ()
        let other = InvariantFake.externalCommit handle
        let saved = saveOnce handle session
        Assert.Equal("Saved", stateName saved)
        Assert.True(InvariantFake.reachableFromHead handle other)
        Assert.Equal(1, saved.RebaseAttempts)

    [<Fact>]
    let SameInvestigationChangedIsAConflict () =
        let handle, session = opened ()
        let (RepoPath events) = StoragePaths.events investigation
        let other = handle.Server.Mutate(StatefulFake.commitOnBranch "main" "other tab" [ events, Text.Encoding.UTF8.GetBytes "changed" ])
        let after = saveOnce handle session
        Assert.Equal("Conflict", stateName after)
        Assert.Equal<CommandEnvelope list>([ command 1 ], after.Unsaved)
        Assert.Equal(other, InvariantFake.head handle)

    [<Fact>]
    let LostAnswerIsReconciledByMarker () =
        let handle, session = opened ()
        handle.Server.Script [ LoseAfterApplying TimedOut ]
        let saved = saveOnce handle session

        match saved.State with
        | SaveState.Saved(Confirmed c) -> Assert.Equal(InvariantFake.head handle, c.CommitOid)
        | other -> Assert.Fail(sprintf "the applied commit was not reconciled: %A" other)

        Assert.Equal(1, handle.Server.State.Log |> List.map fst |> List.filter isMutation |> List.length)

    [<Fact>]
    let LostBeforeApplyingIsResentWithTheSameHead () =
        let handle, session = opened ()
        let base' = InvariantFake.head handle
        handle.Server.Script [ LoseBeforeApplying LostAfterSending ]
        let saved = saveOnce handle session
        Assert.Equal("Saved", stateName saved)
        let bodies = handle.Server.State.Log |> List.map fst |> List.filter isMutation |> List.choose _.Body
        Assert.Equal(2, bodies.Length)
        let (CommitOid b) = base'
        Assert.All(bodies, fun body -> Assert.Contains(sprintf "\"expectedHeadOid\":\"%s\"" b, body))
        Assert.NotEqual<string>(bodies[0], bodies[1])

    // ----- Review T36 F-01: GitHub's compare lists at most 300 changed files and flags no truncation. A list at the
    // cap is unknown, so the save fails closed to Conflict (T-S16) instead of rebasing over another writer's change.

    let private bulkFiles (n: int) =
        [ for i in 1..n -> sprintf "bulk/%04d.json" i, Text.Encoding.UTF8.GetBytes(string i) ]

    [<Fact>]
    let BulkCommitHidingTheInvestigationPastThreeHundredFilesIsAConflict () =
        let handle, session = opened ()
        let (RepoPath events) = StoragePaths.events investigation
        // One commit touching 300 other files and this investigation's events: the fake, like GitHub, lists 300.
        let other =
            handle.Server.Mutate(StatefulFake.commitOnBranch "main" "bulk import" (bulkFiles 300 @ [ events, Text.Encoding.UTF8.GetBytes "theirs" ]))

        let after = saveOnce handle session
        Assert.Equal("Conflict", stateName after)
        Assert.Equal<CommandEnvelope list>([ command 1 ], after.Unsaved)
        Assert.Equal(other, InvariantFake.head handle)
        Assert.Equal(1, handle.Server.State.Log |> List.map fst |> List.filter isMutation |> List.length)

    [<Fact>]
    let ExactlyThreeHundredFilesInOneCommitIsAConflict () =
        let handle, session = opened ()
        let other = handle.Server.Mutate(StatefulFake.commitOnBranch "main" "bulk import" (bulkFiles 300))
        let after = saveOnce handle session
        Assert.Equal("Conflict", stateName after)
        Assert.Equal(other, InvariantFake.head handle)

    [<Fact>]
    let TwoHundredNinetyNineUnrelatedFilesStillRebase () =
        let handle, session = opened ()
        let other = handle.Server.Mutate(StatefulFake.commitOnBranch "main" "bulk import" (bulkFiles 299))
        let saved = saveOnce handle session
        Assert.Equal("Saved", stateName saved)
        Assert.True(InvariantFake.reachableFromHead handle other)
        Assert.Equal(1, saved.RebaseAttempts)

    /// A GitHub-shaped compare answer: one commit after base and `files` changed names, none under the investigation.
    let private compareBody (message: string) (files: int) =
        let names = [ for i in 1..files -> sprintf """{"filename":"bulk/%04d.json"}""" i ] |> String.concat ","
        let (CommitOid c) = head 3

        sprintf
            """{"status":"ahead","ahead_by":1,"behind_by":0,"total_commits":1,"commits":[{"sha":"%s","commit":{"message":%s}}],"files":[%s]}"""
            c
            (Text.Json.JsonSerializer.Serialize message)
            names

    let private answer (body: string) = Answered(RequestId 7L, 200, [], body)
    let private awaiting (a: Awaiting) = { saving with Outstanding = Some(RequestId 7L, a) }

    [<Fact>]
    let RebaseCompareAtTheFileCapCountsAsChanged () =
        let at files = online.Interpret (ctx 50L) (awaiting (FileComparisonAt(head 3))) (answer (compareBody "other" files))
        Assert.Equal(MachineEvent(SaveInput.StaleHeadSameInvestigation(head 3)), at 300)
        Assert.Equal(MachineEvent(SaveInput.StaleHeadSameInvestigation(head 3)), at 301)
        Assert.Equal(MachineEvent(SaveInput.StaleHeadUnrelated(head 3)), at 299)

    [<Fact>]
    let ReconciliationCompareAtTheFileCapCountsAsChanged () =
        let reconciling = { unknownIn with Outstanding = Some(RequestId 7L, ReconcileCommits 1) }
        let at message files = online.Interpret (ctx 50L) reconciling (answer (compareBody message files))
        Assert.Equal(MachineEvent(SaveInput.StaleHeadSameInvestigation(head 3)), at "other" 300)
        Assert.Equal(MachineEvent(SaveInput.ReconcileNothingWrittenHeadMoved(head 3)), at "other" 299)

        // Precedence kept: this tab's marker on the first commit is checked by reading its files, whatever the list.
        let marked = "save\n\n" + Markers.trailer { Session = tab; Sequence = 1L }

        match at marked 300 with
        | NextRequest(next, _) -> Assert.Equal(Some(RequestId 50L, ReconcileFilesAt(head 3)), next.Outstanding)
        | other -> Assert.Fail(sprintf "the marker commit was not read: %A" other)

    // ----- Review T36 F-06: a 404 on the mutation endpoint is an unknown outcome, reconciled; a 404 on a read-back
    // is a failed read (reconcile path), never "different bytes".

    let private notFound = Answered(RequestId 7L, 404, [], """{"message":"Not Found"}""")

    [<Fact>]
    let MutationEndpoint404IsAnUnknownOutcome () =
        Assert.Equal(MachineEvent SaveInput.AnswerLost, online.Interpret (ctx 50L) saving notFound)
        let s, _ = step saving SaveInput.AnswerLost
        Assert.Equal("UnknownOutcome", stateName s)

    [<Fact>]
    let ReadBack404IsAFailedReadNotDifferentBytes () =
        Assert.Equal(MachineEvent SaveInput.ReadBackReadFailed, online.Interpret (ctx 50L) (awaiting (ReadBackAt(head 3))) notFound)

        let reconciling = { unknownIn with Outstanding = Some(RequestId 7L, ReconcileFilesAt(head 3)) }
        Assert.Equal(MachineEvent SaveInput.ReconcileReadFailed, online.Interpret (ctx 50L) reconciling notFound)
