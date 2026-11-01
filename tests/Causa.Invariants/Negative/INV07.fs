// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.App
open Causa.Invariants

/// INV-07 (PERSIST-011-O4): Rehearsal 7 F2, written from the invariant. A refresh is refused, and a completing load is
/// dropped, while the investigation holds unsaved changes, a save in flight or a conflict.
module INV07 =
    let private withSession (engine: Engine) (load: LoadState) (session: InvestigationSession) =
        let model, _ = engine.Init(Route "/")

        { model with
            Connection = Connected true
            Context = Some Fixture.connection
            Loads = Map.ofList [ Fixture.investigationId, load ]
            Sessions = Map.ofList [ Fixture.investigationId, session ] }

    let private holding (machine: SaveMachine) =
        let base' = Drive.start machine
        let unsaved = [ Fixture.command 1 ]

        [ "unsaved changes", { base' with State = SaveState.Unsaved; Unsaved = unsaved }
          "a save in flight", { base' with State = SaveState.Saving; Unsaved = unsaved }
          "a conflict", { base' with State = SaveState.Conflict(Reviewable(Fixture.verifiedAt 9)); Unsaved = unsaved } ]

    [<Fact>]
    let RefreshNeverReplacesWork () =
        let machine = Require.saveMachine ()
        let engine = Require.engine ()
        let loaded = Loaded(Fixture.verifiedAt 1)

        holding machine
        |> List.iter (fun (what, session) ->
            // Refresh is refused: nothing is read and nothing changes.
            let model = withSession engine loaded session
            let model', effects = engine.Update (LoadStep(Fixture.investigationId, LoadInput.RefreshRequested)) model

            Assert.True(
                effects |> List.forall (function Http _ -> false | _ -> true),
                sprintf "refresh with %s sent a request" what
            )

            Assert.Equal<LoadState>(loaded, model'.Loads[Fixture.investigationId])
            Assert.Equal<InvestigationSession>(session, model'.Sessions[Fixture.investigationId])

            // A load that completes meanwhile is dropped: the work and the base head are kept.
            let loading = withSession engine (Loading(RequestId 5L, LoadFilesAt(Fixture.head 2))) session
            let afterLoad, _ = engine.Update (LoadStep(Fixture.investigationId, LoadInput.LoadVerified(Fixture.verifiedAt 2))) loading
            let kept = afterLoad.Sessions[Fixture.investigationId]
            Assert.Equal<Causa.Domain.CommandEnvelope list>(session.Unsaved, kept.Unsaved)
            Assert.Equal(session.BaseHead, kept.BaseHead)
            Assert.Equal(Names.save session.State, Names.save kept.State))
