// Owner: integration (WI-R8-FINALDET). VER-003-O3: never silently fall back to an old version. The SDK cannot roll to
// another release (global.json rollForward "disable"), and when a restored package is older than the lock the
// mandatory gate (`dotnet fsi build.fsx verify-lock`) reports FAIL loudly and exits non-zero instead of accepting it.
namespace Causa.Acceptance.Deterministic

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Xml.Linq
open Xunit

module private Ver003Fallback =
    let rec findRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "repository root (Causa.slnx) not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> findRoot (d.Parent: DirectoryInfo | null)

    let repoRoot = lazy (findRoot (DirectoryInfo AppContext.BaseDirectory))
    let path (relative: string) = Path.Combine(repoRoot.Value, relative)

    let gate (root: string) : int * string =
        let info =
            ProcessStartInfo("dotnet", WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true)
        [ "fsi"; path "build.fsx"; "verify-lock"; "--root"; root ] |> List.iter info.ArgumentList.Add
        use p = Process.Start info |> nonNull
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode, out.Result + err.Result

    let assetsRelative () =
        Directory.GetFiles(path (Path.Combine("tests", "Causa.Acceptance", "obj")), "project.assets.json")
        |> Array.head
        |> fun a -> Path.GetRelativePath(repoRoot.Value, a)

    let snapshot () =
        let dir = Path.Combine(Path.GetTempPath(), "causa-ver003-" + Guid.NewGuid().ToString "N")
        [ ".conditor/lock.json"; "package.json"; "package-lock.json"; "Directory.Packages.props"; "global.json"; assetsRelative () ]
        |> List.iter (fun relative ->
            let destination = Path.Combine(dir, relative)
            Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull) |> ignore
            File.Copy(path relative, destination, true))
        dir

    /// Rewrite the restored assets so `name` resolved to `older` instead of `locked`.
    let downgradeRestored (root: string) (name: string) (locked: string) (older: string) =
        let file = Path.Combine(root, assetsRelative ())
        let assets = JsonNode.Parse(File.ReadAllText file) |> nonNull
        let libraries = (assets["libraries"] |> nonNull).AsObject()
        let key = $"{name}/{locked}"
        let entry = libraries[key] |> nonNull
        libraries.Remove key |> ignore
        libraries.Add($"{name}/{older}", entry.DeepClone())
        File.WriteAllText(file, assets.ToJsonString())

type VER003Tests() =
    [<Fact>]
    member _.``VER-003-O3``() =
        let sdk = (JsonNode.Parse(File.ReadAllText(Ver003Fallback.path "global.json")) |> nonNull)["sdk"] |> nonNull
        Assert.Equal("disable", (sdk["rollForward"] |> nonNull).GetValue<string>())
        let root = Ver003Fallback.snapshot ()
        try
            let name, locked =
                XDocument.Load(Path.Combine(root, "Directory.Packages.props")).Descendants(XName.Get "PackageVersion")
                |> Seq.map (fun e -> (e.Attribute(XName.Get "Include") |> nonNull).Value, (e.Attribute(XName.Get "Version") |> nonNull).Value)
                |> Seq.find (fst >> (=) "xunit")
            let _, baseline = Ver003Fallback.gate root
            Assert.Contains($"PASS    nuget {name}: lock {locked}, installed {locked}", baseline)
            let older = "1.0.0"
            Ver003Fallback.downgradeRestored root name locked older
            let code, output = Ver003Fallback.gate root
            Assert.Equal(1, code)
            Assert.Contains($"FAIL    nuget {name}: lock {locked}, installed {older}", output)
            Assert.DoesNotContain($"PASS    nuget {name}:", output)
            Assert.Contains("Stack lock gate: FAIL", output)
        finally
            Directory.Delete(root, true)
