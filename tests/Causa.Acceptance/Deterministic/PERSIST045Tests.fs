// Owner: integration (WI-R8-ITESTS). PERSIST-045: a mutation's answer is lost after GitHub applied it and before
// GitHub applied it. The real SM-SAVE (SaveProcess.create, Interpret, Step, ReconcileDue) is driven to quiescence
// against the stateful GitHub fake of Causa.TestSupport; nothing of the machine is copied or stubbed.
namespace Causa.Acceptance.Deterministic

open System
open System.Text
open System.Text.Json.Nodes
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

/// Shared, pure support for the save-process acceptance tests (PERSIST-045, PERSIST-047).
module SaveAcceptance =
    let guid (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)
    let investigation = InvestigationId(guid 1)
    let tab = guid 9

    let connection: ConnectionContext =
        { Repository = { Owner = "fake-owner"; Name = "fake-repo"; Branch = "main" }
          Credential = Credential.create "acceptance-fake-credential" }

    let ctx (n: int64) : RequestContext =
        { Connection = connection
          NextId = RequestId n
          MutationInFlightElsewhere = false }

    let machine =
        SaveProcess.create
            { Codec = StorageV1.codec
              FileSetOf = SaveProcess.pendingCommandFiles
              Tab = tab
              NextMarker = Markers.forTab tab
              KnownOffline = false }

    let command (n: int) : CommandEnvelope =
        { CommandId = guid (5000 + n)
          Investigation = investigation
          ExpectedVersion = None }

    let ok (result: Result<'s * SaveEffect list, SaveFault>) =
        match result with
        | Ok value -> value
        | Error fault -> failwithf "a legal transition failed closed: %A" fault

    let isMutation (r: HttpRequest) = r.Body |> Option.exists (fun b -> b.Contains "createCommitOnBranch")
    let stateName (s: InvestigationSession) = SaveProcess.stateName s.State

    /// One request the application sent: what the session awaited when it was sent, the request, and the virtual time.
    type Sent =
        { Awaiting: Awaiting option
          Request: HttpRequest
          AtMs: int }

    /// A run to quiescence: the final session, every request sent in order, every state passed through, and every
    /// input the machine refused (fail closed; the App keeps the session unchanged, as Update.fs does).
    type Run =
        { Session: InvestigationSession
          Sent: Sent list
          States: string list
          Faults: SaveFault list }

    let mutations (run: Run) = run.Sent |> List.filter (fun s -> isMutation s.Request)

    let reconciliationReads (run: Run) =
        run.Sent |> List.filter (fun s -> s.Awaiting = Some ReconcileHead)

    /// Runs effects to quiescence within a virtual time window: each Send is executed once by `exec`; MutationSent
    /// precedes each mutation; a ScheduleReconcileRead timer fires after its delay only while inside the window.
    let drive (windowMs: int) (exec: HttpRequest -> HttpOutcome) (session: InvestigationSession) (effects: SaveEffect list) : Run =
        let rec go (session: InvestigationSession) n clock (sent: Sent list) (states: string list) (faults: SaveFault list) effects =
            let seen s = states @ [ stateName s ]
            let resume session n clock sent states effects = go session n clock sent states faults effects

            match effects with
            | [] ->
                { Session = session
                  Sent = sent
                  States = states
                  Faults = faults }
            | Send request :: rest when (match session.State with SaveState.Conflict _ -> true | _ -> false) ->
                // SM-CONFLICT's read of the GitHub version: sent once, not a save reply (conflict review is SM-CONFLICT's).
                exec request |> ignore
                resume session (n + 1L) clock (sent @ [ { Awaiting = None; Request = request; AtMs = clock } ]) states rest
            | Send request :: rest ->
                let session =
                    if isMutation request then fst (ok (machine.Step (ctx n) session (SaveInput.MutationSent request.Id))) else session

                let record =
                    { Awaiting = Option.map snd session.Outstanding
                      Request = request
                      AtMs = clock }

                match machine.Interpret (ctx (n + 1L)) session (exec request) with
                | NextRequest(next, nextRequest) -> resume next (n + 2L) clock (sent @ [ record ]) (seen next) (Send nextRequest :: rest)
                | MachineEvent input ->
                    match machine.Step (ctx (n + 2L)) session input with
                    | Ok(next, more) -> resume next (n + 3L) clock (sent @ [ record ]) (seen next) (rest @ more)
                    | Error fault -> go session (n + 3L) clock (sent @ [ record ]) states (faults @ [ fault ]) rest
            | ScheduleReconcileRead(_, delay) :: rest when clock + delay <= windowMs ->
                let next, more = ok (machine.ReconcileDue (ctx n) session)
                resume next (n + 1L) (clock + delay) sent (seen next) (rest @ more)
            | ScheduleReconcileRead _ :: rest -> resume session n windowMs sent states rest
            | _ :: rest -> resume session n clock sent states rest

        go session 100L 0 [] [ stateName session ] [] effects

    /// A branch holding one verified investigation, opened at its head, with one unsaved edit and the save requested.
    let editedAndSaveRequested () =
        let handle = InvariantFake.create "fake-owner" "fake-repo" "main" investigation
        let opened = machine.Initial(InvariantFake.openAtHead handle)
        let edited, _ = ok (machine.Step (ctx 1L) opened (SaveInput.Edit(command 1)))
        let saving, effects = ok (machine.Step (ctx 2L) edited SaveInput.SaveRequested)
        handle, saving, effects

    /// The server, losing the answer of the first mutation in the given way and then running `afterLoss` once.
    let losingFirstMutation (directive: FakeDirective) (afterLoss: unit -> unit) (server: StatefulFake.Server) =
        let mutable lost = false

        fun (request: HttpRequest) ->
            if isMutation request && not lost then
                lost <- true
                server.Script [ directive ]
                let outcome = server.Execute request
                afterLoss ()
                outcome
            else
                server.Execute request

    let utf8 (s: string) = Encoding.UTF8.GetBytes s

    let treeAtHead (handle: InvariantFake.Handle) =
        let (CommitOid oid) = InvariantFake.head handle
        handle.Server.State.Commits[oid].Tree |> Map.toList

    let mutationCountOnServer (handle: InvariantFake.Handle) =
        handle.Server.State.Log |> List.map fst |> List.filter isMutation |> List.length

    let isSaved (run: Run) = stateName run.Session = "Saved"

open SaveAcceptance

type PERSIST045Tests() =
    let lostAfterApplying () =
        let handle, saving, effects = editedAndSaveRequested ()
        handle, drive 60_000 (losingFirstMutation (LoseAfterApplying TimedOut) ignore handle.Server) saving effects

    let lostBeforeApplying () =
        let handle, saving, effects = editedAndSaveRequested ()
        let base' = InvariantFake.head handle
        handle, base', drive 60_000 (losingFirstMutation (LoseBeforeApplying LostAfterSending) ignore handle.Server) saving effects

    [<Fact>]
    member _.``PERSIST-045-O1``() =
        // The answer is lost in each of the two ways; both are recognised as lost answers, not as failures or conflicts.
        let _, after = lostAfterApplying ()
        let _, _, before = lostBeforeApplying ()

        for run in [ after; before ] do
            Assert.Contains("UnknownOutcome", run.States)
            Assert.DoesNotContain("SaveFailed", run.States)
            Assert.DoesNotContain("Conflict", run.States)

    [<Fact>]
    member _.``PERSIST-045-O2``() =
        // Unknown commit outcome, and no second mutation before a reconciliation read has decided.
        for directive in [ LoseAfterApplying TimedOut; LoseBeforeApplying LostAfterSending ] do
            let handle, saving, effects = editedAndSaveRequested ()
            let run = drive 60_000 (losingFirstMutation directive ignore handle.Server) saving effects
            let afterFirstMutation = run.States |> List.skipWhile (fun s -> s <> "Saving") |> List.skipWhile (fun s -> s = "Saving")
            Assert.Equal("UnknownOutcome", List.head afterFirstMutation)
            let sent = run.Sent |> List.map (fun s -> s.Awaiting, isMutation s.Request)
            let mutationAt = sent |> List.indexed |> List.filter (fun (_, (_, m)) -> m) |> List.map fst

            match mutationAt with
            | first :: second :: _ ->
                let between = sent[first + 1 .. second - 1]
                Assert.True(between |> List.exists (fun (a, _) -> a = Some ReconcileHead), sprintf "%A: a mutation was resent before any reconciliation read" directive)
            | [ first ] -> Assert.Contains(sent |> List.skip (first + 1), fun (a, _) -> a = Some ReconcileHead)
            | [] -> Assert.Fail "no mutation was sent"

    [<Fact>]
    member _.``PERSIST-045-O3``() =
        // Applied: reconciliation finds this save's commit; it ends Saved, after the read-back, with one mutation.
        let handle, run = lostAfterApplying ()

        match run.Session.State with
        | SaveState.Saved(Confirmed c) -> Assert.Equal(InvariantFake.head handle, c.CommitOid)
        | other -> Assert.Fail(sprintf "the applied commit was not confirmed: %A" other)

        Assert.Equal(1, mutations run |> List.length)
        Assert.Equal(1, mutationCountOnServer handle)
        let reconcile = run.Sent |> List.findIndex (fun s -> s.Awaiting = Some ReconcileHead)
        Assert.Contains(run.Sent |> List.skip reconcile, fun s -> s.Request.Url.Contains "/contents/")

    [<Fact>]
    member _.``PERSIST-045-O4``() =
        // Not applied, head unchanged: sent again at the same expected head, within the limit, and Saved.
        let handle, base', run = lostBeforeApplying ()
        Assert.True(isSaved run, sprintf "%A" run.States)
        let bodies = mutations run |> List.choose (fun s -> s.Request.Body)
        Assert.Equal(2, bodies.Length)
        Assert.True(bodies.Length <= SaveProcess.maxMutations)
        let (CommitOid b) = base'
        Assert.All(bodies, fun body -> Assert.Contains(sprintf "\"expectedHeadOid\":\"%s\"" b, body))
        Assert.Equal(InvariantFake.head handle, run.Session.BaseHead)

    [<Fact>]
    member _.``PERSIST-045-O5``() =
        // Not applied, head moved: the stale-head rules apply. Unrelated change: rebase and keep it. Same investigation
        // changed: Conflict, the changes kept, no further mutation.
        let handle, saving, effects = editedAndSaveRequested ()
        let mutable other = None
        let afterLoss () = other <- Some(InvariantFake.externalCommit handle)
        let run = drive 60_000 (losingFirstMutation (LoseBeforeApplying LostAfterSending) afterLoss handle.Server) saving effects
        Assert.True(isSaved run, sprintf "%A" run.States)
        Assert.Equal(1, run.Session.RebaseAttempts)
        Assert.True(InvariantFake.reachableFromHead handle (Option.get other))

        let handle2, saving2, effects2 = editedAndSaveRequested ()
        let (RepoPath events) = StoragePaths.events investigation

        let changeSame () =
            handle2.Server.Mutate(StatefulFake.commitOnBranch "main" "other tab" [ events, utf8 "changed" ]) |> ignore

        let run2 = drive 60_000 (losingFirstMutation (LoseBeforeApplying LostAfterSending) changeSame handle2.Server) saving2 effects2
        Assert.Equal("Conflict", stateName run2.Session)
        Assert.Equal<CommandEnvelope list>([ command 1 ], run2.Session.Unsaved)
        Assert.Equal(1, mutations run2 |> List.length)

    [<Fact>]
    member _.``PERSIST-045-O6``() =
        // (a) A commit carrying this save's marker but different bytes: Save failed (bytes differ), never Saved.
        let handle, saving, effects = editedAndSaveRequested ()
        let lose = losingFirstMutation (LoseAfterApplying TimedOut) ignore handle.Server

        let tampered (request: HttpRequest) =
            if isMutation request then
                let body = JsonNode.Parse(Option.get request.Body) |> nonNull
                let input = (body["variables"] |> nonNull)["input"] |> nonNull
                let additions = (input["fileChanges"] |> nonNull)["additions"] |> nonNull :?> JsonArray
                let first = additions[0] |> nonNull
                first["contents"] <- JsonValue.Create(Convert.ToBase64String(utf8 "{\"tampered\":true}\n"))
                lose { request with Body = Some(body.ToJsonString()) }
            else
                lose request

        let differ = drive 60_000 tampered saving effects

        match differ.Session.State with
        | SaveState.SaveFailed(BytesDiffer _) -> ()
        | other -> Assert.Fail(sprintf "marker with different bytes: %A" other)

        Assert.Equal(1, mutations differ |> List.length)

        // (b) The branch history rewritten while the answer was lost: Save failed (history rewritten), changes kept.
        let handle2, saving2, effects2 = editedAndSaveRequested ()
        let tree = treeAtHead handle2
        let rewrite () = handle2.Server.Mutate(StatefulFake.rewriteBranch "main" tree) |> ignore
        let rewritten = drive 60_000 (losingFirstMutation (LoseBeforeApplying LostAfterSending) rewrite handle2.Server) saving2 effects2

        match rewritten.Session.State with
        | SaveState.SaveFailed HistoryWasRewritten -> ()
        | other -> Assert.Fail(sprintf "rewritten history: %A" other)

        Assert.Equal<CommandEnvelope list>([ command 1 ], rewritten.Session.Unsaved)
        Assert.Equal(1, mutations rewritten |> List.length)

        // (c) Every reconciliation read fails: Unknown commit outcome with no automatic read left, no second mutation.
        let handle3, saving3, effects3 = editedAndSaveRequested ()
        let mutable mutated = false

        let failingReads (request: HttpRequest) =
            if isMutation request && not mutated then
                mutated <- true
                handle3.Server.Script [ LoseAfterApplying TimedOut ]
                handle3.Server.Execute request
            else
                NotAnswered(request.Id, LostAfterSending)

        let failing = drive 60_000 failingReads saving3 effects3

        match failing.Session.State with
        | SaveState.UnknownOutcome 0 -> ()
        | other -> Assert.Fail(sprintf "failing reconciliation reads: %A" other)

        Assert.Equal(1, mutations failing |> List.length)
        Assert.InRange(reconciliationReads failing |> List.length, 1, SaveProcess.maxAutomaticReads)

    [<Fact>]
    member _.``PERSIST-045-O7``() =
        // Acceptance on the candidate SHA: both lost-answer cases end Saved and never rest in Saving.
        let _, after = lostAfterApplying ()
        let _, _, before = lostBeforeApplying ()
        Assert.True(isSaved after, sprintf "%A" after.States)
        Assert.True(isSaved before, sprintf "%A" before.States)

    [<Fact>]
    member _.``PERSIST-045-O8``() =
        // Covers PERSIST-015: reconciliation is the only path out of a lost answer, bounded by the automatic reads.
        let _, after = lostAfterApplying ()
        let _, _, before = lostBeforeApplying ()

        for run in [ after; before ] do
            Assert.InRange(reconciliationReads run |> List.length, 1, SaveProcess.maxAutomaticReads)
            Assert.True(mutations run |> List.length <= SaveProcess.maxMutations)
