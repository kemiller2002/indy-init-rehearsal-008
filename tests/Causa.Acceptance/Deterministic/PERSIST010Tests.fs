// Owner: persistence. WI-R8-PTESTS: PERSIST-010 the automatic rebase limit, against the stateful fake (PAT-08).
namespace Causa.Acceptance.Deterministic

open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveHarness

module PERSIST010Tests =
    /// Before every commit mutation another writer advances the branch with an unrelated file; the reads in between
    /// (fresh head, file comparison) are answered.
    let private advanceBeforeEveryAttempt (fake: InvariantFake.Handle) =
        fake.Server.Script(
            [ for n in 1..4 do
                  OtherWriterFirst(fake.Branch, [ sprintf "other/advance-%d.txt" n, Encoding.UTF8.GetBytes(string n) ])
                  Deliver
                  Deliver ]
        )

    let private exhausted () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        advanceBeforeEveryAttempt fake
        fake, drain fake tab

    /// O1: one save request makes at most three automatic rebase attempts: at most four commit mutations in total.
    [<Fact>]
    let ``PERSIST-010-O1`` () =
        let fake, tab = exhausted ()
        Assert.Equal(4, mutations fake |> List.length)
        Assert.True(tab.Session.RebaseAttempts <= 3)

    /// O2: every rebase attempt follows a fresh read of the branch head (and the file comparison at that head).
    [<Fact>]
    let ``PERSIST-010-O2`` () =
        let fake, _ = exhausted ()
        let log = requests fake

        let mutationIndexes = log |> List.indexed |> List.filter (snd >> isMutationRequest) |> List.map fst

        for i in List.tail mutationIndexes do
            Assert.True(i >= 2, "a rebase attempt has no preceding reads")
            Assert.True(isRefRead log[i - 2], sprintf "request %d before rebase attempt %d is not a branch-head read: %s" (i - 2) i log[i - 2].Url)
            Assert.Contains("/compare/", log[i - 1].Url)

    /// O3: at the limit the investigation is Save failed because the repository kept changing, the change stays unsaved
    /// in memory, and nothing more is sent until the user asks to save again.
    [<Fact>]
    let ``PERSIST-010-O3`` () =
        let fake, tab = exhausted ()

        match tab.Session.State with
        | SaveState.SaveFailed RepositoryKeptChanging -> ()
        | other -> failwithf "expected SaveFailed RepositoryKeptChanging, got %A" other

        Assert.Equal<Causa.Domain.CommandEnvelope list>([ command 1 ], tab.Session.Unsaved)
        Assert.Empty(tab.Pending)
        let before = requests fake |> List.length
        let again = tab |> input SaveInput.SaveRequested
        Assert.Equal(before, requests fake |> List.length)
        let again = again |> drain fake
        Assert.Equal("Saved", stateName again)
        // The new request is a new save: it may rebase once onto the last writer's commit, within its own limit.
        Assert.InRange(mutations fake |> List.length, 5, 8)

    /// O4 (PAT-08): a branch that advances before every attempt receives no more than four mutations for one save
    /// request, and every other writer's commit stays on the branch.
    [<Fact>]
    let ``PERSIST-010-O4`` () =
        let fake, _ = exhausted ()
        Assert.True((mutations fake |> List.length) <= 4)
        Assert.All(InvariantFake.created fake, fun c -> Assert.True(InvariantFake.reachableFromHead fake c))
