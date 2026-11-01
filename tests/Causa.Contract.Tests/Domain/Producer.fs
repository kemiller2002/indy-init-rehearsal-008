// Owner: domain. IF41-DOMAIN producer contract tests (T14, T13). FrozenFixturesReplay is completed in T15.
module Causa.Contract.Tests.Domain.Producer

open System
open Xunit
open Causa.Domain

let private investigation = InvestigationId(Guid.Parse "00000000-0000-0000-0000-0000000000a1")

let private eventAt (version: int64) : EventEnvelope =
    { EventId = EventId(Guid(int version, 0s, 0s, Array.zeroCreate 8))
      Investigation = investigation
      Version = AggregateVersion version
      Payload = EventPayload "contract-fixture" }

let private historyOf (versions: int64 list) = versions |> List.map eventAt

let private genesis = AggregateVersion 1L

/// The law every Replay must satisfy for the replay preconditions: a refused history names the first version
/// that breaks the run. Shared with T13, which applies it to the kernel's Replay as well.
let replayRefusesGapsLaw (refuse: EventEnvelope list -> Result<'a, ReplayError>) =
    let refusedWith versions =
        match refuse (historyOf versions) with
        | Ok _ -> None
        | Error error -> Some error

    // Negative cases: forged/illegal histories (REHEARSAL-AGENT-INTERFACE-FREEZE, mandatory negative cases).
    Assert.Equal(Some EmptyHistory, refusedWith [])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 3L, AggregateVersion 4L)), refusedWith [ 1L; 2L; 4L ])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 1L, AggregateVersion 2L)), refusedWith [ 2L; 3L ])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 3L, AggregateVersion 2L)), refusedWith [ 1L; 2L; 2L ])
    Assert.Equal(Some(NonContiguousVersion(AggregateVersion 2L, AggregateVersion 3L)), refusedWith [ 1L; 3L; 2L ])

[<Fact>]
let ReplayRefusesGaps () =
    replayRefusesGapsLaw (ReplayOrder.contiguousFrom genesis)

    // Positive control: a contiguous history passes through unchanged, so the refusals above are not vacuous.
    let contiguous = historyOf [ 1L; 2L; 3L ]
    Assert.Equal(Ok contiguous, ReplayOrder.contiguousFrom genesis contiguous)

[<Fact>]
let KernelReplayRefusesGaps () =
    // T13: the same law applied to the kernel's Replay (genesis = version 1; contiguity is checked before any event).
    replayRefusesGapsLaw Kernel.domain.Replay

let private policy: PolicySet = { Id = Policies.defaultPolicyId }
let private actor: ActorContext = { ActorId = "contract-tests" }
let private commandId (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)

let private envelope (n: int) (expected: AggregateVersion option) : CommandEnvelope =
    { CommandId = commandId n
      Investigation = investigation
      ExpectedVersion = expected }

let private isDecision (decision: Decision) =
    match decision with
    | Allowed _
    | Blocked _
    | RequiresReview _
    | UnsupportedSemantics _
    | Conflict _
    | Duplicate _ -> true

/// Histories at every lifecycle state, built only from allowed decisions.
let private historyAfter (commands: Command list) =
    commands
    |> List.indexed
    |> List.fold
        (fun history (i, command) ->
            let summary = Kernel.replay history |> Result.toOption
            let env = envelope (200 + i) (summary |> Option.map _.Version)

            match Kernel.decideOn policy actor history { Envelope = env; Command = command } with
            | Allowed payloads -> history @ Kernel.stamp env summary payloads
            | _ -> history)
        []

let private open' = [ StartInvestigation; RecordObservation; ActivateInvestigation ]

let private histories =
    [ []
      [ StartInvestigation ]
      open'
      open' @ [ MarkAwaitingEvidence ]
      open' @ [ MarkAwaitingVerification ]
      open' @ [ RecordConclusion; ResolveInvestigation ]
      open' @ [ RecordConclusion; ResolveInvestigation; CloseInvestigation ]
      open' @ [ ArchiveInvestigation ] ]
    |> List.map historyAfter

[<Fact>]
let DecideIsTotalOverCommands () =
    let actors = [ actor; { ActorId = "" } ]
    let policies = [ policy; { Id = "unknown-pack" } ]

    for history in histories do
        let summary = Kernel.replay history |> Result.toOption
        let versions = [ None; Some(AggregateVersion 0L); summary |> Option.map _.Version; Some(AggregateVersion 99L) ]

        for p in policies do
            for a in actors do
                for expected in versions do
                    // The frozen Decide over a bare envelope, and the full decision for every command.
                    Assert.True(isDecision (Kernel.domain.Decide p a summary (envelope 1 expected)))

                    for command in Command.all do
                        let decision = Kernel.decideOn p a history { Envelope = envelope 1 expected; Command = command }
                        Assert.True(isDecision decision)

                        // Every Allowed decision evolves: decide never proposes an event Evolve refuses.
                        match decision with
                        | Allowed payloads ->
                            let after = history @ Kernel.stamp (envelope 1 expected) summary payloads
                            Assert.True(Result.isOk (Kernel.domain.Replay after), sprintf "%A allowed but does not replay" command)
                        | _ -> ()

[<Fact>]
let DecideNegatives () =
    // G2 negatives: stale aggregate version, duplicate submission, unauthorized command.
    let history = historyAfter open' // versions 1..3 from commands 200..202
    let current = AggregateVersion 3L

    Assert.Equal(
        Conflict(AggregateVersion 2L, current),
        Kernel.decideOn policy actor history { Envelope = envelope 300 (Some(AggregateVersion 2L)); Command = RecordObservation }
    )

    Assert.Equal(Conflict(AggregateVersion 2L, current), Kernel.domain.Decide policy actor (Some { Id = investigation; Version = current }) (envelope 300 (Some(AggregateVersion 2L))))

    Assert.Equal(
        Duplicate [ EventIds.forCommand (commandId 201) 0 ],
        Kernel.decideOn policy actor history { Envelope = envelope 201 (Some(AggregateVersion 1L)); Command = RecordObservation }
    )

    let unauthorized =
        [ Kernel.decideOn policy { ActorId = "" } history { Envelope = envelope 301 (Some current); Command = RecordObservation }
          Kernel.domain.Decide policy { ActorId = "" } None (envelope 302 None) ]

    unauthorized
    |> List.iter (fun decision ->
        match decision with
        | Blocked blockers -> Assert.Contains(blockers, fun b -> b.Code = "unauthorized")
        | other -> Assert.Fail(sprintf "unauthorized command decided %A" other))

// ----- T15: FrozenFixturesReplay (STORAGE-007, EXT-DATA-011, ADRP-005, CT-003/CT-006).
// The frozen data is read from a checkout of kemiller2002/causa-data at tag causa-storage-v1.0.0, named by the
// environment variable CAUSA_DATA_ROOT. Nothing is vendored into this repository (EXT-DATA-011-O1: a reader/writer
// specification, not its demonstration records), and nothing is written under the checkout (ADRP-005-O2).
module FrozenData =
    open System.IO
    open System.Security.Cryptography
    open System.Text.Json

    /// The release identities (docs/CAUSA-STORAGE-RELEASE-v1.0.0.md). A mismatch is a FAIL (STORAGE-007).
    let identities =
        [ "schema", "contract/v1.0.0/schemas/bundle.json", "0a68af8c93190d2e4d6c27beb2f8cb67c3aebdda586d01beb7122b71af179152"
          "dataset", "fixtures/v1.0.0/manifest.json", "f650a2e509a420872b46177b21f57ef6ebd7625ae4c69dd92198d3ca472c2b86"
          "release", "compatibility/v1.0.0/lock.json", "6a7e7802ff2a3ff500ab6065662514a96e09e6c15eb30212ba1377fcc5bf8fc5" ]

    let rootVariable = "CAUSA_DATA_ROOT"

    let root () : string option =
        match Environment.GetEnvironmentVariable rootVariable with
        | null
        | "" -> None
        | path -> Some path

    let sha256Hex (bytes: byte[]) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let fileSha root (relative: string) = sha256Hex (File.ReadAllBytes(Path.Combine(root, relative)))

    /// CONTRACT.md §2.11: a JSON document (other than events.ndjson, schemas and bundle.json) is hashed over its
    /// canonical text without the trailing LF. The frozen files are canonical text plus exactly one LF (§2.2), so the
    /// digest is over the bytes before that LF; a file without its single final LF is reported, never digested.
    let canonicalSha root (relative: string) : Result<string, string> =
        let bytes = File.ReadAllBytes(Path.Combine(root, relative))
        let n = bytes.Length

        if n >= 2 && bytes[n - 1] = 10uy && bytes[n - 2] <> 10uy then
            Ok(sha256Hex bytes[.. n - 2])
        else
            Error(sprintf "%s does not end with exactly one LF" relative)

    /// Every seed file (fixtures and golden states) with its digest, for the unchanged-after-run check.
    let seedDigests root : Map<string, string> =
        [ "fixtures/v1.0.0"; "contract/v1.0.0/golden" ]
        |> List.collect (fun dir -> Directory.GetFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories) |> List.ofArray)
        |> List.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), sha256Hex (File.ReadAllBytes path))
        |> Map.ofList

    let private str (e: JsonElement) (name: string) : string option =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Option.ofObj (v.GetString())
        | _ -> None

    let private int64Of (e: JsonElement) (name: string) : int64 option =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt64() with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    let private guidOf (e: JsonElement) name =
        str e name
        |> Option.bind (fun s ->
            match Guid.TryParse s with
            | true, g -> Some g
            | _ -> None)

    /// The frozen envelope fields of one events.ndjson line projected onto IF41-DOMAIN's EventEnvelope, with the
    /// contract's own names (eventId, investigationId, aggregateVersion, eventType; CONTRACT.md). Only the domain
    /// kernel's input is built here: verifying bytes, schema and digests is the storage codec's job (IF41-STORAGE).
    let envelopeOf (line: string) : Result<EventEnvelope, string> =
        try
            use doc = JsonDocument.Parse line
            let e = doc.RootElement

            match guidOf e "eventId", guidOf e "investigationId", int64Of e "aggregateVersion", str e "eventType" with
            | Some eventId, Some investigation, Some version, Some kind ->
                Ok
                    { EventId = EventId eventId
                      Investigation = InvestigationId investigation
                      Version = AggregateVersion version
                      Payload = EventPayload kind }
            | _ -> Error "an event line lacks eventId, investigationId, aggregateVersion or eventType"
        with :? JsonException as ex ->
            Error ex.Message

    let historyOf (ndjson: string) : Result<EventEnvelope list, string> =
        ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
        |> List.fold (fun acc line -> acc |> Result.bind (fun h -> envelopeOf line |> Result.map (fun e -> e :: h))) (Ok [])
        |> Result.map List.rev

    /// A JSON element as the kernel's v1 value (CONTRACT.md §2.3: integers only; anything else is not a v1 value).
    let rec valueOf (e: JsonElement) : Result<V1Value, string> =
        let all (items: Result<'a, string> list) =
            List.foldBack (fun item acc -> Result.bind (fun xs -> Result.map (fun x -> x :: xs) item) acc) items (Ok [])

        match e.ValueKind with
        | JsonValueKind.Null -> Ok VNull
        | JsonValueKind.True -> Ok(VBool true)
        | JsonValueKind.False -> Ok(VBool false)
        | JsonValueKind.String -> Ok(VText(e.GetString() |> string))
        | JsonValueKind.Number ->
            match e.TryGetInt64() with
            | true, n -> Ok(VInt n)
            | _ -> Error(sprintf "non-integer number %s" (e.GetRawText()))
        | JsonValueKind.Array -> e.EnumerateArray() |> List.ofSeq |> List.map valueOf |> all |> Result.map VArray
        | JsonValueKind.Object ->
            e.EnumerateObject()
            |> List.ofSeq
            |> List.map (fun p -> valueOf p.Value |> Result.map (fun v -> p.Name, v))
            |> all
            |> Result.map VObject
        | kind -> Error(sprintf "unexpected JSON %A" kind)

    /// One events.ndjson line as the kernel's decoded v1 event (CONTRACT.md §5).
    let v1EventOf (line: string) : Result<V1Event, string> =
        try
            use doc = JsonDocument.Parse line
            let e = doc.RootElement

            match envelopeOf line, str e "occurredAt", guidOf e "correlationId", e.TryGetProperty "actor", e.TryGetProperty "payload" with
            | Ok envelope, Some occurredAt, Some correlation, (true, actor), (true, payload) ->
                match valueOf actor, valueOf payload with
                | Ok actor, Ok payload ->
                    let (EventPayload eventType) = envelope.Payload

                    Ok
                        { EventId = envelope.EventId
                          Investigation = envelope.Investigation
                          Version = envelope.Version
                          EventType = eventType
                          OccurredAt = occurredAt
                          Actor = actor
                          CorrelationId = correlation
                          Payload = payload }
                | Error reason, _
                | _, Error reason -> Error reason
            | Error reason, _, _, _, _ -> Error reason
            | _ -> Error "an event line lacks occurredAt, correlationId, actor or payload"
        with :? JsonException as ex ->
            Error ex.Message

    let v1HistoryOf (ndjson: string) : Result<V1Event list, string> =
        ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
        |> List.fold (fun acc line -> acc |> Result.bind (fun h -> v1EventOf line |> Result.map (fun e -> e :: h))) (Ok [])
        |> Result.map List.rev

    let lifecycleName (lifecycle: InvestigationLifecycle) =
        match lifecycle with
        | Draft -> "draft"
        | Active -> "active"
        | AwaitingEvidence -> "awaitingEvidence"
        | AwaitingVerification -> "awaitingVerification"
        | Resolved -> "resolved"
        | Closed -> "closed"
        | Archived -> "archived"

    let json root (relative: string) =
        use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)))
        doc.RootElement.Clone()

    let arrayLength (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array -> v.GetArrayLength()
        | _ -> -1

    let text (e: JsonElement) name = str e name
    let number (e: JsonElement) name = int64Of e name

    /// The frozen error codes whose refusal is the domain kernel's (replay preconditions, the engine's history
    /// admissibility, a stale expected version). Every other code (bytes, encoding, JSON, schema, canonical form,
    /// digests, manifest, paths, snapshot, event-id derivation, correlation, rewritten history) is refused by the
    /// storage codec and reconciliation (IF41-STORAGE Verify, persistence T20), not by Replay.
    let domainCodes =
        set
            [ "CAUSA.HISTORY_INADMISSIBLE"
              "CAUSA.SEQUENCE_INVALID"
              "CAUSA.UNKNOWN_EVENT_TYPE"
              "CAUSA.INVESTIGATION_MISMATCH"
              "CAUSA.EVENT_ID_DUPLICATE"
              "CAUSA.VERSION_STALE" ]


[<Fact>]
let FrozenFixturesReplay () =
    match FrozenData.root () with
    | None ->
        // STORAGE-007: a missing check is BLOCKED/UNKNOWN, never PASS.
        Assert.Fail(
            sprintf
                "BLOCKED: %s is not set. Check out kemiller2002/causa-data at tag causa-storage-v1.0.0 and set %s to it; the frozen fixtures are not vendored (EXT-DATA-011)."
                FrozenData.rootVariable
                FrozenData.rootVariable
        )
    | Some root ->
        let seedBefore = FrozenData.seedDigests root

        // 1. The three identities (STORAGE-007).
        let identityFailures =
            FrozenData.identities
            |> List.choose (fun (name, path, expected) ->
                let actual = FrozenData.fileSha root path
                if actual = expected then None else Some(sprintf "%s identity %s: sha256 %s, released %s" name path actual expected))

        Assert.True(List.isEmpty identityFailures, String.concat "\n" identityFailures)

        // 2. Seed integrity: every frozen investigation's files match the dataset manifest's digests.
        let manifest = FrozenData.json root "fixtures/v1.0.0/manifest.json"
        let investigations = manifest.GetProperty("investigations").EnumerateArray() |> List.ofSeq
        Assert.Equal(5, investigations.Length)

        let seedFailures =
            investigations
            |> List.collect (fun entry ->
                let id = FrozenData.text entry "investigationId" |> Option.defaultValue "?"
                let dir = sprintf "fixtures/v1.0.0/investigations/%s" id

                // eventsSha256 covers the file bytes; manifestSha256 and snapshotSha256 are canonical digests (§2.11, §4).
                [ "events.ndjson", "eventsSha256", Ok(FrozenData.fileSha root (sprintf "%s/events.ndjson" dir))
                  "snapshot.json", "snapshotSha256", FrozenData.canonicalSha root (sprintf "%s/snapshot.json" dir)
                  "manifest.json", "manifestSha256", FrozenData.canonicalSha root (sprintf "%s/manifest.json" dir) ]
                |> List.choose (fun (file, field, actual) ->
                    match actual, FrozenData.text entry field with
                    | Ok actual, Some expected when expected = actual -> None
                    | Ok actual, expected -> Some(sprintf "%s/%s: sha256 %s, manifest %A" dir file actual expected)
                    | Error reason, _ -> Some reason))

        Assert.True(List.isEmpty seedFailures, String.concat "\n" seedFailures)

        // 3. Five golden scenarios: replay each history through the F# kernel and compare the expected projection.
        let replayed =
            investigations
            |> List.map (fun entry ->
                let id = FrozenData.text entry "investigationId" |> Option.defaultValue "?"
                let key = FrozenData.text entry "scenarioKey" |> Option.defaultValue id
                let ndjson = IO.File.ReadAllText(IO.Path.Combine(root, sprintf "fixtures/v1.0.0/investigations/%s/events.ndjson" id))
                let golden = FrozenData.json root (sprintf "contract/v1.0.0/golden/%s.state.json" key)

                let failures =
                    match FrozenData.v1HistoryOf ndjson with
                    | Error reason -> [ sprintf "%s: the frozen history does not decode: %s" key reason ]
                    | Ok history ->
                        match StorageV1Replay.replay history with
                        | Error refusal -> [ sprintf "%s: the kernel refused the frozen history: %s %A" key (StorageV1Replay.refusalCode refusal) refusal ]
                        | Ok state ->
                            let (AggregateVersion version) = state.Version
                            let projected = StorageV1Replay.stateObject state
                            let count name = match StorageV1Replay.field name projected with VArray xs -> xs.Length | _ -> -1
                            let digest = StorageV1Replay.stateSha256 state
                            let goldenDigest = FrozenData.canonicalSha root (sprintf "contract/v1.0.0/golden/%s.state.json" key)

                            [ FrozenData.number entry "expectedVersion" <> Some version, sprintf "version %d, expected %A" version (FrozenData.number entry "expectedVersion")
                              FrozenData.text entry "expectedLifecycle" <> Some state.Lifecycle,
                              sprintf "lifecycle %s, expected %A" state.Lifecycle (FrozenData.text entry "expectedLifecycle")
                              FrozenData.arrayLength golden "observations" <> count "observations",
                              sprintf "observations %d, golden %d" (count "observations") (FrozenData.arrayLength golden "observations")
                              FrozenData.arrayLength golden "hypotheses" <> count "hypotheses",
                              sprintf "hypotheses %d, golden %d" (count "hypotheses") (FrozenData.arrayLength golden "hypotheses")
                              FrozenData.arrayLength golden "conclusions" <> count "conclusions",
                              sprintf "conclusions %d, golden %d" (count "conclusions") (FrozenData.arrayLength golden "conclusions")
                              // The golden projection is compared byte for byte by its digest (§8.4): the manifest's
                              // expectedStateSha256 and the canonical digest of the golden state file.
                              FrozenData.text entry "expectedStateSha256" <> Some digest,
                              sprintf "state sha256 %s, expectedStateSha256 %A" digest (FrozenData.text entry "expectedStateSha256")
                              goldenDigest <> Ok digest, sprintf "state sha256 %s, golden file %A" digest goldenDigest ]
                            |> List.filter fst
                            |> List.map (fun (_, reason) -> sprintf "%s: %s" key reason)

                key, failures)

        let goldenFailures = replayed |> List.collect snd

        // 4. Negative fixtures: every one whose refusal is the kernel's is refused by it, for the right reason. Only
        // meaningful once the positive control (step 3) passes, so it is evaluated only then.
        let negativeDir = IO.Path.Combine(root, "fixtures/v1.0.0/negative")
        let negatives = IO.Directory.GetFiles(negativeDir, "*.json") |> Array.sort |> List.ofArray
        Assert.Equal(90, negatives.Length)

        // The kernel's share of the negatives (replay preconditions, admissibility, stale version): 40 of the 90.
        let kernelNegatives =
            negatives
            |> List.filter (fun path ->
                let fixture = FrozenData.json root (IO.Path.GetRelativePath(root, path))
                FrozenData.text fixture "expectedErrorCode" |> Option.exists FrozenData.domainCodes.Contains)

        Assert.Equal(40, kernelNegatives.Length)

        let negativeFailures =
            if not (List.isEmpty goldenFailures) then
                [ "negative fixtures not evaluated: the positive control (the five frozen scenarios) does not replay, so a refusal would prove nothing" ]
            else
                negatives
                |> List.choose (fun path ->
                    let fixture = FrozenData.json root (IO.Path.GetRelativePath(root, path))
                    let name = FrozenData.text fixture "name" |> Option.defaultValue path
                    let code = FrozenData.text fixture "expectedErrorCode" |> Option.defaultValue "?"
                    let decode (e: Text.Json.JsonElement) = Text.Encoding.UTF8.GetString(Convert.FromBase64String(e.GetProperty("base64").GetString() |> string))

                    if not (FrozenData.domainCodes.Contains code) then
                        None
                    elif code = "CAUSA.VERSION_STALE" then
                        let key = FrozenData.text fixture "baseScenarioKey" |> Option.defaultValue "?"
                        let entry = investigations |> List.find (fun e -> FrozenData.text e "scenarioKey" = Some key)
                        let id = FrozenData.text entry "investigationId" |> Option.defaultValue "?"
                        let ndjson = IO.File.ReadAllText(IO.Path.Combine(root, sprintf "fixtures/v1.0.0/investigations/%s/events.ndjson" id))
                        let stale = FrozenData.number fixture "expectedAggregateVersion" |> Option.defaultValue 0L

                        match FrozenData.v1HistoryOf ndjson, FrozenData.v1HistoryOf (decode (fixture.GetProperty "appendedEvents")) with
                        | Ok history, Ok [ _ ] ->
                            match StorageV1Replay.replay history with
                            | Error refusal -> Some(sprintf "%s (%s): the base history does not replay: %A" name code refusal)
                            | Ok state ->
                                match StorageV1Replay.checkAppend (AggregateVersion stale) state with
                                | Some(Conflict _) -> None
                                | other -> Some(sprintf "%s (%s): decided %A, not a version conflict" name code other)
                        | _ -> Some(sprintf "%s (%s): the base history or the appended event does not decode" name code)
                    else
                        let domainCode = FrozenData.text fixture "expectedDomainCode"

                        match FrozenData.v1HistoryOf (decode ((fixture.GetProperty "files").GetProperty "events.ndjson")) with
                        | Error reason -> Some(sprintf "%s (%s): the history does not decode, so the kernel never saw it: %s" name code reason)
                        | Ok history ->
                            match StorageV1Replay.replay history with
                            | Ok _ -> Some(sprintf "%s (%s): the kernel accepted an inadmissible history" name code)
                            | Error refusal when StorageV1Replay.refusalCode refusal <> code ->
                                Some(sprintf "%s: refused as %s (%A), expected %s" name (StorageV1Replay.refusalCode refusal) refusal code)
                            | Error(V1HistoryInadmissible(actual, _, detail)) when domainCode.IsSome && domainCode <> Some actual ->
                                Some(sprintf "%s: domain code %s (%s), expected %A" name actual detail domainCode)
                            | Error _ -> None)

        // 5. The seed is unchanged after the run (STORAGE-007, ADRP-005-O2).
        let seedAfter = FrozenData.seedDigests root
        let seedChanged = if seedAfter = seedBefore then [] else [ "the frozen seed changed during the run" ]

        let all = goldenFailures @ negativeFailures @ seedChanged
        Assert.True(List.isEmpty all, String.concat "\n" all)
