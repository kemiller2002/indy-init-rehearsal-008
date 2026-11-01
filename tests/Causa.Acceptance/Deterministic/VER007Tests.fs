// Owner: ui-2 (T29). VER-007: resolved application, dependency and build versions are shown in Administration only
// (O1), and they are the versions of the components actually loaded, never aspirational ones (O2). These tests drive
// the real projection (Views/Administration.fs through View.render and Program.step), not a copy of it.
namespace Causa.Acceptance.Deterministic

open System
open System.IO
open System.Reflection
open System.Text.Json.Nodes
open Xunit
open Causa.App
open Causa.App.Views
open Causa.Persistence

module private Ver007 =
    let rec findRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "repository root (Causa.slnx) not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> findRoot (d.Parent: DirectoryInfo | null)

    let repoRoot = lazy (findRoot (DirectoryInfo AppContext.BaseDirectory))

    let rows (name: string) (view: ShellView) =
        view
        |> List.pick (fun (key, value) ->
            match value with
            | Rows rows when key = name -> Some rows
            | _ -> None)

    let field (name: string) (row: (string * string) list) = row |> List.find (fst >> (=) name) |> snd

    let byKey (key: string) (rows: (string * string) list list) = rows |> List.find (field "key" >> (=) key)

    let isVersionKey (key: string) =
        key.StartsWith("admin", StringComparison.Ordinal) || key.StartsWith("settings", StringComparison.Ordinal)

    let reportOf (libraries: (string * string) list) =
        let entries = libraries |> List.map (fun (n, v) -> $"\"{n}\":{{\"version\":\"{v}\"}}") |> String.concat ","
        "{\"libraries\":{" + entries + "}}"

    let event (name: string) (value: string) =
        let o = JsonObject()
        o["kind"] <- JsonValue.Create "Event"
        let e = JsonObject()
        e["name"] <- JsonValue.Create name
        e["value"] <- JsonValue.Create value
        o["event"] <- e
        o.ToJsonString()

type VER007Tests() =
    /// O1: the versions live in Administration's own lists (bound only inside the Administration section of
    /// web/index.html, checked in the browser by UI-018-O5); no other projection key carries a version or commit.
    [<Fact>]
    member _.``VER-007-O1``() =
        let state = { View.initial with Booted = true; Hash = "#/" }
        let view = View.render state
        let versions = Ver007.rows "adminVersions" view
        let engine = Administration.loadedEngine
        // The distinctive facts (a bare "1.0.0" also appears in data such as the graph document's schema version).
        let shown =
            [ "commit"; "runtime"; "storage" ]
            |> List.map (fun key -> versions |> Ver007.byKey key)
            |> List.filter (Ver007.field "state" >> (=) "verified")
            |> List.map (Ver007.field "value")

        Assert.Equal<string list>(
            [ "app"; "commit"; "runtime"; "limen"; "forma"; "formaWorkflow"; "folio"; "storage"; "interfaces" ],
            versions |> List.map (Ver007.field "key")
        )

        Assert.True(engine.InformationalVersion.IsSome, "the loaded engine assembly reports a version")
        // No key outside Administration/Settings carries any shown version, commit or runtime text.
        view
        |> List.filter (fst >> Ver007.isVersionKey >> not)
        |> List.iter (fun (key, value) ->
            match value with
            | Text text -> shown |> List.iter (fun v -> Assert.False(text.Contains v, $"{key} shows {v}"))
            | _ -> ())

        let index = File.ReadAllText(Path.Combine(Ver007.repoRoot.Value, "web", "index.html"))
        let admin = index.IndexOf("aria-labelledby=\"causa-admin-heading\"", StringComparison.Ordinal)
        let adminEnd = index.IndexOf("</section>", admin, StringComparison.Ordinal)
        let bound = index.IndexOf("data-each=\"adminVersions\"", StringComparison.Ordinal)
        Assert.True(admin > 0 && bound > admin && bound < adminEnd, "adminVersions is bound only inside Administration")
        Assert.Equal(index.IndexOf("adminVersions", StringComparison.Ordinal), index.LastIndexOf("adminVersions", StringComparison.Ordinal))

    /// O2: every shown version equals what the loaded component reports; a component that reports nothing is "not
    /// available", and before the browser reports, its libraries are "unknown", never a guessed version.
    [<Fact>]
    member _.``VER-007-O2``() =
        // Engine facts are the loaded assembly's own metadata and runtime.
        let informational =
            typeof<Administration.BrowserReport>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            |> nonNull

        let version, commit = Administration.splitInformational informational.InformationalVersion
        let before = View.render { View.initial with Booted = true } |> Ver007.rows "adminVersions"
        Assert.Equal(version, before |> Ver007.byKey "app" |> Ver007.field "value")

        match commit with
        | Some sha -> Assert.Equal(sha, before |> Ver007.byKey "commit" |> Ver007.field "value")
        | None -> Assert.Equal("unavailable", before |> Ver007.byKey "commit" |> Ver007.field "state")

        Assert.Equal(Runtime.InteropServices.RuntimeInformation.FrameworkDescription, before |> Ver007.byKey "runtime" |> Ver007.field "value")
        Assert.Equal("causa-storage " + StorageV1.formatVersion, before |> Ver007.byKey "storage" |> Ver007.field "value")

        // The interface manifest version is the frozen manifest's own.
        let manifest =
            JsonNode.Parse(File.ReadAllText(Path.Combine(Ver007.repoRoot.Value, "evidence", "interface-manifest.json")))
            |> nonNull

        Assert.Equal((manifest["version"] |> nonNull).GetValue<string>(), Administration.InterfaceManifestVersion)

        // Before the browser reports: unknown. After: exactly the reported versions; absent ones are not available.
        for key in [ "limen"; "forma"; "formaWorkflow"; "folio" ] do
            Assert.Equal("unknown", before |> Ver007.byKey key |> Ver007.field "state")

        let reported = Ver007.reportOf [ "limen", "9.8.7-test"; "forma", "6.5.4-test" ]
        let _, answer = Program.step { View.initial with Booted = true } (Program.decode (Ver007.event Administration.ReportEvent reported))
        let after = answer.View |> Ver007.rows "adminVersions"
        Assert.Equal("9.8.7-test", after |> Ver007.byKey "limen" |> Ver007.field "value")
        Assert.Equal("6.5.4-test", after |> Ver007.byKey "forma" |> Ver007.field "value")

        for key in [ "formaWorkflow"; "folio" ] do
            Assert.Equal("unavailable", after |> Ver007.byKey key |> Ver007.field "state")
            Assert.StartsWith("Not available.", after |> Ver007.byKey key |> Ver007.field "value")

        // An unreadable report fails visibly; it never keeps or invents a version.
        let _, broken = Program.step { View.initial with Booted = true } (Program.decode (Ver007.event Administration.ReportEvent "{not json"))
        let limen = broken.View |> Ver007.rows "adminVersions" |> Ver007.byKey "limen"
        Assert.Equal("failed", Ver007.field "state" limen)
        Assert.StartsWith("Failed.", Ver007.field "value" limen)

        // Nothing unverified is shown as verified in a tab without a repository (UI-018-O4).
        for key in [ "provider"; "catalog"; "dataset"; "replay" ] do
            Assert.NotEqual<string>("verified", after |> List.append (answer.View |> Ver007.rows "adminStatus") |> Ver007.byKey key |> Ver007.field "state")
