// Owner: persistence. T19: SM-CONFLICT (state-machine-contracts.json): the resolution of one genuine conflict.
// Pure. No transition of this machine writes to GitHub (INV-08): it reads the GitHub version, reviews, reapplies
// through the engine, or discards after confirmation.
namespace Causa.Persistence

open Causa.Persistence.ErrorMatrix

[<RequireQualifiedAccess>]
module ConflictMachine =
    let name = "SM-CONFLICT"

    let stateName (view: ConflictView) =
        match view with
        | ReadingRemote _ -> "ReadingRemote"
        | Reviewable _ -> "Reviewable"
        | Reapplying _ -> "Reapplying"
        | Reapplied _ -> "Reapplied"
        | ConfirmingDiscard _ -> "ConfirmingDiscard"
        | Discarded _ -> "Discarded"
        | RemoteUnavailable -> "RemoteUnavailable"
        | Unresolvable _ -> "Unresolvable"

    let inputName (input: ConflictInput) =
        match input with
        | ConflictInput.RemoteVerified _ -> "RemoteVerified"
        | ConflictInput.RemoteReadFailed _ -> "RemoteReadFailed"
        | ConflictInput.RemoteMissingOrInvalid _ -> "RemoteMissingOrInvalid"
        | ConflictInput.ReadAgain -> "ReadAgain"
        | ConflictInput.ReviewChosen -> "ReviewChosen"
        | ConflictInput.ReapplyChosen -> "ReapplyChosen"
        | ConflictInput.ReapplyDone _ -> "ReapplyDone"
        | ConflictInput.DiscardChosen -> "DiscardChosen"
        | ConflictInput.DiscardConfirmed -> "DiscardConfirmed"
        | ConflictInput.DiscardCancelled -> "DiscardCancelled"
        | ConflictInput.ExportChosen -> "ExportChosen"
        | ConflictInput.SupersededReply _ -> "SupersededReply"

    let private fault view input reason =
        Error
            { Machine = name
              State = stateName view
              Input = inputName input
              Reason = reason }

    /// The read of the GitHub version at the branch's current head (head oid and the three files in one query).
    /// Used by SM-SAVE when it starts SM-CONFLICT, by T-C11, and by the engine for T-C04/T-C13: RemoteUnavailable and
    /// Unresolvable do not name their investigation (frozen ConflictView), so the engine sends this request with the
    /// RequestId the transition recorded.
    let readRequest (context: RequestContext) (investigation: Causa.Domain.InvestigationId) : HttpRequest =
        GitHubRequests.fileSetAtBranch
            context.NextId
            context.Connection
            (StoragePaths.manifest investigation)
            (StoragePaths.events investigation)
            (StoragePaths.snapshot investigation)

    let private reading (context: RequestContext) (investigation: Causa.Domain.InvestigationId) =
        ReadingRemote context.NextId, [ Send(readRequest context investigation) ]

    let private reasonOf (failure: VerificationFailure option) =
        match failure with
        | None -> "the investigation is missing on the branch"
        | Some f -> sprintf "the GitHub version does not verify: %A" f

    let step (knownOffline: bool) (context: RequestContext) (view: ConflictView) (input: ConflictInput) : Result<ConflictView * SaveEffect list, SaveFault> =
        match view, input with
        // T-C01
        | ReadingRemote _, ConflictInput.RemoteVerified remote -> Ok(Reviewable remote, [])
        // T-C02: keep changes; never Discard-only.
        | ReadingRemote _, ConflictInput.RemoteReadFailed _ ->
            Ok(RemoteUnavailable, [ Announce(Assertive, "GitHub could not be read. Your changes are kept; read again when online.") ])
        // T-C03
        | ReadingRemote _, ConflictInput.RemoteMissingOrInvalid failure -> Ok(Unresolvable(reasonOf failure), [])
        // T-C14, T-C15: a reply that is not current is discarded.
        | ReadingRemote _, ConflictInput.SupersededReply _
        | Reviewable _, ConflictInput.SupersededReply _ -> Ok(view, [])
        // T-C11: re-read at the branch's current head.
        | Reviewable remote, ConflictInput.ReadAgain -> Ok(reading context remote.Investigation.Id)
        // T-C05: show both versions (a view concern; nothing changes here).
        | Reviewable _, ConflictInput.ReviewChosen -> Ok(view, [])
        // T-C06: the engine re-decides each unsaved command at the held GitHub version.
        | Reviewable remote, ConflictInput.ReapplyChosen -> Ok(Reapplying remote, [])
        // T-C08
        | Reviewable remote, ConflictInput.DiscardChosen ->
            Ok(ConfirmingDiscard remote, [ Announce(Assertive, "Discard your unsaved changes and load the GitHub version?") ])
        // T-C07: accepted commands become unsaved changes on the held version (SM-SAVE ConflictResolvedReapplied).
        | Reapplying remote, ConflictInput.ReapplyDone(_, refused) -> Ok(Reapplied(remote, refused), [])
        // T-C09
        | ConfirmingDiscard remote, ConflictInput.DiscardConfirmed -> Ok(Discarded remote, [])
        // T-C10
        | ConfirmingDiscard remote, ConflictInput.DiscardCancelled -> Ok(Reviewable remote, [])
        // T-C04, T-C13: a new request identity; the engine sends readRequest with it (see readRequest).
        | RemoteUnavailable, ConflictInput.ReadAgain when not knownOffline -> Ok(ReadingRemote context.NextId, [])
        | Unresolvable _, ConflictInput.ReadAgain when not knownOffline -> Ok(ReadingRemote context.NextId, [])
        // T-C12: offer the unsaved commands as a file; nothing is written to GitHub or browser storage. The view
        // does not hold the commands, so the effect is empty and the engine fills it from the session's unsaved
        // commands (Unsaved @ AfterInFlight).
        | Unresolvable _, ConflictInput.ExportChosen -> Ok(view, [ ExportCommands [] ])
        | _ -> fault view input "not a transition of SM-CONFLICT in this state (fail closed)"

    /// The answer to the outstanding read of the GitHub version. Any answer not to the current read is superseded.
    let interpret (codec: StorageCodec) (_: RequestContext) (view: ConflictView) (outcome: HttpOutcome) : ConflictInterpretation =
        let answerId =
            match outcome with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> id

        match view with
        | ReadingRemote current when current = answerId ->
            let classified = classify (Read "graphql:conflict-file-set") outcome

            match classified.Result, outcome with
            | Ok answer, _ ->
                match Answers.fileSet answer.Body with
                | Some read ->
                    match read.Manifest, read.Events, read.Snapshot with
                    | Some m, Some e, Some s ->
                        match codec.Verify read.At { Manifest = m; Events = e; Snapshot = s } with
                        | Ok remote -> ConflictEvent(ConflictInput.RemoteVerified remote)
                        | Error failures -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid(List.tryHead failures))
                    | _ -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid None)
                | None when Answers.refMissing answer.Body -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid None)
                | None -> ConflictEvent(ConflictInput.RemoteReadFailed LostAfterSending)
            | Error(NetworkUnavailable failure), _ -> ConflictEvent(ConflictInput.RemoteReadFailed failure)
            | Error(NotFound _), _ -> ConflictEvent(ConflictInput.RemoteMissingOrInvalid None)
            | Error _, NotAnswered(_, failure) -> ConflictEvent(ConflictInput.RemoteReadFailed failure)
            | Error _, Answered _ -> ConflictEvent(ConflictInput.RemoteReadFailed LostAfterSending)
        | _ -> ConflictEvent(ConflictInput.SupersededReply answerId)
