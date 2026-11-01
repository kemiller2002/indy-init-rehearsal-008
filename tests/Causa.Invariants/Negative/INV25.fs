// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-25 (PERSIST-012-O2): a conflict whose GitHub version is deleted or unverifiable is recoverable: it offers an
/// export of the unsaved commands, writes nothing to GitHub or browser storage, and keeps the work.
module INV25 =
    [<Fact>]
    let UnresolvableConflictExports () =
        let machine = Require.saveMachine ()

        let failures: VerificationFailure option list =
            [ None // deleted
              Some(DigestMismatch "eventsSha256")
              Some(SnapshotDiffersFromReplay [ "title" ]) ]

        failures
        |> List.iter (fun failure ->
            match machine.StepConflict (Fixture.context 1L) (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteMissingOrInvalid failure) with
            | Ok((Unresolvable _ as view), effects) ->
                Assert.Empty(Requests.sends effects)

                match machine.StepConflict (Fixture.context 2L) view ConflictInput.ExportChosen with
                | Ok(after, exportEffects) ->
                    Assert.Equal<ConflictView>(view, after)
                    Assert.Empty(Requests.sends exportEffects)

                    Assert.True(
                        exportEffects |> List.exists (function ExportCommands _ -> true | _ -> false),
                        "ExportChosen offered no export of the unsaved commands"
                    )
                | Error fault -> Assert.Fail(sprintf "ExportChosen refused in Unresolvable: %A" fault)
            | other -> Assert.Fail(sprintf "an unverifiable GitHub version (%A) did not make the conflict Unresolvable: %A" failure other))

        // The session keeps every unsaved change while the conflict is unresolvable, and nothing is written.
        let session =
            { Drive.start machine with
                State = SaveState.Conflict(Unresolvable "deleted")
                Unsaved = [ Fixture.command 1; Fixture.command 2 ] }

        [ SaveInput.Edit(Fixture.command 3); SaveInput.WentOffline; SaveInput.WentOnline ]
        |> List.fold
            (fun (current: InvestigationSession) input ->
                match machine.Step (Fixture.context 3L) current input with
                | Ok(next, effects) ->
                    Assert.Empty(Requests.mutations effects)
                    Assert.True(List.forall (fun c -> List.contains c (next.Unsaved @ next.AfterInFlight)) current.Unsaved, sprintf "%A dropped work" input)
                    next
                | Error fault -> failwithf "legal input %A refused in Conflict: %A" input fault)
            session
        |> ignore
