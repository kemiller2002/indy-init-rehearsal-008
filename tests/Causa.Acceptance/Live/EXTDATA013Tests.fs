// Owner: persistence. T25: EXT-DATA-013 the active demonstration catalog. After an explicit Connect to
// kemiller2002/causa-data (read-only CAUSA_GITHUB_TOKEN, CI live.yml), the catalog named by `catalog/active.json` is
// loaded through HistoricalCatalog: every case read at one commit, verified and replayed by the F# kernel
// (StorageV1Replay) to the manifest's expected state. Without the secret the tests are BLOCKED (fail), never skipped
// (PF-015, PF-020). Nothing of the catalog is bundled in this repository.
namespace Causa.Acceptance.Live

open System
open System.IO
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module CatalogScenario =

    /// The catalog repository from the CI secret, or BLOCKED. CAUSA_DATA_REPOSITORY may name another owner/name.
    let liveReadConnection () : ConnectionContext =
        let token = LiveGitHub.requireSecret "CAUSA_GITHUB_TOKEN"

        let owner, name =
            match Environment.GetEnvironmentVariable "CAUSA_DATA_REPOSITORY" with
            | null
            | "" -> "kemiller2002", "causa-data"
            | v ->
                match v.Split '/' with
                | [| o; n |] -> o, n
                | _ -> failwith "CAUSA_DATA_REPOSITORY must be owner/name"

        { Repository = { Owner = owner; Name = name; Branch = "main" }
          Credential = Credential.create token }

    type LiveLoad =
        { Connection: ConnectionContext
          Connected: ConnectResult
          Load: CatalogLoad
          Sent: HttpRequest list }

    /// Connect, then load the catalog, recording every request. One load per test run (read-only, shared).
    let private loadOnce =
        lazy
            (let connection = liveReadConnection ()
             let sent = Collections.Concurrent.ConcurrentQueue<HttpRequest>()

             let execute request =
                 sent.Enqueue request
                 LiveGitHub.execute request

             let id = Conformance.freshId ()
             let connected = GitHubProvider.connector.InterpretConnect id (execute (GitHubProvider.connector.ConnectRequest id connection))

             let load =
                 match connected with
                 | ConnectAccepted _ -> HistoricalCatalog.run Conformance.freshId execute connection
                 | other -> CatalogFailed(CatalogMalformed("connect", sprintf "%A" other))

             { Connection = connection; Connected = connected; Load = load; Sent = List.ofSeq sent })

    let live () = loadOnce.Force()

    let catalog () : Catalog =
        match (live ()).Load with
        | CatalogReady c -> c
        | other -> failwithf "the active catalog did not load: %A" other

    let idText (InvestigationId g) = g.ToString("D").ToLowerInvariant()

    /// A file of the catalog repository at the commit the catalog was read at (a query).
    let fileAt (path: string) : byte[] =
        let l = live ()
        let c = catalog ()

        match GitHubProvider.interpretContents (LiveGitHub.execute (GitHubRequests.contentsAt (Conformance.freshId ()) l.Connection c.ReadAt (RepoPath path))) with
        | ContentsBytes(_, bytes) -> bytes
        | other -> failwithf "%s unreadable: %A" path other

    /// The five synthetic v1.0.0 fixtures of the retained compatibility dataset, read from the same commit.
    let syntheticFixtures () : CatalogEntry list =
        match HistoricalCatalog.parseManifest "fixtures/v1.0.0/manifest.json" (fileAt "fixtures/v1.0.0/manifest.json") with
        | Ok entries -> entries
        | Error failure -> failwithf "the dataset manifest does not parse: %A" failure

    let rec private repositoryRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "the repository root (Causa.slnx) was not found"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> repositoryRoot d.Parent

    /// Every shipped source and web file of this repository (what a build could bundle).
    let shippedFiles () =
        let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)

        [ "src"; "web" ]
        |> List.map (fun d -> Path.Combine(root, d))
        |> List.filter Directory.Exists
        |> List.collect (fun d -> Directory.GetFiles(d, "*", SearchOption.AllDirectories) |> List.ofArray)
        |> List.filter (fun p -> not (p.Contains $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}" || p.Contains $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" || p.Contains "node_modules"))

module EXTDATA013Tests =
    open CatalogScenario

    let private expectedCases =
        [ "apollo-1", "AS-204"; "apollo-11", "Apollo 11"; "challenger-sts-51l", "STS-51-L"; "burned-out-lightbulb", "lightbulb" ]

    /// O1: the active catalog is the one catalog/active.json names: exactly four investigations, Apollo 1 (AS-204),
    /// Apollo 11, Challenger (STS-51-L) and the burned-out lightbulb (DEC-R7-006).
    [<Fact>]
    let ``EXT-DATA-013-O1`` () =
        let c = catalog ()
        let pointer = HistoricalCatalog.parsePointer (fileAt "catalog/active.json")
        Assert.Equal(Ok c.Pointer, pointer)
        Assert.Equal(4, c.Cases.Length)

        let entries = c.Cases |> List.map (function CaseLoaded(e, _) | CaseRefused(e, _) -> e)
        Assert.Equal<string list>(List.map fst expectedCases |> List.sort, entries |> List.map _.CaseKey |> List.sort)

        for key, titlePart in expectedCases do
            let entry = entries |> List.find (fun e -> e.CaseKey = key)
            Assert.Contains(titlePart, entry.Title)

    /// O2: after an explicit Connect, all four load through full contract validation and F# replay to the states the
    /// manifest records; no synthetic investigation is presented as a catalog case; none of their records is bundled.
    [<Fact>]
    let ``EXT-DATA-013-O2`` () =
        let l = live ()

        match l.Connected with
        | ConnectAccepted _ -> ()
        | other -> failwithf "Connect failed: %A" other

        let c = catalog ()
        // Connect came first, and connect and load sent queries only (EXT-DATA-006-O1).
        Assert.Equal(GitHubRequests.repository (RequestId 0L) l.Connection |> _.Url, l.Sent.Head.Url)
        Assert.All(l.Sent, fun r -> Assert.Equal(Get, r.Method))

        for case in c.Cases do
            match case with
            | CaseLoaded(entry, state) ->
                Assert.Equal(entry.ExpectedStateSha256, StorageV1Replay.stateSha256 state)
                Assert.Equal(entry.ExpectedLifecycle, state.Lifecycle)
                Assert.Equal(AggregateVersion entry.ExpectedVersion, state.Version)
                Assert.Equal(entry.Investigation, state.InvestigationId)
            | CaseRefused(entry, failures) -> Assert.Fail(sprintf "%s refused: %A" entry.CaseKey failures)

        let presented = HistoricalCatalog.loaded c |> List.map (fun (e, _) -> e.Investigation)
        Assert.Equal(4, presented.Length)
        let synthetic = syntheticFixtures () |> List.map _.Investigation |> set
        Assert.All(presented, fun id -> Assert.False(synthetic.Contains id, idText id))

        // Not bundled: no shipped file carries a case's investigation id or its events digest.
        let markers = c.Cases |> List.collect (function CaseLoaded(e, _) | CaseRefused(e, _) -> [ idText e.Investigation; e.EventsSha256 ])

        for file in shippedFiles () do
            let text = File.ReadAllText file
            Assert.All(markers, fun m -> Assert.False(text.Contains(m, StringComparison.OrdinalIgnoreCase), sprintf "%s bundles %s" file m))

    /// O3: the five synthetic v1.0.0 fixtures remain the storage contract's test corpus only: none is a catalog case.
    [<Fact>]
    let ``EXT-DATA-013-O3`` () =
        let c = catalog ()
        let fixtures = syntheticFixtures ()
        Assert.Equal(5, fixtures.Length)
        let caseIds = c.Cases |> List.map (function CaseLoaded(e, _) | CaseRefused(e, _) -> e.Investigation) |> set
        Assert.All(fixtures, fun f -> Assert.False(caseIds.Contains f.Investigation, f.CaseKey))
        Assert.False(c.Pointer.Workspace.StartsWith("fixtures", StringComparison.Ordinal))
        Assert.All(c.Cases, fun case -> Assert.DoesNotContain("fixtures/", (match case with CaseLoaded(e, _) | CaseRefused(e, _) -> e.Path)))
