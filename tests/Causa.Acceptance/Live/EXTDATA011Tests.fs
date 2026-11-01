// Owner: domain. WI-R8-DTESTS: EXT-DATA-011, the frozen Causa storage contract as a reader/writer specification.
// After reading the frozen v1.0.0 compatibility dataset from kemiller2002/causa-data at one commit (read-only
// CAUSA_GITHUB_TOKEN, CI live.yml), the reader verifies and replays every fixture, the writer's output verifies at the
// same contract version, nothing of the dataset is vendored here, and incompatible data fails closed. Without the
// secret the tests are BLOCKED (fail), never skipped (PF-015, PF-020).
namespace Causa.Acceptance.Live

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

/// The frozen storage contract read live. The checks are pure functions of a file reader (path -> bytes at the read
/// commit); only `live` touches the network.
module StorageContractScenario =
    let datasetManifest = "fixtures/v1.0.0/manifest.json"

    let idText (InvestigationId g) = g.ToString("D").ToLowerInvariant()

    let casePath (entry: CatalogEntry) (file: string) =
        sprintf "fixtures/v1.0.0/investigations/%s/%s" (idText entry.Investigation) file

    /// The data repository from the CI secret, or BLOCKED. CAUSA_DATA_REPOSITORY may name another owner/name.
    let private connection () : ConnectionContext =
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

    /// The branch head and a reader of files at that one commit (queries only). Read once per run.
    let live =
        lazy
            (let c = connection ()

             let head =
                 match LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) c) with
                 | Answered(_, 200, _, body) ->
                     use doc = JsonDocument.Parse body
                     CommitOid(doc.RootElement.GetProperty("object").GetProperty("sha").GetString() |> string)
                 | other -> failwithf "the branch head of %s/%s was not readable: %A" c.Repository.Owner c.Repository.Name other

             let cache = Collections.Concurrent.ConcurrentDictionary<string, byte[]>()

             let fileAt (path: string) : byte[] =
                 cache.GetOrAdd(
                     path,
                     fun p ->
                         match GitHubProvider.interpretContents (LiveGitHub.execute (GitHubRequests.contentsAt (Conformance.freshId ()) c head (RepoPath p))) with
                         | ContentsBytes(_, bytes) -> bytes
                         | other -> failwithf "%s unreadable at %A: %A" p head other
                 )

             head, fileAt)

    let entries (fileAt: string -> byte[]) : CatalogEntry list =
        match HistoricalCatalog.parseManifest datasetManifest (fileAt datasetManifest) with
        | Ok entries -> entries
        | Error failure -> failwithf "the dataset manifest does not parse: %A" failure

    let filesOf (fileAt: string -> byte[]) (entry: CatalogEntry) : FileSet =
        { Manifest = fileAt (casePath entry "manifest.json")
          Events = fileAt (casePath entry "events.ndjson")
          Snapshot = fileAt (casePath entry "snapshot.json") }

    let rec private repositoryRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "the repository root (Causa.slnx) was not found"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> repositoryRoot d.Parent

    /// Every file of this repository a build or a test could carry (sources, web, tests, fixtures), without build
    /// output and dependencies.
    let repositoryFiles () =
        let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
        let sep = Path.DirectorySeparatorChar

        [ "src"; "web"; "tests" ]
        |> List.map (fun d -> Path.Combine(root, d))
        |> List.filter Directory.Exists
        |> List.collect (fun d -> Directory.GetFiles(d, "*", SearchOption.AllDirectories) |> Array.map string |> List.ofArray)
        |> List.filter (fun p ->
            not (p.Contains $"{sep}bin{sep}" || p.Contains $"{sep}obj{sep}" || p.Contains "node_modules" || p.EndsWith ".dll" || p.EndsWith ".png"))

    /// Replace one manifest field's string value; the result is still canonical JSON of the same shape.
    let withManifestField (field: string) (value: string) (files: FileSet) : FileSet =
        let text = Encoding.UTF8.GetString files.Manifest
        let from = sprintf "\"%s\":\"1.0.0\"" field

        if not (text.Contains from) then
            failwithf "the manifest does not carry %s 1.0.0" field

        { files with Manifest = Encoding.UTF8.GetBytes(text.Replace(from, sprintf "\"%s\":\"%s\"" field value)) }

    let refused (case: CatalogCase) =
        match case with
        | CaseRefused(_, failures) -> Some failures
        | CaseLoaded _ -> None

    /// O1 checks: the reader admits every frozen fixture exactly (digests, canonical text, kernel replay to the
    /// recorded state); the writer's file sets carry the contract versions and verify; and no repository file holds a
    /// fixture's investigation id or digest (the demonstration records are read, never vendored).
    let readerWriterProblems (fileAt: string -> byte[]) =
        let all = entries fileAt

        [ if all.Length <> 5 then
              yield sprintf "the frozen v1.0.0 dataset has %d investigations, not 5" all.Length
          for entry in all do
              match HistoricalCatalog.verifyCase entry (filesOf fileAt entry) with
              | CaseLoaded(_, state) ->
                  if StorageV1Replay.stateSha256 state <> entry.ExpectedStateSha256 then
                      yield sprintf "%s replayed to another state" entry.CaseKey
                  if state.Version <> AggregateVersion entry.ExpectedVersion then
                      yield sprintf "%s replayed to version %A" entry.CaseKey state.Version
              | CaseRefused(_, failures) -> yield sprintf "the reader refused frozen fixture %s: %A" entry.CaseKey failures

          // The writer: what a save writes is a v1.0.0 file set that a reader verifies back to the same history.
          let id = all |> List.tryHead |> Option.map _.Investigation |> Option.defaultValue (InvestigationId(Guid "00000000-0000-4000-8000-000000000011"))

          let history: EventEnvelope list =
              [ { EventId = EventId(Guid "00000000-0000-4000-8000-000000001101")
                  Investigation = id
                  Version = AggregateVersion 1L
                  Payload = EventPayload "InvestigationStarted" } ]

          let written = StorageV1.encodeHistory id history
          let manifest = Encoding.UTF8.GetString written.Manifest

          for field in [ "formatVersion"; "schemaVersion"; "semanticVersion" ] do
              if not (manifest.Contains(sprintf "\"%s\":\"1.0.0\"" field)) then
                  yield sprintf "the writer's manifest does not state %s 1.0.0" field

          match StorageV1.codec.Verify (CommitOid "writer") written with
          | Ok verified when verified.History = history -> ()
          | Ok verified -> yield sprintf "the writer's file set verified to another history: %A" verified.History
          | Error failures -> yield sprintf "the writer's file set does not verify: %A" failures

          // Not vendored.
          let markers = all |> List.collect (fun e -> [ idText e.Investigation; e.EventsSha256; e.SnapshotSha256; e.ManifestSha256 ])

          for file in repositoryFiles () do
              let text = File.ReadAllText file

              for m in markers do
                  if text.Contains(m, StringComparison.OrdinalIgnoreCase) then
                      yield sprintf "%s vendors a demonstration record (%s)" file m ]

    /// O2 checks: the contract is versioned (StorageV1.formatVersion is 1.0.0 and every fixture states it), and data of
    /// another version, or altered without re-hashing, fails closed: refused, never loaded.
    let versionProblems (fileAt: string -> byte[]) =
        let all = entries fileAt

        [ if StorageV1.formatVersion <> "1.0.0" then
              yield sprintf "the storage contract version is %s, not 1.0.0" StorageV1.formatVersion
          for entry in all do
              let files = filesOf fileAt entry

              for field in [ "formatVersion"; "schemaVersion"; "semanticVersion" ] do
                  for version in [ "2.0.0"; "1.1.0"; "0.9.0" ] do
                      let incompatible = withManifestField field version files

                      match refused (HistoricalCatalog.verifyCase entry incompatible) with
                      | Some failures when failures |> List.exists (function ContractValueMismatch(f, "1.0.0", v) -> f = field && v = version | _ -> false) -> ()
                      | Some failures -> yield sprintf "%s %s %s refused without naming the version: %A" entry.CaseKey field version failures
                      | None -> yield sprintf "%s with %s %s was loaded (not fail closed)" entry.CaseKey field version

                      match StorageV1.codec.Verify (CommitOid "incompatible") incompatible with
                      | Ok _ -> yield sprintf "the codec accepted %s with %s %s" entry.CaseKey field version
                      | Error _ -> ()

              // Altered events without re-hashing: refused.
              let altered = { files with Events = Array.append files.Events (Encoding.UTF8.GetBytes "{}\n") }

              match refused (HistoricalCatalog.verifyCase entry altered) with
              | Some _ -> ()
              | None -> yield sprintf "%s with altered events was loaded (not fail closed)" entry.CaseKey ]

module EXTDATA011Tests =
    open StorageContractScenario

    /// O1: compatibility with the frozen Causa storage contract as a reader/writer specification, without vendoring
    /// its demonstration records.
    [<Fact>]
    let ``EXT-DATA-011-O1`` () =
        let _, fileAt = live.Force()

        match readerWriterProblems fileAt with
        | [] -> ()
        | problems -> Assert.Fail(String.concat "\n" problems)

    /// O2: the contract is versioned and incompatible data fails closed.
    [<Fact>]
    let ``EXT-DATA-011-O2`` () =
        let _, fileAt = live.Force()

        match versionProblems fileAt with
        | [] -> ()
        | problems -> Assert.Fail(String.concat "\n" problems)
