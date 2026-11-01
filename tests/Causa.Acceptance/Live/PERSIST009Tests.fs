// Owner: persistence. WI-R8-PTESTS: PERSIST-009 automatic rebase over unrelated commits (PAT-04), against the stateful
// fake and against the live write-test repository (CI; BLOCKED without CAUSA_WRITE_TOKEN). This file also holds the
// provider setup the live persistence tests after it share (LiveSave).
namespace Causa.Acceptance.Live

open System
open System.Text
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport
open Causa.Acceptance.Deterministic

/// One provider: its connection on a fresh branch, and an Execute that records every request (test edge).
type Provider =
    { Connection: ConnectionContext
      Execute: Execute
      Requests: unit -> HttpRequest list
      /// Every request with its outcome, oldest first.
      Exchanges: unit -> (HttpRequest * HttpOutcome) list }

module LiveSave =
    let investigation = SaveHarness.investigation

    let private recording (connection: ConnectionContext) (execute: Execute) : Provider =
        let log = Collections.Generic.List<HttpRequest * HttpOutcome>()

        { Connection = connection
          Execute =
            fun request ->
                let outcome = execute request
                lock log (fun () -> log.Add((request, outcome)))
                outcome
          Requests = fun () -> lock log (fun () -> log |> Seq.map fst |> List.ofSeq)
          Exchanges = fun () -> lock log (fun () -> List.ofSeq log) }

    /// The stateful fake (deterministic part of each test).
    let fake () : Provider =
        let server = Conformance.newFake ()
        recording Conformance.fakeConnection server.Execute

    /// A fresh branch of the live write-test repository (CI only).
    let live (prefix: string) : Provider =
        let connection = Conformance.liveWriteConnection ()
        recording (Conformance.freshBranch LiveGitHub.execute connection prefix) LiveGitHub.execute

    let head (p: Provider) : CommitOid =
        p.Execute(GitHubRequests.branchHead (Conformance.freshId ()) p.Connection)
        |> Conformance.headOid
        |> Option.map CommitOid
        |> Option.defaultWith (fun () -> failwith "branch head unreadable")

    let commit (p: Provider) (headline: string) (files: (string * byte[]) list) : CommitOid =
        let outcome =
            p.Execute(GitHubRequests.createCommitOnBranch (Conformance.freshId ()) p.Connection (head p) headline [ for (path, bytes) in files -> RepoPath path, bytes ])

        Conformance.commitOid outcome |> Option.map CommitOid |> Option.defaultWith (fun () -> failwithf "commit refused: %A" outcome)

    let private event (n: int) : EventEnvelope =
        { EventId = EventId(Guid(sprintf "00000000-0000-4000-8000-00000000f%03d" n))
          Investigation = investigation
          Version = AggregateVersion(int64 n)
          Payload = EventPayload "InvestigationStarted" }

    let private historyFiles (events: int) =
        let files = StorageV1.encodeHistory investigation [ for n in 1..events -> event n ]
        let file (RepoPath p) bytes = p, bytes

        files,
        [ file (StoragePaths.manifest investigation) files.Manifest
          file (StoragePaths.events investigation) files.Events
          file (StoragePaths.snapshot investigation) files.Snapshot ]

    /// Writes a verified investigation to the branch and returns it as a load reads it.
    let seed (p: Provider) : VerifiedInvestigation =
        let files, tree = historyFiles 1
        let at = commit p "seed investigation" tree

        match StorageV1.verify at files with
        | Ok verified -> verified
        | Error failures -> failwithf "the seeded investigation does not verify: %A" failures

    /// Another writer saves a valid newer version of the same investigation.
    let otherWriterSavesInvestigation (p: Provider) : CommitOid = commit p "other writer, same investigation" (snd (historyFiles 2))

    let mutable private unrelated = 0

    /// Another writer (another investigation, tab or device) commits an unrelated file.
    let otherWriterUnrelated (p: Provider) : CommitOid =
        let n = Threading.Interlocked.Increment(&unrelated)
        commit p "other writer, unrelated" [ sprintf "other/unrelated-%d-%d.txt" n (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Encoding.UTF8.GetBytes(string n) ]

    /// True when the commit is reachable from the branch head (compare commit...head is ahead or identical).
    let reachable (p: Provider) (CommitOid oid as c) : bool =
        match p.Execute(GitHubRequests.compareAll (Conformance.freshId ()) p.Connection c (head p)) with
        | Answered(_, 200, _, body) ->
            use doc = JsonDocument.Parse body

            match doc.RootElement.TryGetProperty "status" with
            | true, s -> s.GetString() = "ahead" || s.GetString() = "identical"
            | _ -> false
        | other -> failwithf "compare %s...head failed: %A" oid other

    let private str (e: JsonElement) : string = e.GetString() |> Option.ofObj |> Option.defaultValue ""

    let mutationInput (request: HttpRequest) : JsonElement =
        use doc = JsonDocument.Parse(request.Body |> Option.defaultValue "{}")
        doc.RootElement.GetProperty("variables").GetProperty("input").Clone()

    let expectedHeadOf (request: HttpRequest) = str ((mutationInput request).GetProperty("expectedHeadOid"))

    let additionsOf (request: HttpRequest) =
        (mutationInput request).GetProperty("fileChanges").GetProperty("additions").GetRawText()

    let messageOf (request: HttpRequest) =
        let m = (mutationInput request).GetProperty("message")

        match m.TryGetProperty "body" with
        | true, b -> str (m.GetProperty "headline") + "\n\n" + str b
        | _ -> str (m.GetProperty "headline")

    /// The tab's own save mutations (the test's setup commits are mutations too, but are not the tab's).
    let isSave (request: HttpRequest) =
        SaveHarness.isMutationRequest request && (messageOf request).StartsWith("Causa: save investigation", StringComparison.Ordinal)

    let mutations (p: Provider) = p.Requests() |> List.filter isSave

    /// Delivers effects one at a time, recording the state name after each, until none is pending.
    let drainRecording (p: Provider) (tab: Tab) : Tab * string list =
        let rec go n (t: Tab) states =
            if List.isEmpty t.Pending then t, List.rev states
            elif n > 200 then failwith "the save did not settle within 200 effects"
            else
                let next = SaveHarness.deliverWith p.Connection p.Execute t
                go (n + 1) next (SaveHarness.stateName next :: states)

        go 0 tab []

    /// Runs a scenario against the fake, then against the live repository.
    let onBoth (prefix: string) (scenario: Provider -> unit) =
        scenario (fake ())
        scenario (live prefix)

module PERSIST009Tests =
    open LiveSave

    type private Run =
        { Provider: Provider
          Base: CommitOid
          Unrelated: CommitOid list
          Final: Tab
          States: string list }

    /// PAT-04: the tab opens the investigation, edits, two unrelated commits advance the branch, the tab saves.
    let private pat04 (p: Provider) : Run =
        let verified = seed p
        let tab = SaveHarness.openTabAt verified |> SaveHarness.edit 1
        let others = [ otherWriterUnrelated p; otherWriterUnrelated p ]
        let tab = tab |> SaveHarness.inputWith p.Connection SaveInput.SaveRequested
        let final, states = drainRecording p tab
        { Provider = p; Base = verified.ReadAt; Unrelated = others; Final = final; States = states }

    /// O1: after the stale head the tab reads the new branch head and compares the investigation's files at the base
    /// head and at the new head.
    [<Fact>]
    let ``PERSIST-009-O1`` () =
        onBoth "probe/persist009-o1" (fun p ->
            let run = pat04 p
            let log = p.Requests()
            let first = log |> List.findIndex isSave
            Assert.True(SaveHarness.isRefRead log[first + 1], log[first + 1].Url)
            let (CommitOid b) = run.Base
            let (CommitOid h) = List.last run.Unrelated
            Assert.Contains(sprintf "/compare/%s...%s" b h, log[first + 2].Url))

    /// O2: unchanged investigation files mean the branch moved only for unrelated changes: no conflict.
    [<Fact>]
    let ``PERSIST-009-O2`` () =
        onBoth "probe/persist009-o2" (fun p ->
            let run = pat04 p
            Assert.DoesNotContain("Conflict", run.States)
            Assert.Equal("Saved", SaveHarness.stateName run.Final))

    /// O3: the same events, manifest and snapshot are sent again with the new head as expectedHeadOid, without asking
    /// the user.
    [<Fact>]
    let ``PERSIST-009-O3`` () =
        onBoth "probe/persist009-o3" (fun p ->
            let run = pat04 p
            let ms = mutations p
            Assert.Equal(2, ms.Length)
            let (CommitOid b) = run.Base
            let (CommitOid h) = List.last run.Unrelated
            Assert.Equal(b, expectedHeadOf ms[0])
            Assert.Equal(h, expectedHeadOf ms[1])
            Assert.Equal(additionsOf ms[0], additionsOf ms[1]))

    /// O4: while it rebases, the investigation stays Saving.
    [<Fact>]
    let ``PERSIST-009-O4`` () =
        onBoth "probe/persist009-o4" (fun p ->
            let run = pat04 p
            let saved = run.States |> List.findIndex ((=) "Saved")
            Assert.True(saved > 0)
            Assert.All(run.States |> List.take saved, fun s -> Assert.Equal("Saving", s))
            Assert.All(run.States |> List.skip saved, fun s -> Assert.Equal("Saved", s)))

    /// O5 (PAT-04): the change saves without user action after unrelated commits advance the branch, and the unrelated
    /// commits stay on the branch.
    [<Fact>]
    let ``PERSIST-009-O5`` () =
        onBoth "probe/persist009-o5" (fun p ->
            let run = pat04 p

            match run.Final.Session.State with
            | SaveState.Saved(Confirmed c) -> Assert.Equal(head p, c.CommitOid)
            | other -> failwithf "expected Saved by confirmation, got %A" other

            Assert.All(run.Unrelated, fun c -> Assert.True(reachable p c)))
