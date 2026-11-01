// Owner: persistence. WI-R8-PTESTS: PERSIST-015 unknown commit outcome and reconciliation, with injected lost answers
// against the stateful fake (PAT-06).
namespace Causa.Acceptance.Deterministic

open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveHarness

module PERSIST015Tests =
    let private lost = LoseBeforeApplying LostAfterSending

    /// Edit, save, and lose the mutation's answer with the given directive; returns the tab right after the loss.
    let private saveAndLose (directive: FakeDirective) =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        fake.Server.Script [ directive ]
        fake, tab |> deliverOne fake

    let private hasSend (tab: Tab) =
        tab.Pending |> List.exists (function
            | Send _ -> true
            | _ -> false)

    let private isUnknown (tab: Tab) =
        match tab.Session.State with
        | SaveState.UnknownOutcome _ -> true
        | _ -> false

    /// O1: when the mutation may have reached GitHub but no recognised answer came back, the outcome is unknown.
    [<Fact>]
    let ``PERSIST-015-O1`` () =
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        Assert.True(isUnknown tab, sprintf "expected UnknownOutcome, got %A" tab.Session.State)
        // The commit did reach GitHub, yet nothing is reported as saved.
        Assert.NotEqual(tab.Session.BaseHead, InvariantFake.head fake)

    /// O2: a timeout, a lost response, a dropped connection after sending and an unrecognised answer are all unknown.
    [<Fact>]
    let ``PERSIST-015-O2`` () =
        for directive in [ LoseAfterApplying TimedOut; LoseAfterApplying LostAfterSending; LoseBeforeApplying LostAfterSending; LoseBeforeApplying TimedOut ] do
            let _, tab = saveAndLose directive
            Assert.True(isUnknown tab, sprintf "%A: expected UnknownOutcome, got %A" directive tab.Session.State)

        // An answer the code does not recognise (200 with a body that names no commit).
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested

        match tab.Pending with
        | Send request :: _ ->
            let session =
                match tab.Machine.Step (context ()) tab.Session (SaveInput.MutationSent request.Id) with
                | Ok(s, _) -> s
                | Error f -> failwithf "%A" f

            match tab.Machine.Interpret (context ()) session (Answered(request.Id, 200, [ "content-type", "application/json" ], "{\"data\":{}}")) with
            | MachineEvent SaveInput.AnswerLost -> ()
            | other -> failwithf "an unrecognised answer must be unknown (AnswerLost), got %A" other
        | other -> failwithf "expected the mutation request, got %A" other

    /// O3: in Unknown commit outcome no other mutation is sent until reconciliation decides; a save request is refused.
    [<Fact>]
    let ``PERSIST-015-O3`` () =
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        Assert.False(hasSend tab)

        match tab.Machine.Step (context ()) tab.Session SaveInput.SaveRequested with
        | Ok(next, effects) ->
            Assert.True(isUnknown { tab with Session = next })
            Assert.DoesNotContain(effects, fun e -> match e with Send r -> isMutationRequest r | _ -> false)
        | Error _ -> ()

        let tab = tab |> edit 2
        Assert.False(hasSend tab)
        Assert.Equal(1, mutations fake |> List.length)

    /// O4: reconciliation reads the branch head, then the commits after the expected head.
    [<Fact>]
    let ``PERSIST-015-O4`` () =
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        let expected = tab.Session.BaseHead
        drain fake tab |> ignore
        let log = requests fake
        let after = log |> List.skip ((log |> List.findIndex isMutationRequest) + 1)
        Assert.True(isRefRead after[0], after[0].Url)
        let (CommitOid e) = expected
        Assert.Contains(sprintf "/compare/%s..." e, after[1].Url)

    /// O5: a commit carrying this save's marker whose tree holds the written bytes confirms the save.
    [<Fact>]
    let ``PERSIST-015-O5`` () =
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        let tab = drain fake tab

        match tab.Session.State with
        | SaveState.Saved(Confirmed c) -> Assert.Equal(InvariantFake.head fake, c.CommitOid)
        | other -> failwithf "expected Saved by reconciliation, got %A" other

        Assert.Equal(1, mutations fake |> List.length)

    /// O6: a commit carrying the marker whose bytes differ: Save failed, not reported as saved.
    [<Fact>]
    let ``PERSIST-015-O6`` () =
        let fake, tab = saveAndLose lost
        let (RepoPath manifest) = StoragePaths.manifest investigation
        let message = sprintf "Causa: save\n\n%s" (Markers.trailer { Session = tab.Marker; Sequence = 1L })
        let forged = fake.Server.Mutate(StatefulFake.commitOnBranch fake.Branch message [ manifest, Encoding.UTF8.GetBytes "{\"not\":\"the written bytes\"}" ])
        let tab = drain fake tab

        match tab.Session.State with
        | SaveState.SaveFailed(BytesDiffer c) -> Assert.Equal(forged, c)
        | other -> failwithf "expected SaveFailed BytesDiffer, got %A" other

        Assert.Equal(1, mutations fake |> List.length)

    /// O7: head unchanged and no commit carries the marker: nothing was written, and the save is sent again within
    /// the PERSIST-010 limit.
    [<Fact>]
    let ``PERSIST-015-O7`` () =
        let fake, tab = saveAndLose lost
        let h0 = InvariantFake.head fake
        let tab = drain fake tab
        Assert.Equal("Saved", stateName tab)
        let ms = mutations fake
        Assert.Equal(2, ms.Length)
        let (CommitOid h) = h0
        Assert.All(ms, fun m -> Assert.Contains(h, m.Body.Value))

    /// O8: head moved and no commit carries the marker: not written, and the stale-head rules apply (an unrelated
    /// change rebases onto the new head).
    [<Fact>]
    let ``PERSIST-015-O8`` () =
        let fake, tab = saveAndLose lost
        let external = InvariantFake.externalCommit fake
        let tab = drain fake tab
        Assert.Equal("Saved", stateName tab)
        let ms = mutations fake
        Assert.Equal(2, ms.Length)
        let (CommitOid e) = external
        Assert.Contains(e, ms[1].Body.Value)
        Assert.True(InvariantFake.reachableFromHead fake external)

        // When the investigation's own files changed, the stale-head rules make it a conflict, with nothing sent.
        let fake, tab = saveAndLose lost
        otherWriterChangesInvestigation fake |> ignore
        let tab = drain fake tab

        match tab.Session.State with
        | SaveState.Conflict _ -> ()
        | other -> failwithf "expected Conflict, got %A" other

        Assert.Equal(1, mutations fake |> List.length)

    /// O9: the expected head is no longer an ancestor of the branch head: Save failed, history rewritten.
    [<Fact>]
    let ``PERSIST-015-O9`` () =
        let fake, tab = saveAndLose lost
        fake.Server.Mutate(StatefulFake.rewriteBranch fake.Branch [ "README.md", Encoding.UTF8.GetBytes "rewritten" ]) |> ignore
        let tab = drain fake tab

        match tab.Session.State with
        | SaveState.SaveFailed HistoryWasRewritten -> ()
        | other -> failwithf "expected SaveFailed HistoryWasRewritten, got %A" other

        Assert.Equal(1, mutations fake |> List.length)

    /// O10: a reconciliation read that fails leaves the investigation in Unknown commit outcome.
    [<Fact>]
    let ``PERSIST-015-O10`` () =
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        fake.Server.Script [ lost; lost; lost; lost ]
        let tab = drain fake tab
        Assert.True(isUnknown tab, sprintf "expected UnknownOutcome, got %A" tab.Session.State)
        Assert.Equal(1, mutations fake |> List.length)

    /// O11 (PAT-06): every branch of the rule, each from an injected lost answer, ends where the rule says.
    [<Fact>]
    let ``PERSIST-015-O11`` () =
        ``PERSIST-015-O5`` ()
        ``PERSIST-015-O6`` ()
        ``PERSIST-015-O7`` ()
        ``PERSIST-015-O8`` ()
        ``PERSIST-015-O9`` ()
        ``PERSIST-015-O10`` ()

    /// PERSIST-015-O4 beyond GitHub's 250-commit compare page: more than 250 commits after the expected head.
    [<Fact>]
    let ReconcileBeyond250Commits () =
        // This save's commit applied, then 260 unrelated commits: the marker commit is still found and confirmed.
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        for _ in 1..260 do InvariantFake.externalCommit fake |> ignore
        let tab = drain fake tab
        Assert.Equal("Saved", stateName tab)

        // Nothing written, then 260 unrelated commits: reconciliation still decides (not written; rebase and save).
        let fake, tab = saveAndLose lost
        for _ in 1..260 do InvariantFake.externalCommit fake |> ignore
        let tab = drain fake tab
        Assert.Equal("Saved", stateName tab)
        Assert.Equal(2, mutations fake |> List.length)

    /// T32 beyond 250 commits, when the investigation's own files changed among them: the stale-head conflict, with
    /// nothing more sent; reconciliation reads stay bounded (one head read and one compare page).
    [<Fact>]
    let ReconcileBeyond250CommitsInvestigationChanged () =
        let fake, tab = saveAndLose lost
        for _ in 1..130 do InvariantFake.externalCommit fake |> ignore
        otherWriterChangesInvestigation fake |> ignore
        for _ in 1..130 do InvariantFake.externalCommit fake |> ignore
        let before = requests fake |> List.length
        let tab = drain fake tab

        match tab.Session.State with
        | SaveState.Conflict _ -> ()
        | other -> failwithf "expected Conflict, got %A" other

        Assert.Equal(1, mutations fake |> List.length)
        let compares = requests fake |> List.skip before |> List.filter (fun r -> r.Url.Contains("/compare/", System.StringComparison.Ordinal))
        Assert.Equal(1, compares.Length)

    /// T32 (WI-R8-ITESTS): when the last automatic reconciliation read fails, the machine takes its own
    /// ReconcileReadFailed as ReconcileReadsSpent (SM-SAVE T-S30): Unknown commit outcome with no read left, nothing
    /// outstanding, Check again offered, no fault. Check again then reaches the decision.
    [<Fact>]
    let LastReconcileReadFailureOffersCheckAgain () =
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        fake.Server.Script [ lost; lost; lost ]
        let tab = drain fake tab
        Assert.Empty(tab.Faults)
        Assert.Equal(SaveState.UnknownOutcome 0, tab.Session.State)
        Assert.Equal(None, tab.Session.Outstanding)
        Assert.Contains(tab.Announced, fun a -> a.Contains("Check again", System.StringComparison.Ordinal))

        let tab = tab |> input SaveInput.CheckAgain |> drain fake
        Assert.Empty(tab.Faults)

        match tab.Session.State with
        | SaveState.Saved(Confirmed c) -> Assert.Equal(InvariantFake.head fake, c.CommitOid)
        | other -> failwithf "expected Saved after Check again, got %A" other

        // A Check again read that fails also offers Check again, never a fault.
        let fake, tab = saveAndLose (LoseAfterApplying LostAfterSending)
        fake.Server.Script [ lost; lost; lost ]
        let tab = drain fake tab
        fake.Server.Script [ lost ]
        let tab = tab |> input SaveInput.CheckAgain |> drain fake
        Assert.Empty(tab.Faults)
        Assert.Equal(SaveState.UnknownOutcome 0, tab.Session.State)
        Assert.Equal(None, tab.Session.Outstanding)
