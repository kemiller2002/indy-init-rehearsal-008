// Owner: integration. T21 (integration checkpoint 2): PAT-02 concurrent GitHub commits, against the stateful fake and
// then live against kemiller2002/causa-write-test on a fresh branch of this CI run (live.yml). Without
// CAUSA_WRITE_TOKEN the live half is BLOCKED (fails), never skipped (PF-015, PF-020). Also holds the PAT harness
// (several tabs, other writers, evidence of the real commits) that PERSIST042Tests and PERSIST043Tests share.
namespace Causa.Acceptance.Live

open System
open System.IO
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

/// One tab's save: its final session, the bytes it wrote, and every (request, answer) exchanged for it.
type TabSave =
    { Tab: Guid
      Session: InvestigationSession
      Written: FileSet
      Exchanges: (HttpRequest * HttpOutcome) list }

module Pat =
    open ProviderScenario

    /// The fake with its server exposed, so a test can script failures (PAT-03 injected failures).
    let scriptableFake () : ProviderUnderTest * StatefulFake.Server =
        let server = Conformance.newFake ()
        let sent = Collections.Concurrent.ConcurrentQueue<HttpRequest>()

        { Mode = FakeProvider
          Execute =
            fun request ->
                sent.Enqueue request
                server.Execute request
          Connection = Conformance.fakeConnection
          Sent = fun () -> List.ofSeq sent },
        server

    /// One save through SM-SAVE for the given tab (its own marker session), recording what was exchanged.
    let saveAs ((tab, nextMarker): Guid * (unit -> WriteMarker)) (p: ProviderUnderTest) (opened: VerifiedInvestigation) (commands: CommandEnvelope list) : TabSave =
        let exchanges = Collections.Concurrent.ConcurrentQueue<HttpRequest * HttpOutcome>()

        let execute request =
            let outcome = p.Execute request
            exchanges.Enqueue((request, outcome))
            outcome

        let fileSetOf = GitHubProvider.fileSetOf StorageV1.codec opened (decided opened)
        let setup = GitHubProvider.saveSetup tab nextMarker fileSetOf false
        let machine = SaveProcess.create setup

        let step session input =
            match machine.Step (context p) session input with
            | Ok next -> next
            | Error fault -> failwithf "%A: SM-SAVE fault %A" p.Mode fault

        let edited = commands |> List.fold (fun s c -> fst (step s (SaveInput.Edit c))) (machine.Initial opened)
        let written = fileSetOf edited

        let rec run (session: InvestigationSession) (effects: SaveEffect list) (budget: int) =
            match effects |> List.tryPick (function Send r -> Some r | _ -> None) with
            // Entering Conflict sends the remote read SM-CONFLICT interprets (the review), not SM-SAVE: the save ends here.
            | _ when (match session.State with SaveState.Conflict _ -> true | _ -> false) -> session
            | _ when budget = 0 -> failwithf "%A: the save did not settle" p.Mode
            | None -> session
            | Some request ->
                match machine.Interpret (context p) session (execute request) with
                | MachineEvent input ->
                    let next, more = step session input
                    run next more (budget - 1)
                | NextRequest(next, request) -> run next [ Send request ] (budget - 1)

        let session, effects = step edited SaveInput.SaveRequested

        { Tab = tab
          Session = run session effects 64
          Written = written
          Exchanges = List.ofSeq exchanges }

    /// A new tab: its marker session and its marker source (one source per tab, so markers never repeat).
    let newTab () : Guid * (unit -> WriteMarker) =
        let tab = Guid.NewGuid()
        tab, Markers.forTab tab

    let isMutation (request: HttpRequest) =
        request.Body |> Option.exists (fun b -> b.Contains "createCommitOnBranch")

    let tabMutations (s: TabSave) = s.Exchanges |> List.filter (fst >> isMutation)

    let input (request: HttpRequest) =
        use doc = JsonDocument.Parse(request.Body.Value)
        doc.RootElement.GetProperty("variables").GetProperty("input").Clone()

    let expectedHead (request: HttpRequest) = CommitOid((input request).GetProperty("expectedHeadOid").GetString() |> nonNull)

    let paths (request: HttpRequest) =
        [ for a in (input request).GetProperty("fileChanges").GetProperty("additions").EnumerateArray() -> a.GetProperty("path").GetString() |> nonNull ]

    let marker (request: HttpRequest) =
        let body = (input request).GetProperty("message").GetProperty("body").GetString() |> nonNull

        body.Split('\n')
        |> Array.tryFind (fun l -> l.StartsWith(Markers.trailerKey, StringComparison.Ordinal))
        |> Option.defaultWith (fun () -> failwithf "mutation without a write marker: %s" body)

    let confirmedAt (mode: ProviderMode) (session: InvestigationSession) : CommitOid =
        match session.State with
        | SaveState.Saved(Confirmed c) -> c.CommitOid
        | other -> failwithf "%A: expected a confirmed save, got %A" mode other

    /// A writer outside every tab commits files to the branch at its current head (retries while its own head read
    /// was already overtaken). Returns the commit GitHub created.
    let otherWriter (p: ProviderUnderTest) (headline: string) (files: (string * byte[]) list) : CommitOid =
        let rec attempt n =
            let h = head p

            let answer =
                p.Execute(GitHubRequests.createCommitOnBranch (Conformance.freshId ()) p.Connection h headline [ for (path, bytes) in files -> RepoPath path, bytes ])

            match Conformance.commitOid answer with
            | Some oid -> CommitOid oid
            | None when n > 1 -> attempt (n - 1)
            | None -> failwithf "%A: other writer could not commit: %A" p.Mode answer

        attempt 3

    /// True when `commit` is an ancestor of (or equal to) the branch head, read from GitHub's compare.
    let reachableFromHead (p: ProviderUnderTest) (commit: CommitOid) : bool =
        match p.Execute(GitHubRequests.compare (Conformance.freshId ()) p.Connection commit (head p) 1) with
        | Answered(_, 200, _, body) ->
            use doc = JsonDocument.Parse body
            let status = doc.RootElement.GetProperty("status").GetString()
            status = "ahead" || status = "identical"
        | other -> failwithf "%A: compare unreadable: %A" p.Mode other

    /// The bytes of one file at a commit (REST contents, base64).
    let fileAt (p: ProviderUnderTest) (commit: CommitOid) (path: string) : byte[] option =
        match p.Execute(GitHubRequests.contentsAt (Conformance.freshId ()) p.Connection commit (RepoPath path)) with
        | Answered(_, 200, _, body) ->
            use doc = JsonDocument.Parse body

            match doc.RootElement.TryGetProperty "content" with
            | true, c -> Some(Convert.FromBase64String((c.GetString() |> nonNull).Replace("\n", "")))
            | _ -> None
        | _ -> None

    let private evidenceLock = obj ()

    /// Evidence of a run: repository, branch and the real commits, appended to the recordings directory the
    /// workflow prints and uploads (PERSIST-025-O5, PF-014). Fake runs are labelled and never read as live.
    let record (p: ProviderUnderTest) (test: string) (commits: (string * CommitOid) list) =
        let mode =
            match p.Mode with
            | FakeProvider -> "provider=stateful-fake (NOT live verification)"
            | LiveProvider repo -> sprintf "provider=live-github repository=%s" repo

        let line =
            sprintf
                "%s %s branch=%s run=%s %s"
                (DateTimeOffset.UtcNow.ToString "yyyy-MM-ddTHH:mm:ssZ")
                test
                p.Connection.Repository.Branch
                (Environment.GetEnvironmentVariable "GITHUB_RUN_ID" |> Option.ofObj |> Option.defaultValue "local")
                (String.concat " " (mode :: [ for (what, CommitOid c) in commits -> sprintf "%s=%s" what c ]))

        Console.WriteLine line

        match p.Mode, Environment.GetEnvironmentVariable "CAUSA_RECORDINGS_OUT" with
        | LiveProvider _, NonNull dir when dir <> "" ->
            Directory.CreateDirectory dir |> ignore
            lock evidenceLock (fun () -> File.AppendAllText(Path.Combine(dir, "T21-live-evidence.txt"), line + "\n"))
        | _ -> ()

    /// Create an investigation and open it at the head (one tab's starting point).
    let createAndOpen (p: ProviderUnderTest) : InvestigationId * VerifiedInvestigation =
        let investigation = InvestigationId(Guid.NewGuid())
        create p investigation |> ignore
        investigation, opened (openAtHead p investigation)

module PERSIST041Tests =
    open ProviderScenario
    open Pat

    /// PAT-02: three tabs save changes to three different investigations at the same moment. Every change is
    /// confirmed exactly once, each tab shows its own investigation Saved, every mutation is one createCommitOnBranch
    /// with an expected head and paths inside its investigation, markers are unique, and every commit stays
    /// reachable from the branch head. Fake first, then live.
    [<Fact>]
    let ``PERSIST-041-O3`` () =
        onBoth "probe/t21-pat02" (fun p ->
            let tabs = [ for _ in 1..3 -> newTab (), createAndOpen p ]

            let saves =
                tabs
                |> List.map (fun (tab, (investigation, v)) -> Threading.Tasks.Task.Run(fun () -> saveAs tab p v [ command investigation ]))
                |> List.map (fun t -> t.Result)

            let confirmed = saves |> List.map (fun s -> s.Session.Id, confirmedAt p.Mode s.Session)

            // Each tab shows its own investigation, Saved, with nothing left unsaved.
            for ((_, (investigation, _)), s) in List.zip tabs saves do
                Assert.Equal(investigation, s.Session.Id)
                Assert.Empty(s.Session.Unsaved)

            // Every change confirmed exactly once: three distinct commits, one per save.
            Assert.Equal(3, confirmed |> List.map snd |> List.distinct |> List.length)

            // Every mutation: one createCommitOnBranch at the GraphQL endpoint, a 40-hex expected head, paths only
            // under its own investigation; at most four per save (R-11 bounds).
            for s in saves do
                let ms = tabMutations s
                Assert.InRange(ms.Length, 1, 4)

                for (m, _) in ms do
                    Assert.Equal(GitHubRequests.graphqlUrl, m.Url)
                    let (CommitOid e) = expectedHead m
                    Assert.Matches("^[0-9a-f]{40}$", e)
                    Assert.All(paths m, fun path -> Assert.True(StoragePaths.isUnder s.Session.Id path, path))

            // Markers unique across every mutation every tab sent.
            let markers = saves |> List.collect tabMutations |> List.map (fst >> marker)
            Assert.Equal(markers.Length, (List.distinct markers).Length)

            // Every confirmed commit stays reachable from the branch head, and each investigation reads back what
            // its tab wrote.
            for (s, (investigation, c)) in List.zip saves confirmed do
                Assert.True(reachableFromHead p c, sprintf "%A not reachable from head" c)
                let atHead = opened (openAtHead p investigation)
                Assert.Equal<byte[]>(s.Written.Events, atHead.Bytes.Events)

            record p "PERSIST-041-O3" [ for (i, (_, c)) in List.indexed confirmed -> sprintf "tab%d-confirmed" (i + 1), c ])
