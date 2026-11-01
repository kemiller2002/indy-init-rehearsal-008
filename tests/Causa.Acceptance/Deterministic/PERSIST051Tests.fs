// Owner: domain. WI-R8-DTESTS: PERSIST-051, the PAT-12 save-state machine model test. The model (Pat12, in
// PERSIST023Tests.fs) enumerates every SM-SAVE state and input against the real SaveProcess; these tests bind its
// checks to the PERSIST-051 obligations.
namespace Causa.Acceptance.Deterministic

open System
open Microsoft.FSharp.Reflection
open Xunit
open Causa.Domain
open Causa.Persistence

type PERSIST051Tests() =
    /// O1: an F# test enumerates every save state (7) and every input (27) of the machine: the model's lists are
    /// exactly the union cases, and all 189 pairs are stepped.
    [<Fact>]
    member _.``PERSIST-051-O1``() =
        Pat12.assertNone "the model does not enumerate SM-SAVE" (Pat12.enumerationProblems ())
        let all = Pat12.pairs.Force()
        Assert.Equal(7, Pat12.stateNames.Length)
        Assert.Equal(27, Pat12.inputNames.Length)
        Assert.Equal(189, all.Length)
        Assert.Equal(189, all |> List.map fst |> List.distinct |> List.length)
        Assert.All(all, fun (_, observations) -> Assert.NotEmpty observations)

    /// O2: each legal transition reaches the state PERSIST-031 defines, every illegal input fails closed with a typed
    /// fault, and Saved is reached only from a read-back confirmation.
    [<Fact>]
    member _.``PERSIST-051-O2``() =
        let all = Pat12.pairs.Force()
        Pat12.assertNone "legal transitions" (Pat12.legalProblems all)
        Pat12.assertNone "illegal pairs" (Pat12.illegalProblems all)
        Pat12.assertNone "Saved without confirmation" (Pat12.savedProblems all)

    /// O3: a compile-time negative check: code outside the persistence module that builds a Confirmation (and so
    /// Saved(Confirmed _)) does not compile against the production assemblies; the public constructor does.
    [<Fact>]
    member _.``PERSIST-051-O3``() =
        Pat12.assertNone "a confirmation can be forged outside Causa.Persistence" (Pat12.forgeryProblems.Force())

    /// O4: the whole model passes on the candidate SHA (this run): enumeration, legal and illegal pairs, Saved only
    /// from a confirmation, and the compile check.
    [<Fact>]
    member _.``PERSIST-051-O4``() =
        let all = Pat12.pairs.Force()

        Pat12.assertNone
            "PAT-12"
            (Pat12.enumerationProblems ()
             @ Pat12.legalProblems all
             @ Pat12.illegalProblems all
             @ Pat12.savedProblems all
             @ Pat12.forgeryProblems.Force())

    /// O5: it covers PERSIST-007 (Saved only after the read-back bytes equal the bytes written; a mismatch is
    /// SaveFailed BytesDiffer with the changes kept), PERSIST-023 (no forgeable Saved, illegal inputs fail closed) and
    /// PERSIST-031 (exactly seven states).
    [<Fact>]
    member _.``PERSIST-051-O5``() =
        // PERSIST-031: the seven states.
        Assert.Equal<string list>(
            [ "Saved"; "Unsaved"; "Saving"; "Offline"; "Conflict"; "UnknownOutcome"; "SaveFailed" ],
            FSharpType.GetUnionCases(typeof<SaveState>, true) |> Array.map _.Name |> List.ofArray
        )

        // PERSIST-007: the read-back decides; a mismatch keeps the changes and fails the save.
        let saving = Pat12.sessionsIn "Saving" |> List.head
        let written = StorageV1.encodeHistory Pat12.investigation [ Pat12.firstEvent ]
        let tampered = { written with Events = Array.append written.Events [| 0x0Auy |] }
        Assert.True(Option.isNone (Confirmation.ofReadBack Pat12.investigation (Pat12.head 6) DateTimeOffset.UnixEpoch written tampered))

        match Pat12.online.Step (Pat12.context false) saving (SaveInput.ReadBackMismatched(Pat12.head 6)) with
        | Ok(next, _) ->
            Assert.Equal(SaveState.SaveFailed(BytesDiffer(Pat12.head 6)), next.State)
            Assert.Equal<CommandEnvelope list>(saving.Unsaved, next.Unsaved)
        | Error fault -> Assert.Fail(sprintf "a read-back mismatch while saving was refused: %A" fault)

        let confirmed = Pat12.confirmation 6

        match Pat12.online.Step (Pat12.context false) saving (SaveInput.ReadBackMatched confirmed) with
        | Ok(next, _) ->
            Assert.Equal(SaveState.Saved(Confirmed confirmed), next.State)
            Assert.Equal(confirmed.CommitOid, next.BaseHead)
            Assert.Empty(next.Unsaved)
        | Error fault -> Assert.Fail(sprintf "a matching read-back while saving was refused: %A" fault)

        // PERSIST-023: the machine-wide checks.
        let all = Pat12.pairs.Force()
        Pat12.assertNone "PERSIST-023 fail closed" (Pat12.illegalProblems all)
        Pat12.assertNone "PERSIST-023 Saved without confirmation" (Pat12.savedProblems all)
