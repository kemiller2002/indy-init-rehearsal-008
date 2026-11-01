// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.Invariants

/// INV-11 (PERSIST-005-O2): every file set read or written satisfies causa-storage v1.0.0, and the replayed history is
/// contiguous. For each integrity check, a forged input that this check rejects is refused; for the domain's replay
/// checks the test also removes the check and shows the forged input is then accepted (the mutant is caught).
module INV11 =
    let private v n = Fixture.event n

    /// Forged histories, each breaking exactly one replay precondition of a valid history 1, 2, 3.
    let private forgedHistories =
        Map.ofList
            [ "replay-gap", [ v 1L; v 3L ]
              "replay-duplicate", [ v 1L; v 2L; v 2L ]
              "replay-reorder", [ v 2L; v 1L; v 3L ]
              "replay-genesis", [ v 2L; v 3L ]
              "replay-empty", [] ]

    let private genesis = AggregateVersion 1L

    /// The check with its guard removed: every history passes.
    let private replayCheckRemoved (_: AggregateVersion) (history: EventEnvelope list) : Result<EventEnvelope list, ReplayError> = Ok history

    let private replayCase (name: string) =
        let forged = forgedHistories[name]
        Assert.True(Result.isOk (ReplayOrder.contiguousFrom genesis [ v 1L; v 2L; v 3L ]), "a contiguous history must pass")
        Assert.True(Result.isError (ReplayOrder.contiguousFrom genesis forged), sprintf "%s: forged history accepted" name)
        // Mutation: with the guard removed the same forged history is accepted, so this test would fail.
        Assert.True(Result.isOk (replayCheckRemoved genesis forged), sprintf "%s: the mutant did not change the outcome" name)

    let private kernelReplayCase () =
        let kernel = Require.kernel ()

        forgedHistories
        |> Map.iter (fun name forged ->
            Assert.True(Result.isError (kernel.Replay forged), sprintf "kernel Replay folded the %s history" name))

    // ----- causa-storage checks: forge one field of a file set the codec itself wrote, then verify.

    let private sha256Hex (data: byte[]) = Convert.ToHexString(SHA256.HashData data).ToLowerInvariant()

    let private keepPrefix (old: string) (hex: string) = if old.StartsWith "sha256:" then "sha256:" + hex else hex

    let private parse (bytes: byte[]) =
        match JsonNode.Parse(Encoding.UTF8.GetString bytes) with
        | :? JsonObject as o -> o
        | _ -> failwith "the codec wrote a manifest or snapshot that is not a JSON object"

    let private serialize (node: JsonObject) = Encoding.UTF8.GetBytes(node.ToJsonString())

    /// The first object (depth-first) holding the field, so per-investigation manifests and nested entries both work.
    let rec private holder (field: string) (node: JsonNode) : JsonObject option =
        match node with
        | :? JsonObject as o when o.ContainsKey field -> Some o
        | :? JsonObject as o -> o |> Seq.tryPick (fun kv -> match kv.Value with null -> None | child -> holder field child)
        | :? JsonArray as a -> a |> Seq.tryPick (fun child -> match child with null -> None | c -> holder field c)
        | _ -> None

    let private editManifest (field: string) (edit: JsonObject -> unit) (files: FileSet) =
        let manifest = parse files.Manifest

        match holder field manifest with
        | Some o ->
            edit o
            { files with Manifest = serialize manifest }
        | None -> failwithf "the codec's manifest has no %s field; the forged case cannot be built" field

    let private text (o: JsonObject) (field: string) =
        match o[field] with
        | null -> failwithf "%s is null" field
        | node -> node.GetValue<string>()

    let private replaceDigest field (files: FileSet) =
        files
        |> editManifest field (fun o ->
            let old = text o field
            o[field] <- JsonValue.Create(keepPrefix old (sha256Hex (Encoding.UTF8.GetBytes("forged" + old)))))

    let private contractFields = [ "contractVersion"; "storageVersion"; "contract"; "schemaVersion"; "formatVersion" ]

    let private forgeContractValue (files: FileSet) =
        let manifest = parse files.Manifest

        match contractFields |> List.tryPick (fun f -> holder f manifest |> Option.map (fun o -> f, o)) with
        | Some(field, o) ->
            o[field] <- JsonValue.Create("0.0.0-forged")
            { files with Manifest = serialize manifest }
        | None -> failwithf "the codec's manifest has none of the contract-fixed fields %A" contractFields

    /// Changes the snapshot's content and re-digests it, so only snapshot-equals-replay can reject it.
    let private forgeSnapshotState (files: FileSet) =
        let snapshot = parse files.Snapshot
        snapshot["forgedState"] <- JsonValue.Create(true)

        match holder "title" snapshot with
        | Some o -> o["title"] <- JsonValue.Create("forged title")
        | None -> ()

        let bytes = serialize snapshot

        { files with Snapshot = bytes }
        |> editManifest "snapshotSha256" (fun o -> o["snapshotSha256"] <- JsonValue.Create(keepPrefix (text o "snapshotSha256") (sha256Hex bytes)))

    let private flipFirstByte (data: byte[]) = data |> Array.mapi (fun i b -> if i = 0 then b ^^^ 0x20uy else b)

    type private Expected = VerificationFailure -> bool

    let private storageCases: Map<string, (FileSet -> FileSet) * Expected> =
        let digest (f: VerificationFailure) =
            match f with
            | DigestMismatch _ -> true
            | _ -> false

        Map.ofList
            [ "storage-events-bytes", ((fun fs -> { fs with Events = flipFirstByte fs.Events }), digest)
              "storage-eventsSha256", (replaceDigest "eventsSha256", digest)
              "storage-snapshotSha256", (replaceDigest "snapshotSha256", digest)
              "storage-required-field",
              (editManifest "eventsSha256" (fun o -> o.Remove "eventsSha256" |> ignore),
               (function
               | MissingRequiredField _
               | SchemaInvalid _ -> true
               | _ -> false))
              "storage-contract-value",
              (forgeContractValue,
               (function
               | ContractValueMismatch _ -> true
               | _ -> false))
              "storage-schema",
              ((fun fs -> { fs with Manifest = Encoding.UTF8.GetBytes "[\"not a manifest\"]" }),
               (function
               | SchemaInvalid _ -> true
               | _ -> false))
              "storage-snapshot-equals-replay",
              (forgeSnapshotState,
               (function
               | SnapshotDiffersFromReplay _
               | SchemaInvalid _ -> true
               | _ -> false)) ]

    let private storageCase (name: string) =
        let codec = Require.storageCodec ()
        let verified = Fixture.verifiedAt 1
        let commit = Fixture.head 77
        let written = codec.Encode { verified with History = [] } verified.History

        match codec.Verify commit written with
        | Error failures -> Assert.Fail(sprintf "the codec refused a file set it wrote itself: %A" failures)
        | Ok _ -> ()

        let forge, expected = storageCases[name]

        match codec.Verify commit (forge written) with
        | Ok _ -> Assert.Fail(sprintf "%s: the forged file set was accepted" name)
        | Error failures -> Assert.True(List.exists expected failures, sprintf "%s: refused for another reason: %A" name failures)

    [<Theory>]
    [<InlineData("replay-gap")>]
    [<InlineData("replay-duplicate")>]
    [<InlineData("replay-reorder")>]
    [<InlineData("replay-genesis")>]
    [<InlineData("replay-empty")>]
    [<InlineData("kernel-replay")>]
    [<InlineData("storage-events-bytes")>]
    [<InlineData("storage-eventsSha256")>]
    [<InlineData("storage-snapshotSha256")>]
    [<InlineData("storage-required-field")>]
    [<InlineData("storage-contract-value")>]
    [<InlineData("storage-schema")>]
    [<InlineData("storage-snapshot-equals-replay")>]
    let EachIntegrityCheckHasAMutant (case: string) =
        match case with
        | "kernel-replay" -> kernelReplayCase ()
        | c when c.StartsWith "replay-" -> replayCase c
        | c -> storageCase c
