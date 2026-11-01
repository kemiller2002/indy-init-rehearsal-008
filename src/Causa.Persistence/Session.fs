// Owner: persistence. T19: SM-SAVE (state-machine-contracts.json) and the save process: one commit per save,
// read-back confirmation, automatic rebase over unrelated changes, conflict, reconciliation of unknown outcomes, and
// the request bounds (ADR-R7-001, PERSIST-006..PERSIST-016). Step and Interpret are pure functions of their inputs;
// the only effect they use is the tab's marker source, injected at the edge (SaveSetup.NextMarker).
namespace Causa.Persistence

open System
open Causa.Domain
open Causa.Persistence.ErrorMatrix

/// What the save process needs from its host.
type SaveSetup =
    { Codec: StorageCodec
      /// The file set a save writes for the session's in-flight commands (InvestigationSession.Unsaved). Deterministic
      /// for one session: the read-back compares the bytes re-read at the commit with it.
      FileSetOf: InvestigationSession -> FileSet
      /// The tab's random session identifier (PERSIST-014).
      Tab: Guid
      /// The tab's marker source (Markers.forTab Tab): a new marker for every mutation.
      NextMarker: unit -> WriteMarker
      /// SM-NETWORK says the tab is offline. The engine builds the machine for each update with its current network
      /// state, so the "known offline" guards (T-S02, T-S05, T-S52, T-S55, T-S57, T-L02, T-C04, T-C13) are decided
      /// from a value, not from an effect.
      KnownOffline: bool }

[<RequireQualifiedAccess>]
module SaveProcess =
    let name = "SM-SAVE"

    /// Bounds from the matrix (R-11): at most three automatic rebases (GH-STALE-HEAD), so four mutations per save
    /// request (PERSIST-010), and three automatic reconciliation reads (GH-NETWORK-AFTER) at 2, 4 and 8 s (PERSIST-016).
    let maxRebases = maxAttempts GhStaleHead
    let maxMutations = maxRebases + 1
    let maxAutomaticReads = maxAttempts GhNetworkAfter
    let readDelays = [ 2000; 4000; 8000 ]

    let stateName (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "UnknownOutcome"
        | SaveState.SaveFailed _ -> "SaveFailed"

    let inputName (input: SaveInput) =
        match input with
        | SaveInput.Edit _ -> "Edit"
        | SaveInput.SaveRequested -> "SaveRequested"
        | SaveInput.MutationSent _ -> "MutationSent"
        | SaveInput.ReadBackMatched _ -> "ReadBackMatched"
        | SaveInput.ReadBackMismatched _ -> "ReadBackMismatched"
        | SaveInput.StaleHeadUnrelated _ -> "StaleHeadUnrelated"
        | SaveInput.StaleHeadSameInvestigation _ -> "StaleHeadSameInvestigation"
        | SaveInput.ProviderRefused _ -> "ProviderRefused"
        | SaveInput.CredentialRefused -> "CredentialRefused"
        | SaveInput.AnswerLost -> "AnswerLost"
        | SaveInput.FailedBeforeSending -> "FailedBeforeSending"
        | SaveInput.ReconcileMarkerBytesMatch _ -> "ReconcileMarkerBytesMatch"
        | SaveInput.ReconcileMarkerBytesDiffer _ -> "ReconcileMarkerBytesDiffer"
        | SaveInput.ReconcileNothingWrittenHeadUnchanged -> "ReconcileNothingWrittenHeadUnchanged"
        | SaveInput.ReconcileNothingWrittenHeadMoved _ -> "ReconcileNothingWrittenHeadMoved"
        | SaveInput.ReconcileHistoryRewritten -> "ReconcileHistoryRewritten"
        | SaveInput.ReconcileReadFailed -> "ReconcileReadFailed"
        | SaveInput.ReconcileReadsSpent -> "ReconcileReadsSpent"
        | SaveInput.CheckAgain -> "CheckAgain"
        | SaveInput.WentOffline -> "WentOffline"
        | SaveInput.WentOnline -> "WentOnline"
        | SaveInput.ConflictResolvedReapplied _ -> "ConflictResolvedReapplied"
        | SaveInput.ConflictResolvedDiscarded _ -> "ConflictResolvedDiscarded"
        | SaveInput.SupersededReply _ -> "SupersededReply"
        | SaveInput.QueuedSaveStarted -> "QueuedSaveStarted"
        | SaveInput.RebaseReadFailed -> "RebaseReadFailed"
        | SaveInput.ReadBackReadFailed -> "ReadBackReadFailed"

    let private fault (session: InvestigationSession) (input: SaveInput) (reason: string) =
        Error
            { Machine = name
              State = stateName session.State
              Input = inputName input
              Reason = reason }

    let initial (verified: VerifiedInvestigation) : InvestigationSession =
        { Id = verified.Investigation.Id
          BaseHead = verified.ReadAt
          State = SaveState.Saved(VerifiedAt verified)
          Unsaved = []
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0 }

    let private paths (id: InvestigationId) =
        [ StoragePaths.manifest id; StoragePaths.events id; StoragePaths.snapshot id ]

    let private filesInOrder (files: FileSet) = [ files.Manifest; files.Events; files.Snapshot ]

    let private idText (InvestigationId g) = g.ToString("D").ToLowerInvariant()

    /// T-S04 effects: one commit (events, manifest, snapshot) under the investigation's prefix, expectedHeadOid = the
    /// head given, a new write marker, sent as createCommitOnBranch. Records the mutation as Outstanding.
    let private sendMutation (setup: SaveSetup) (context: RequestContext) (session: InvestigationSession) (expected: CommitOid) =
        let files = setup.FileSetOf session
        let additions = List.zip (paths session.Id) (filesInOrder files)
        let marker = setup.NextMarker()

        let request =
            GitHubRequests.createCommitWithTrailer
                context.NextId
                context.Connection
                expected
                (sprintf "Causa: save investigation %s" (idText session.Id))
                (Markers.trailer marker)
                additions

        { session with
            State = SaveState.Saving
            BaseHead = expected
            Outstanding = Some(context.NextId, MutationAnswer)
            Mutations = session.Mutations + 1 },
        [ Send request ]

    /// A new save request: fresh bounds (T-S04, T-S35, T-S54).
    let private newRequest (session: InvestigationSession) =
        { session with Mutations = 0; RebaseAttempts = 0; ReconcileReads = 0 }

    /// Changes made during the save join the unsaved changes when the save stops without confirming anything.
    let private settle (session: InvestigationSession) (state: SaveState) =
        { session with
            State = state
            Unsaved = session.Unsaved @ session.AfterInFlight
            AfterInFlight = []
            Outstanding = None }

    let private failed session failure text =
        settle session (SaveState.SaveFailed failure), [ Announce(Assertive, text) ]

    /// T-S11/T-S12, T-S21/T-S22: advance the base head to the confirmed commit; changes made during the save stay
    /// unsaved.
    let private confirmed (session: InvestigationSession) (input: SaveInput) (confirmation: Confirmation.T) =
        if confirmation.InvestigationId <> session.Id then
            fault session input "the confirmation names another investigation"
        else
            let later = session.AfterInFlight

            Ok(
                { session with
                    State = (if List.isEmpty later then SaveState.Saved(Confirmed confirmation) else SaveState.Unsaved)
                    BaseHead = confirmation.CommitOid
                    Unsaved = later
                    AfterInFlight = []
                    Outstanding = None },
                [ Announce(Polite, "Saved to GitHub.") ]
            )

    /// Schedule the next automatic reconciliation read, if any is left (PERSIST-016).
    let private scheduleRead (session: InvestigationSession) =
        if session.ReconcileReads < maxAutomaticReads then
            let delay = readDelays[min session.ReconcileReads (readDelays.Length - 1)]

            { session with
                ReconcileReads = session.ReconcileReads + 1
                State = SaveState.UnknownOutcome(maxAutomaticReads - session.ReconcileReads - 1)
                Outstanding = None },
            [ ScheduleReconcileRead(session.Id, delay) ]
        else
            { session with State = SaveState.UnknownOutcome 0; Outstanding = None },
            [ Announce(Polite, "GitHub's answer is unknown. Choose Check again.") ]

    /// T-S19, T-S58, T-S59: the outcome is unknown; no mutation is sent until reconciliation decides.
    let private unknown (session: InvestigationSession) (text: string) =
        let next, effects = scheduleRead session
        next, Announce(Polite, text) :: effects

    /// One reconciliation read: the branch head (bypassing caches is the host's fetch mode for ref reads).
    let private reconcileRead (context: RequestContext) (session: InvestigationSession) =
        { session with Outstanding = Some(context.NextId, ReconcileHead) },
        [ Send(GitHubRequests.branchHead context.NextId context.Connection) ]

    /// T-S16, T-S27: start SM-CONFLICT by reading the GitHub version; nothing more is sent.
    let private conflict (context: RequestContext) (session: InvestigationSession) =
        settle session (SaveState.Conflict(ReadingRemote context.NextId)),
        [ Send(ConflictMachine.readRequest context session.Id)
          Announce(Assertive, "This investigation changed on GitHub. Your changes are kept.") ]

    /// T-S14 / T-S26: rebase over unrelated changes within the bounds, else Save failed (T-S15 / T-S56).
    let private rebase setup context (session: InvestigationSession) (freshHead: CommitOid) =
        if session.RebaseAttempts < maxRebases && session.Mutations < maxMutations then
            sendMutation setup context { session with RebaseAttempts = session.RebaseAttempts + 1 } freshHead
        else
            failed session RepositoryKeptChanging "Save failed: the repository kept changing. Your changes are kept."

    let private append (session: InvestigationSession) (command: CommandEnvelope) =
        { session with Unsaved = session.Unsaved @ [ command ] }

    let private appendAfterInFlight (session: InvestigationSession) (command: CommandEnvelope) =
        { session with AfterInFlight = session.AfterInFlight @ [ command ] }

    let private causeOf (error: ProviderError) =
        match error with
        | PermissionDenied _ -> "Save failed: this credential cannot write to the repository."
        | ValidationFailed _ -> "Save failed: GitHub refused the change as invalid."
        | RateLimited _ -> "Save failed: GitHub's rate limit was reached. Try again later."
        | NotFound _ -> "Save failed: the branch was not found."
        | HistoryRewritten _ -> "Save failed: the branch history was rewritten."
        | _ -> "Save failed. Your changes are kept."

    let step (setup: SaveSetup) (context: RequestContext) (session: InvestigationSession) (input: SaveInput) =
        let ok s = Ok(s, [])
        let busy = context.MutationInFlightElsewhere
        let offline = setup.KnownOffline
        let toOffline text = Ok(settle session SaveState.Offline, [ Announce(Assertive, text) ])

        match session.State, input with
        // ----- Saved
        | SaveState.Saved _, SaveInput.Edit command when offline -> ok { append session command with State = SaveState.Offline } // T-S02
        | SaveState.Saved _, SaveInput.Edit command -> ok { append session command with State = SaveState.Unsaved } // T-S01
        | SaveState.Saved _, SaveInput.WentOffline -> ok session // T-S41
        | SaveState.Saved _, SaveInput.WentOnline -> ok session // T-S42
        // ----- Unsaved
        | SaveState.Unsaved, SaveInput.Edit command -> ok (append session command) // T-S03
        | SaveState.Unsaved, SaveInput.SaveRequested when offline -> toOffline "Offline: nothing was sent." // T-S05
        | SaveState.Unsaved, SaveInput.SaveRequested when busy ->
            Ok(session, [ Announce(Polite, "Waiting for another save in this tab to finish.") ]) // T-S53
        | SaveState.Unsaved, SaveInput.SaveRequested -> Ok(sendMutation setup context (newRequest session) session.BaseHead) // T-S04
        | SaveState.Unsaved, SaveInput.WentOffline -> ok { session with State = SaveState.Offline; Outstanding = None } // T-S06
        | SaveState.Unsaved, SaveInput.WentOnline -> ok session // T-S43
        | SaveState.Unsaved, SaveInput.QueuedSaveStarted when offline -> toOffline "Offline: the waiting save was not sent." // T-S55
        | SaveState.Unsaved, SaveInput.QueuedSaveStarted when not busy ->
            Ok(sendMutation setup context (newRequest session) session.BaseHead) // T-S54
        // ----- Saving
        | SaveState.Saving, SaveInput.Edit command -> ok (appendAfterInFlight session command) // T-S10
        | SaveState.Saving, SaveInput.MutationSent id ->
            match session.Outstanding with
            | Some(current, MutationAnswer) when current = id -> ok session // T-S61
            | _ -> fault session input "MutationSent does not name the outstanding mutation"
        | SaveState.Saving, SaveInput.ReadBackMatched confirmation -> confirmed session input confirmation // T-S11, T-S12
        | SaveState.Saving, SaveInput.ReadBackMismatched commit ->
            Ok(failed session (BytesDiffer commit) "Save failed: GitHub holds different bytes. Your changes are kept.") // T-S13
        | SaveState.Saving, SaveInput.StaleHeadUnrelated freshHead -> Ok(rebase setup context session freshHead) // T-S14, T-S15
        | SaveState.Saving, SaveInput.StaleHeadSameInvestigation _ -> Ok(conflict context session) // T-S16
        | SaveState.Saving, SaveInput.ProviderRefused error -> Ok(failed session (Refused error) (causeOf error)) // T-S17
        | SaveState.Saving, SaveInput.CredentialRefused ->
            Ok(failed session CredentialNotAccepted "Save failed: GitHub did not accept the credential. Reconnect to save.") // T-S18
        | SaveState.Saving, SaveInput.AnswerLost -> Ok(unknown session "GitHub's answer was lost. Checking what was saved.") // T-S19
        | SaveState.Saving, SaveInput.FailedBeforeSending ->
            Ok(settle session SaveState.Offline, [ Announce(Assertive, "Offline: nothing was sent. Save again when online.") ]) // T-S20
        | SaveState.Saving, SaveInput.WentOffline -> ok session // T-S45
        | SaveState.Saving, SaveInput.WentOnline -> ok session // T-S46
        | SaveState.Saving, SaveInput.SupersededReply _ -> ok session // T-S39
        | SaveState.Saving, SaveInput.RebaseReadFailed when offline -> toOffline "Offline: nothing of this save was written." // T-S57
        | SaveState.Saving, SaveInput.RebaseReadFailed ->
            Ok(unknown session "GitHub could not be read after a stale head. Checking what was saved.") // T-S59
        | SaveState.Saving, SaveInput.ReadBackReadFailed ->
            Ok(unknown session "The saved commit could not be re-read. Checking what was saved.") // T-S58
        // ----- Offline
        | SaveState.Offline, SaveInput.Edit command -> ok (append session command) // T-S07
        | SaveState.Offline, SaveInput.SaveRequested when offline -> Ok(session, [ Announce(Assertive, "Offline: nothing was sent.") ]) // T-S08
        | SaveState.Offline, SaveInput.WentOffline -> ok session // T-S44
        | SaveState.Offline, SaveInput.WentOnline -> ok { session with State = SaveState.Unsaved } // T-S09
        | SaveState.Offline, SaveInput.QueuedSaveStarted -> ok session // T-S60
        // ----- Conflict
        | SaveState.Conflict _, SaveInput.Edit command -> ok (append session command) // T-S36
        | SaveState.Conflict _, SaveInput.WentOffline -> ok session // T-S48
        | SaveState.Conflict RemoteUnavailable, SaveInput.WentOnline when not offline ->
            Ok(
                { session with State = SaveState.Conflict(ReadingRemote context.NextId) },
                [ Send(ConflictMachine.readRequest context session.Id) ]
            ) // T-S49
        | SaveState.Conflict _, SaveInput.WentOnline -> ok session // T-S49
        | SaveState.Conflict(Reapplied(remote, _)), SaveInput.ConflictResolvedReapplied accepted ->
            ok
                { session with
                    State = SaveState.Unsaved
                    BaseHead = remote.ReadAt
                    Unsaved = accepted
                    AfterInFlight = []
                    Outstanding = None } // T-S37
        | SaveState.Conflict(Discarded remote), SaveInput.ConflictResolvedDiscarded verified when verified = remote ->
            Ok(
                { session with
                    State = SaveState.Saved(VerifiedAt verified)
                    BaseHead = verified.ReadAt
                    Unsaved = []
                    AfterInFlight = []
                    Outstanding = None },
                [ Announce(Polite, "Your changes were discarded; the GitHub version is loaded.") ]
            ) // T-S38
        // ----- UnknownOutcome
        | SaveState.UnknownOutcome _, SaveInput.Edit command -> ok (appendAfterInFlight session command) // T-S33
        | SaveState.UnknownOutcome _, SaveInput.StaleHeadSameInvestigation _ -> Ok(conflict context session) // T-S27
        | SaveState.UnknownOutcome _, SaveInput.ReconcileMarkerBytesMatch confirmation -> confirmed session input confirmation // T-S21, T-S22
        | SaveState.UnknownOutcome _, SaveInput.ReconcileMarkerBytesDiffer commit ->
            Ok(failed session (BytesDiffer commit) "Save failed: a commit of this save holds different bytes. Not saved.") // T-S23
        | SaveState.UnknownOutcome _, SaveInput.ReconcileNothingWrittenHeadUnchanged ->
            if session.Mutations < maxMutations then
                Ok(sendMutation setup context session session.BaseHead) // T-S24
            else
                Ok(failed session RepositoryKeptChanging "Save failed: the mutation limit was reached. Your changes are kept.") // T-S25
        | SaveState.UnknownOutcome _, SaveInput.ReconcileNothingWrittenHeadMoved head -> Ok(rebase setup context session head) // T-S26, T-S56
        | SaveState.UnknownOutcome _, SaveInput.ReconcileHistoryRewritten ->
            Ok(failed session HistoryWasRewritten "Save failed: the branch history was rewritten. Your changes are kept.") // T-S28
        | SaveState.UnknownOutcome _, SaveInput.ReconcileReadFailed when session.ReconcileReads < maxAutomaticReads ->
            Ok(scheduleRead session) // T-S29
        | SaveState.UnknownOutcome _, SaveInput.ReconcileReadsSpent ->
            Ok(
                { session with State = SaveState.UnknownOutcome 0; ReconcileReads = maxAutomaticReads; Outstanding = None },
                [ Announce(Polite, "GitHub's answer is still unknown. Choose Check again.") ]
            ) // T-S30
        | SaveState.UnknownOutcome left, SaveInput.CheckAgain when left = 0 || session.ReconcileReads >= maxAutomaticReads ->
            Ok(reconcileRead context session) // T-S31
        | SaveState.UnknownOutcome _, SaveInput.WentOffline -> ok session // T-S47
        | SaveState.UnknownOutcome _, SaveInput.WentOnline -> Ok(reconcileRead context session) // T-S32
        | SaveState.UnknownOutcome _, SaveInput.SupersededReply _ -> ok session // T-S40
        // ----- SaveFailed
        | SaveState.SaveFailed _, SaveInput.Edit command -> ok (append session command) // T-S34
        | SaveState.SaveFailed _, SaveInput.SaveRequested when offline -> toOffline "Offline: nothing was sent." // T-S52
        | SaveState.SaveFailed _, SaveInput.SaveRequested when not busy ->
            Ok(sendMutation setup context (newRequest session) session.BaseHead) // T-S35
        | SaveState.SaveFailed _, SaveInput.WentOffline -> ok session // T-S50
        | SaveState.SaveFailed _, SaveInput.WentOnline -> ok session // T-S51
        | _ -> fault session input "not a transition of SM-SAVE in this state, or its guard does not hold (fail closed)"

    /// The timer of ScheduleReconcileRead fired.
    let reconcileDue (context: RequestContext) (session: InvestigationSession) =
        match session.State, session.Outstanding with
        | SaveState.UnknownOutcome _, None -> Ok(reconcileRead context session)
        | SaveState.UnknownOutcome _, Some _ -> Ok(session, [])
        | _ ->
            Error
                { Machine = name
                  State = stateName session.State
                  Input = "ReconcileDue"
                  Reason = "no reconciliation is pending" }

    // ----- Interpretation of answers

    let private commitTime (outcome: HttpOutcome) =
        match outcome with
        | Answered(_, _, headers, _) ->
            headers
            |> List.tryFind (fun (k, _) -> String.Equals(k, "date", StringComparison.OrdinalIgnoreCase))
            |> Option.bind (fun (_, v) ->
                match DateTimeOffset.TryParse(v, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
                | true, t -> Some t
                | _ -> None)
            |> Option.defaultValue DateTimeOffset.UnixEpoch
        | NotAnswered _ -> DateTimeOffset.UnixEpoch

    type private FileCheck =
        | NextFile of RepoPath
        | AllMatch of Confirmation.T
        | Differ
        | Unreadable

    /// One file of a re-read at `commit`: compared with the bytes written; the files are read manifest, events,
    /// snapshot, each a single request.
    let private checkFile (setup: SaveSetup) (session: InvestigationSession) (commit: CommitOid) (outcome: HttpOutcome) =
        match (classify (Read "rest:contents") outcome).Result with
        | Ok answer ->
            match Answers.contents answer.Body with
            | Some(path, bytes) ->
                let written = setup.FileSetOf session
                let slots = List.zip (paths session.Id) (filesInOrder written)

                match slots |> List.tryFindIndex (fun (RepoPath p, _) -> p = path) with
                | Some i when snd slots[i] = bytes ->
                    if i < slots.Length - 1 then
                        NextFile(fst slots[i + 1])
                    else
                        // The earlier files were each compared equal before this one was requested.
                        match Confirmation.ofReadBack session.Id commit (commitTime outcome) written { written with Snapshot = bytes } with
                        | Some c -> AllMatch c
                        | None -> Differ
                | Some _ -> Differ
                | None -> Unreadable
            | None -> Unreadable
        | Error(NotFound _) -> Differ
        | Error _ -> Unreadable

    let private outcomeId (outcome: HttpOutcome) =
        match outcome with
        | Answered(id, _, _, _)
        | NotAnswered(id, _) -> id

    /// Whether the investigation's own files changed between the base head and `head`, from a compare answer. Unknown
    /// (no file list, or a truncated one) counts as changed: never overwrite on a guess (PERSIST-011).
    let private investigationChanged (session: InvestigationSession) (view: CompareView) =
        match view.Files with
        | Some files when view.Total = int64 view.Commits.Length -> files |> List.exists (StoragePaths.isUnder session.Id)
        | _ -> true

    let interpret (setup: SaveSetup) (context: RequestContext) (session: InvestigationSession) (outcome: HttpOutcome) : Interpretation =
        let id = outcomeId outcome
        let next awaiting request = NextRequest({ session with Outstanding = Some(context.NextId, awaiting) }, request)

        match session.Outstanding with
        | Some(current, awaiting) when current = id ->
            match awaiting with
            | MutationAnswer ->
                let classified = classify (Mutation session.BaseHead) outcome

                match classified.Result with
                | Ok answer ->
                    match Answers.commitOid answer.Body with
                    | Some commit ->
                        next (ReadBackAt commit) (GitHubRequests.contentsAt context.NextId context.Connection commit (StoragePaths.manifest session.Id))
                    | None -> MachineEvent SaveInput.AnswerLost
                | Error(StaleHead _) -> next FreshHead (GitHubRequests.branchHead context.NextId context.Connection)
                | Error AuthenticationFailed -> MachineEvent SaveInput.CredentialRefused
                | Error error ->
                    match meaning (Mutation session.BaseHead) classified with
                    | FailedBeforeSendingMeaning -> MachineEvent SaveInput.FailedBeforeSending
                    | UnknownMutationOutcome -> MachineEvent SaveInput.AnswerLost
                    | FailedRead
                    | Definite -> MachineEvent(SaveInput.ProviderRefused error)
            | ReadBackAt commit ->
                match checkFile setup session commit outcome with
                | NextFile path -> next (ReadBackAt commit) (GitHubRequests.contentsAt context.NextId context.Connection commit path)
                | AllMatch confirmation -> MachineEvent(SaveInput.ReadBackMatched confirmation)
                | Differ -> MachineEvent(SaveInput.ReadBackMismatched commit)
                | Unreadable -> MachineEvent SaveInput.ReadBackReadFailed
            | ReconcileFilesAt commit ->
                match checkFile setup session commit outcome with
                | NextFile path -> next (ReconcileFilesAt commit) (GitHubRequests.contentsAt context.NextId context.Connection commit path)
                | AllMatch confirmation -> MachineEvent(SaveInput.ReconcileMarkerBytesMatch confirmation)
                | Differ -> MachineEvent(SaveInput.ReconcileMarkerBytesDiffer commit)
                | Unreadable -> MachineEvent SaveInput.ReconcileReadFailed
            | FreshHead ->
                match (classify (Read "rest:ref") outcome).Result with
                | Ok answer ->
                    match Answers.headOid answer.Body with
                    | Some head -> next (FileComparisonAt head) (GitHubRequests.compareAll context.NextId context.Connection session.BaseHead head)
                    | None -> MachineEvent SaveInput.RebaseReadFailed
                | Error _ -> MachineEvent SaveInput.RebaseReadFailed
            | FileComparisonAt head ->
                match (classify (Compare session.BaseHead) outcome).Result with
                | Ok answer ->
                    match Answers.compare answer.Body with
                    | Some view when investigationChanged session view -> MachineEvent(SaveInput.StaleHeadSameInvestigation head)
                    | Some _ -> MachineEvent(SaveInput.StaleHeadUnrelated head)
                    | None -> MachineEvent SaveInput.RebaseReadFailed
                // The base is no longer reachable: the investigation was rewritten on the branch, a conflict (PERSIST-011).
                | Error(HistoryRewritten _) -> MachineEvent(SaveInput.StaleHeadSameInvestigation head)
                | Error _ -> MachineEvent SaveInput.RebaseReadFailed
            | ReconcileHead ->
                match (classify (Read "rest:ref") outcome).Result with
                | Ok answer ->
                    match Answers.headOid answer.Body with
                    | Some head when head = session.BaseHead -> MachineEvent SaveInput.ReconcileNothingWrittenHeadUnchanged
                    | Some head -> next (ReconcileCommits 1) (GitHubRequests.compareAll context.NextId context.Connection session.BaseHead head)
                    | None -> MachineEvent SaveInput.ReconcileReadFailed
                | Error _ -> MachineEvent SaveInput.ReconcileReadFailed
            | ReconcileCommits _ ->
                match (classify (Compare session.BaseHead) outcome).Result with
                | Ok answer ->
                    match Answers.compare answer.Body with
                    | Some view ->
                        // A mutation guarded by the base head that applied is the first commit after it.
                        match view.Commits with
                        | (first, message) :: _ when Markers.ofSession setup.Tab message ->
                            next (ReconcileFilesAt first) (GitHubRequests.contentsAt context.NextId context.Connection first (StoragePaths.manifest session.Id))
                        | [] -> MachineEvent SaveInput.ReconcileReadFailed
                        | commits when view.Total = int64 commits.Length ->
                            let head = fst (List.last commits)

                            if investigationChanged session view then
                                MachineEvent(SaveInput.StaleHeadSameInvestigation head)
                            else
                                MachineEvent(SaveInput.ReconcileNothingWrittenHeadMoved head)
                        | _ -> MachineEvent SaveInput.ReconcileReadFailed
                    | None -> MachineEvent SaveInput.ReconcileReadFailed
                | Error(HistoryRewritten _) -> MachineEvent SaveInput.ReconcileHistoryRewritten
                | Error _ -> MachineEvent SaveInput.ReconcileReadFailed
        | _ -> MachineEvent(SaveInput.SupersededReply id)

    /// The save machine of one tab.
    let create (setup: SaveSetup) : SaveMachine =
        { Initial = initial
          Interpret = interpret setup
          Step = step setup
          ReconcileDue = reconcileDue
          InterpretConflict = ConflictMachine.interpret setup.Codec
          StepConflict = ConflictMachine.step setup.KnownOffline
          InterpretLoad = LoadMachine.interpret setup.Codec
          StepLoad = LoadMachine.step setup.KnownOffline }

    /// The file set of the session's in-flight commands alone, one recorded event per command, encoded with
    /// causa-storage v1.0.0. Deterministic. It stands in until the engine supplies the base-aware FileSetOf (the
    /// loaded history plus the kernel's decided events, T13/T22): the session does not hold its base history.
    let pendingCommandFiles (session: InvestigationSession) : FileSet =
        session.Unsaved
        |> List.mapi (fun i command ->
            { EventId = EventId command.CommandId
              Investigation = session.Id
              Version = AggregateVersion(int64 (i + 1))
              Payload = EventPayload "CommandRecorded" })
        |> StorageV1.encodeHistory session.Id

    /// A tab's save machine with a fresh session identifier and the stand-in file sets (used by the invariant
    /// bindings and the state-machine tests).
    let forNewTab () : SaveMachine =
        let tab = Guid.NewGuid()

        create
            { Codec = StorageV1.codec
              FileSetOf = pendingCommandFiles
              Tab = tab
              NextMarker = Markers.forTab tab
              KnownOffline = false }

/// T20: what opening an investigation at the branch head found (EXT-DATA-005, EXT-DATA-007, PERSIST-005).
type OpenResult =
    /// The three files read at the head verified as a reader would (causa-data v1.0.0 §11); ReadAt is the base head.
    | Opened of VerifiedInvestigation
    /// No file of the investigation exists at this head: the only state from which an explicit create may be sent.
    | AuthorizedEmpty of at: CommitOid
    /// Files exist but fail the storage contract or replay: fail closed, never load partially (EXT-DATA-005-O2).
    | Incompatible of at: CommitOid * VerificationFailure list
    /// GraphQL truncated a file: re-read each file with the contents API at this commit (GH-LARGE-FILE).
    | LargeFiles of at: CommitOid
    | OpenRefused of ProviderError

/// Why an explicit create was not sent (EXT-DATA-006-O2).
type CreateRefusal =
    /// Connect did not state write permission for this credential.
    | NoWriteAccess
    /// The head was not read as empty for this investigation in this tab: never overwrite unread remote history.
    | HeadNotReadEmpty
    /// A file outside the investigation's own prefix (PERSIST-006-O5, causa-data v1.0.0 §9.4.4).
    | OutsidePrefix of paths: string list
    /// Another mutation of this tab is in flight: at most one at a time (PERSIST-006-O6); the create waits in memory.
    | MutationInFlight

/// One file read through the contents API (GH-LARGE-FILE handling).
type ContentsRead =
    | ContentsBytes of path: string * bytes: byte[]
    /// `encoding: none`: the file is too large for the contents API; its blob sha names the one re-read.
    | ContentsTooLarge of path: string * blobSha: string
    | ContentsUnreadable of ProviderError

/// T20: the GitHub provider. Connect, read (open at head), explicit create and the write/conflict paths, all as pure
/// request builders and answer interpreters over IF41-WIRE: the engine executes requests through Limen and never
/// touches a browser API or the credential (DEC-R7-001, PF-015). Connect and read only ever build queries
/// (EXT-DATA-006-O1); the only mutation is createCommitOnBranch with an expected head the tab read.
[<RequireQualifiedAccess>]
module GitHubProvider =
    open System.Text.Json

    let private outcomeId (outcome: HttpOutcome) =
        match outcome with
        | Answered(id, _, _, _)
        | NotAnswered(id, _) -> id

    let private parse (body: string) =
        try
            use doc = JsonDocument.Parse body
            Some(doc.RootElement.Clone())
        with :? JsonException ->
            None

    let private prop (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None
        | _ -> None

    let private str (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.String -> e.GetString() |> Option.ofObj
        | _ -> None

    let private isTrue (e: JsonElement option) =
        match e with
        | Some v -> v.ValueKind = JsonValueKind.True
        | None -> false

    /// Write access comes only from the permissions GitHub states for the repository (push, maintain or admin),
    /// never from a successful read or from a token having been supplied (EXT-DATA-005, EXT-DATA-007-O2).
    let canWrite (repositoryBody: string) : bool option =
        parse repositoryBody
        |> Option.bind (prop "permissions")
        |> Option.map (fun p -> isTrue (prop "push" p) || isTrue (prop "maintain" p) || isTrue (prop "admin" p))

    /// Connect's answer. An answer to any request but the one sent is superseded; a token alone never connects.
    let interpretConnect (sent: RequestId) (outcome: HttpOutcome) : ConnectResult =
        if outcomeId outcome <> sent then
            ConnectSuperseded
        else
            match (classify ConnectRepository outcome).Result with
            | Ok answer ->
                match canWrite answer.Body with
                | Some write -> ConnectAccepted write
                | None -> ConnectRejected(Malformed "the repository answer has no permissions object")
            | Error error -> ConnectRejected error

    /// The Connector the App's SM-CONNECTION drives (IF41-ERRORS ConnectResult): GET the repository, nothing else.
    let connector: Connector =
        { ConnectRequest = GitHubRequests.repository
          InterpretConnect = interpretConnect }

    /// Open (read): the investigation's three files and the head's oid in one GraphQL query at the branch head.
    let openRequest (id: RequestId) (connection: ConnectionContext) (investigation: InvestigationId) : HttpRequest =
        GitHubRequests.fileSetAtBranch
            id
            connection
            (StoragePaths.manifest investigation)
            (StoragePaths.events investigation)
            (StoragePaths.snapshot investigation)

    let private truncatedAt (body: string) : CommitOid option =
        let target =
            parse body
            |> Option.bind (prop "data")
            |> Option.bind (prop "repository")
            |> Option.bind (fun r -> prop "ref" r |> Option.bind (prop "target") |> Option.orElse (prop "object" r))

        match target with
        | Some t ->
            let truncated slot =
                prop slot t |> Option.bind (prop "object") |> Option.bind (prop "isTruncated") |> isTrue

            if [ "manifest"; "events"; "snapshot" ] |> List.exists truncated then
                prop "oid" t |> Option.bind str |> Option.map CommitOid
            else
                None
        | None -> None

    /// Interpret the open read: verify through the codec (full contract validation and F# replay) or fail closed.
    let interpretOpen (codec: StorageCodec) (outcome: HttpOutcome) : OpenResult =
        match (classify (Read "graphql:fileSet") outcome).Result with
        | Error error -> OpenRefused error
        | Ok answer ->
            match Answers.fileSet answer.Body with
            | Some { At = at; Manifest = None; Events = None; Snapshot = None } -> AuthorizedEmpty at
            | Some { At = at; Manifest = Some m; Events = Some e; Snapshot = Some s } ->
                match codec.Verify at { Manifest = m; Events = e; Snapshot = s } with
                | Ok verified -> Opened verified
                | Error failures -> Incompatible(at, failures)
            | Some read ->
                let missing =
                    [ "manifest.json", read.Manifest; "events.ndjson", read.Events; "snapshot.json", read.Snapshot ]
                    |> List.filter (snd >> Option.isNone)
                    |> List.map (fun (file, _) -> MissingRequiredField(file, "file"))

                Incompatible(read.At, missing)
            | None ->
                match truncatedAt answer.Body with
                | Some at -> LargeFiles at
                | None when Answers.refMissing answer.Body -> OpenRefused(NotFound "branch")
                | None -> OpenRefused(Malformed "the file-set answer does not decode")

    /// Decode a contents (or blob) read. `encoding: none` names the blob to re-read once (GH-LARGE-FILE).
    let interpretContents (outcome: HttpOutcome) : ContentsRead =
        match (classify (Read "rest:contents") outcome).Result with
        | Error error -> ContentsUnreadable error
        | Ok answer ->
            match parse answer.Body with
            | Some root ->
                let path = prop "path" root |> Option.bind str |> Option.defaultValue ""

                match prop "encoding" root |> Option.bind str, prop "content" root |> Option.bind str, prop "sha" root |> Option.bind str with
                | Some "none", _, Some sha -> ContentsTooLarge(path, sha)
                | Some "base64", Some content, _ ->
                    try
                        ContentsBytes(path, Convert.FromBase64String(content.Replace("\n", "").Replace("\r", "")))
                    with :? FormatException ->
                        ContentsUnreadable(Malformed "the content is not base64")
                | _ -> ContentsUnreadable(Malformed "the contents answer has no decodable content")
            | None -> ContentsUnreadable(Malformed "the contents answer does not decode")

    /// The large-file re-read: at most maxAttempts GhLargeFile (one) blob read after `encoding: none`; never more.
    let largeFileRetry (id: RequestId) (connection: ConnectionContext) (attemptsSoFar: int) (read: ContentsRead) : HttpRequest option =
        match read with
        | ContentsTooLarge(_, sha) when attemptsSoFar < maxAttempts GhLargeFile -> Some(GitHubRequests.blob id connection sha)
        | _ -> None

    let private additions (investigation: InvestigationId) (files: FileSet) =
        [ StoragePaths.manifest investigation, files.Manifest
          StoragePaths.events investigation, files.Events
          StoragePaths.snapshot investigation, files.Snapshot ]

    /// Explicit create (EXT-DATA-006-O2): only after Connect stated write access and an open read found this
    /// investigation absent at the head; the commit is guarded by that head, carries the tab's write marker and adds
    /// only the investigation's own three files.
    let createRequest
        (context: RequestContext)
        (writeAccess: bool)
        (headRead: OpenResult)
        (investigation: InvestigationId)
        (files: FileSet)
        (marker: WriteMarker)
        : Result<HttpRequest, CreateRefusal> =
        let changes = additions investigation files

        let outside =
            changes |> List.map (fun (RepoPath p, _) -> p) |> List.filter (StoragePaths.isUnder investigation >> not)

        match writeAccess, headRead with
        | false, _ -> Error NoWriteAccess
        | true, _ when context.MutationInFlightElsewhere -> Error MutationInFlight
        | true, AuthorizedEmpty _ when not outside.IsEmpty -> Error(OutsidePrefix outside)
        | true, AuthorizedEmpty head ->
            let (InvestigationId g) = investigation

            Ok(
                GitHubRequests.createCommitWithTrailer
                    context.NextId
                    context.Connection
                    head
                    (sprintf "Create investigation %s" (g.ToString("D").ToLowerInvariant()))
                    (Markers.trailer marker)
                    changes
            )
        | true, _ -> Error HeadNotReadEmpty

    /// The provider-side FileSetOf the engine supplies to SaveSetup: the confirmed history the tab opened plus the
    /// events the kernel decided for the session's in-flight commands, encoded with causa-storage v1.0.0. `decided`
    /// is the engine's (integration/ui); the encoding and the base are persistence's.
    let fileSetOf (codec: StorageCodec) (opened: VerifiedInvestigation) (decided: InvestigationSession -> EventEnvelope list) : InvestigationSession -> FileSet =
        fun session -> codec.Encode opened (decided session)

    /// The SaveSetup for one tab, built per update with SM-NETWORK's current state (KnownOffline injection point).
    let saveSetup (tab: Guid) (nextMarker: unit -> WriteMarker) (fileSet: InvestigationSession -> FileSet) (knownOffline: bool) : SaveSetup =
        { Codec = StorageV1.codec
          FileSetOf = fileSet
          Tab = tab
          NextMarker = nextMarker
          KnownOffline = knownOffline }
