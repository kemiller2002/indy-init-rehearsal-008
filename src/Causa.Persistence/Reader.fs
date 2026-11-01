// Owner: persistence. T19: the causa-storage v1.0.0 codec (IF41-STORAGE) used to write a save's file set and to
// verify every file set read, as a reader would (causa-data v1.0.0 §11).
namespace Causa.Persistence

open System
open System.Text
open Causa.Domain

/// The paths of one investigation's files under its own prefix (causa-data v1.0.0 §9.4.4).
[<RequireQualifiedAccess>]
module StoragePaths =
    let private guidText (InvestigationId g) = g.ToString("D").ToLowerInvariant()

    let prefix (id: InvestigationId) : RepoPath = RepoPath(sprintf "investigations/%s/" (guidText id))

    let private under id name =
        let (RepoPath p) = prefix id
        RepoPath(p + name)

    let manifest id = under id "manifest.json"
    let events id = under id "events.ndjson"
    let snapshot id = under id "snapshot.json"

    /// True when the path lies under the investigation's prefix.
    let isUnder (id: InvestigationId) (path: string) =
        let (RepoPath p) = prefix id
        path.StartsWith(p, StringComparison.Ordinal)

/// causa-storage v1.0.0 for the fields the domain stubs carry. Deviation (reported): the stub EventEnvelope has no
/// timestamps, actor or payload body, so createdAt/updatedAt/occurredAt/recordedAt/actor/payload/provenance are not
/// written; the domain's full shapes (T13) extend this codec.
[<RequireQualifiedAccess>]
module StorageV1 =
    let formatVersion = "1.0.0"

    let private contractFixed =
        [ "formatVersion", formatVersion; "schemaVersion", "1.0.0"; "semanticVersion", "1.0.0" ]

    let private guid (g: Guid) = g.ToString("D").ToLowerInvariant()
    let private investigationText (InvestigationId g) = guid g

    let private eventJson (e: EventEnvelope) : Json =
        let (EventId id), (AggregateVersion v), (EventPayload kind) = e.EventId, e.Version, e.Payload

        JObject
            [ "formatVersion", JString formatVersion
              "eventId", JString(guid id)
              "investigationId", JString(investigationText e.Investigation)
              "aggregateVersion", JInt v
              "eventType", JString kind
              "semanticVersion", JString "1.0.0" ]

    let private versionOf (history: EventEnvelope list) =
        history |> List.tryLast |> Option.map (fun e -> let (AggregateVersion v) = e.Version in v) |> Option.defaultValue 0L

    /// The snapshot is a deterministic projection of the replayed history, never a second source of truth.
    let private snapshotJson (id: InvestigationId) (history: EventEnvelope list) : Json =
        let version = versionOf history

        JObject
            [ "formatVersion", JString formatVersion
              "investigationId", JString(investigationText id)
              "aggregateVersion", JInt version
              "projectionVersion", JInt 1L
              "semanticVersion", JString "1.0.0"
              "state", JObject [ "investigationId", JString(investigationText id); "version", JInt version ] ]

    let private eventsBytes (history: EventEnvelope list) =
        history |> List.map (fun e -> Canonical.render (eventJson e) + "\n") |> String.concat "" |> Encoding.UTF8.GetBytes

    let encodeHistory (id: InvestigationId) (history: EventEnvelope list) : FileSet =
        let events = eventsBytes history
        let snapshot = Canonical.bytes (snapshotJson id history)
        let version = versionOf history

        let manifest =
            JObject(
                (contractFixed |> List.map (fun (k, v) -> k, JString v))
                @ [ "investigationId", JString(investigationText id)
                    "aggregateVersion", JInt version
                    "eventCount", JInt(int64 history.Length)
                    "eventsSha256", JString(Canonical.sha256Hex events)
                    "snapshotVersion", JInt 1L
                    "snapshotSha256", JString(Canonical.sha256Hex snapshot)
                    "domainPacks", JArray [] ]
            )

        { Manifest = Canonical.bytes manifest; Events = events; Snapshot = snapshot }

    let encode (verified: VerifiedInvestigation) (appended: EventEnvelope list) : FileSet =
        encodeHistory verified.Investigation.Id (verified.History @ appended)

    let private requiredManifest =
        [ "formatVersion"; "investigationId"; "schemaVersion"; "semanticVersion"; "aggregateVersion"; "eventCount"
          "eventsSha256"; "snapshotVersion"; "snapshotSha256"; "domainPacks" ]

    let private text name json =
        match Canonical.field name json with
        | Some(JString s) -> Some s
        | _ -> None

    let private integer name json =
        match Canonical.field name json with
        | Some(JInt n) -> Some n
        | _ -> None

    let private parseEvent (line: string) : Result<EventEnvelope, VerificationFailure> =
        match Canonical.parse (Encoding.UTF8.GetBytes line) with
        | Ok(JObject _ as json) ->
            match text "eventId" json, text "investigationId" json, integer "aggregateVersion" json, text "eventType" json with
            | Some eid, Some iid, Some v, Some kind ->
                match Guid.TryParse eid, Guid.TryParse iid with
                | (true, e), (true, i) ->
                    Ok
                        { EventId = EventId e
                          Investigation = InvestigationId i
                          Version = AggregateVersion v
                          Payload = EventPayload kind }
                | _ -> Error(SchemaInvalid("events.ndjson", "an identifier is not a UUID"))
            | _ -> Error(MissingRequiredField("events.ndjson", "eventId, investigationId, aggregateVersion or eventType"))
        | Ok _ -> Error(SchemaInvalid("events.ndjson", "a line is not a JSON object"))
        | Error e -> Error(SchemaInvalid("events.ndjson", e))

    let private parseEvents (bytes: byte[]) : Result<EventEnvelope list, VerificationFailure list> =
        let content = Encoding.UTF8.GetString bytes

        if content.Length > 0 && not (content.EndsWith "\n") then
            Error [ SchemaInvalid("events.ndjson", "the last line has no terminal LF") ]
        else
            let results = content.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList |> List.map parseEvent

            match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] -> Ok(results |> List.choose Result.toOption)
            | errors -> Error errors

    /// Verifies a file set as a reader would: schema, contract-fixed values, digests, contiguous replay and the
    /// snapshot equal to the replayed projection. Every failure found is reported.
    let verify (commit: CommitOid) (files: FileSet) : Result<VerifiedInvestigation, VerificationFailure list> =
        match Canonical.parse files.Manifest with
        | Error e -> Error [ SchemaInvalid("manifest.json", e) ]
        | Ok(JObject _ as manifest) ->
            let missing =
                requiredManifest
                |> List.filter (fun f -> (Canonical.field f manifest).IsNone)
                |> List.map (fun f -> MissingRequiredField("manifest.json", f))

            let contract =
                contractFixed
                |> List.choose (fun (f, expected) ->
                    match text f manifest with
                    | Some actual when actual <> expected -> Some(ContractValueMismatch(f, expected, actual))
                    | _ -> None)

            let digest field (data: byte[]) =
                match text field manifest with
                | Some recorded when recorded <> Canonical.sha256Hex data -> [ DigestMismatch field ]
                | _ -> []

            let digests = digest "eventsSha256" files.Events @ digest "snapshotSha256" files.Snapshot

            let investigation =
                text "investigationId" manifest
                |> Option.bind (fun s -> match Guid.TryParse s with | true, g -> Some(InvestigationId g) | _ -> None)

            let replayed =
                match parseEvents files.Events with
                | Error errors -> Error errors
                | Ok history ->
                    match ReplayOrder.contiguousFrom (AggregateVersion 1L) history with
                    | Error e -> Error [ ReplayRejected e ]
                    | Ok history ->
                        let mismatched =
                            match investigation with
                            | Some id when history |> List.forall (fun e -> e.Investigation = id) -> []
                            | _ -> [ ContractValueMismatch("investigationId", "the manifest's investigation", "another") ]

                        let counts =
                            [ "aggregateVersion"; "eventCount" ]
                            |> List.choose (fun f ->
                                match integer f manifest with
                                | Some n when n <> int64 history.Length -> Some(ContractValueMismatch(f, string history.Length, string n))
                                | _ -> None)

                        match mismatched @ counts with
                        | [] -> Ok history
                        | errors -> Error errors

            let snapshotCheck history id =
                match Canonical.parse files.Snapshot with
                | Error e -> [ SchemaInvalid("snapshot.json", e) ]
                | Ok actual ->
                    match Canonical.differences "" (snapshotJson id history) actual with
                    | [] -> []
                    | paths -> [ SnapshotDiffersFromReplay paths ]

            let replayFailures, snapshotFailures, history =
                match replayed, investigation with
                | Ok history, Some id -> [], snapshotCheck history id, history
                | Ok history, None -> [], [], history
                | Error errors, _ -> errors, [], []

            match missing @ contract @ digests @ replayFailures @ snapshotFailures, investigation with
            | [], Some id ->
                Ok
                    { Investigation = { Id = id; Version = AggregateVersion(versionOf history) }
                      History = history
                      ReadAt = commit
                      Bytes = files }
            | [], None -> Error [ MissingRequiredField("manifest.json", "investigationId") ]
            | failures, _ -> Error failures
        | Ok _ -> Error [ SchemaInvalid("manifest.json", "the manifest is not a JSON object") ]

    let codec: StorageCodec =
        { PathOf = StoragePaths.prefix
          Encode = encode
          Verify = verify }
