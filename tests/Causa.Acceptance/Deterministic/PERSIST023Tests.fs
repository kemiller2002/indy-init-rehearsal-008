// Owner: domain. WI-R8-DTESTS: PERSIST-023 (truthful save state is structural) and the PAT-12 model of SM-SAVE that
// PERSIST-023 and PERSIST-051 share. The model is written from the requirement and the SM-SAVE contract
// (requirements-compiled/v4.1.1/state-machine-contracts.json, transcribed below), not from the implementation: it
// drives the real SaveProcess through every (state, input) pair and compiles application-side code against the real
// Causa.Persistence assembly to show that Saved cannot be forged.
namespace Causa.Acceptance.Deterministic

open System
open System.Diagnostics
open System.IO
open Microsoft.FSharp.Reflection
open Xunit
open Causa.Domain
open Causa.Persistence

/// PAT-12: the SM-SAVE model shared by PERSIST023Tests and PERSIST051Tests. Pure except for the compile check, which
/// runs the F# compiler as a child process at the edge.
module Pat12 =
    let private guid (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)
    let investigation = InvestigationId(guid 1)
    let head (n: int) = CommitOid(sprintf "%040x" n)

    let firstEvent: EventEnvelope =
        { EventId = EventId(guid 1001)
          Investigation = investigation
          Version = AggregateVersion 1L
          Payload = EventPayload "InvestigationStarted" }

    let verifiedAt (n: int) : VerifiedInvestigation =
        { Investigation = { Id = investigation; Version = AggregateVersion 1L }
          History = [ firstEvent ]
          ReadAt = head n
          Bytes = StorageV1.encodeHistory investigation [ firstEvent ] }

    let command (n: int) : CommandEnvelope =
        { CommandId = guid (5000 + n)
          Investigation = investigation
          ExpectedVersion = None }

    let private connection: ConnectionContext =
        { Repository = { Owner = "causa-test"; Name = "data"; Branch = "main" }
          Credential = Credential.create "pat12-test-token-not-a-credential" }

    let context (busy: bool) : RequestContext =
        { Connection = connection
          NextId = RequestId 40L
          MutationInFlightElsewhere = busy }

    let private machine (knownOffline: bool) : SaveMachine =
        let tab = guid 9

        SaveProcess.create
            { Codec = StorageV1.codec
              FileSetOf = SaveProcess.pendingCommandFiles
              Tab = tab
              NextMarker = Markers.forTab tab
              KnownOffline = knownOffline }

    let online = machine false
    let offline = machine true

    /// A confirmation built the only way there is: bytes read back at the commit equal to the bytes written.
    let confirmation (n: int) : Confirmation.T =
        let files = StorageV1.encodeHistory investigation [ firstEvent ]

        Confirmation.ofReadBack investigation (head n) DateTimeOffset.UnixEpoch files files
        |> Option.defaultWith (fun () -> failwith "identical bytes must confirm")

    let private remote = verifiedAt 21
    let private conf = confirmation 30
    let private start = SaveProcess.initial (verifiedAt 1)

    let private inState (state: SaveState) = { start with State = state; Unsaved = [ command 1 ] }

    /// The contract's state and input names, in the contract's order (PERSIST-031: exactly seven states).
    let stateNames = [ "Saved"; "Unsaved"; "Saving"; "Offline"; "Conflict"; "UnknownOutcome"; "SaveFailed" ]

    let inputNames =
        [ "Edit"; "SaveRequested"; "MutationSent"; "ReadBackMatched"; "ReadBackMismatched"; "StaleHeadUnrelated"
          "StaleHeadSameInvestigation"; "ProviderRefused"; "CredentialRefused"; "AnswerLost"; "FailedBeforeSending"
          "ReconcileMarkerBytesMatch"; "ReconcileMarkerBytesDiffer"; "ReconcileNothingWrittenHeadUnchanged"
          "ReconcileNothingWrittenHeadMoved"; "ReconcileHistoryRewritten"; "ReconcileReadFailed"; "ReconcileReadsSpent"
          "CheckAgain"; "WentOffline"; "WentOnline"; "ConflictResolvedReapplied"; "ConflictResolvedDiscarded"
          "SupersededReply"; "QueuedSaveStarted"; "RebaseReadFailed"; "ReadBackReadFailed" ]

    /// The model's name for a state: a total match, so a state added to SaveState does not compile here unnamed.
    let nameOfState (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "UnknownOutcome"
        | SaveState.SaveFailed _ -> "SaveFailed"

    /// One value of every input (checked against the union by reflection in enumerationProblems).
    let inputOf (name: string) : SaveInput =
        match name with
        | "Edit" -> SaveInput.Edit(command 2)
        | "SaveRequested" -> SaveInput.SaveRequested
        | "MutationSent" -> SaveInput.MutationSent(RequestId 7L)
        | "ReadBackMatched" -> SaveInput.ReadBackMatched conf
        | "ReadBackMismatched" -> SaveInput.ReadBackMismatched(head 3)
        | "StaleHeadUnrelated" -> SaveInput.StaleHeadUnrelated(head 3)
        | "StaleHeadSameInvestigation" -> SaveInput.StaleHeadSameInvestigation(head 3)
        | "ProviderRefused" -> SaveInput.ProviderRefused(PermissionDenied "write")
        | "CredentialRefused" -> SaveInput.CredentialRefused
        | "AnswerLost" -> SaveInput.AnswerLost
        | "FailedBeforeSending" -> SaveInput.FailedBeforeSending
        | "ReconcileMarkerBytesMatch" -> SaveInput.ReconcileMarkerBytesMatch conf
        | "ReconcileMarkerBytesDiffer" -> SaveInput.ReconcileMarkerBytesDiffer(head 3)
        | "ReconcileNothingWrittenHeadUnchanged" -> SaveInput.ReconcileNothingWrittenHeadUnchanged
        | "ReconcileNothingWrittenHeadMoved" -> SaveInput.ReconcileNothingWrittenHeadMoved(head 3)
        | "ReconcileHistoryRewritten" -> SaveInput.ReconcileHistoryRewritten
        | "ReconcileReadFailed" -> SaveInput.ReconcileReadFailed
        | "ReconcileReadsSpent" -> SaveInput.ReconcileReadsSpent
        | "CheckAgain" -> SaveInput.CheckAgain
        | "WentOffline" -> SaveInput.WentOffline
        | "WentOnline" -> SaveInput.WentOnline
        | "ConflictResolvedReapplied" -> SaveInput.ConflictResolvedReapplied [ command 1 ]
        | "ConflictResolvedDiscarded" -> SaveInput.ConflictResolvedDiscarded remote
        | "SupersededReply" -> SaveInput.SupersededReply(RequestId 99L)
        | "QueuedSaveStarted" -> SaveInput.QueuedSaveStarted
        | "RebaseReadFailed" -> SaveInput.RebaseReadFailed
        | "ReadBackReadFailed" -> SaveInput.ReadBackReadFailed
        | other -> invalidArg "name" $"not an SM-SAVE input: {other}"

    /// Sessions in each state: the guard-relevant variants (changes made during the save, rebase and mutation limits,
    /// automatic reads left or spent, each conflict view), so every guarded transition of the contract is reachable.
    let sessionsIn (name: string) : InvestigationSession list =
        let saving = { inState SaveState.Saving with Outstanding = Some(RequestId 7L, MutationAnswer); Mutations = 1 }
        let unknown reads left = { inState (SaveState.UnknownOutcome left) with ReconcileReads = reads; Mutations = 1 }

        match name with
        | "Saved" -> [ start; { start with State = SaveState.Saved(Confirmed conf) } ]
        | "Unsaved" -> [ inState SaveState.Unsaved ]
        | "Saving" ->
            [ saving
              { saving with AfterInFlight = [ command 2 ] }
              { saving with RebaseAttempts = SaveProcess.maxRebases; Mutations = SaveProcess.maxMutations } ]
        | "Offline" -> [ inState SaveState.Offline ]
        | "Conflict" ->
            [ ReadingRemote(RequestId 3L); Reviewable remote; Reapplying remote; Reapplied(remote, []); ConfirmingDiscard remote
              Discarded remote; RemoteUnavailable; Unresolvable "deleted" ]
            |> List.map (SaveState.Conflict >> inState)
        | "UnknownOutcome" ->
            let u = unknown 1 (SaveProcess.maxAutomaticReads - 1)

            [ u
              { u with AfterInFlight = [ command 2 ] }
              { u with Mutations = SaveProcess.maxMutations; RebaseAttempts = SaveProcess.maxRebases }
              unknown SaveProcess.maxAutomaticReads 0 ]
        | "SaveFailed" ->
            [ RepositoryKeptChanging; CredentialNotAccepted; Refused(PermissionDenied "write") ]
            |> List.map (SaveState.SaveFailed >> inState)
        | other -> invalidArg "name" $"not an SM-SAVE state: {other}"

    /// SM-SAVE's legal pairs and, for each, the states its transitions reach (state-machine-contracts.json, SM-SAVE,
    /// counts: 7 states, 27 events, 61 transitions, 189 pairs, 50 legal, 139 illegal).
    let legal: Map<string * string, Set<string>> =
        [ "Saved", "Edit", [ "Unsaved"; "Offline" ] // T-S01, T-S02
          "Saved", "WentOffline", [ "Saved" ] // T-S41
          "Saved", "WentOnline", [ "Saved" ] // T-S42
          "Unsaved", "Edit", [ "Unsaved" ] // T-S03
          "Unsaved", "SaveRequested", [ "Saving"; "Offline"; "Unsaved" ] // T-S04, T-S05, T-S53
          "Unsaved", "WentOffline", [ "Offline" ] // T-S06
          "Unsaved", "WentOnline", [ "Unsaved" ] // T-S43
          "Unsaved", "QueuedSaveStarted", [ "Saving"; "Offline" ] // T-S54, T-S55
          "Saving", "Edit", [ "Saving" ] // T-S10
          "Saving", "MutationSent", [ "Saving" ] // T-S61
          "Saving", "ReadBackMatched", [ "Saved"; "Unsaved" ] // T-S11, T-S12
          "Saving", "ReadBackMismatched", [ "SaveFailed" ] // T-S13
          "Saving", "StaleHeadUnrelated", [ "Saving"; "SaveFailed" ] // T-S14, T-S15
          "Saving", "StaleHeadSameInvestigation", [ "Conflict" ] // T-S16
          "Saving", "ProviderRefused", [ "SaveFailed" ] // T-S17
          "Saving", "CredentialRefused", [ "SaveFailed" ] // T-S18
          "Saving", "AnswerLost", [ "UnknownOutcome" ] // T-S19
          "Saving", "FailedBeforeSending", [ "Offline" ] // T-S20
          "Saving", "WentOffline", [ "Saving" ] // T-S45
          "Saving", "WentOnline", [ "Saving" ] // T-S46
          "Saving", "SupersededReply", [ "Saving" ] // T-S39
          "Saving", "RebaseReadFailed", [ "Offline"; "UnknownOutcome" ] // T-S57, T-S59
          "Saving", "ReadBackReadFailed", [ "UnknownOutcome" ] // T-S58
          "Offline", "Edit", [ "Offline" ] // T-S07
          "Offline", "SaveRequested", [ "Offline" ] // T-S08
          "Offline", "WentOffline", [ "Offline" ] // T-S44
          "Offline", "WentOnline", [ "Unsaved" ] // T-S09
          "Offline", "QueuedSaveStarted", [ "Offline" ] // T-S60
          "Conflict", "Edit", [ "Conflict" ] // T-S36
          "Conflict", "WentOffline", [ "Conflict" ] // T-S48
          "Conflict", "WentOnline", [ "Conflict" ] // T-S49
          "Conflict", "ConflictResolvedReapplied", [ "Unsaved" ] // T-S37
          "Conflict", "ConflictResolvedDiscarded", [ "Saved" ] // T-S38
          "UnknownOutcome", "Edit", [ "UnknownOutcome" ] // T-S33
          "UnknownOutcome", "StaleHeadSameInvestigation", [ "Conflict" ] // T-S27
          "UnknownOutcome", "ReconcileMarkerBytesMatch", [ "Saved"; "Unsaved" ] // T-S21, T-S22
          "UnknownOutcome", "ReconcileMarkerBytesDiffer", [ "SaveFailed" ] // T-S23
          "UnknownOutcome", "ReconcileNothingWrittenHeadUnchanged", [ "Saving"; "SaveFailed" ] // T-S24, T-S25
          "UnknownOutcome", "ReconcileNothingWrittenHeadMoved", [ "Saving"; "SaveFailed" ] // T-S26, T-S56
          "UnknownOutcome", "ReconcileHistoryRewritten", [ "SaveFailed" ] // T-S28
          "UnknownOutcome", "ReconcileReadFailed", [ "UnknownOutcome" ] // T-S29
          "UnknownOutcome", "ReconcileReadsSpent", [ "UnknownOutcome" ] // T-S30
          "UnknownOutcome", "CheckAgain", [ "UnknownOutcome" ] // T-S31
          "UnknownOutcome", "WentOffline", [ "UnknownOutcome" ] // T-S47
          "UnknownOutcome", "WentOnline", [ "UnknownOutcome" ] // T-S32
          "UnknownOutcome", "SupersededReply", [ "UnknownOutcome" ] // T-S40
          "SaveFailed", "Edit", [ "SaveFailed" ] // T-S34
          "SaveFailed", "SaveRequested", [ "Saving"; "Offline" ] // T-S35, T-S52
          "SaveFailed", "WentOffline", [ "SaveFailed" ] // T-S50
          "SaveFailed", "WentOnline", [ "SaveFailed" ] ] // T-S51
        |> List.map (fun (s, i, next) -> (s, i), set next)
        |> Map.ofList

    /// One step of the real machine for one (state, input) pair in one session, online or known offline, with or
    /// without another mutation in flight in the tab.
    type Observation =
        { State: string
          Input: string
          Before: InvestigationSession
          Given: SaveInput
          Result: Result<InvestigationSession * SaveEffect list, SaveFault> }

    let observe (stateName: string) (inputName: string) : Observation list =
        let input = inputOf inputName

        [ for session in sessionsIn stateName do
              for m in [ online; offline ] do
                  for busy in [ false; true ] do
                      yield
                          { State = stateName
                            Input = inputName
                            Before = session
                            Given = input
                            Result = m.Step (context busy) session input } ]

    type Pairs = ((string * string) * Observation list) list

    /// Every (state, input) pair of SM-SAVE, each observed in every session variant.
    let everyPair () : Pairs =
        [ for s in stateNames do
              for i in inputNames do
                  yield (s, i), observe s i ]

    let private caseNames (t: Type) =
        FSharpType.GetUnionCases(t, true) |> Array.map _.Name |> List.ofArray

    /// The model enumerates exactly the machine's states and inputs: no case is missing or invented.
    let enumerationProblems () =
        [ if caseNames typeof<SaveState> <> stateNames then
              yield sprintf "SaveState cases %A are not the model's states %A" (caseNames typeof<SaveState>) stateNames
          if caseNames typeof<SaveInput> <> inputNames then
              yield sprintf "SaveInput cases %A are not the model's inputs %A" (caseNames typeof<SaveInput>) inputNames
          for s in stateNames do
              for session in sessionsIn s do
                  if nameOfState session.State <> s then
                      yield sprintf "a session for %s is in %s" s (nameOfState session.State)
          for i in inputNames do
              let case, _ = FSharpValue.GetUnionFields(inputOf i, typeof<SaveInput>, true)

              if case.Name <> i then
                  yield sprintf "inputOf %s built %s" i case.Name ]

    let private namesPair (pair: string * string) (f: SaveFault) =
        f.Machine = "SM-SAVE" && f.State = fst pair && f.Input = snd pair

    /// Legal pairs: every accepted step reaches a state the contract names for the pair, every refusal is a typed
    /// SM-SAVE fault naming the pair (a guard that does not hold fails closed), and the variants together reach every
    /// state the contract names.
    let legalProblems (pairs: Pairs) =
        [ for (pair, observations) in pairs do
              match legal.TryFind pair with
              | None -> ()
              | Some expected ->
                  let reached =
                      observations
                      |> List.choose (fun o ->
                          match o.Result with
                          | Ok(next, _) -> Some(nameOfState next.State)
                          | Error _ -> None)
                      |> set

                  for r in reached do
                      if not (expected.Contains r) then
                          yield sprintf "%A reached %s; the contract allows %A" pair r expected

                  for e in expected do
                      if not (reached.Contains e) then
                          yield sprintf "%A never reached %s (contract transition not realised)" pair e

                  for o in observations do
                      match o.Result with
                      | Error f when not (namesPair pair f) -> yield sprintf "%A refused with a fault naming another pair: %A" pair f
                      | _ -> () ]

    /// Illegal pairs: every one fails closed with a typed fault naming the machine, the state and the input, in every
    /// session variant (PERSIST-023-O4). Error carries no effect and no new session, so state, base head and unsaved
    /// changes stay those the caller holds.
    let illegalProblems (pairs: Pairs) =
        [ for (pair, observations) in pairs do
              if not (legal.ContainsKey pair) then
                  for o in observations do
                      match o.Result with
                      | Ok(next, effects) -> yield sprintf "illegal %A was accepted: %s with %A" pair (nameOfState next.State) effects
                      | Error f when not (namesPair pair f) -> yield sprintf "illegal %A faulted as %A" pair f
                      | Error f when String.IsNullOrWhiteSpace f.Reason -> yield sprintf "illegal %A faulted without a reason" pair
                      | Error _ -> () ]

    /// Saved is reached only from a read-back confirmation (ReadBackMatched, ReconcileMarkerBytesMatch: the very
    /// confirmation given) or, by owner decision ODR-V4-08, from a verified read after a confirmed discard
    /// (ConflictResolvedDiscarded: the very verified read given). A step that stays in Saved keeps its basis.
    let savedProblems (pairs: Pairs) =
        [ for (pair, observations) in pairs do
              for o in observations do
                  match o.Result with
                  | Ok(next, _) ->
                      match next.State, o.Given, o.Before.State with
                      | SaveState.Saved(Confirmed c), (SaveInput.ReadBackMatched given | SaveInput.ReconcileMarkerBytesMatch given), _ when c = given -> ()
                      | SaveState.Saved(VerifiedAt v), SaveInput.ConflictResolvedDiscarded given, _ when v = given -> ()
                      | SaveState.Saved basis, _, SaveState.Saved before when basis = before -> ()
                      | SaveState.Saved basis, _, _ -> yield sprintf "%A reached Saved (%A) without a read-back confirmation" pair basis
                      | _ -> ()
                  | Error _ -> () ]

    // ---- the compile-time negative check (PERSIST-023-O3, PERSIST-051-O3) ----

    let rec private repositoryRoot (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "the repository root (Causa.slnx) was not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> repositoryRoot d.Parent

    let root = lazy (repositoryRoot (DirectoryInfo AppContext.BaseDirectory))

    type Compiled = { ExitCode: int; Output: string }

    /// Type-checks an F# script against the built Causa.Domain and Causa.Persistence assemblies with the SDK's F#
    /// compiler (dotnet fsi), as application code in another assembly is compiled.
    let compile (name: string) (body: string) : Compiled =
        let dir = Path.Combine(Path.GetTempPath(), "causa-pat12-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let script = Path.Combine(dir, name + ".fsx")

        let references =
            [ typeof<InvestigationId>.Assembly.Location; typeof<SaveState>.Assembly.Location ]
            |> List.distinct
            |> List.map (fun p -> sprintf "#r @\"%s\"" p)
            |> String.concat "\n"

        File.WriteAllText(script, references + "\nopen Causa.Domain\nopen Causa.Persistence\n" + body + "\nprintfn \"PAT12-COMPILED\"\n")

        let dotnet =
            match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
            | null
            | "" -> "dotnet"
            | p -> p

        let info = ProcessStartInfo(dotnet, $"fsi --quiet --nologo \"{script}\"")
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        info.WorkingDirectory <- dir

        use p =
            match Process.Start info with
            | null -> failwith "dotnet fsi could not be started"
            | p -> p

        let stdout = p.StandardOutput.ReadToEndAsync()
        let stderr = p.StandardError.ReadToEndAsync()

        if not (p.WaitForExit 180000) then
            p.Kill true
            failwith "dotnet fsi did not finish within 180 s"

        p.WaitForExit()

        try
            Directory.Delete(dir, true)
        with _ ->
            ()

        { ExitCode = p.ExitCode
          Output = stdout.Result + stderr.Result }

    /// Application code that holds everything a Confirmation records and tries to build one, then Saved from it.
    let forgeries =
        [ "record",
          "let c : Confirmation.T = { Investigation = InvestigationId(System.Guid.Empty); Commit = CommitOid \"0\"; CommittedAt = System.DateTimeOffset.UnixEpoch }\nlet s = SaveState.Saved(Confirmed c)"
          "copyAndUpdate",
          "let f = { Manifest = [||]; Events = [||]; Snapshot = [||] }\nlet c0 = (Confirmation.ofReadBack (InvestigationId(System.Guid.Empty)) (CommitOid \"0\") System.DateTimeOffset.UnixEpoch f f).Value\nlet c : Confirmation.T = { c0 with Commit = CommitOid \"1\" }\nlet s = SaveState.Saved(Confirmed c)" ]

    /// The positive control: the same prelude compiles when it uses the one public constructor, so a forgery's failure
    /// is the access check, not a broken harness.
    let control =
        "let f = { Manifest = [||]; Events = [||]; Snapshot = [||] }\nlet c = Confirmation.ofReadBack (InvestigationId(System.Guid.Empty)) (CommitOid \"0\") System.DateTimeOffset.UnixEpoch f f\nlet s = c |> Option.map (Confirmed >> SaveState.Saved)"

    /// F#'s private-representation errors: FS1093 ("not accessible from this code location") or FS0039 (the private
    /// field labels are not defined outside the module).
    let refusedForAccess (compiled: Compiled) =
        compiled.ExitCode <> 0
        && not (compiled.Output.Contains "PAT12-COMPILED")
        && (compiled.Output.Contains "FS1093" || compiled.Output.Contains "not accessible" || compiled.Output.Contains "FS0039")

    let forgeryProblems =
        lazy
            (let ok = compile "control" control

             [ if ok.ExitCode <> 0 || not (ok.Output.Contains "PAT12-COMPILED") then
                   yield sprintf "the positive control did not compile, so the check proves nothing: %s" ok.Output
               for (name, body) in forgeries do
                   let compiled = compile name body

                   if not (refusedForAccess compiled) then
                       yield sprintf "application code could forge a Confirmation (%s): exit %d\n%s" name compiled.ExitCode compiled.Output ])

    /// Shipped sources outside the persistence module that call the confirmation constructor: adapters, view code and
    /// the App must reach Saved only through the machine (PERSIST-023-O3).
    let constructorCallsOutsidePersistence () =
        let sep = Path.DirectorySeparatorChar
        let src = Path.Combine(root.Force(), "src")
        let allowed = set [ "SaveMachine.fs"; "Session.fs" ]

        Directory.GetFiles(src, "*.fs", SearchOption.AllDirectories)
        |> Array.map string
        |> Array.filter (fun p -> not (p.Contains $"{sep}obj{sep}" || p.Contains $"{sep}bin{sep}"))
        |> Array.filter (fun p -> not (p.Contains $"{sep}Causa.Persistence{sep}" && allowed.Contains(Path.GetFileName(p: string) |> string)))
        |> Array.filter (fun p -> File.ReadAllText(p).Contains "ofReadBack")
        |> List.ofArray

    let assertNone (what: string) (problems: string list) =
        if not (List.isEmpty problems) then
            Assert.Fail(sprintf "%s: %d problem(s)\n%s" what problems.Length (String.concat "\n" (List.truncate 25 problems)))

    let pairs = lazy (everyPair ())

type PERSIST023Tests() =
    /// O1: the save state is computed by one F# machine (SaveState is the only save-state union of the Causa
    /// assemblies, with exactly the machine's states), and the indicator renders only that machine's projection: a
    /// pure function of the session whose label and glyph are those of the machine's state.
    [<Fact>]
    member _.``PERSIST-023-O1``() =
        let assemblies =
            [ typeof<CommandEnvelope>.Assembly; typeof<SaveState>.Assembly; typeof<Causa.App.Projections>.Assembly ]
            |> List.distinct

        let saveStateTypes =
            assemblies
            |> List.collect (fun a -> a.GetTypes() |> List.ofArray)
            // A union case with fields compiles to a nested subclass of its union; the union itself is the type.
            |> List.filter (fun t -> FSharpType.IsUnion(t, true) && not (t.IsNested && t.BaseType = t.DeclaringType))
            |> List.filter (fun t ->
                let names = FSharpType.GetUnionCases(t, true) |> Array.map _.Name |> set
                names.Contains "Saved" || (names.Contains "Saving" && names.Contains "Unsaved"))
            |> List.distinct

        Assert.Equal<Type list>([ typeof<SaveState> ], saveStateTypes)
        Assert.Equal<string list>(Pat12.stateNames, FSharpType.GetUnionCases(typeof<SaveState>, true) |> Array.map _.Name |> List.ofArray)

        for s in Pat12.stateNames do
            for session in Pat12.sessionsIn s do
                let view = Causa.App.Projection.value.SaveIndicator session
                Assert.Equal(view, Causa.App.Projection.value.SaveIndicator session)
                Assert.Equal(Causa.App.SaveIndicatorProjection.label session.State, view.Label)
                Assert.Equal(Causa.App.SaveIndicatorProjection.glyph session.State, view.Glyph)
                Assert.Equal(SaveProcess.stateName session.State, Pat12.nameOfState session.State)

    /// O2: Saved is constructible only from the PERSIST-007 read-back: the confirmation's one constructor returns
    /// nothing unless the bytes re-read at the commit equal the bytes written, and no step of the machine reaches
    /// Saved except from such a confirmation (or the owner-approved verified read after a discard, ODR-V4-08).
    [<Fact>]
    member _.``PERSIST-023-O2``() =
        let written = StorageV1.encodeHistory Pat12.investigation [ Pat12.firstEvent ]
        let at = Pat12.head 5
        let differs = { written with Snapshot = Array.append written.Snapshot [| 0x20uy |] }
        Assert.True(Option.isNone (Confirmation.ofReadBack Pat12.investigation at DateTimeOffset.UnixEpoch written differs))
        Assert.True(Option.isNone (Confirmation.ofReadBack Pat12.investigation at DateTimeOffset.UnixEpoch written { written with Events = [||] }))

        match Confirmation.ofReadBack Pat12.investigation at DateTimeOffset.UnixEpoch written written with
        | Some c ->
            Assert.Equal(at, c.CommitOid)
            Assert.Equal(Pat12.investigation, c.InvestigationId)
        | None -> Assert.Fail "identical read-back bytes did not confirm"

        Pat12.assertNone "Saved reached without a read-back confirmation" (Pat12.savedProblems (Pat12.pairs.Force()))

    /// O3: adapters, view code and test fixtures cannot construct a confirmation: compiling code that builds one
    /// (record expression or copy-and-update) against the production assemblies fails on access while the public
    /// read-back constructor compiles, and no shipped source outside persistence calls that constructor.
    [<Fact>]
    member _.``PERSIST-023-O3``() =
        let t = typeof<Confirmation.T>
        Assert.Empty(t.GetConstructors())
        Assert.Empty(t.GetFields())
        Pat12.assertNone "a confirmation can be forged outside Causa.Persistence" (Pat12.forgeryProblems.Force())
        Assert.Empty(Pat12.constructorCallsOutsidePersistence ())

    /// O4: every illegal input fails closed with a typed fault and leaves the state unchanged, in every session
    /// variant, online and offline.
    [<Fact>]
    member _.``PERSIST-023-O4``() =
        let all = Pat12.pairs.Force()
        let illegal = all |> List.filter (fun (p, _) -> not (Pat12.legal.ContainsKey p))
        Assert.Equal(139, illegal.Length)
        Pat12.assertNone "illegal inputs not failing closed" (Pat12.illegalProblems all)

    /// O5: PAT-12 verifies every state and input pair (189 = 7 x 27, against the contract's 50 legal pairs and their
    /// target states) and verifies that application code cannot construct Saved.
    [<Fact>]
    member _.``PERSIST-023-O5``() =
        let all = Pat12.pairs.Force()
        Pat12.assertNone "the model does not enumerate SM-SAVE" (Pat12.enumerationProblems ())
        Assert.Equal(189, all.Length)
        Assert.Equal(50, Pat12.legal.Count)
        Pat12.assertNone "legal transitions" (Pat12.legalProblems all)
        Pat12.assertNone "illegal pairs" (Pat12.illegalProblems all)
        Pat12.assertNone "Saved without confirmation" (Pat12.savedProblems all)
        Pat12.assertNone "forgery" (Pat12.forgeryProblems.Force())
