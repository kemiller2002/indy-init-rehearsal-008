// Owner: persistence. T18: PERSIST-025 testable provider boundary: one scenario, the stateful fake and the real repo.
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Persistence
open Causa.Persistence.ErrorMatrix
open Causa.TestSupport

module PERSIST025Tests =

    let private bytes (s: string) = Encoding.UTF8.GetBytes s
    let private conn = Conformance.fakeConnection
    let private oidOf (CommitOid o) = o

    let private commit (server: StatefulFake.Server) (expected: CommitOid) (name: string) =
        server.Execute(GitHubRequests.createCommitOnBranch (Conformance.freshId ()) conn expected name [ RepoPath("inv/" + name), bytes name ])

    /// O1: the scenario is written against Execute only; the fake and the live adapter are both an Execute.
    [<Fact>]
    let ``PERSIST-025-O1`` () =
        let fake = Conformance.newFake ()
        let providers: (ProviderMode * Execute) list = [ FakeProvider, fake.Execute; LiveProvider "unused", LiveGitHub.execute ]
        Assert.Equal(2, providers.Length)
        let run = Conformance.run FakeProvider fake.Execute conn "o1"
        Assert.Equal("ACCEPTED", run.Steps.Head.Class)

    /// O2: the fake enforces expectedHeadOid, keeps real ancestry, accepts other writers, and loses answers before
    /// and after applying.
    [<Fact>]
    let ``PERSIST-025-O2`` () =
        let server = Conformance.newFake ()
        let h0 = (StatefulFake.head "main" server.State).Value

        // expectedHeadOid enforced: a stale guard is refused in the recorded moved-head form, nothing written.
        let a = commit server h0 "a"
        Assert.Equal(Accepted, (classify (Mutation h0) a).Class)
        let ha = (StatefulFake.head "main" server.State).Value
        let stale = commit server h0 "b"
        Assert.Equal(GhStaleHead, (classify (Mutation h0) stale).Class)
        Assert.Equal(ha, (StatefulFake.head "main" server.State).Value)

        // Real ancestry: h0 is an ancestor of ha; compare reports ahead by one.
        Assert.Contains(oidOf h0, StatefulFake.ancestors ha server.State)
        let cmp = server.Execute(GitHubRequests.compare (Conformance.freshId ()) conn h0 ha 1)
        Assert.Equal(Accepted, (classify (Compare h0) cmp).Class)

        // Another writer commits first: our guarded commit is stale.
        server.Script [ OtherWriterFirst("main", [ "inv-other/x.json", bytes "x" ]) ]
        let raced = commit server ha "c"
        Assert.Equal(GhStaleHead, (classify (Mutation ha) raced).Class)
        let hOther = (StatefulFake.head "main" server.State).Value
        Assert.NotEqual(ha, hOther)

        // Lost before applying: nothing changes on the server.
        server.Script [ LoseBeforeApplying LostAfterSending ]
        let lostBefore = commit server hOther "d"
        Assert.Equal(GhNetworkAfter, (classify (Mutation hOther) lostBefore).Class)
        Assert.Equal(hOther, (StatefulFake.head "main" server.State).Value)

        // Lost after applying: the commit exists although the caller never heard; compare finds it.
        server.Script [ LoseAfterApplying TimedOut ]
        let lostAfter = commit server hOther "e"
        Assert.Equal(GhNetworkAfter, (classify (Mutation hOther) lostAfter).Class)
        let hAfter = (StatefulFake.head "main" server.State).Value
        Assert.NotEqual(hOther, hAfter)
        let found = server.Execute(GitHubRequests.compare (Conformance.freshId ()) conn hOther hAfter 1)

        match found with
        | Answered(_, 200, _, body) -> Assert.Contains("\"e\"", body)
        | other -> failwithf "compare after lost answer failed: %A" other

        // Rewritten history is detected by ancestry, not by status.
        let _ = server.Mutate(StatefulFake.rewriteBranch "main" [ "x", bytes "y" ])
        let rewritten = (StatefulFake.head "main" server.State).Value
        let diverged = server.Execute(GitHubRequests.compare (Conformance.freshId ()) conn hAfter rewritten 1)
        Assert.Equal(GhHistoryRewritten, (classify (Compare hAfter) diverged).Class)

    /// O3: the same scenario against the real GitHub test repository (CI, CAUSA_WRITE_TOKEN; BLOCKED without it).
    [<Fact>]
    let ``PERSIST-025-O3`` () =
        let live = Conformance.liveWriteConnection ()
        let branch = Conformance.freshBranch LiveGitHub.execute live "probe/persist025"
        let liveRun = Conformance.run (LiveProvider(live.Repository.Owner + "/" + live.Repository.Name)) LiveGitHub.execute branch "persist025"
        let fakeRun = Conformance.run FakeProvider (Conformance.newFake ()).Execute conn "persist025"
        // Fake/real divergence is a failure (PF-013): the classes of every step must agree.
        Assert.Equal<string list>(fakeRun.Steps |> List.map _.Class, liveRun.Steps |> List.map _.Class)
        Console.WriteLine(Conformance.evidence liveRun)

    /// O4: a fake-only pass never reads as live verification.
    [<Fact>]
    let ``PERSIST-025-O4`` () =
        let run = Conformance.run FakeProvider (Conformance.newFake ()).Execute conn "o4"
        let text = Conformance.evidence run
        Assert.Contains("NOT live verification", text)
        Assert.DoesNotContain("provider=live-github", text)

    /// O5: the live run's evidence names the real commits it created, and each exists on GitHub.
    [<Fact>]
    let ``PERSIST-025-O5`` () =
        let live = Conformance.liveWriteConnection ()
        let branch = Conformance.freshBranch LiveGitHub.execute live "probe/persist025-o5"
        let run = Conformance.run (LiveProvider(live.Repository.Owner + "/" + live.Repository.Name)) LiveGitHub.execute branch "o5"
        Assert.NotEmpty run.Created
        let text = Conformance.evidence run

        for oid in run.Created do
            Assert.Contains(oid, text)
            let readBack = LiveGitHub.execute (GitHubRequests.compare (Conformance.freshId ()) branch (CommitOid oid) (CommitOid oid) 1)
            Assert.Equal(Accepted, (classify (Compare(CommitOid oid)) readBack).Class)

        Console.WriteLine text
