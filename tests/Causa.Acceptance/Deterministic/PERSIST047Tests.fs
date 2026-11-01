// Owner: integration (WI-R8-ITESTS). PERSIST-047: every request the application sends for one save request is
// counted over 60 seconds (virtual time: every reconciliation timer fires at its scheduled delay) in three conditions.
// The real SM-SAVE is driven by SaveAcceptance.drive (PERSIST045Tests.fs); the regression test for R6-D-003.
namespace Causa.Acceptance.Deterministic

open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveAcceptance

type PERSIST047Tests() =
    static let window = 60_000

    /// Every request fails as if offline: nothing reaches GitHub.
    static let offline () =
        let _, saving, effects = editedAndSaveRequested ()
        drive window (fun request -> NotAnswered(request.Id, FailedBeforeSending)) saving effects

    /// Every mutation's answer is lost (GitHub did not apply it); reads are answered.
    static let answersLost () =
        let handle, saving, effects = editedAndSaveRequested ()

        let exec (request: HttpRequest) =
            if isMutation request then handle.Server.Script [ LoseBeforeApplying LostAfterSending ]
            handle.Server.Execute request

        drive window exec saving effects

    /// The branch advances (an unrelated writer) before every attempt.
    static let branchAdvances () =
        let handle, saving, effects = editedAndSaveRequested ()

        let exec (request: HttpRequest) =
            if isMutation request then InvariantFake.externalCommit handle |> ignore
            handle.Server.Execute request

        drive window exec saving effects

    static let conditions () =
        [ "offline", offline (); "answers lost", answersLost (); "branch advances", branchAdvances () ]

    static let terminal (run: Run) =
        match run.Session.State with
        | SaveState.Offline
        | SaveState.UnknownOutcome _
        | SaveState.SaveFailed _ -> true
        | _ -> false

    [<Fact>]
    member _.``PERSIST-047-O1``() =
        // One save request, counted for 60 seconds: every request is inside the window and the run is quiescent.
        for name, run in conditions () do
            Assert.True(run.Sent.Length >= 1, name)
            Assert.All(run.Sent, fun s -> Assert.InRange(s.AtMs, 0, window))

    [<Fact>]
    member _.``PERSIST-047-O2``() =
        // The three conditions behave as named.
        let off = offline ()
        Assert.Equal(1, off.Sent.Length)
        let lost = answersLost ()
        Assert.Contains("UnknownOutcome", lost.States)
        let moving = branchAdvances ()
        Assert.True(moving.Session.RebaseAttempts >= 1, sprintf "%A" moving.States)

    [<Fact>]
    member _.``PERSIST-047-O3``() =
        // At most four commit mutations and at most four reconciliation reads in each condition.
        for name, run in conditions () do
            Assert.True(mutations run |> List.length <= 4, sprintf "%s: %d mutations" name (mutations run |> List.length))
            Assert.True(reconciliationReads run |> List.length <= 4, sprintf "%s: %d reconciliation reads" name (reconciliationReads run |> List.length))

    [<Fact>]
    member _.``PERSIST-047-O4``() =
        // The final state is Offline, Unknown commit outcome or Save failed (PERSIST-031), never Saving.
        for name, run in conditions () do
            Assert.True(terminal run, sprintf "%s ended %s" name (stateName run.Session))
            Assert.NotEqual<string>("Saving", stateName run.Session)

    [<Fact>]
    member _.``PERSIST-047-O5``() =
        // Acceptance on the candidate SHA: all three conditions are bounded and end in a terminal state.
        for name, run in conditions () do
            Assert.True(terminal run && mutations run |> List.length <= 4 && reconciliationReads run |> List.length <= 4, name)

    [<Fact>]
    member _.``PERSIST-047-O6``() =
        // Covers PERSIST-010 (rebase bound) and PERSIST-016 (reconciliation bound); R6-D-003 was an unbounded resend loop.
        let moving = branchAdvances ()
        Assert.True(moving.Session.RebaseAttempts <= SaveProcess.maxRebases)
        let lost = answersLost ()
        Assert.True(lost.Session.ReconcileReads <= SaveProcess.maxAutomaticReads)
        Assert.True(mutations lost |> List.length <= SaveProcess.maxMutations)
