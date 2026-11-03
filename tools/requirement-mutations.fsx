// Owner: integration. Adversarial mutation runs of the requirement verification pipeline against the real deployed
// page: each mutation deliberately breaks one requirement in the served site (and its source), runs the requirement
// probes and `dokimos verification requirements`, and is "caught" only when the verifier refuses the scoped
// requirement with the expected status. The baseline (no mutation) must be accepted, or nothing below means anything.
//
//   dotnet fsi tools/requirement-mutations.fsx -- --manifest M --dokimos DLL --out F [--port 4321]
//
// Expects dist/ to be built (dotnet fsi build.fsx site). Starts its own static server for dist/ and restores every
// mutated file afterwards. Exit 0 when the baseline is accepted and every mutation is caught; 1 otherwise.
open System
open System.Diagnostics
open System.IO
open System.Text.Json

let args = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray

let rec option name rest =
    match rest with
    | flag :: value :: _ when flag = name -> Some value
    | _ :: tail -> option name tail
    | [] -> None

let required name =
    option name args |> Option.defaultWith (fun () -> failwith $"missing {name}")

let manifest = Path.GetFullPath(required "--manifest")
let dokimos = Path.GetFullPath(required "--dokimos")
let out = Path.GetFullPath(required "--out")
let port = option "--port" args |> Option.defaultValue "4321"
let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let work = Path.Combine(Path.GetTempPath(), $"causa-mutations-{Guid.NewGuid():N}")

let run (file: string) (arguments: string list) (env: (string * string) list) =
    let info = ProcessStartInfo(file, arguments, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true)
    env |> List.iter (fun (k, v) -> info.Environment[k] <- v)
    // Reuse the server this script starts instead of rebuilding the site for every run.
    info.Environment.Remove "CI" |> ignore
    use p = Process.Start info
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    p.ExitCode, stdout.Result + stderr.Result

let git (arguments: string list) = run "git" arguments [] |> snd |> fun s -> s.Trim()
let commit = git [ "rev-parse"; "HEAD" ]

/// A mutation: the files it rewrites (path, text -> text), the requirement it targets, and the obligation whose
/// status proves it was caught.
type Mutation =
    { Id: string
      Scenario: string
      Requirement: string
      Obligation: string
      Expected: string
      Rewrites: (string * (string -> string)) list }

let field = """<input name="investigation" data-requirement-id="INV-006" data-event="investigation.id">"""

let replace (old: string) (replacement: string) (text: string) =
    if not (text.Contains old) then failwith $"mutation target not found: {old}"
    text.Replace(old, replacement)

/// Drop the one line that contains `marker` (whatever its indentation).
let removeLine (marker: string) (text: string) =
    let lines = text.Split('\n')
    if (lines |> Array.filter (fun l -> l.Contains marker)).Length <> 1 then failwith $"mutation target not found once: {marker}"
    lines |> Array.filter (fun l -> not (l.Contains marker)) |> String.concat "\n"

let both (rewrite: string -> string) = [ "web/index.html", rewrite; "dist/index.html", rewrite ]

let mutations =
    [ { Id = "M1"; Scenario = "remove a required field"; Requirement = "INV-006"; Obligation = "INV-006/V1/exists"; Expected = "FAIL"
        Rewrites = both (replace field "") }
      { Id = "M2"; Scenario = "render the ID on an unrelated hidden element"; Requirement = "INV-006"; Obligation = "INV-006/V1/exists"; Expected = "FAIL"
        Rewrites = both (replace field """<input name="investigation" data-event="investigation.id"><span hidden data-requirement-id="INV-006"></span>""") }
      { Id = "M3"; Scenario = "render the field disabled"; Requirement = "INV-006"; Obligation = "INV-006/V3/editable"; Expected = "FAIL"
        Rewrites = both (replace field """<input name="investigation" data-requirement-id="INV-006" data-event="investigation.id" disabled>""") }
      { Id = "M4"; Scenario = "duplicate an ID in one scope"; Requirement = "INV-004"; Obligation = "INV-004/V1/exists"; Expected = "FAIL"
        Rewrites =
          both (replace """<button type="button" data-requirement-id="INV-014" """ """<button type="button" data-requirement-id="INV-004" data-was="INV-014" """) }
      { Id = "M5"; Scenario = "remove a requirement's test while keeping the requirement"; Requirement = "INV-006"; Obligation = "INV-006/V4/validation"; Expected = "NOT_RUN"
        Rewrites =
          [ ("tests/browser/requirements/investigation.requirements.spec.js",
             removeLine """probe.record("INV-006", "validation", { attempts });""") ] } ]

let restore (saved: (string * string) list) =
    saved |> List.iter (fun (path, text) -> File.WriteAllText(Path.Combine(root, path), text))

// Every file any mutation rewrites, as it is now: restored on every exit, including a kill (SIGTERM raises
// ProcessExit), so an interrupted run never leaves a mutated page or test behind.
let originals =
    mutations |> List.collect (fun m -> m.Rewrites |> List.map fst) |> List.distinct
    |> List.filter (fun path -> File.Exists(Path.Combine(root, path)))
    |> List.map (fun path -> path, File.ReadAllText(Path.Combine(root, path)))

AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> restore originals)
Console.CancelKeyPress.Add(fun _ -> restore originals)

/// One pipeline run: probes then verification scoped to the requirement. Returns (exit code, report).
let pipeline (label: string) (requirement: string) (results: string option -> string) =
    let dir = Path.Combine(work, label)
    let observations = Path.Combine(dir, "observations")
    let playwright = Path.Combine(dir, "playwright.json")
    Directory.CreateDirectory dir |> ignore

    let env =
        [ "REQUIREMENT_MANIFEST", manifest
          "REQUIREMENT_SOURCE_COMMIT", commit
          "REQUIREMENT_RUN_ID", $"mutation-{label}"
          "REQUIREMENT_OBSERVATIONS_DIR", observations
          "PLAYWRIGHT_JSON_OUTPUT_NAME", playwright ]

    let probeExit, _ = run "npx" [ "playwright"; "test"; "--project=chromium"; "tests/browser/requirements"; "--reporter=json" ] env
    let report = Path.Combine(dir, "verification.json")

    let code, text =
        run "dotnet" [ dokimos; "verification"; "requirements"; "--manifest"; manifest; "--observations"; observations; "--results"; results (Some playwright)
                       "--root"; root; "--candidate"; commit; "--requirement"; requirement; "--out"; report ] []

    probeExit, code, (if File.Exists report then File.ReadAllText report else text)

let statusOf (report: string) (obligation: string) =
    try
        use doc = JsonDocument.Parse report
        doc.RootElement.GetProperty("requirements").EnumerateArray()
        |> Seq.collect (fun r -> r.GetProperty("obligations").EnumerateArray())
        |> Seq.tryFind (fun o -> o.GetProperty("id").GetString() = obligation)
        |> Option.map (fun o -> o.GetProperty("status").GetString() |> string)
        |> Option.defaultValue "ABSENT"
    with _ -> "UNREADABLE"

if not (File.Exists(Path.Combine(root, "dist", "index.html"))) then
    failwith "dist/index.html is missing: build the site first (dotnet fsi build.fsx site)"

// The same static server the Playwright config uses, with its request log discarded: an unread redirected pipe
// would fill and stall the server.
let server =
    Process.Start(ProcessStartInfo("sh", [ "-c"; $"exec python3 -m http.server {port} --bind 127.0.0.1 >/dev/null 2>&1" ], WorkingDirectory = root))

let rows =
    try
        Threading.Thread.Sleep 1500
        let stopwatch = Stopwatch.StartNew()
        let _, baselineCode, baselineReport = pipeline "baseline" "INV-006" (Option.get)

        let baseline =
            {| id = "M0"; scenario = "no mutation: the slice is implemented correctly"; requirement = "INV-006"; expectedExit = 0; exit = baselineCode
               caught = baselineCode = 0; status = statusOf baselineReport "INV-006/V4/validation"; seconds = stopwatch.Elapsed.TotalSeconds |}

        let mutated =
            mutations
            |> List.map (fun m ->
                let started = Stopwatch.StartNew()
                let saved = m.Rewrites |> List.map (fun (path, _) -> path, File.ReadAllText(Path.Combine(root, path)))

                try
                    m.Rewrites |> List.iter (fun (path, rewrite) -> File.WriteAllText(Path.Combine(root, path), rewrite (File.ReadAllText(Path.Combine(root, path)))))
                    let _, code, report = pipeline m.Id m.Requirement Option.get
                    let status = statusOf report m.Obligation

                    {| id = m.Id; scenario = m.Scenario; requirement = m.Requirement; expectedExit = 4; exit = code
                       caught = code = 4 && status = m.Expected; status = status; seconds = started.Elapsed.TotalSeconds |}
                finally
                    restore saved)

        // M6: evidence from a test that did not execute is offered as passed: mark the probes' tests skipped.
        let started = Stopwatch.StartNew()
        let skipped (playwright: string option) =
            let path = Option.get playwright
            let edited = Path.Combine(Path.GetDirectoryName path, "playwright-skipped.json")
            File.WriteAllText(edited, (File.ReadAllText path).Replace("\"status\": \"expected\"", "\"status\": \"skipped\""))
            edited

        let _, code, report = pipeline "M6" "INV-006" skipped

        let unexecuted =
            {| id = "M6"; scenario = "mark an unexecuted test as the source of passing evidence"; requirement = "INV-006"; expectedExit = 4; exit = code
               caught = code = 4 && statusOf report "INV-006/V3/enter" = "NOT_RUN"; status = statusOf report "INV-006/V3/enter"; seconds = started.Elapsed.TotalSeconds |}

        baseline :: mutated @ [ unexecuted ]
    finally
        if not server.HasExited then server.Kill true

let allCaught = rows |> List.forall _.caught

let document =
    {| schema = "causa.requirement-mutations/1"
       sourceCommit = commit
       manifest = manifest |> File.ReadAllText |> fun t -> JsonDocument.Parse(t).RootElement.GetProperty("contentHash").GetString()
       accepted = allCaught
       runs = rows |}

Directory.CreateDirectory(Path.GetDirectoryName out) |> ignore
File.WriteAllText(out, JsonSerializer.Serialize(document, JsonSerializerOptions(WriteIndented = true)) + "\n")

for r in rows do
    printfn "%s %-7s %s %s exit %d status %s (%.1fs)" r.id (if r.caught then "CAUGHT" else "MISSED") r.requirement r.scenario r.exit r.status r.seconds

exit (if allCaught then 0 else 1)
