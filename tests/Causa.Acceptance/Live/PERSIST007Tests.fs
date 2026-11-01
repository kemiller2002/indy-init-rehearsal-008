// Owner: persistence. T20: PERSIST-007 confirm by read-back, against the fake and the live write-test repository,
// and the GH-LARGE-FILE path (one blob re-read after `encoding: none`).
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module PERSIST007Tests =
    open ProviderScenario

    let private setUp (p: ProviderUnderTest) =
        let investigation = InvestigationId(Guid.NewGuid())
        create p investigation |> ignore
        investigation, opened (openAtHead p investigation)

    /// O1: confirmed only when GitHub returned the commit and a re-read at it returned the written bytes.
    [<Fact>]
    let ``PERSIST-007-O1`` () =
        onBoth "probe/persist007-o1" (fun p ->
            let investigation, first = setUp p
            let session, written = save p first [ command investigation ]

            match session.State with
            | SaveState.Saved(Confirmed c) ->
                let atCommit = (readAt p c.CommitOid investigation).Value
                Assert.Equal<byte[]>(written.Events, atCommit.Events.Value)
                // The read-back requests name the commit the mutation returned.
                let (CommitOid oid) = c.CommitOid
                Assert.Contains(p.Sent(), fun r -> r.Url.Contains("ref=" + oid))
            | other -> failwithf "%A: expected Saved(Confirmed), got %A" p.Mode other)

    /// O2: only a confirmed save moves to Saved and advances the base head.
    [<Fact>]
    let ``PERSIST-007-O2`` () =
        onBoth "probe/persist007-o2" (fun p ->
            let investigation, first = setUp p
            let session, _ = save p first [ command investigation ]
            Assert.NotEqual(first.ReadAt, session.BaseHead)
            Assert.Equal(head p, session.BaseHead))

    /// O3: an edit made while the save was in flight stays unsaved after the confirmation.
    [<Fact>]
    let ``PERSIST-007-O3`` () =
        onBoth "probe/persist007-o3" (fun p ->
            let investigation, first = setUp p
            let setup = GitHubProvider.saveSetup tab markers (GitHubProvider.fileSetOf StorageV1.codec first (decided first)) false
            let machine = SaveProcess.create setup
            let ok r = match r with Ok x -> x | Error f -> failwithf "%A" f
            let s1, _ = ok (machine.Step (context p) (machine.Initial first) (SaveInput.Edit(command investigation)))
            let s2, effects = ok (machine.Step (context p) s1 SaveInput.SaveRequested)
            let late = command investigation
            let s3, _ = ok (machine.Step (context p) s2 (SaveInput.Edit late))

            let rec run session effects n =
                match effects |> List.tryPick (function Send r -> Some r | _ -> None) with
                | None -> session
                | Some _ when n = 0 -> failwith "did not settle"
                | Some request ->
                    match machine.Interpret (context p) session (p.Execute request) with
                    | MachineEvent e ->
                        let next, more = ok (machine.Step (context p) session e)
                        run next more (n - 1)
                    | NextRequest(next, r) -> run next [ Send r ] (n - 1)

            let settled = run s3 effects 32
            Assert.Equal<CommandEnvelope list>([ late ], settled.Unsaved @ settled.AfterInFlight)
            Assert.Equal(SaveState.Unsaved, settled.State))

    /// O4 (PAT-02/PAT-10): Saved appears only after the read-back: the last request before Saved is a re-read at the
    /// commit the mutation returned, and the new tab reads the same commit.
    [<Fact>]
    let ``PERSIST-007-O4`` () =
        onBoth "probe/persist007-o4" (fun p ->
            let investigation, first = setUp p
            let before = (mutations p).Length
            let session, _ = save p first [ command investigation ]
            let sent = p.Sent()
            let lastMutation = sent |> List.findIndexBack (fun r -> r.Body |> Option.exists (fun b -> b.Contains "createCommitOnBranch"))
            Assert.Equal(before + 1, (mutations p).Length)
            let (CommitOid oid) = session.BaseHead
            Assert.True(sent |> List.skip (lastMutation + 1) |> List.exists (fun r -> r.Url.Contains("ref=" + oid)))
            Assert.Equal(session.BaseHead, (opened (openAtHead p investigation)).ReadAt))

    /// Every request the machine emits is executed and its meaning stepped until nothing is outstanding; a scheduled
    /// reconciliation read fires at once (ReconcileDue).
    let private settle (p: ProviderUnderTest) (machine: SaveMachine) (session: InvestigationSession) (effects: SaveEffect list) =
        let ok r =
            match r with
            | Ok x -> x
            | Error f -> failwithf "%A: SM-SAVE fault %A" p.Mode f

        let rec run (session: InvestigationSession) (effects: SaveEffect list) (budget: int) =
            let send = effects |> List.tryPick (function Send r -> Some r | _ -> None)
            let timer = effects |> List.exists (function ScheduleReconcileRead _ -> true | _ -> false)

            match send, timer with
            | _ when budget = 0 -> failwithf "%A: the save did not settle" p.Mode
            | Some request, _ ->
                match machine.Interpret (context p) session (p.Execute request) with
                | MachineEvent input ->
                    let next, more = ok (machine.Step (context p) session input)
                    run next more (budget - 1)
                | NextRequest(next, request) -> run next [ Send request ] (budget - 1)
            | None, true ->
                let next, more = ok (machine.ReconcileDue (context p) session)
                run next more (budget - 1)
            | None, false -> session

        run session effects 64

    /// Enough commands that events.ndjson is over the contents API's 1 MB limit.
    let private largeCommandCount = 9000

    /// One save through SM-SAVE whose events.ndjson is over 1 MB; `beforeSave` runs after create and open (for the
    /// fake's scripted lost answer). Asserts Saved(Confirmed) at the branch head after exactly one blob read, named by
    /// the written events file's blob sha, sent after the mutation.
    let private saveLarge (p: ProviderUnderTest) (beforeSave: unit -> unit) =
        let investigation, first = setUp p
        let fileSetOf = GitHubProvider.fileSetOf StorageV1.codec first (decided first)
        let machine = SaveProcess.create (GitHubProvider.saveSetup tab markers fileSetOf false)

        let ok r =
            match r with
            | Ok x -> x
            | Error f -> failwithf "%A: SM-SAVE fault %A" p.Mode f

        let edited =
            List.init largeCommandCount (fun _ -> command investigation)
            |> List.fold (fun s c -> fst (ok (machine.Step (context p) s (SaveInput.Edit c)))) (machine.Initial first)

        let written = fileSetOf edited
        Assert.True(written.Events.Length > StatefulFake.contentsLimit, sprintf "events.ndjson is %d bytes" written.Events.Length)
        beforeSave ()
        let mutationsBefore = (mutations p).Length
        let saving, effects = ok (machine.Step (context p) edited SaveInput.SaveRequested)
        let settled = settle p machine saving effects

        match settled.State with
        | SaveState.Saved(Confirmed c) ->
            Assert.Equal(head p, c.CommitOid)
            Assert.Equal(c.CommitOid, settled.BaseHead)
        | other -> failwithf "%A: expected Saved(Confirmed) after the large-file re-read, got %A" p.Mode other

        let sent = p.Sent()
        let afterMutation = sent |> List.skip (1 + (sent |> List.findIndexBack (fun r -> r.Body |> Option.exists (fun b -> b.Contains "createCommitOnBranch"))))
        let blobs = afterMutation |> List.filter (fun r -> r.Url.Contains "/git/blobs/")
        Assert.Single(blobs) |> ignore
        Assert.EndsWith("/git/blobs/" + Answers.gitBlobSha written.Events, blobs.Head.Url)
        Assert.Equal(mutationsBefore + 1, (mutations p).Length)
        afterMutation

    /// A fake provider whose server stays reachable, so a test can script a lost answer.
    let private scriptableFake () =
        let server = Conformance.newFake ()
        let sent = Collections.Concurrent.ConcurrentQueue<HttpRequest>()

        server,
        { Mode = FakeProvider
          Execute =
            fun request ->
                sent.Enqueue request
                server.Execute request
          Connection = Conformance.fakeConnection
          Sent = fun () -> List.ofSeq sent }

    /// GH-LARGE-FILE (PERSIST-007-O1): a file over 1 MB comes back from the contents API as `encoding: none`; the save
    /// re-reads its blob exactly once and confirms only when the blob's bytes are the bytes written. Driven end to end
    /// through SM-SAVE: read-back after the mutation, and reconciliation after a lost answer (PERSIST-015), against
    /// the stateful fake; then the read-back against the live write-test repository (CI, CAUSA_WRITE_TOKEN).
    [<Fact>]
    let ``ReadBackOfFileOver1MB`` () =
        // Fake, read-back: three contents reads at the commit and one blob read for events.ndjson.
        let readBack = saveLarge (fake ()) ignore
        Assert.Equal(3, readBack |> List.filter (fun r -> r.Url.Contains "/contents/") |> List.length)

        // Fake, reconciliation: the mutation applies but its answer is lost (UnknownOutcome); the marker is found and
        // the files at the marker commit are compared, the large one through one blob read.
        let server, p = scriptableFake ()
        let reconciled = saveLarge p (fun () -> server.Script [ LoseAfterApplying TimedOut ])
        Assert.Contains(reconciled, fun r -> r.Url.Contains "/compare/")

        // Live: the same save against the write-test repository on a fresh branch.
        saveLarge (live "probe/persist007-large") ignore |> ignore
