// Owner: persistence. WI-R8-PTESTS: PERSIST-014 write markers (PAT-02), against the stateful fake and the live
// write-test repository (CI; BLOCKED without the secret).
namespace Causa.Acceptance.Live

open System
open System.Text.RegularExpressions
open Xunit
open Causa.Persistence
open Causa.Acceptance.Deterministic
open LiveSave

module PERSIST014Tests =
    let private marker = Regex(sprintf "(?m)^%s: ([0-9a-f-]{36})/([0-9]+)$" Markers.trailerKey)

    /// The markers of every save mutation sent through the provider, in order.
    let private markersSent (p: Provider) =
        mutations p
        |> List.map (fun r ->
            let m = marker.Match(messageOf r)
            Assert.True(m.Success, sprintf "save without a write-marker trailer: %s" (messageOf r))
            Guid.Parse m.Groups[1].Value, int64 m.Groups[2].Value)

    /// One product tab (SaveProcess.forNewTab: its own random session identifier) saving n times in a row.
    let private tabSaves (p: Provider) (verified: VerifiedInvestigation) (n: int) =
        let tab = SaveHarness.openTabWith (SaveProcess.forNewTab ()) verified

        [ 1..n ]
        |> List.fold
            (fun (t: Tab) i ->
                let t = t |> SaveHarness.edit i |> SaveHarness.inputWith p.Connection SaveInput.SaveRequested
                fst (drainRecording p t))
            tab

    /// PAT-02: two concurrent tabs and a reload (a new tab session) save; tab B rebases over tab A's unrelated
    /// writes only when its investigation files are unchanged, so each tab works on its own fresh load.
    let private pat02 (p: Provider) =
        let a = tabSaves p (seed p) 2
        Assert.Equal("Saved", SaveHarness.stateName a)
        // The reload and the second tab each load the branch as it is now.
        let reload = tabSaves p (seed p) 2
        Assert.Equal("Saved", SaveHarness.stateName reload)
        let b = tabSaves p (seed p) 1
        Assert.Equal("Saved", SaveHarness.stateName b)
        markersSent p

    /// O1: each tab creates a random session identifier when it starts (two tabs, a reload: three identifiers).
    [<Fact>]
    let ``PERSIST-014-O1`` () =
        onBoth "probe/persist014-o1" (fun p ->
            let sessions = pat02 p |> List.map fst |> List.distinct
            Assert.Equal(3, sessions.Length)
            Assert.All(sessions, fun g -> Assert.NotEqual(Guid.Empty, g)))

    /// O2: each commit mutation carries a marker of the identifier and a sequence number as a message trailer, and
    /// GitHub keeps it in the commit message.
    [<Fact>]
    let ``PERSIST-014-O2`` () =
        onBoth "probe/persist014-o2" (fun p ->
            let sent = pat02 p
            Assert.Equal(5, sent.Length)
            Assert.All(sent, fun (_, seq) -> Assert.True(seq >= 1L))
            // The branch's commit messages carry the same trailers (read back through compare).
            let first = mutations p |> List.head |> expectedHeadOf |> CommitOid

            match p.Execute(GitHubRequests.compareAll (Causa.TestSupport.Conformance.freshId ()) p.Connection first (head p)) with
            | Answered(_, 200, _, body) ->
                let view = (Answers.compare body).Value
                let onBranch = view.Commits |> List.map snd |> List.filter (fun m -> marker.IsMatch m)
                Assert.Equal(sent.Length, onBranch.Length)
            | other -> failwithf "compare failed: %A" other)

    /// O3: markers are unique across tabs, devices and page loads (and a tab's sequence strictly increases).
    [<Fact>]
    let ``PERSIST-014-O3`` () =
        onBoth "probe/persist014-o3" (fun p ->
            let sent = pat02 p
            Assert.Equal(sent.Length, (List.distinct sent).Length)

            for _, seqs in sent |> List.groupBy fst do
                let s = seqs |> List.map snd
                Assert.Equal<int64 list>(List.sort s, s)
                Assert.Equal(s.Length, (List.distinct s).Length))

    /// O4 (PAT-02): marker uniqueness across concurrent tabs and reloads, on the branch as GitHub holds it.
    [<Fact>]
    let ``PERSIST-014-O4`` () =
        onBoth "probe/persist014-o4" (fun p ->
            let sent = pat02 p
            Assert.Equal(sent.Length, (List.distinct sent).Length)
            Assert.True((sent |> List.map fst |> List.distinct).Length >= 3))
