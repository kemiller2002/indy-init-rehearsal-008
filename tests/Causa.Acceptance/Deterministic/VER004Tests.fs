// Owner: integration (WI-R8-FINALDET). VER-004: the run lock pins exact versions and immutable digests/commit SHAs in
// machine-readable manifests (O1), and the lock cannot drift mid-run unnoticed: the mandatory gate CI runs,
// `dotnet fsi build.fsx verify-lock`, fails when the lock and what was restored disagree (O2).
namespace Causa.Acceptance.Deterministic

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq
open Xunit

module private Ver004Lock =
    let rec findRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "repository root (Causa.slnx) not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> findRoot (d.Parent: DirectoryInfo | null)

    let repoRoot = lazy (findRoot (DirectoryInfo AppContext.BaseDirectory))
    let path (relative: string) = Path.Combine(repoRoot.Value, relative)
    let json (relative: string) = JsonNode.Parse(File.ReadAllText(path relative)) |> nonNull
    let str (node: JsonNode | null) = node |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>())

    let at (key: string) (n: JsonNode | null) : JsonNode | null =
        match n with
        | null -> null
        | n -> n[key]

    let isExactVersion (v: string) = Regex.IsMatch(v, @"^\d+\.\d+\.\d+$")
    let isCommitSha (v: string) = Regex.IsMatch(v, @"^[0-9a-f]{40}$")

    let lockedNpm () =
        ((json "package-lock.json")["packages"] |> nonNull).AsObject()
        |> Seq.filter (fun kv -> kv.Key.StartsWith "node_modules/")
        |> Seq.map (fun kv -> kv.Key, kv.Value |> nonNull)
        |> List.ofSeq

    let declaredNuGet (propsPath: string) =
        XDocument.Load(propsPath).Descendants(XName.Get "PackageVersion")
        |> Seq.map (fun e -> (e.Attribute(XName.Get "Include") |> nonNull).Value, (e.Attribute(XName.Get "Version") |> nonNull).Value)
        |> List.ofSeq

    let gate (root: string) : int * string =
        let info =
            ProcessStartInfo("dotnet", WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true)
        [ "fsi"; path "build.fsx"; "verify-lock"; "--root"; root ] |> List.iter info.ArgumentList.Add
        use p = Process.Start info |> nonNull
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode, out.Result + err.Result

    /// A throw-away copy of the lock manifests and one restored project's assets file.
    let snapshot () =
        let dir = Path.Combine(Path.GetTempPath(), "causa-ver004-" + Guid.NewGuid().ToString "N")
        let assets = Directory.GetFiles(path (Path.Combine("tests", "Causa.Acceptance", "obj")), "project.assets.json") |> Array.head
        [ ".conditor/lock.json"; "package.json"; "package-lock.json"; "Directory.Packages.props"; "global.json"
          Path.GetRelativePath(repoRoot.Value, assets) ]
        |> List.iter (fun relative ->
            let destination = Path.Combine(dir, relative)
            Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull) |> ignore
            File.Copy(path relative, destination, true))
        dir

type VER004Tests() =
    [<Fact>]
    member _.``VER-004-O1``() =
        // npm: lockfile v3, every locked package has an exact version, an immutable https source and a sha512 digest.
        Assert.Equal(3, ((Ver004Lock.json "package-lock.json")["lockfileVersion"] |> nonNull).GetValue<int>())
        let npm = Ver004Lock.lockedNpm ()
        Assert.NotEmpty npm
        npm
        |> List.iter (fun (name, entry) ->
            let version = Ver004Lock.str entry["version"] |> Option.defaultValue ""
            Assert.True(Ver004Lock.isExactVersion version, $"{name}: version '{version}' is not an exact pin")
            Assert.StartsWith("https://", Ver004Lock.str entry["resolved"] |> Option.defaultValue "")
            Assert.StartsWith("sha512-", Ver004Lock.str entry["integrity"] |> Option.defaultValue ""))
        // package.json: every specifier is an exact pin or an immutable release URL naming the locked version.
        let declared = Ver004Lock.json "package.json"
        [ "dependencies"; "devDependencies" ]
        |> List.collect (fun section -> (declared[section] |> nonNull).AsObject() |> Seq.map (fun kv -> kv.Key, Ver004Lock.str kv.Value |> Option.defaultValue "") |> List.ofSeq)
        |> List.iter (fun (name, spec) ->
            let locked = npm |> List.find (fst >> (=) ("node_modules/" + name)) |> snd
            let version = Ver004Lock.str locked["version"] |> Option.defaultValue "<none>"
            match spec.StartsWith "https://" with
            | true -> Assert.Contains($"/v{version}/", spec)
            | false -> Assert.Equal(version, spec))
        // NuGet: central package versions are exact, never ranges or floating versions.
        let nuget = Ver004Lock.declaredNuGet (Ver004Lock.path "Directory.Packages.props")
        Assert.NotEmpty nuget
        nuget |> List.iter (fun (name, v) -> Assert.True(Ver004Lock.isExactVersion v, $"{name}: '{v}' is not an exact pin"))
        // SDK: exact version with roll-forward disabled.
        let sdk = (Ver004Lock.json "global.json")["sdk"] |> nonNull
        Assert.True(Ver004Lock.isExactVersion (Ver004Lock.str sdk["version"] |> Option.defaultValue ""))
        Assert.Equal(Some "disable", Ver004Lock.str sdk["rollForward"])
        // Conditor: stack components are exact versions and requirement sources are commit SHAs, bound by a digest.
        let conditor = Ver004Lock.json ".conditor/lock.json"
        Assert.Matches("^[0-9a-f]{64}$", Ver004Lock.str conditor["manifestSha256"] |> Option.defaultValue "")
        let manifest = conditor["manifest"] |> nonNull
        let components = (manifest["components"] |> nonNull).AsArray() |> List.ofSeq
        Assert.NotEmpty components
        components |> List.iter (fun c -> Assert.True(Ver004Lock.isExactVersion (Ver004Lock.str (Ver004Lock.at "version" c) |> Option.defaultValue "")))
        let requirements = (manifest["requirements"] |> nonNull).AsArray() |> List.ofSeq
        Assert.NotEmpty requirements
        requirements
        |> List.iter (fun r -> Assert.True(Ver004Lock.isCommitSha (Ver004Lock.str (r |> Ver004Lock.at "source" |> Ver004Lock.at "commit") |> Option.defaultValue "")))

    [<Fact>]
    member _.``VER-004-O2``() =
        let root = Ver004Lock.snapshot ()
        try
            let props = Path.Combine(root, "Directory.Packages.props")
            let name, locked = Ver004Lock.declaredNuGet props |> List.find (fst >> (=) "xunit")
            let _, baseline = Ver004Lock.gate root
            Assert.Contains($"PASS    nuget {name}: lock {locked}, installed {locked}", baseline)
            // The lock moves mid-run while the restored artifacts stay put: the gate fails, it does not follow the drift.
            let drifted = "99.0.0"
            let doc = XDocument.Load props
            doc.Descendants(XName.Get "PackageVersion")
            |> Seq.filter (fun e -> (e.Attribute(XName.Get "Include") |> nonNull).Value = name)
            |> Seq.iter (fun e -> (e.Attribute(XName.Get "Version") |> nonNull).Value <- drifted)
            doc.Save props
            let code, output = Ver004Lock.gate root
            Assert.Equal(1, code)
            Assert.Contains($"FAIL    nuget {name}: lock {drifted}, installed {locked}", output)
            Assert.DoesNotContain($"PASS    nuget {name}:", output)
            Assert.Contains("Stack lock gate: FAIL", output)
        finally
            Directory.Delete(root, true)
