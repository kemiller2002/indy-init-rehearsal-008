// Owner: integration (T11). VER-008: a mandatory gate verifies the run lock against the installed artifacts, and a
// missing or drifted artifact is FAIL/BLOCKED, never PASS. The gate under test is the real one CI runs:
// `dotnet fsi build.fsx verify-lock` (DEP-CURRENT-008-O2).
namespace Causa.Acceptance.Deterministic

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Xunit

module private LockGate =
    let rec findRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "repository root (Causa.slnx) not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> findRoot (d.Parent: DirectoryInfo | null)

    let repoRoot = lazy (findRoot (DirectoryInfo AppContext.BaseDirectory))

    let gate (root: string) : int * string =
        let info =
            ProcessStartInfo("dotnet", WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true)
        [ "fsi"; Path.Combine(repoRoot.Value, "build.fsx"); "verify-lock"; "--root"; root ] |> List.iter info.ArgumentList.Add
        use p = Process.Start info |> nonNull
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        p.ExitCode, out.Result + err.Result

    let copyInto (target: string) (relative: string) =
        let source = Path.Combine(repoRoot.Value, relative)
        let destination = Path.Combine(target, relative)
        Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull) |> ignore
        File.Copy(source, destination, true)

    let lockedPackages () =
        let lockJson = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot.Value, "package-lock.json"))) |> nonNull
        (lockJson["packages"] |> nonNull).AsObject()
        |> Seq.map (fun kv -> kv.Key)
        |> Seq.filter (fun k -> k.StartsWith "node_modules/" && not (k.Substring(13).Contains "/node_modules/"))
        |> List.ofSeq

    /// A throw-away copy of exactly the files the gate reads: the locks, each installed package's package.json and
    /// one restored project's assets file.
    let snapshot () =
        let dir = Path.Combine(Path.GetTempPath(), "causa-ver008-" + Guid.NewGuid().ToString "N")
        [ ".conditor/lock.json"; "package.json"; "package-lock.json"; "Directory.Packages.props"; "global.json" ]
        |> List.iter (copyInto dir)
        lockedPackages () |> List.iter (fun p -> copyInto dir (p + "/package.json"))
        let assets =
            Directory.GetFiles(Path.Combine(repoRoot.Value, "tests", "Causa.Acceptance", "obj"), "project.assets.json")
            |> Array.head
        Path.GetRelativePath(repoRoot.Value, assets) |> copyInto dir
        dir

    let setInstalledVersion (root: string) (package: string) (version: string) =
        let path = Path.Combine(root, "node_modules", package, "package.json")
        let json = JsonNode.Parse(File.ReadAllText path) |> nonNull
        json["version"] <- JsonValue.Create version
        File.WriteAllText(path, json.ToJsonString())

type VER008Tests() =
    [<Fact>]
    member _.``VER-008-O1``() =
        // The gate verifies the locked manifest against what this checkout actually installed and restored.
        let code, output = LockGate.gate LockGate.repoRoot.Value
        Assert.True((code = 0), output)
        Assert.Contains("Stack lock gate: PASS", output)
        Assert.Contains("PASS    conditor limen (@echelon-foundry/limen): lock 0.9.1, installed 0.9.1", output)
        Assert.Contains("PASS    conditor forma (@echelon-foundry/design-system): lock 0.12.0, installed 0.12.0", output)
        Assert.Contains("PASS    conditor folio (@echelon-foundry/print-components): lock 0.4.1, installed 0.4.1", output)
        Assert.DoesNotContain("FAIL", output)
        Assert.DoesNotContain("BLOCKED", output)

    [<Fact>]
    member _.``VER-008-O2``() =
        let root = LockGate.snapshot ()
        try
            let baseline, baselineOutput = LockGate.gate root
            Assert.True((baseline = 0), baselineOutput)
            // A drifted install fails the gate.
            LockGate.setInstalledVersion root "@echelon-foundry/limen" "0.9.2"
            let drifted, driftOutput = LockGate.gate root
            Assert.Equal(1, drifted)
            Assert.Contains("FAIL    conditor limen (@echelon-foundry/limen): lock 0.9.1, installed 0.9.2", driftOutput)
            Assert.Contains("Stack lock gate: FAIL", driftOutput)
            // A missing artifact is BLOCKED, never PASS.
            Directory.Delete(Path.Combine(root, "node_modules", "axe-core"), true)
            let missing, missingOutput = LockGate.gate root
            Assert.Equal(1, missing)
            Assert.Contains("BLOCKED npm axe-core: lock 4.14.0, installed missing", missingOutput)
        finally
            Directory.Delete(root, true)
