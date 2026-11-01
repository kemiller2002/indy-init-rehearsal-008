// Owner: persistence. T20: PERSIST-006 one atomic commit per save with an expected head, on the fake and live.
namespace Causa.Acceptance.Live

open System
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module PERSIST006Tests =
    open ProviderScenario

    let private input (request: HttpRequest) =
        use doc = JsonDocument.Parse(request.Body.Value)
        doc.RootElement.GetProperty("variables").GetProperty("input").Clone()

    let private expectedHead (request: HttpRequest) = CommitOid((input request).GetProperty("expectedHeadOid").GetString() |> nonNull)

    let private paths (request: HttpRequest) =
        [ for a in (input request).GetProperty("fileChanges").GetProperty("additions").EnumerateArray() -> a.GetProperty("path").GetString() |> nonNull ]

    /// Create, open, save once: the scenario every obligation below inspects.
    let private createAndSave (p: ProviderUnderTest) =
        let investigation = InvestigationId(Guid.NewGuid())
        let created, _ = create p investigation
        let first = opened (openAtHead p investigation)
        let sentBefore = (mutations p).Length
        let session, written = save p first [ command investigation; command investigation ]
        investigation, created, session, written, (mutations p) |> List.skip sentBefore

    let private confirmedAt (session: InvestigationSession) =
        match session.State with
        | SaveState.Saved(Confirmed c) -> c.CommitOid
        | other -> failwithf "expected a confirmed save, got %A" other

    /// O1: one save publishes exactly one commit through one createCommitOnBranch mutation.
    [<Fact>]
    let ``PERSIST-006-O1`` () =
        onBoth "probe/persist006-o1" (fun p ->
            let _, created, session, _, saveMutations = createAndSave p
            Assert.Equal(1, saveMutations.Length)
            Assert.Equal(GitHubRequests.graphqlUrl, saveMutations.Head.Url)
            let after = confirmedAt session
            Assert.NotEqual(created, after)
            Assert.Equal(after, head p))

    /// O2: the commit contains the events append, manifest and snapshot together.
    [<Fact>]
    let ``PERSIST-006-O2`` () =
        onBoth "probe/persist006-o2" (fun p ->
            let investigation, _, session, written, saveMutations = createAndSave p
            let expectedPaths = [ StoragePaths.manifest investigation; StoragePaths.events investigation; StoragePaths.snapshot investigation ] |> List.map (fun (RepoPath x) -> x)
            Assert.Equal<string list>(expectedPaths, paths saveMutations.Head)
            let atCommit = (readAt p (confirmedAt session) investigation).Value
            Assert.Equal<byte[]>(written.Manifest, atCommit.Manifest.Value)
            Assert.Equal<byte[]>(written.Events, atCommit.Events.Value)
            Assert.Equal<byte[]>(written.Snapshot, atCommit.Snapshot.Value))

    /// O3: expectedHeadOid is the head the change was decided at (the head the tab read).
    [<Fact>]
    let ``PERSIST-006-O3`` () =
        onBoth "probe/persist006-o3" (fun p ->
            let _, created, _, _, saveMutations = createAndSave p
            Assert.Equal(created, expectedHead saveMutations.Head))

    /// O4: no Contents API write, no forced ref update, no write without an expected head.
    [<Fact>]
    let ``PERSIST-006-O4`` () =
        onBoth "probe/persist006-o4" (fun p ->
            createAndSave p |> ignore

            for r in p.Sent() do
                Assert.True(r.Method = Get || r.Method = Post, sprintf "%A %s" r.Method r.Url)
                Assert.DoesNotContain("/contents/", if r.Method = Get then "" else r.Url)
                Assert.DoesNotContain("/git/refs", if r.Method = Get then "" else r.Url)

            for m in mutations p do
                let (CommitOid e) = expectedHead m
                Assert.Matches("^[0-9a-f]{40}$", e))

    /// O5: before sending, every changed file is under the investigation's own prefix.
    [<Fact>]
    let ``PERSIST-006-O5`` () =
        onBoth "probe/persist006-o5" (fun p ->
            let investigation, _, _, _, _ = createAndSave p

            for m in mutations p do
                Assert.All(paths m, fun path -> Assert.True(StoragePaths.isUnder investigation path, path)))

    /// O6: at most one mutation in flight per tab: a create while another mutation is in flight is not sent.
    [<Fact>]
    let ``PERSIST-006-O6`` () =
        onBoth "probe/persist006-o6" (fun p ->
            let investigation = InvestigationId(Guid.NewGuid())
            let empty = openAtHead p investigation
            let busy = { context p with MutationInFlightElsewhere = true }
            let files = StorageV1.encodeHistory investigation (started investigation)
            Assert.Equal(Error MutationInFlight, GitHubProvider.createRequest busy true empty investigation files (markers ()) |> Result.map ignore)
            Assert.Empty(mutations p))

    /// O7 (PAT-02): every mutation the scenario sent obeys the rules: GraphQL createCommitOnBranch, a 40-hex expected
    /// head the tab read, additions only under the prefix, and a write-marker trailer.
    [<Fact>]
    let ``PERSIST-006-O7`` () =
        onBoth "probe/persist006-o7" (fun p ->
            let investigation, created, _, _, _ = createAndSave p
            let all = mutations p
            Assert.Equal(2, all.Length)
            let readHeads = set [ created ] |> Set.add (expectedHead all.Head)

            for m in all do
                Assert.Equal(GitHubRequests.graphqlUrl, m.Url)
                Assert.Contains(expectedHead m, readHeads)
                Assert.All(paths m, fun path -> Assert.True(StoragePaths.isUnder investigation path))
                let message = (input m).GetProperty("message")
                Assert.Contains(Markers.trailerKey, message.GetProperty("body").GetString())
                Assert.False((input m).GetProperty("fileChanges").TryGetProperty("deletions") |> fst))
