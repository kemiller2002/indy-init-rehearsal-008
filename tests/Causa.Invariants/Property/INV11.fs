// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open System
open Causa.Domain
open Causa.Persistence
open Causa.Invariants

/// INV-11 (PERSIST-005-O2): every file set the application writes re-reads and replays to its snapshot.
module INV11 =
    /// The kernel's event identity (T13): derived from the command id and index without collisions between commands.
    let private eventId (command: CommandEnvelope) (index: int) = EventIds.forCommand command.CommandId index

    let private next (state: Investigation option) =
        match state with
        | Some s -> let (AggregateVersion v) = s.Version in AggregateVersion(v + 1L)
        | None -> AggregateVersion 1L

    /// Folds commands through the kernel's decide and evolve from no investigation; refused commands are left out.
    /// Returns the history and the investigation it evolves to.
    let history (kernel: DomainKernel) (start: (EventEnvelope list * Investigation option)) (commands: CommandEnvelope list) =
        commands
        |> List.fold
            (fun (events, state) (command: CommandEnvelope) ->
                let command = { command with ExpectedVersion = state |> Option.map (fun (s: Investigation) -> s.Version) }

                match kernel.Decide Fixture.policy Fixture.actor state command with
                | Allowed payloads ->
                    payloads
                    |> List.indexed
                    |> List.fold
                        (fun (events, state) (i, payload) ->
                            let envelope =
                                { EventId = eventId command i
                                  Investigation = Fixture.investigationId
                                  Version = next state
                                  Payload = payload }

                            match kernel.Evolve state envelope with
                            | Ok evolved -> events @ [ envelope ], Some evolved
                            | Error _ -> events, state)
                        (events, state)
                | Blocked _
                | RequiresReview _
                | UnsupportedSemantics _
                | Conflict _
                | Duplicate _ -> events, state)
            start

    let commands: Gen<CommandEnvelope list> =
        Gen.listOf 8 (Gen.intRange 1 10000) |> Gen.map (List.distinct >> List.map Fixture.command)

    let private empty = { Manifest = [||]; Events = [||]; Snapshot = [||] }

    let replaysToSnapshot (kernel: DomainKernel) (codec: StorageCodec) (commands: CommandEnvelope list) =
        match history kernel ([], None) commands with
        | [], _
        | _, None -> Error "the kernel accepted none of the generated commands, so no file set could be written"
        | events, Some investigation ->
            let prior = { Investigation = investigation; History = []; ReadAt = CommitOid ""; Bytes = empty }
            let written = codec.Encode prior events

            match codec.Verify (Fixture.head 88) written with
            | Error failures -> Error(sprintf "a written file set did not verify: %A" failures)
            | Ok verified ->
                Check.all
                    [ Check.expect (verified.History = events) "the re-read history differs from the written one"
                      Check.expect (verified.Investigation = investigation) "the re-read investigation differs from the decided one"
                      Check.expect (kernel.Replay verified.History = Ok verified.Investigation) "the history does not replay to the snapshot"
                      Check.expect (codec.Encode { verified with History = [] } verified.History = written) "re-encoding is not byte-identical" ]

    [<Xunit.Fact>]
    let WrittenFileSetsReplay () =
        let kernel = Require.kernel ()
        let codec = Require.storageCodec ()
        Check.check 150 commands (replaysToSnapshot kernel codec)
