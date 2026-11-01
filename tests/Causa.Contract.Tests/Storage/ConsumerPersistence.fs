// Owner: persistence. T25: IF41-STORAGE consumer test. The catalog load (HistoricalCatalog) is the storage codec's
// consumer: every case it reads is verified and replayed through the F# kernel (StorageV1Replay), fail closed per
// item. The corpus is the frozen synthetic v1.0.0 dataset (kemiller2002/causa-data at tag causa-storage-v1.0.0, named
// by CAUSA_DATA_ROOT; the storage contract's test corpus, EXT-DATA-013-O3), served by the stateful fake as a catalog
// workspace. Nothing is vendored (EXT-DATA-011-O1). Without CAUSA_DATA_ROOT the test is BLOCKED (fails), never skipped.
namespace Causa.Contract.Tests.Storage

open System
open System.IO
open System.Text
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module ConsumerPersistence =

    let private root () =
        match Environment.GetEnvironmentVariable "CAUSA_DATA_ROOT" with
        | null
        | "" -> failwith "BLOCKED: CAUSA_DATA_ROOT (a checkout of kemiller2002/causa-data at causa-storage-v1.0.0) is not set; the catalog consumer test did not run."
        | path -> path

    let private workspace = "workspaces/demo/contract-corpus"

    let private idText (InvestigationId g) = g.ToString("D").ToLowerInvariant()

    /// The frozen dataset served as a catalog: catalog/active.json, a catalog manifest whose cases are the dataset's
    /// investigations (with their directory under the workspace), and each investigation's three files, byte for byte.
    let private catalogRepository (root: string) (forge: string -> byte[] -> byte[]) =
        let dataset = File.ReadAllBytes(Path.Combine(root, "fixtures/v1.0.0/manifest.json"))

        let entries =
            match HistoricalCatalog.parseManifest "fixtures/v1.0.0/manifest.json" dataset with
            | Ok entries -> entries
            | Error failure -> failwithf "the frozen dataset manifest does not parse: %A" failure

        let cases =
            entries
            |> List.map (fun e ->
                JObject
                    [ "caseKey", JString e.CaseKey
                      "title", JString e.Title
                      "investigationId", JString(idText e.Investigation)
                      "path", JString(sprintf "%s/investigations/%s" workspace (idText e.Investigation))
                      "manifestSha256", JString e.ManifestSha256
                      "eventsSha256", JString e.EventsSha256
                      "snapshotSha256", JString e.SnapshotSha256
                      "expectedStateSha256", JString e.ExpectedStateSha256
                      "expectedLifecycle", JString e.ExpectedLifecycle
                      "expectedVersion", JInt e.ExpectedVersion ])

        let line json = Array.append (Canonical.bytes json) [| 10uy |]

        let active =
            JObject
                [ "activeCatalog", JString "contract-corpus"
                  "catalogId", JString "causa-contract-corpus"
                  "catalogVersion", JString "1.0.0"
                  "manifest", JString "catalog/contract-corpus/manifest.json"
                  "workspace", JString workspace ]

        let files =
            [ "catalog/active.json", line active
              "catalog/contract-corpus/manifest.json", line (JObject [ "cases", JArray cases ])
              for e in entries do
                  for f in HistoricalCatalog.caseFiles do
                      let source = Path.Combine(root, "fixtures/v1.0.0/investigations", idText e.Investigation, f)
                      let target = sprintf "%s/investigations/%s/%s" workspace (idText e.Investigation) f
                      target, forge target (File.ReadAllBytes source) ]

        entries, StatefulFake.Server(StatefulFake.create "fake-owner" "fake-repo" "main" files)

    let private load (server: StatefulFake.Server) =
        match HistoricalCatalog.run Conformance.freshId server.Execute Conformance.fakeConnection with
        | CatalogReady catalog -> catalog
        | other -> failwithf "the catalog did not load: %A" other

    [<Fact>]
    let CatalogCasesVerifyAndReplay () =
        let root = root ()

        // Every frozen case verifies and replays to the manifest's expected state, lifecycle and version.
        let entries, server = catalogRepository root (fun _ bytes -> bytes)
        let catalog = load server
        Assert.Equal(5, entries.Length)
        Assert.Equal(entries.Length, catalog.Cases.Length)

        for case in catalog.Cases do
            match case with
            | CaseLoaded(entry, state) ->
                Assert.Equal(entry.ExpectedStateSha256, StorageV1Replay.stateSha256 state)
                Assert.Equal(entry.ExpectedLifecycle, state.Lifecycle)
                Assert.Equal(AggregateVersion entry.ExpectedVersion, state.Version)
            | CaseRefused(entry, failures) -> Assert.Fail(sprintf "%s refused: %A" entry.CaseKey failures)

        // The load reads only (EXT-DATA-006-O1) and every file read names the commit the head read returned.
        let (CommitOid at) = catalog.ReadAt
        let sent = server.State.Log |> List.map fst
        Assert.All(sent, fun r -> Assert.Equal(Get, r.Method))
        Assert.All(sent |> List.filter (fun r -> r.Url.Contains "/contents/"), fun r -> Assert.Contains("ref=" + at, r.Url))

        // Fail closed per item: one forged events file refuses that case only; the others still load.
        let forgedId = entries.Head.Investigation
        let forgedPath = sprintf "/investigations/%s/events.ndjson" (idText forgedId)

        let forge (path: string) (bytes: byte[]) =
            if path.EndsWith forgedPath then
                Encoding.UTF8.GetBytes((Encoding.UTF8.GetString bytes).Replace("\"aggregateVersion\":2,", "\"aggregateVersion\":3,"))
            else
                bytes

        let _, forgedServer = catalogRepository root forge
        let forged = load forgedServer

        for case in forged.Cases do
            match case with
            | CaseRefused(entry, failures) when entry.Investigation = forgedId -> Assert.Contains(DigestMismatch "eventsSha256", failures)
            | CaseRefused(entry, failures) -> Assert.Fail(sprintf "%s refused: %A" entry.CaseKey failures)
            | CaseLoaded(entry, _) -> Assert.NotEqual(forgedId, entry.Investigation)

        Assert.Equal(entries.Length - 1, (HistoricalCatalog.loaded forged).Length)
