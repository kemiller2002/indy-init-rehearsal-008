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

    /// GH-LARGE-FILE: a file over 1 MB comes back from the contents API as `encoding: none`; the provider re-reads its
    /// blob exactly once and gets the bytes written. Live: a 1.5 MB file committed to the fresh branch.
    [<Fact>]
    let ``ReadBackOfFileOver1MB`` () =
        let large = Encoding.UTF8.GetBytes(String('x', 1_500_000))
        let id () = Conformance.freshId ()

        // Fake: the contents API's large-file form, then the blob.
        let tooLarge = Answered(id (), 200, [], """{"type":"file","path":"big.bin","encoding":"none","size":1500000,"content":"","sha":"abc123"}""")

        match GitHubProvider.interpretContents tooLarge with
        | ContentsTooLarge(_, sha) as read ->
            Assert.Equal("abc123", sha)
            let retry = (GitHubProvider.largeFileRetry (id ()) Conformance.fakeConnection 0 read).Value
            Assert.EndsWith("/git/blobs/abc123", retry.Url)
            Assert.Equal(None, GitHubProvider.largeFileRetry (id ()) Conformance.fakeConnection 1 read)
        | other -> failwithf "expected ContentsTooLarge, got %A" other

        // Live.
        let p = live "probe/persist007-large"
        let h = head p

        let commit =
            p.Execute(GitHubRequests.createCommitOnBranch (id ()) p.Connection h "large file" [ RepoPath "probes/large/big.txt", large ])
            |> Conformance.commitOid
            |> Option.get

        let read = GitHubProvider.interpretContents (p.Execute(GitHubRequests.contentsAt (id ()) p.Connection (CommitOid commit) (RepoPath "probes/large/big.txt")))

        let bytes =
            match read with
            | ContentsBytes(_, b) -> b
            | ContentsTooLarge _ ->
                match GitHubProvider.largeFileRetry (id ()) p.Connection 0 read with
                | Some blob ->
                    match GitHubProvider.interpretContents (p.Execute blob) with
                    | ContentsBytes(_, b) -> b
                    | other -> failwithf "blob re-read failed: %A" other
                | None -> failwith "no re-read offered"
            | ContentsUnreadable e -> failwithf "contents unreadable: %A" e

        Assert.Equal<byte[]>(large, bytes)
