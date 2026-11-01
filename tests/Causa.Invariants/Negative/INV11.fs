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
/// contiguous. For each integrity check, a forged input that only this check rejects is refused with exactly that
/// check's failure, and a mutation step deletes the check and shows this test's verdict then fails (the mutant is
/// caught). T35 R-07/R-08 (WI-R8-T15): exact refusals, schema-valid snapshot forgery, non-vacuous mutants.
module INV11 =
    let private v n = Fixture.event n

    /// Forged histories, each breaking exactly one replay precondition of a valid history 1, 2, 3, with the exact
    /// refusal the precondition owes: EmptyHistory, or NonContiguousVersion(expected, found) at the first break.
    let private forgedHistories: Map<string, EventEnvelope list * ReplayError> =
        let broken expected found = NonContiguousVersion(AggregateVersion expected, AggregateVersion found)

        Map.ofList
            [ "replay-gap", ([ v 1L; v 3L ], broken 2L 3L)
              "replay-duplicate", ([ v 1L; v 2L; v 2L ], broken 3L 2L)
              "replay-reorder", ([ v 2L; v 1L; v 3L ], broken 1L 2L)
              "replay-genesis", ([ v 2L; v 3L ], broken 1L 2L)
              "replay-empty", ([], EmptyHistory) ]

    let private genesis = AggregateVersion 1L
    let private valid = [ v 1L; v 2L; v 3L ]

    type private ReplayCheck = AggregateVersion -> EventEnvelope list -> Result<EventEnvelope list, ReplayError>

    /// This test's verdict on a replay check for one forged history: the valid history passes unchanged and the forged
    /// one is refused with exactly the owed error.
    let private verdict (check: ReplayCheck) (forged: EventEnvelope list, owed: ReplayError) : Result<unit, string> =
        match check genesis valid, check genesis forged with
        | Ok passed, Error actual when passed = valid && actual = owed -> Ok()
        | Ok passed, _ when passed <> valid -> Error "the valid history did not pass unchanged"
        | Error error, _ -> Error(sprintf "the valid history was refused: %A" error)
        | Ok _, Ok _ -> Error "the forged history was accepted"
        | Ok _, Error actual -> Error(sprintf "refused with %A, owed %A" actual owed)

    /// Mutants of the check, each removing one guard: the verdict must reject every mutant on the case that guard owns.
    let private guardRemoved: ReplayCheck = fun _ history -> Ok history

    /// Keeps only the emptiness guard (the contiguity and genesis guards are removed).
    let private onlyEmptinessGuard: ReplayCheck =
        fun _ history ->
            match history with
            | [] -> Error EmptyHistory
            | _ -> Ok history

    let private replayCase (name: string) =
        let case = forgedHistories[name]

        match verdict ReplayOrder.contiguousFrom case with
        | Ok() -> ()
        | Error reason -> Assert.Fail(sprintf "%s: the production check fails the verdict: %s" name reason)

        // Mutation (R-08): the verdict is not vacuous. With the guard removed this test's verdict fails, so removing
        // the check from production would turn this case red.
        Assert.True(Result.isError (verdict guardRemoved case), sprintf "%s: the verdict accepts a check with every guard removed" name)

        match name with
        | "replay-empty" -> ()
        | _ ->
            Assert.True(
                Result.isError (verdict onlyEmptinessGuard case),
                sprintf "%s: the verdict accepts a check without its contiguity/genesis guard" name
            )

    let private kernelReplayCase () =
        let kernel = Require.kernel ()

        forgedHistories
        |> Map.iter (fun name (forged, owed) ->
            match kernel.Replay forged with
            | Ok _ -> Assert.Fail(sprintf "kernel Replay folded the %s history" name)
            | Error actual -> Assert.True((actual = owed), sprintf "kernel Replay refused %s with %A, owed %A" name actual owed))

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

    /// Changes one snapshot value without changing its shape (a schema-valid string replaced by another string, no
    /// property added or removed) and re-digests it, so the schema and digest checks pass and only
    /// snapshot-equals-replay can reject it (R-07, T35).
    let private forgeSnapshotState (files: FileSet) =
        let snapshot = parse files.Snapshot

        match holder "version" snapshot with
        | Some o -> o["version"] <- JsonValue.Create(999L)
        | None -> failwith "the codec's snapshot has no version; the snapshot-equals-replay case cannot be built"

        let bytes = serialize snapshot

        { files with Snapshot = bytes }
        |> editManifest "snapshotSha256" (fun o -> o["snapshotSha256"] <- JsonValue.Create(keepPrefix (text o "snapshotSha256") (sha256Hex bytes)))

    let private flipFirstByte (data: byte[]) = data |> Array.mapi (fun i b -> if i = 0 then b ^^^ 0x20uy else b)

    /// How a case's refusal is judged. Only: every failure the codec reports is the named check's (the forged set is
    /// one that only that check rejects). Includes: the named check's failure is among them (cases whose forgery
    /// necessarily breaks more than one check, such as raw bytes that no longer parse).
    type private Refusal =
        | Only of (VerificationFailure -> bool)
        | Includes of (VerificationFailure -> bool)

    let private storageCases: Map<string, (FileSet -> FileSet) * Refusal> =
        let digest (field: string) (f: VerificationFailure) =
            match f with
            | DigestMismatch named -> named = field
            | _ -> false

        Map.ofList
            [ "storage-events-bytes", ((fun fs -> { fs with Events = flipFirstByte fs.Events }), Includes(digest "eventsSha256"))
              "storage-eventsSha256", (replaceDigest "eventsSha256", Only(digest "eventsSha256"))
              "storage-snapshotSha256", (replaceDigest "snapshotSha256", Only(digest "snapshotSha256"))
              "storage-required-field",
              (editManifest "eventsSha256" (fun o -> o.Remove "eventsSha256" |> ignore),
               Only(function
                   | MissingRequiredField(_, field) -> field = "eventsSha256"
                   | _ -> false))
              "storage-contract-value",
              (forgeContractValue,
               Includes(function
                   | ContractValueMismatch _ -> true
                   | _ -> false))
              "storage-schema",
              ((fun fs -> { fs with Manifest = Encoding.UTF8.GetBytes "[\"not a manifest\"]" }),
               Includes(function
                   | SchemaInvalid _ -> true
                   | _ -> false))
              "storage-snapshot-equals-replay",
              (forgeSnapshotState,
               Only(function
                   | SnapshotDiffersFromReplay _ -> true
                   | _ -> false)) ]

    let private named (refusal: Refusal) =
        match refusal with
        | Only p
        | Includes p -> p

    /// This test's verdict on a Verify function for one forged set.
    let private storageVerdict verify commit (forged: FileSet) (refusal: Refusal) : Result<unit, string> =
        match verify commit forged, refusal with
        | Ok _, _ -> Error "the forged file set was accepted"
        | Error failures, Only p when not (List.isEmpty failures) && List.forall p failures -> Ok()
        | Error failures, Includes p when List.exists p failures -> Ok()
        | Error failures, _ -> Error(sprintf "refused for another reason, or not only by the named check: %A" failures)

    /// The codec with the named check deleted: its failures are dropped, and a set with no other failure verifies.
    let private checkRemoved (codec: StorageCodec) (accepted: VerifiedInvestigation) (refusal: Refusal) =
        fun commit files ->
            match codec.Verify commit files with
            | Ok verified -> Ok verified
            | Error failures ->
                match failures |> List.filter (named refusal >> not) with
                | [] -> Ok accepted
                | rest -> Error rest

    let private storageCase (name: string) =
        let codec = Require.storageCodec ()
        let verified = Fixture.verifiedAt 1
        let commit = Fixture.head 77
        let written = codec.Encode { verified with History = [] } verified.History

        let accepted =
            match codec.Verify commit written with
            | Error failures -> failwithf "the codec refused a file set it wrote itself: %A" failures
            | Ok accepted -> accepted

        let forge, refusal = storageCases[name]
        let forged = forge written

        match storageVerdict codec.Verify commit forged refusal with
        | Ok() -> ()
        | Error reason -> Assert.Fail(sprintf "%s: %s" name reason)

        // Mutation step (as Mutation.INV01): delete the named check from the codec and the verdict must fail, so a
        // codec without this check cannot pass this case.
        Assert.True(
            Result.isError (storageVerdict (checkRemoved codec accepted refusal) commit forged refusal),
            sprintf "%s: the case still passes with the named check deleted (surviving mutant)" name
        )

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
