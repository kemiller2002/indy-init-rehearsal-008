// Owner: integration. Causa's build script (dotnet fsi build.fsx <target> [options]; no FAKE).
//
//   site                    publish the WASM engine (src/Causa.Host) and assemble the deployable site in dist/:
//                           web/* + _framework/ + the locked Limen, Forma and Folio browser assets
//   verify-lock [--root D] [--report F]
//                           the stack-lock gate (VER-008, DEP-CURRENT-008-O2): installed npm packages, restored NuGet
//                           packages and the .NET SDK must equal the run lock (.conditor/lock.json, package.json,
//                           package-lock.json, Directory.Packages.props, global.json). Any FAIL or BLOCKED exits 1.
//   test                    dotnet test Causa.slnx; fails when no test passed or any test was skipped
//
// Pure functions decide; the only effects are reading files, running processes, copying and printing.
open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Xml.Linq

// ---------------------------------------------------------------------------------------------------------------
// Process and file edges
// ---------------------------------------------------------------------------------------------------------------

type ProcessResult = { ExitCode: int; Output: string }

let run (workingDirectory: string) (fileName: string) (arguments: string list) : ProcessResult =
    let info = ProcessStartInfo(fileName, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true)
    arguments |> List.iter info.ArgumentList.Add
    use p = Process.Start info
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { ExitCode = p.ExitCode; Output = stdout.Result + stderr.Result }

let runEcho workingDirectory fileName arguments =
    let result = run workingDirectory fileName arguments
    printfn "%s" result.Output
    result

let tryReadJson (path: string) : JsonElement option =
    if File.Exists path then
        try
            use doc = JsonDocument.Parse(File.ReadAllText path)
            Some(doc.RootElement.Clone())
        with :? JsonException ->
            None
    else
        None

let prop (name: string) (e: JsonElement) : JsonElement option =
    match e.ValueKind with
    | JsonValueKind.Object ->
        match e.TryGetProperty name with
        | true, v -> Some v
        | _ -> None
    | _ -> None

let str (e: JsonElement) : string option =
    if e.ValueKind = JsonValueKind.String then Option.ofObj (e.GetString()) else None

let objectEntries (e: JsonElement) : (string * JsonElement) list =
    if e.ValueKind = JsonValueKind.Object then
        e.EnumerateObject() |> Seq.map (fun p -> p.Name, p.Value) |> List.ofSeq
    else
        []

// ---------------------------------------------------------------------------------------------------------------
// Stack-lock gate (pure core)
// ---------------------------------------------------------------------------------------------------------------

type Verdict =
    | Pass
    | Fail
    | Blocked

type Check = { Item: string; Expected: string; Installed: string; Verdict: Verdict }

/// Pure: compare one expected version with what is installed (None = missing, which is BLOCKED, never PASS).
let compare (item: string) (expected: string) (installed: string option) : Check =
    match installed with
    | None -> { Item = item; Expected = expected; Installed = "missing"; Verdict = Blocked }
    | Some v when v = expected -> { Item = item; Expected = expected; Installed = v; Verdict = Pass }
    | Some v -> { Item = item; Expected = expected; Installed = v; Verdict = Fail }

/// Conditor component ids of the browser libraries and the npm package each one is installed as.
let componentPackages =
    [ "limen", "@echelon-foundry/limen"
      "forma", "@echelon-foundry/design-system"
      "folio", "@echelon-foundry/print-components" ]

/// The inputs of the gate, read once from disk; everything after this is pure.
type LockInputs =
    { ConditorComponents: (string * string) list
      LockedNpm: (string * string) list
      DeclaredNpm: (string * string) list
      InstalledNpm: Map<string, string>
      DeclaredNuGet: (string * string) list
      RestoredNuGet: Map<string, string list>
      RestoredProjects: int
      LockedSdk: string option
      InstalledSdk: string option }

let conditorChecks (inputs: LockInputs) : Check list =
    match inputs.ConditorComponents with
    | [] -> [ compare "conditor lock (.conditor/lock.json)" "components" None ]
    | components ->
        componentPackages
        |> List.map (fun (id, package) ->
            match components |> List.tryFind (fst >> (=) id) with
            | None -> compare $"conditor {id}" "a locked version" None
            | Some(_, version) -> compare $"conditor {id} ({package})" version (inputs.InstalledNpm.TryFind package))

let npmChecks (inputs: LockInputs) : Check list =
    match inputs.LockedNpm with
    | [] -> [ compare "package-lock.json" "lockfileVersion 3 packages" None ]
    | locked -> locked |> List.map (fun (package, version) -> compare $"npm {package}" version (inputs.InstalledNpm.TryFind package))

/// Pure: a package.json specifier is an exact pin (x.y.z) or an immutable release URL; a range is never a lock.
let isExactPin (spec: string) =
    Text.RegularExpressions.Regex.IsMatch(spec, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")

let manifestChecks (inputs: LockInputs) : Check list =
    match inputs.DeclaredNpm with
    | [] -> [ compare "package.json dependencies" "declared packages" None ]
    | declared ->
        declared
        |> List.choose (fun (package, spec) ->
            if spec.StartsWith "https://" then None
            elif isExactPin spec then Some(compare $"package.json {package}" spec (inputs.InstalledNpm.TryFind package))
            else Some { Item = $"package.json {package}"; Expected = "an exact pin"; Installed = spec; Verdict = Fail })

let nugetChecks (inputs: LockInputs) : Check list =
    let restored =
        if inputs.RestoredProjects = 0 then
            [ compare "restored .NET projects (obj/project.assets.json)" "at least one" None ]
        else
            []
    let perPackage =
        inputs.DeclaredNuGet
        |> List.collect (fun (package, version) ->
            match inputs.RestoredNuGet.TryFind(package.ToLowerInvariant()) with
            | None -> []
            | Some versions -> versions |> List.distinct |> List.map (fun v -> compare $"nuget {package}" version (Some v)))
    restored @ perPackage

let sdkChecks (inputs: LockInputs) : Check list =
    match inputs.LockedSdk with
    | None -> [ compare "global.json sdk.version" "a pinned SDK" None ]
    | Some sdk -> [ compare ".NET SDK" sdk inputs.InstalledSdk ]

/// Pure: every check of the gate.
let evaluate (inputs: LockInputs) : Check list =
    conditorChecks inputs @ manifestChecks inputs @ npmChecks inputs @ nugetChecks inputs @ sdkChecks inputs

let passed (checks: Check list) = checks |> List.forall (fun c -> c.Verdict = Pass)

// ---------------------------------------------------------------------------------------------------------------
// Stack-lock gate (reading the inputs)
// ---------------------------------------------------------------------------------------------------------------

let readConditor root =
    tryReadJson (Path.Combine(root, ".conditor", "lock.json"))
    |> Option.bind (prop "manifest")
    |> Option.bind (prop "components")
    |> Option.map (fun cs ->
        cs.EnumerateArray()
        |> Seq.choose (fun c ->
            match prop "id" c |> Option.bind str, prop "version" c |> Option.bind str with
            | Some id, Some v -> Some(id, v)
            | _ -> None)
        |> List.ofSeq)
    |> Option.defaultValue []

let readLockedNpm root =
    tryReadJson (Path.Combine(root, "package-lock.json"))
    |> Option.bind (prop "packages")
    |> Option.map (
        objectEntries
        >> List.choose (fun (key, value) ->
            match key.StartsWith "node_modules/", prop "version" value |> Option.bind str with
            | true, Some v when not (key.Substring("node_modules/".Length).Contains "/node_modules/") ->
                Some(key.Substring "node_modules/".Length, v)
            | _ -> None)
    )
    |> Option.defaultValue []

let readDeclaredNpm root =
    tryReadJson (Path.Combine(root, "package.json"))
    |> Option.map (fun pkg ->
        [ "dependencies"; "devDependencies" ]
        |> List.collect (fun section ->
            pkg |> prop section |> Option.map objectEntries |> Option.defaultValue []
            |> List.choose (fun (name, v) -> str v |> Option.map (fun spec -> name, spec))))
    |> Option.defaultValue []

let readInstalledNpm root (packages: string list) =
    packages
    |> List.choose (fun package ->
        tryReadJson (Path.Combine(root, "node_modules", package, "package.json"))
        |> Option.bind (prop "version")
        |> Option.bind str
        |> Option.map (fun v -> package, v))
    |> Map.ofList

let readDeclaredNuGet root =
    let path = Path.Combine(root, "Directory.Packages.props")
    if File.Exists path then
        XDocument.Load(path).Descendants(XName.Get "PackageVersion")
        |> Seq.choose (fun e ->
            match e.Attribute(XName.Get "Include"), e.Attribute(XName.Get "Version") with
            | null, _
            | _, null -> None
            | i, v -> Some(i.Value, v.Value))
        |> List.ofSeq
    else
        []

let readRestoredNuGet root =
    let assets =
        [ "src"; "tests" ]
        |> List.map (fun d -> Path.Combine(root, d))
        |> List.filter Directory.Exists
        |> List.collect (fun d -> Directory.GetFiles(d, "project.assets.json", SearchOption.AllDirectories) |> List.ofArray)
    let libraries =
        assets
        |> List.choose tryReadJson
        |> List.collect (fun a ->
            a |> prop "libraries" |> Option.map objectEntries |> Option.defaultValue [] |> List.map fst)
        |> List.choose (fun key ->
            match key.Split '/' with
            | [| name; version |] -> Some(name.ToLowerInvariant(), version)
            | _ -> None)
    assets.Length, libraries |> List.groupBy fst |> List.map (fun (k, vs) -> k, List.map snd vs) |> Map.ofList

let readLockedSdk root =
    tryReadJson (Path.Combine(root, "global.json")) |> Option.bind (prop "sdk") |> Option.bind (prop "version") |> Option.bind str

let readInstalledSdk root =
    let result = run root "dotnet" [ "--version" ]
    if result.ExitCode = 0 then Some(result.Output.Trim()) else None

let readLockInputs (root: string) : LockInputs =
    let lockedNpm = readLockedNpm root
    let declaredNpm = readDeclaredNpm root
    let restoredProjects, restoredNuGet = readRestoredNuGet root
    { ConditorComponents = readConditor root
      LockedNpm = lockedNpm
      DeclaredNpm = declaredNpm
      InstalledNpm =
        readInstalledNpm root ((lockedNpm @ declaredNpm |> List.map fst) @ (componentPackages |> List.map snd) |> List.distinct)
      DeclaredNuGet = readDeclaredNuGet root
      RestoredNuGet = restoredNuGet
      RestoredProjects = restoredProjects
      LockedSdk = readLockedSdk root
      InstalledSdk = readInstalledSdk root }

let verdictText =
    function
    | Pass -> "PASS"
    | Fail -> "FAIL"
    | Blocked -> "BLOCKED"

let reportJson (root: string) (checks: Check list) : string =
    let options = JsonWriterOptions(Indented = true)
    use stream = new MemoryStream()
    (
        use w = new Utf8JsonWriter(stream, options)
        w.WriteStartObject()
        w.WriteString("gate", "VER-008 stack lock")
        w.WriteString("root", Path.GetFullPath root)
        w.WriteString("checkedAt", DateTimeOffset.UtcNow.ToString "O")
        w.WriteString("verdict", (if passed checks then "PASS" else "FAIL"))
        w.WriteStartArray "checks"
        checks
        |> List.iter (fun c ->
            w.WriteStartObject()
            w.WriteString("item", c.Item)
            w.WriteString("expected", c.Expected)
            w.WriteString("installed", c.Installed)
            w.WriteString("verdict", verdictText c.Verdict)
            w.WriteEndObject())
        w.WriteEndArray()
        w.WriteEndObject()
    )
    Text.Encoding.UTF8.GetString(stream.ToArray())

let verifyLock (root: string) (report: string option) : int =
    let checks = evaluate (readLockInputs root)
    checks |> List.iter (fun c -> printfn "%-7s %s: lock %s, installed %s" (verdictText c.Verdict) c.Item c.Expected c.Installed)
    report |> Option.iter (fun path -> File.WriteAllText(path, reportJson root checks))
    if passed checks then
        printfn "Stack lock gate: PASS (%d checks)" checks.Length
        0
    else
        eprintfn "Stack lock gate: FAIL. Installed artifacts differ from the run lock or are missing (VER-008)."
        1

// ---------------------------------------------------------------------------------------------------------------
// Site assembly
// ---------------------------------------------------------------------------------------------------------------

/// The locked browser assets the site serves, as paths relative to node_modules/.
let siteNodeAssets =
    [ "@echelon-foundry/limen/dist"
      "@echelon-foundry/design-system/dist"
      "@echelon-foundry/print-components/src"
      // Forma workflow 1.2.0 (DEV-R8-05, CR-T24-01): its dist/ includes the engine/ WebAssembly files.
      "@echelon-foundry/forma-workflow/dist" ]

let rec copyDirectory (source: string) (target: string) =
    Directory.CreateDirectory target |> ignore
    Directory.GetFiles source |> Array.iter (fun f -> File.Copy(f, Path.Combine(target, Path.GetFileName f), true))
    Directory.GetDirectories source |> Array.iter (fun d -> copyDirectory d (Path.Combine(target, Path.GetFileName d)))

let site (root: string) : int =
    let dist = Path.Combine(root, "dist")
    let publish = Path.Combine(root, "artifacts", "host-publish")
    [ dist; publish ] |> List.filter Directory.Exists |> List.iter (fun d -> Directory.Delete(d, true))
    let published =
        runEcho root "dotnet" [ "publish"; "src/Causa.Host/Causa.Host.csproj"; "-c"; "Release"; "-o"; publish; "--nologo" ]
    let missing = siteNodeAssets |> List.filter (fun a -> not (Directory.Exists(Path.Combine(root, "node_modules", a))))
    match published.ExitCode, missing with
    | 0, [] ->
        copyDirectory (Path.Combine(root, "web")) dist
        copyDirectory (Path.Combine(publish, "wwwroot", "_framework")) (Path.Combine(dist, "_framework"))
        siteNodeAssets |> List.iter (fun a -> copyDirectory (Path.Combine(root, "node_modules", a)) (Path.Combine(dist, "node_modules", a)))
        File.WriteAllText(Path.Combine(dist, ".nojekyll"), "")
        printfn "Site assembled in %s" dist
        0
    | 0, assets ->
        eprintfn "Run npm ci first; missing node_modules assets: %s" (String.Join(", ", assets))
        1
    | code, _ -> code

// ---------------------------------------------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------------------------------------------

let test (root: string) : int =
    let result = runEcho root "dotnet" [ "test"; "Causa.slnx"; "-c"; "Release" ]
    let passedAny = Text.RegularExpressions.Regex.IsMatch(result.Output, @"Passed:\s*[1-9]")
    let skippedAny = Text.RegularExpressions.Regex.IsMatch(result.Output, @"Skipped:\s*[1-9]")
    match result.ExitCode, passedAny, skippedAny with
    | 0, true, false -> 0
    | 0, false, _ ->
        eprintfn "No test passed. An empty test run is not a pass."
        1
    | 0, _, true ->
        eprintfn "Tests were skipped. A run that skips tests is not a pass."
        1
    | code, _, _ -> code

// ---------------------------------------------------------------------------------------------------------------
// Entry
// ---------------------------------------------------------------------------------------------------------------

let rec option (name: string) (args: string list) : string option =
    match args with
    | n :: v :: _ when n = name -> Some v
    | _ :: rest -> option name rest
    | [] -> None

let args = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray
let here = __SOURCE_DIRECTORY__

let exitCode =
    match args with
    | "site" :: _ -> site here
    | "verify-lock" :: rest -> verifyLock (option "--root" rest |> Option.defaultValue here) (option "--report" rest)
    | "test" :: _ -> test here
    | _ ->
        eprintfn "usage: dotnet fsi build.fsx (site | verify-lock [--root DIR] [--report FILE] | test)"
        2

exit exitCode
