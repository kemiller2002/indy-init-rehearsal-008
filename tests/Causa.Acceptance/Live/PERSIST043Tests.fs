// Owner: integration. T21 (integration checkpoint 2): PAT-04 automatic rebase of unrelated changes, against the
// stateful fake and live against kemiller2002/causa-write-test on a fresh branch of this CI run (live.yml). Without
// CAUSA_WRITE_TOKEN the live half is BLOCKED (fails), never skipped (PF-015, PF-020).
namespace Causa.Acceptance.Live

open System
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module PERSIST043Tests =
    open ProviderScenario
    open Pat

    type private Scenario =
        { Tab: Guid
          X: InvestigationId
          XRead: VerifiedInvestigation
          Y: InvestigationId
          YSave: TabSave
          YCommit: CommitOid
          OutsidePath: string
          OutsideBytes: byte[]
          OutsideCommit: CommitOid
          HeadBeforeSave: CommitOid
          XSave: TabSave }

    /// Tab A reads investigation X. Before it saves X, the same tab saves investigation Y and another writer commits
    /// a file outside the investigations; both advance the branch. Then tab A saves X on the head it read.
    let private scenario (p: ProviderUnderTest) : Scenario =
        let tab = newTab ()
        let x, xRead = createAndOpen p
        let y, yRead = createAndOpen p
        let ySave = saveAs tab p yRead [ command y ]
        let yCommit = confirmedAt p.Mode ySave.Session
        let outsidePath = sprintf "outside/t21-%s.txt" (Guid.NewGuid().ToString "N")
        let outsideBytes = Encoding.UTF8.GetBytes "a file outside the investigations"
        let outsideCommit = otherWriter p "T21 other writer: file outside the investigations" [ outsidePath, outsideBytes ]
        let headBefore = head p
        let xSave = saveAs tab p xRead [ command x ]

        { Tab = fst tab
          X = x
          XRead = xRead
          Y = y
          YSave = ySave
          YCommit = yCommit
          OutsidePath = outsidePath
          OutsideBytes = outsideBytes
          OutsideCommit = outsideCommit
          HeadBeforeSave = headBefore
          XSave = xSave }

    let private evidence (s: Scenario) =
        [ "x-read", s.XRead.ReadAt
          "same-tab-y", s.YCommit
          "other-writer-outside", s.OutsideCommit
          "x-confirmed", confirmedAt FakeProvider s.XSave.Session ]

    /// O1: commits to another investigation and to a file outside the investigations advance the branch before the
    /// tab saves; the save's first mutation is sent on the head the tab read.
    [<Fact>]
    let ``PERSIST-043-O1`` () =
        onBoth "probe/t21-pat04-o1" (fun p ->
            let s = scenario p
            Assert.NotEqual(s.XRead.ReadAt, s.HeadBeforeSave)
            Assert.Equal(s.OutsideCommit, s.HeadBeforeSave)
            Assert.True(reachableFromHead p s.YCommit)
            Assert.Equal(s.XRead.ReadAt, expectedHead (fst (tabMutations s.XSave).Head))
            record p "PERSIST-043-O1" (evidence s))

    /// O2: one of those commits is made by the same tab (its write marker carries the tab's marker session).
    [<Fact>]
    let ``PERSIST-043-O2`` () =
        onBoth "probe/t21-pat04-o2" (fun p ->
            let s = scenario p
            let yMarkers = tabMutations s.YSave |> List.map (fst >> marker)
            Assert.NotEmpty(yMarkers)
            Assert.All(yMarkers, fun m -> Assert.True(Markers.ofSession s.Tab m, m))
            let xMarkers = tabMutations s.XSave |> List.map (fst >> marker)
            Assert.Empty(Set.intersect (set yMarkers) (set xMarkers))
            record p "PERSIST-043-O2" (evidence s))

    /// O3: the save is confirmed without user action through at most three rebase attempts, and the unrelated
    /// commits remain on the branch unchanged.
    [<Fact>]
    let ``PERSIST-043-O3`` () =
        onBoth "probe/t21-pat04-o3" (fun p ->
            let s = scenario p
            let confirmed = confirmedAt p.Mode s.XSave.Session
            Assert.InRange(s.XSave.Session.RebaseAttempts, 1, 3)
            Assert.InRange((tabMutations s.XSave).Length, 2, 4)
            Assert.Equal(confirmed, head p)
            Assert.True(reachableFromHead p s.YCommit)
            Assert.True(reachableFromHead p s.OutsideCommit)
            Assert.Equal<byte[]>(s.OutsideBytes, (fileAt p confirmed s.OutsidePath).Value)
            Assert.Equal<byte[]>(s.YSave.Written.Events, (opened (openAtHead p s.Y)).Bytes.Events)
            Assert.Equal<byte[]>(s.XSave.Written.Events, (opened (openAtHead p s.X)).Bytes.Events)
            record p "PERSIST-043-O3" (evidence s))

    /// O4: the PAT-04 scenario passes live on the candidate SHA, with the real commits named in the evidence.
    [<Fact>]
    let ``PERSIST-043-O4`` () =
        let p = live "probe/t21-pat04-o4"
        let s = scenario p
        let confirmed = confirmedAt p.Mode s.XSave.Session
        Assert.Equal(confirmed, head p)
        Assert.InRange(s.XSave.Session.RebaseAttempts, 1, 3)

        record
            p
            (sprintf "PERSIST-043-O4 source=%s" (Environment.GetEnvironmentVariable "GITHUB_SHA" |> Option.ofObj |> Option.defaultValue "local"))
            (evidence s)

    /// O5 (PERSIST-009, PERSIST-013): the rebased mutation is sent on the fresh head and still changes only the
    /// investigation's own files.
    [<Fact>]
    let ``PERSIST-043-O5`` () =
        onBoth "probe/t21-pat04-o5" (fun p ->
            let s = scenario p
            let last = fst (List.last (tabMutations s.XSave))
            Assert.Equal(s.HeadBeforeSave, expectedHead last)
            Assert.All(paths last, fun path -> Assert.True(StoragePaths.isUnder s.X path, path))
            record p "PERSIST-043-O5" (evidence s))
