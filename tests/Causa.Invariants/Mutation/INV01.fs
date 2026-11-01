// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Mutation

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.Invariants

/// INV-01 (PERSIST-023-O2): Saved only after a re-read at the returned commit gave exactly the written bytes. The
/// read-back comparison lives in Confirmation.ofReadBack, the only constructor of the proof SM-SAVE needs for Saved.
/// The mutation check removes that comparison (the read-back is compared with itself) and shows the named check fails.
module INV01 =
    type OfReadBack = InvestigationId -> CommitOid -> DateTimeOffset -> FileSet -> FileSet -> Confirmation.T option

    /// A written file set and a read-back that is either identical or differs in exactly one byte of one file.
    type ReadBackCase =
        { Written: FileSet
          ReadBack: FileSet
          Identical: bool }

    let private bytes = Gen.intRange 1 24 |> Gen.bind (fun n -> Gen.listOfLength n (Gen.intRange 0 255)) |> Gen.map (List.map byte >> List.toArray)

    let private flipOneByte (data: byte[]) (at: int) =
        data |> Array.mapi (fun i b -> if i = at % data.Length then b ^^^ 0x01uy else b)

    let cases: Gen<ReadBackCase> =
        Gen.map3 (fun m e s -> { Manifest = m; Events = e; Snapshot = s }) bytes bytes bytes
        |> Gen.bind (fun written ->
            Gen.intRange 0 3
            |> Gen.bind (fun which ->
                Gen.intRange 0 1000
                |> Gen.map (fun at ->
                    let readBack =
                        match which with
                        | 0 -> { written with Manifest = flipOneByte written.Manifest at }
                        | 1 -> { written with Events = flipOneByte written.Events at }
                        | 2 -> { written with Snapshot = flipOneByte written.Snapshot at }
                        | _ ->
                            { Manifest = Array.copy written.Manifest
                              Events = Array.copy written.Events
                              Snapshot = Array.copy written.Snapshot }

                    { Written = written
                      ReadBack = readBack
                      Identical = (which = 3) })))

    /// The named check: a confirmation exists exactly when the read-back equals the written bytes, and it names the
    /// investigation and the commit that was re-read.
    let readBackGuardHolds (ofReadBack: OfReadBack) (case: ReadBackCase) : Result<unit, string> =
        let commit = Fixture.head 7

        match ofReadBack Fixture.investigationId commit DateTimeOffset.UnixEpoch case.Written case.ReadBack, case.Identical with
        | Some confirmation, true ->
            Check.expect
                (confirmation.CommitOid = commit && confirmation.InvestigationId = Fixture.investigationId)
                "the confirmation names another commit or investigation"
        | None, false -> Ok()
        | Some _, false -> Error "confirmed although the read-back bytes differ from the written bytes"
        | None, true -> Error "refused a read-back identical to the written bytes"

    /// The injected defect: the read-back comparison compares the written bytes with themselves.
    let readBackSkipped: OfReadBack =
        fun investigation commit at written _readBack -> Confirmation.ofReadBack investigation commit at written written

    [<Fact>]
    let ReadBackSkipped () =
        // The real guard satisfies the named check on every case.
        Check.check 300 cases (readBackGuardHolds Confirmation.ofReadBack)

        // With the comparison removed, the same check fails on some generated case.
        let caught =
            Seq.init 300 (fun i -> fst (Gen.run cases (Seed(uint64 (i + 1)))))
            |> Seq.exists (fun case -> Result.isError (readBackGuardHolds readBackSkipped case))

        Assert.True(caught, "the read-back mutant survived: no generated case made the named check fail")
