// Owner: persistence. T19: SM-CONFLICT transition tests (state-machine-contracts.json), one per transition, and the
// illegal pairs. The shared, pure test support of the three machine files lives first in compile order, here.
namespace Causa.Persistence.Tests.StateMachines

open System
open Xunit
open Causa.Domain
open Causa.Persistence

module Support =
    let guid (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)
    let investigation = InvestigationId(guid 1)
    let head (n: int) = CommitOid(sprintf "%040x" n)

    let event (v: int64) : EventEnvelope =
        { EventId = EventId(guid (1000 + int v))
          Investigation = investigation
          Version = AggregateVersion v
          Payload = EventPayload "InvestigationStarted" }

    let verifiedAt (n: int) : VerifiedInvestigation =
        { Investigation = { Id = investigation; Version = AggregateVersion 1L }
          History = [ event 1L ]
          ReadAt = head n
          Bytes = StorageV1.encodeHistory investigation [ event 1L ] }

    let command (n: int) : CommandEnvelope =
        { CommandId = guid (5000 + n)
          Investigation = investigation
          ExpectedVersion = None }

    let token = "sm-test-token-not-a-credential"

    let connection: ConnectionContext =
        { Repository = { Owner = "causa-test"; Name = "data"; Branch = "main" }
          Credential = Credential.create token }

    let ctx (n: int64) : RequestContext =
        { Connection = connection
          NextId = RequestId n
          MutationInFlightElsewhere = false }

    let busy (n: int64) = { ctx n with MutationInFlightElsewhere = true }
    let tab = guid 9

    let setup (offline: bool) : SaveSetup =
        { Codec = StorageV1.codec
          FileSetOf = SaveProcess.pendingCommandFiles
          Tab = tab
          NextMarker = Markers.forTab tab
          KnownOffline = offline }

    let online = SaveProcess.create (setup false)
    let offline = SaveProcess.create (setup true)

    let confirmation (n: int) =
        let files = StorageV1.encodeHistory investigation [ event 1L ]

        Confirmation.ofReadBack investigation (head n) DateTimeOffset.UnixEpoch files files
        |> Option.defaultWith (fun () -> failwith "identical bytes must confirm")

    let start = SaveProcess.initial (verifiedAt 1)

    let sessionIn (state: SaveState) =
        { start with
            State = state
            Unsaved = [ command 1 ] }

    let saving = { sessionIn SaveState.Saving with Outstanding = Some(RequestId 7L, MutationAnswer); Mutations = 1 }

    let sends (effects: SaveEffect list) =
        effects
        |> List.choose (function
            | Send r -> Some r
            | _ -> None)

    let isMutation (r: HttpRequest) = r.Body |> Option.exists (fun b -> b.Contains "createCommitOnBranch")
    let mutations effects = sends effects |> List.filter isMutation

    let schedules (effects: SaveEffect list) =
        effects
        |> List.choose (function
            | ScheduleReconcileRead(_, ms) -> Some ms
            | _ -> None)

    let ok (result: Result<'s * SaveEffect list, SaveFault>) =
        match result with
        | Ok value -> value
        | Error fault -> failwithf "a legal transition failed closed: %A" fault

    let faults (result: Result<'s * SaveEffect list, SaveFault>) =
        match result with
        | Ok(state, effects) -> failwithf "an illegal pair was accepted: %A %A" state effects
        | Error fault -> fault

    /// Every (state, event) name pair of a machine, minus the legal ones, must fail closed and name both.
    let illegalPairs (states: (string * 's) list) (events: (string * 'i) list) (legal: Set<string * string>) =
        [ for (sn, s) in states do
              for (en, e) in events do
                  if not (legal.Contains(sn, en)) then
                      yield sn, s, en, e ]

module SMCONFLICT =
    open Support

    let private remote = verifiedAt 21
    let private step view input = ok (online.StepConflict (ctx 50L) view input)

    let private states: (string * ConflictView) list =
        [ "ReadingRemote", ReadingRemote(RequestId 1L)
          "Reviewable", Reviewable remote
          "Reapplying", Reapplying remote
          "Reapplied", Reapplied(remote, [])
          "ConfirmingDiscard", ConfirmingDiscard remote
          "Discarded", Discarded remote
          "RemoteUnavailable", RemoteUnavailable
          "Unresolvable", Unresolvable "deleted" ]

    let private events: (string * ConflictInput) list =
        [ "RemoteVerified", ConflictInput.RemoteVerified remote
          "RemoteReadFailed", ConflictInput.RemoteReadFailed LostAfterSending
          "RemoteMissingOrInvalid", ConflictInput.RemoteMissingOrInvalid None
          "ReadAgain", ConflictInput.ReadAgain
          "ReviewChosen", ConflictInput.ReviewChosen
          "ReapplyChosen", ConflictInput.ReapplyChosen
          "ReapplyDone", ConflictInput.ReapplyDone([ command 1 ], [])
          "DiscardChosen", ConflictInput.DiscardChosen
          "DiscardConfirmed", ConflictInput.DiscardConfirmed
          "DiscardCancelled", ConflictInput.DiscardCancelled
          "ExportChosen", ConflictInput.ExportChosen
          "SupersededReply", ConflictInput.SupersededReply(RequestId 99L) ]

    let legal =
        set
            [ "ReadingRemote", "RemoteVerified"; "ReadingRemote", "RemoteReadFailed"; "ReadingRemote", "RemoteMissingOrInvalid"
              "ReadingRemote", "SupersededReply"; "Reviewable", "ReadAgain"; "Reviewable", "ReviewChosen"; "Reviewable", "ReapplyChosen"
              "Reviewable", "DiscardChosen"; "Reviewable", "SupersededReply"; "Reapplying", "ReapplyDone"
              "ConfirmingDiscard", "DiscardConfirmed"; "ConfirmingDiscard", "DiscardCancelled"; "RemoteUnavailable", "ReadAgain"
              "Unresolvable", "ReadAgain"; "Unresolvable", "ExportChosen" ]

    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        let pairs = illegalPairs states events legal
        Assert.Equal(81, pairs.Length)

        for (sn, s, en, e) in pairs do
            for machine in [ online; offline ] do
                let fault = faults (machine.StepConflict (ctx 1L) s e)
                Assert.Equal("SM-CONFLICT", fault.Machine)
                Assert.Equal(sn, fault.State)
                Assert.Equal(en, fault.Input)

    [<Fact>]
    let ``T-C01`` () =
        let view, effects = step (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteVerified remote)
        Assert.Equal(Reviewable remote, view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C02`` () =
        let view, effects = step (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteReadFailed TimedOut)
        Assert.Equal(RemoteUnavailable, view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C03`` () =
        match step (ReadingRemote(RequestId 1L)) (ConflictInput.RemoteMissingOrInvalid(Some(DigestMismatch "eventsSha256"))) with
        | Unresolvable _, effects -> Assert.Empty(sends effects)
        | other -> Assert.Fail(sprintf "%A" other)

    [<Fact>]
    let ``T-C04`` () =
        let view, _ = step RemoteUnavailable ConflictInput.ReadAgain
        Assert.Equal(ReadingRemote(RequestId 50L), view)
        // The request the engine sends for it reads the GitHub version at the branch head; it is a query, never a write.
        let request = ConflictMachine.readRequest (ctx 50L) investigation
        Assert.Equal(RequestId 50L, request.Id)
        Assert.False(isMutation request)
        Assert.True(Result.isError (offline.StepConflict (ctx 50L) RemoteUnavailable ConflictInput.ReadAgain), "offline: no read")

    [<Fact>]
    let ``T-C05`` () =
        let view, effects = step (Reviewable remote) ConflictInput.ReviewChosen
        Assert.Equal(Reviewable remote, view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C06`` () =
        let view, effects = step (Reviewable remote) ConflictInput.ReapplyChosen
        Assert.Equal(Reapplying remote, view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C07`` () =
        let refused = [ { Command = command 2; Reason = "blocked" } ]
        let view, effects = step (Reapplying remote) (ConflictInput.ReapplyDone([ command 1 ], refused))
        Assert.Equal(Reapplied(remote, refused), view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C08`` () =
        let view, effects = step (Reviewable remote) ConflictInput.DiscardChosen
        Assert.Equal(ConfirmingDiscard remote, view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C09`` () =
        let view, effects = step (ConfirmingDiscard remote) ConflictInput.DiscardConfirmed
        Assert.Equal(Discarded remote, view)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-C10`` () =
        let view, _ = step (ConfirmingDiscard remote) ConflictInput.DiscardCancelled
        Assert.Equal(Reviewable remote, view)

    [<Fact>]
    let ``T-C11`` () =
        let view, effects = step (Reviewable remote) ConflictInput.ReadAgain
        Assert.Equal(ReadingRemote(RequestId 50L), view)

        match sends effects with
        | [ request ] ->
            Assert.Equal(RequestId 50L, request.Id)
            Assert.False(isMutation request)
            Assert.Contains("refs/heads/main", defaultArg request.Body "")
        | other -> Assert.Fail(sprintf "expected one read: %A" other)

    [<Fact>]
    let ``T-C12`` () =
        let view, effects = step (Unresolvable "deleted") ConflictInput.ExportChosen
        Assert.Equal(Unresolvable "deleted", view)
        Assert.Empty(sends effects)
        Assert.True(effects |> List.exists (function ExportCommands _ -> true | _ -> false))

    [<Fact>]
    let ``T-C13`` () =
        let view, _ = step (Unresolvable "deleted") ConflictInput.ReadAgain
        Assert.Equal(ReadingRemote(RequestId 50L), view)
        Assert.True(Result.isError (offline.StepConflict (ctx 50L) (Unresolvable "x") ConflictInput.ReadAgain), "offline: no read")

    [<Fact>]
    let ``T-C14`` () =
        let view, effects = step (ReadingRemote(RequestId 3L)) (ConflictInput.SupersededReply(RequestId 1L))
        Assert.Equal(ReadingRemote(RequestId 3L), view)
        Assert.Empty(effects)
        // Interpretation: an answer to another request is the superseded reply.
        match online.InterpretConflict (ctx 4L) (ReadingRemote(RequestId 3L)) (Answered(RequestId 1L, 200, [], "{}")) with
        | ConflictEvent(ConflictInput.SupersededReply(RequestId 1L)) -> ()
        | other -> Assert.Fail(sprintf "%A" other)

    [<Fact>]
    let ``T-C15`` () =
        let view, effects = step (Reviewable remote) (ConflictInput.SupersededReply(RequestId 1L))
        Assert.Equal(Reviewable remote, view)
        Assert.Empty(effects)

    /// The read of the GitHub version decodes and verifies with the storage codec (T-C01/T-C03 from an answer).
    [<Fact>]
    let InterpretVerifiesTheGitHubVersion () =
        let files = StorageV1.encodeHistory investigation [ event 1L ]
        let text (b: byte[]) = Text.Json.JsonSerializer.Serialize(Text.Encoding.UTF8.GetString b)
        let blob b = sprintf """{"object":{"text":%s,"isTruncated":false}}""" (text b)
        let (CommitOid oid) = head 5

        let body manifest =
            sprintf
                """{"data":{"repository":{"ref":{"target":{"oid":"%s","manifest":%s,"events":%s,"snapshot":%s}}}}}"""
                oid
                manifest
                (blob files.Events)
                (blob files.Snapshot)

        match online.InterpretConflict (ctx 2L) (ReadingRemote(RequestId 1L)) (Answered(RequestId 1L, 200, [], body (blob files.Manifest))) with
        | ConflictEvent(ConflictInput.RemoteVerified v) -> Assert.Equal(head 5, v.ReadAt)
        | other -> Assert.Fail(sprintf "%A" other)

        match online.InterpretConflict (ctx 2L) (ReadingRemote(RequestId 1L)) (Answered(RequestId 1L, 200, [], body "null")) with
        | ConflictEvent(ConflictInput.RemoteMissingOrInvalid None) -> ()
        | other -> Assert.Fail(sprintf "%A" other)

        match online.InterpretConflict (ctx 2L) (ReadingRemote(RequestId 1L)) (NotAnswered(RequestId 1L, TimedOut)) with
        | ConflictEvent(ConflictInput.RemoteReadFailed TimedOut) -> ()
        | other -> Assert.Fail(sprintf "%A" other)
