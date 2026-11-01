// Owner: persistence. WI-R8-PTESTS: PERSIST-012 conflict resolution (SM-CONFLICT), against the stateful fake (PAT-05).
namespace Causa.Acceptance.Deterministic

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport
open SaveHarness

module PERSIST012Tests =
    /// Another writer saves a valid newer version of the same investigation (two events).
    let private otherWriterSavesValidVersion (fake: InvariantFake.Handle) =
        let event (n: int) : EventEnvelope =
            { EventId = EventId(Guid(sprintf "00000000-0000-4000-8000-00000000f%03d" n))
              Investigation = investigation
              Version = AggregateVersion(int64 n)
              Payload = EventPayload "InvestigationStarted" }

        let files = StorageV1.encodeHistory investigation [ event 1; event 2 ]
        let file (RepoPath p) bytes = p, bytes

        fake.Server.Mutate(
            StatefulFake.commitOnBranch
                fake.Branch
                "other writer, same investigation"
                [ file (StoragePaths.manifest investigation) files.Manifest
                  file (StoragePaths.events investigation) files.Events
                  file (StoragePaths.snapshot investigation) files.Snapshot ]
        )

    /// A tab in Conflict, the GitHub version read and verified (Reviewable).
    let private reviewable () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> edit 2
        let other = otherWriterSavesValidVersion fake
        let tab = tab |> input SaveInput.SaveRequested |> drain fake

        match tab.Session.State with
        | SaveState.Conflict(Reviewable remote) -> fake, other, tab, remote
        | otherState -> failwithf "expected Conflict(Reviewable), got %A" otherState

    let private conflictStep (tab: Tab) (i: ConflictInput) =
        match tab.Session.State with
        | SaveState.Conflict view ->
            match tab.Machine.StepConflict (context ()) view i with
            | Ok(next, effects) -> { tab with Session = { tab.Session with State = SaveState.Conflict next } }, effects
            | Error fault -> failwithf "unexpected conflict fault: %A" fault
        | other -> failwithf "not in Conflict: %A" other

    let private noMutation (effects: SaveEffect list) =
        Assert.DoesNotContain(effects, fun e -> match e with Send r -> isMutationRequest r | _ -> false)

    /// O1: a conflict stays in Conflict until the user chooses a resolution: saving, edits and connectivity changes
    /// leave it in Conflict.
    [<Fact>]
    let ``PERSIST-012-O1`` () =
        let fake, _, tab, _ = reviewable ()

        for i in [ SaveInput.SaveRequested; SaveInput.QueuedSaveStarted; SaveInput.CheckAgain; SaveInput.Edit(command 3); SaveInput.WentOffline; SaveInput.WentOnline ] do
            match tab.Machine.Step (context ()) tab.Session i with
            | Ok({ State = SaveState.Conflict _ }, effects) -> noMutation effects
            | Ok(next, _) -> failwithf "%A left Conflict: %A" i next.State
            | Error _ -> ()

        Assert.Equal(1, mutations fake |> List.length)

    /// O2: three actions are offered from the reviewable conflict: review, reapply, discard.
    [<Fact>]
    let ``PERSIST-012-O2`` () =
        let _, _, tab, remote = reviewable ()

        match (fst (conflictStep tab ConflictInput.ReviewChosen)).Session.State with
        | SaveState.Conflict(Reviewable r) -> Assert.Equal(remote, r)
        | other -> failwithf "review: %A" other

        match (fst (conflictStep tab ConflictInput.ReapplyChosen)).Session.State with
        | SaveState.Conflict(Reapplying r) -> Assert.Equal(remote, r)
        | other -> failwithf "reapply: %A" other

        match (fst (conflictStep tab ConflictInput.DiscardChosen)).Session.State with
        | SaveState.Conflict(ConfirmingDiscard r) -> Assert.Equal(remote, r)
        | other -> failwithf "discard: %A" other

    /// O4: accepted commands become unsaved changes on the new base head; refused commands are listed with the
    /// engine's reason and are not applied.
    [<Fact>]
    let ``PERSIST-012-O4`` () =
        let _, _, tab, remote = reviewable ()
        let tab, _ = conflictStep tab ConflictInput.ReapplyChosen
        let refused = [ { Command = command 2; Reason = "the engine refused it at the GitHub version" } ]
        let tab, _ = conflictStep tab (ConflictInput.ReapplyDone([ command 1 ], refused))

        match tab.Session.State with
        | SaveState.Conflict(Reapplied(r, listed)) ->
            Assert.Equal(remote, r)
            Assert.Equal<RefusedCommand list>(refused, listed)
        | other -> failwithf "expected Reapplied, got %A" other

        let tab = tab |> input (SaveInput.ConflictResolvedReapplied [ command 1 ])
        Assert.Equal("Unsaved", stateName tab)
        Assert.Equal<CommandEnvelope list>([ command 1 ], tab.Session.Unsaved)
        Assert.Equal(remote.ReadAt, tab.Session.BaseHead)

    /// O5: reapply does not save; the user saves the result.
    [<Fact>]
    let ``PERSIST-012-O5`` () =
        let fake, _, tab, _ = reviewable ()
        let tab, e1 = conflictStep tab ConflictInput.ReapplyChosen
        let tab, e2 = conflictStep tab (ConflictInput.ReapplyDone([ command 1; command 2 ], []))
        let tab = tab |> input (SaveInput.ConflictResolvedReapplied [ command 1; command 2 ])
        noMutation (e1 @ e2 @ tab.Pending)
        Assert.Empty(tab.Pending)
        Assert.Equal("Unsaved", stateName tab)
        Assert.Equal(1, mutations fake |> List.length)

    /// O6: discard asks for confirmation first; cancelling returns to review, confirming discards.
    [<Fact>]
    let ``PERSIST-012-O6`` () =
        let _, _, tab, remote = reviewable ()
        let asking, effects = conflictStep tab ConflictInput.DiscardChosen
        Assert.Contains(effects, fun e -> match e with Announce(_, text) -> text.Contains("Discard", StringComparison.Ordinal) | _ -> false)
        Assert.Equal<CommandEnvelope list>([ command 1; command 2 ], asking.Session.Unsaved)

        match (fst (conflictStep asking ConflictInput.DiscardCancelled)).Session.State with
        | SaveState.Conflict(Reviewable r) -> Assert.Equal(remote, r)
        | other -> failwithf "cancel: %A" other

        match (fst (conflictStep asking ConflictInput.DiscardConfirmed)).Session.State with
        | SaveState.Conflict(Discarded r) -> Assert.Equal(remote, r)
        | other -> failwithf "confirm: %A" other

        // Without confirmation there is no path from Reviewable to Discarded.
        match tab.Session.State with
        | SaveState.Conflict view ->
            Assert.True(Result.isError (tab.Machine.StepConflict (context ()) view ConflictInput.DiscardConfirmed))
        | _ -> ()

    /// O7: no action replaces the GitHub version with the tab's version: from every conflict view, no input emits a
    /// commit mutation.
    [<Fact>]
    let ``PERSIST-012-O7`` () =
        let _, _, tab, remote = reviewable ()

        let views =
            [ ReadingRemote(RequestId 1L); Reviewable remote; Reapplying remote; Reapplied(remote, []); ConfirmingDiscard remote
              Discarded remote; RemoteUnavailable; Unresolvable "invalid" ]

        let inputs =
            [ ConflictInput.RemoteVerified remote; ConflictInput.RemoteReadFailed LostAfterSending; ConflictInput.RemoteMissingOrInvalid None
              ConflictInput.ReadAgain; ConflictInput.ReviewChosen; ConflictInput.ReapplyChosen
              ConflictInput.ReapplyDone([ command 1 ], []); ConflictInput.DiscardChosen; ConflictInput.DiscardConfirmed
              ConflictInput.DiscardCancelled; ConflictInput.ExportChosen; ConflictInput.SupersededReply(RequestId 1L) ]

        for view in views do
            for i in inputs do
                match tab.Machine.StepConflict (context ()) view i with
                | Ok(_, effects) -> noMutation effects
                | Error _ -> ()

    /// O8 (PAT-05): each action against the fake, and no action overwrites the other writer's commit.
    [<Fact>]
    let ``PERSIST-012-O8`` () =
        // Discard: the GitHub version is loaded as Saved at the other writer's commit; nothing is sent.
        let fake, other, tab, remote = reviewable ()
        let tab, _ = conflictStep tab ConflictInput.DiscardChosen
        let tab, _ = conflictStep tab ConflictInput.DiscardConfirmed
        let tab = tab |> input (SaveInput.ConflictResolvedDiscarded remote)

        match tab.Session.State with
        | SaveState.Saved(VerifiedAt v) -> Assert.Equal(other, v.ReadAt)
        | s -> failwithf "discard: %A" s

        Assert.Equal(1, mutations fake |> List.length)
        Assert.Equal(other, InvariantFake.head fake)

        // Reapply, then the user saves: the new commit sits on top of the other writer's commit.
        let fake, other, tab, _ = reviewable ()
        let tab, _ = conflictStep tab ConflictInput.ReapplyChosen
        let tab, _ = conflictStep tab (ConflictInput.ReapplyDone([ command 1 ], []))
        let tab = tab |> input (SaveInput.ConflictResolvedReapplied [ command 1 ]) |> input SaveInput.SaveRequested |> drain fake
        Assert.Equal("Saved", stateName tab)
        Assert.True(InvariantFake.reachableFromHead fake other)
        Assert.All(InvariantFake.created fake, fun c -> Assert.True(InvariantFake.reachableFromHead fake c))

        // Review changes nothing.
        let fake, other, tab, _ = reviewable ()
        let _, effects = conflictStep tab ConflictInput.ReviewChosen
        noMutation effects
        Assert.Equal(other, InvariantFake.head fake)
