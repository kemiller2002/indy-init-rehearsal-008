// Owner: persistence. T18: PERSIST-008 detect a stale head, against the stateful fake and the live test repository.
namespace Causa.Acceptance.Live

open System.Text
open Xunit
open Causa.Persistence
open Causa.Persistence.ErrorMatrix
open Causa.TestSupport

module PERSIST008Tests =

    let private conn = Conformance.fakeConnection

    let private commit (execute: Execute) (connection: ConnectionContext) (expected: CommitOid) (name: string) =
        execute (GitHubRequests.createCommitOnBranch (Conformance.freshId ()) connection expected name [ RepoPath("inv/" + name), Encoding.UTF8.GetBytes name ])

    /// O1: a refusal because the head moved is a stale head, and nothing was written.
    [<Fact>]
    let ``PERSIST-008-O1`` () =
        let server = Conformance.newFake ()
        let h0 = (StatefulFake.head "main" server.State).Value
        server.Script [ OtherWriterFirst("main", [ "other.json", Encoding.UTF8.GetBytes "o" ]) ]
        let moved = commit server.Execute conn h0 "mine"
        let hOther = (StatefulFake.head "main" server.State).Value

        match (classify (Mutation h0) moved).Result with
        | Error(StaleHead(e, Some actual)) ->
            Assert.Equal(h0, e)
            Assert.Equal(hOther, actual)
        | other -> failwithf "expected StaleHead, got %A" other

        // Nothing written: the branch still points at the other writer's commit, which lacks our file.
        Assert.Equal(hOther, (StatefulFake.head "main" server.State).Value)
        let (CommitOid h) = hOther
        Assert.False(server.State.Commits[h].Tree.ContainsKey "inv/mine")

    /// O2: classification comes from typed content; stale head stays distinct from network, auth, permission,
    /// rate limit and validation (every recorded form classifies to its own class).
    [<Fact>]
    let ``PERSIST-008-O2`` () =
        let expected = CommitOid(String.replicate 40 "b")

        let classesOf matrixClass =
            Recordings.all
            |> List.filter (fun r -> r.MatrixClass = matrixClass)
            |> List.map (fun r -> (classify (Mutation expected) (Recordings.asOutcome (RequestId 1L) r)).Class)

        for c in [ "GH-AUTH"; "GH-PERMISSION"; "GH-RATE-PRIMARY"; "GH-RATE-SECONDARY"; "GH-VALIDATION" ] do
            Assert.DoesNotContain(GhStaleHead, classesOf c)

        Assert.All(classesOf "GH-STALE-HEAD", fun c -> Assert.Equal(GhStaleHead, c))

    /// O3: a network failure is never a stale head or a conflict, whether the fake lost the answer before or after
    /// applying the commit.
    [<Fact>]
    let ``PERSIST-008-O3`` () =
        let server = Conformance.newFake ()
        let h0 = (StatefulFake.head "main" server.State).Value

        for directive in [ LoseBeforeApplying FailedBeforeSending; LoseBeforeApplying LostAfterSending; LoseAfterApplying TimedOut ] do
            server.Script [ directive ]
            let head = (StatefulFake.head "main" server.State).Value
            let c = classify (Mutation head) (commit server.Execute conn head "n")
            Assert.NotEqual(GhStaleHead, c.Class)
            Assert.True(c.Class = GhNetworkBefore || c.Class = GhNetworkAfter)

        Assert.NotEqual(h0, (StatefulFake.head "main" server.State).Value)

    /// O4: the same stale-head classification against the live test repository (CI; BLOCKED without the secret).
    [<Fact>]
    let ``PERSIST-008-O4`` () =
        let live = Conformance.liveWriteConnection ()
        let branch = Conformance.freshBranch LiveGitHub.execute live "probe/persist008"
        let h0 = (LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch) |> Conformance.headOid).Value
        let first = commit LiveGitHub.execute branch (CommitOid h0) "first"
        Assert.Equal(Accepted, (classify (Mutation(CommitOid h0)) first).Class)
        let stale = commit LiveGitHub.execute branch (CommitOid h0) "second"
        Assert.Equal(GhStaleHead, (classify (Mutation(CommitOid h0)) stale).Class)
        let after = LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch) |> Conformance.headOid
        Assert.Equal(Conformance.commitOid first, after)
