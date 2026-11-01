// Owner: persistence. WI-R8-PTESTS: PERSIST-013 no write removes, replaces or hides another writer's commit (PAT-02,
// PAT-04, PAT-05), against the stateful fake and the live write-test repository (CI; BLOCKED without the secret).
namespace Causa.Acceptance.Live

open System
open System.Text
open System.Text.Json
open Xunit
open Causa.Persistence
open Causa.Acceptance.Deterministic
open LiveSave

module PERSIST013Tests =
    /// The three scenarios; each returns every other writer's commit it made.
    let private pat02 (p: Provider) =
        let tab = SaveHarness.openTabAt (seed p) |> SaveHarness.edit 1 |> SaveHarness.inputWith p.Connection SaveInput.SaveRequested
        let final, _ = drainRecording p tab
        Assert.Equal("Saved", SaveHarness.stateName final)
        []

    let private pat04 (p: Provider) =
        let tab = SaveHarness.openTabAt (seed p) |> SaveHarness.edit 1
        let others = [ otherWriterUnrelated p; otherWriterUnrelated p ]
        let final, _ = drainRecording p (tab |> SaveHarness.inputWith p.Connection SaveInput.SaveRequested)
        Assert.Equal("Saved", SaveHarness.stateName final)
        others

    /// A genuine conflict, resolved by reapplying the tab's change on the GitHub version and saving again.
    let private pat05 (p: Provider) =
        let tab = SaveHarness.openTabAt (seed p) |> SaveHarness.edit 1
        let other = otherWriterSavesInvestigation p
        let tab, _ = drainRecording p (tab |> SaveHarness.inputWith p.Connection SaveInput.SaveRequested)

        let tab =
            match tab.Session.State with
            | SaveState.Conflict(Reviewable _ as view) ->
                let step view i =
                    match tab.Machine.StepConflict (SaveHarness.context ()) view i with
                    | Ok(next, _) -> next
                    | Error f -> failwithf "%A" f

                let view = step (step view ConflictInput.ReapplyChosen) (ConflictInput.ReapplyDone([ SaveHarness.command 1 ], []))
                { tab with Session = { tab.Session with State = SaveState.Conflict view } }
            | s -> failwithf "expected Conflict(Reviewable), got %A" s

        let tab =
            tab
            |> SaveHarness.inputWith p.Connection (SaveInput.ConflictResolvedReapplied [ SaveHarness.command 1 ])
            |> SaveHarness.inputWith p.Connection SaveInput.SaveRequested

        let final, _ = drainRecording p tab
        Assert.Equal("Saved", SaveHarness.stateName final)
        [ other ]

    let private scenarios = [ pat02; pat04; pat05 ]

    /// O1: no write removes, replaces or hides a commit made by another writer.
    [<Fact>]
    let ``PERSIST-013-O1`` () =
        onBoth "probe/persist013-o1" (fun p ->
            for scenario in [ pat04; pat05 ] do
                Assert.All(scenario p, fun c -> Assert.True(reachable p c)))

    /// O2: no force-push, ref reset, delete-and-recreate, or write against a head the tab has not read from GitHub:
    /// the tab sends only GETs and GraphQL createCommitOnBranch with additions, each guarded by a head read in this
    /// session (the loaded base, a branch-head read, or a confirmed commit).
    [<Fact>]
    let ``PERSIST-013-O2`` () =
        onBoth "probe/persist013-o2" (fun p ->
            pat04 p |> ignore
            pat05 p |> ignore
            let log = p.Requests()

            for r in log do
                Assert.True(r.Method = HttpMethod.Get || (r.Method = HttpMethod.Post && r.Url.EndsWith("/graphql", StringComparison.Ordinal)), sprintf "%A %s" r.Method r.Url)

            for r in log |> List.filter isSave do
                let changes = (mutationInput r).GetProperty("fileChanges")
                Assert.False(changes.TryGetProperty("deletions") |> fst, "a save deleted files")

            // Every save's expectedHeadOid is a commit GitHub named in an answer earlier in the same run.
            let seen = Collections.Generic.HashSet<string>()
            let hex40 = RegularExpressions.Regex("\\b[0-9a-f]{40}\\b")

            for r, outcome in p.Exchanges() do
                if isSave r then Assert.Contains(expectedHeadOf r, seen)

                match outcome with
                | Answered(_, _, _, body) -> for m in hex40.Matches body do seen.Add m.Value |> ignore
                | NotAnswered _ -> ())

    /// O3: every other writer's commit remains reachable from the branch head after saves, rebases and a conflict
    /// resolution.
    [<Fact>]
    let ``PERSIST-013-O3`` () =
        onBoth "probe/persist013-o3" (fun p ->
            let others = scenarios |> List.collect (fun s -> s p)
            Assert.All(others, fun c -> Assert.True(reachable p c)))

    /// O4 (PAT-02, PAT-04, PAT-05): after each scenario, every commit made by every writer is reachable.
    [<Fact>]
    let ``PERSIST-013-O4`` () =
        onBoth "probe/persist013-o4" (fun p ->
            for scenario in scenarios do
                let others = scenario p
                Assert.All(others, fun c -> Assert.True(reachable p c))
                Assert.True(reachable p (head p)))
