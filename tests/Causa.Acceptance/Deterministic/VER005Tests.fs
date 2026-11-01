// Owner: integration (WI-R8-FINALDET). VER-005-O1: no alpha, beta, RC, unpublished source or unverified local build is
// adopted. Every pinned version in the run's manifests is a stable release, and every locked npm artifact comes from a
// published registry tarball or a published GitHub release asset with a sha512 digest, never a local path, link,
// workspace or git source.
namespace Causa.Acceptance.Deterministic

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq
open Xunit

module private Ver005Stable =
    let rec findRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "repository root (Causa.slnx) not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> findRoot (d.Parent: DirectoryInfo | null)

    let repoRoot = lazy (findRoot (DirectoryInfo AppContext.BaseDirectory))
    let path (relative: string) = Path.Combine(repoRoot.Value, relative)
    let json (relative: string) = JsonNode.Parse(File.ReadAllText(path relative)) |> nonNull
    let str (node: JsonNode | null) = node |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""

    let at (key: string) (n: JsonNode | null) : JsonNode | null =
        match n with
        | null -> null
        | n -> n[key]

    /// Pure: a stable release is x.y.z with no prerelease or build suffix.
    let isStable (v: string) = Regex.IsMatch(v, @"^\d+\.\d+\.\d+$")

    /// Pure: a published artifact is a registry tarball or a GitHub release asset.
    let isPublished (url: string) =
        Regex.IsMatch(url, @"^https://registry\.npmjs\.org/.+\.tgz$")
        || Regex.IsMatch(url, @"^https://github\.com/[^/]+/[^/]+/releases/download/v\d+\.\d+\.\d+/.+\.tgz$")

type VER005Tests() =
    [<Fact>]
    member _.``VER-005-O1``() =
        // The rules themselves reject what VER-005 forbids.
        [ "1.0.0-alpha.1"; "2.0.0-beta"; "3.1.0-rc.2"; "10.0.100-preview.7"; "1.2.3+local" ]
        |> List.iter (fun v -> Assert.False(Ver005Stable.isStable v, v))
        [ "file:../limen"; "link:../forma"; "git+https://github.com/x/y.git"; "https://github.com/x/y/archive/main.tgz" ]
        |> List.iter (fun u -> Assert.False(Ver005Stable.isPublished u, u))
        // npm lock.
        let npm =
            ((Ver005Stable.json "package-lock.json")["packages"] |> nonNull).AsObject()
            |> Seq.filter (fun kv -> kv.Key.StartsWith "node_modules/")
            |> Seq.map (fun kv -> kv.Key, kv.Value |> nonNull)
            |> List.ofSeq
        Assert.NotEmpty npm
        npm
        |> List.iter (fun (name, entry) ->
            Assert.True(Ver005Stable.isStable (Ver005Stable.str entry["version"]), $"{name}: prerelease or unpinned version")
            Assert.True(Ver005Stable.isPublished (Ver005Stable.str entry["resolved"]), $"{name}: not a published artifact")
            Assert.StartsWith("sha512-", Ver005Stable.str entry["integrity"])
            Assert.Null(entry["link"]))
        // NuGet central versions.
        XDocument.Load(Ver005Stable.path "Directory.Packages.props").Descendants(XName.Get "PackageVersion")
        |> List.ofSeq
        |> fun versions ->
            Assert.NotEmpty versions
            versions |> List.iter (fun e -> Assert.True(Ver005Stable.isStable (e.Attribute(XName.Get "Version") |> nonNull).Value))
        // SDK and stack components.
        Assert.True(Ver005Stable.isStable (Ver005Stable.json "global.json" |> Ver005Stable.at "sdk" |> Ver005Stable.at "version" |> Ver005Stable.str))
        ((((Ver005Stable.json ".conditor/lock.json")["manifest"] |> nonNull)["components"] |> nonNull).AsArray())
        |> Seq.iter (fun c -> Assert.True(Ver005Stable.isStable (c |> Ver005Stable.at "version" |> Ver005Stable.str)))
