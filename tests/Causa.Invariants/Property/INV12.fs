// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.Invariants

/// INV-12 (PERSIST-012-O3): the reapply result equals folding the unsaved commands through decide/evolve at the GitHub
/// version; refused commands carry the engine's reason and are never applied.
module INV12 =
    /// The reference: each unsaved command in order, decided by the kernel against the GitHub version as evolved so far.
    let reference (kernel: DomainKernel) (remote: VerifiedInvestigation) (unsaved: CommandEnvelope list) =
        let accepted, refused, _ =
            unsaved
            |> List.fold
                (fun (accepted, refused, (history, state)) command ->
                    let after = INV11.history kernel (history, state) [ command ]

                    if List.length (fst after) > List.length history then
                        accepted @ [ command ], refused, after
                    else
                        accepted, refused @ [ command ], (history, state))
                ([], [], (remote.History, Some remote.Investigation))

        accepted, refused

    let private conflictModel (engine: Engine) (remote: VerifiedInvestigation) (unsaved: CommandEnvelope list) =
        let model, _ = engine.Init(Route "/")

        let session =
            { Id = Fixture.investigationId
              BaseHead = Fixture.head 1
              State = SaveState.Conflict(Reviewable remote)
              Unsaved = unsaved
              AfterInFlight = []
              Outstanding = None
              Mutations = 1
              RebaseAttempts = 0
              ReconcileReads = 0 }

        { model with
            Connection = Connected true
            Context = Some Fixture.connection
            Sessions = Map.ofList [ Fixture.investigationId, session ] }

    let private cases = Gen.map2 (fun remote unsaved -> remote, unsaved) INV11.commands INV11.commands

    let reappliedByTheEngine (kernel: DomainKernel) (engine: Engine) (remoteCommands: CommandEnvelope list, unsaved: CommandEnvelope list) =
        match INV11.history kernel ([], None) remoteCommands with
        | _, None -> Error "the kernel accepted none of the generated remote commands"
        | history, Some investigation ->
            let remote = { Investigation = investigation; History = history; ReadAt = Fixture.head 70; Bytes = Fixture.fileSet "remote" }
            let accepted, refused = reference kernel remote unsaved
            let model, _ = engine.Update (ConflictStep(Fixture.investigationId, ConflictInput.ReapplyChosen)) (conflictModel engine remote unsaved)
            let session = model.Sessions[Fixture.investigationId]

            match session.State with
            | SaveState.Unsaved ->
                // Unsaved is the outcome only when the engine refused nothing: a refused command must stay in the
                // conflict view with the engine's reason, never be dropped silently (R-10, T35).
                Check.all
                    [ Check.expect (List.isEmpty refused) (sprintf "reapply ended Unsaved, but the kernel refuses %A: refused commands left without a reason" refused)
                      Check.expect (session.Unsaved = accepted) (sprintf "accepted %A, the kernel accepts %A" session.Unsaved accepted)
                      Check.expect (session.BaseHead = remote.ReadAt) "reapply is not based on the GitHub version" ]
            | SaveState.Conflict(Reapplied(onto, refusedByEngine)) ->
                Check.all
                    [ Check.expect (onto.ReadAt = remote.ReadAt) "reapplied onto another version"
                      Check.expect (refusedByEngine |> List.map (fun r -> r.Command) = refused) (sprintf "refused %A, the kernel refuses %A" refusedByEngine refused)
                      Check.expect (refusedByEngine |> List.forall (fun r -> r.Reason.Trim() <> "")) "a refused command carries no reason" ]
            | other -> Error(sprintf "ReapplyChosen left the session in %A, not re-decided by the engine" (Names.save other))

    [<Xunit.Fact>]
    let ReapplyEqualsEngineFold () =
        let kernel = Require.kernel ()
        let engine = Require.engine ()
        Check.check 150 cases (reappliedByTheEngine kernel engine)
