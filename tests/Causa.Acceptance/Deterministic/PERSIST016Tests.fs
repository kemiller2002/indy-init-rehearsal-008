// Owner: persistence. WI-R8-PTESTS: PERSIST-016 bounded automatic network activity per save request (PAT-08).
namespace Causa.Acceptance.Deterministic

open System.Text
open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveHarness

module PERSIST016Tests =
    let private reads (fake: InvariantFake.Handle) =
        requests fake |> List.filter (isMutationRequest >> not) |> List.length

    /// A network that fails every request after sending it.
    let private totalFailure () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        fake.Server.Script(List.replicate 50 (LoseBeforeApplying LostAfterSending))
        fake, drain fake tab

    /// A branch another writer advances before every mutation.
    let private constantAdvance () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested

        fake.Server.Script(
            [ for n in 1..4 do
                  OtherWriterFirst(fake.Branch, [ sprintf "other/advance-%d.txt" n, Encoding.UTF8.GetBytes(string n) ])
                  Deliver
                  Deliver ]
        )

        fake, drain fake tab

    /// O1: automatic network activity for one save request is bounded (it settles with nothing pending).
    [<Fact>]
    let ``PERSIST-016-O1`` () =
        for fake, tab in [ totalFailure (); constantAdvance () ] do
            Assert.Empty(tab.Pending)
            Assert.True((requests fake).Length <= 4 + 4 + 2 * 3, sprintf "%d requests" (requests fake).Length)

    /// O2: commit mutations are limited by PERSIST-010 (at most four).
    [<Fact>]
    let ``PERSIST-016-O2`` () =
        let fake, _ = constantAdvance ()
        Assert.Equal(4, mutations fake |> List.length)

    /// O3: reconciliation of one unknown outcome makes at most three automatic reads, at least 2, 4 and 8 seconds
    /// apart, plus one read per offline-to-online transition; then it waits for Check again.
    [<Fact>]
    let ``PERSIST-016-O3`` () =
        let fake, tab = totalFailure ()
        Assert.Equal<int list>([ 2000; 4000; 8000 ], tab.Delays)
        Assert.Equal(3, reads fake)
        Assert.Empty(tab.Pending)

        match tab.Session.State with
        | SaveState.UnknownOutcome 0 -> ()
        | other -> failwithf "expected UnknownOutcome with no automatic reads left, got %A" other

        // Going online sends exactly one read.
        let online = tab |> input SaveInput.WentOnline
        Assert.Equal(1, online.Pending |> List.filter (function Send r -> not (isMutationRequest r) | _ -> false) |> List.length)
        let online = drain fake online
        Assert.Equal(4, reads fake)
        Assert.Equal<int list>([ 2000; 4000; 8000 ], online.Delays)

        // Check again sends one read when the user chooses it.
        let again = online |> input SaveInput.CheckAgain
        Assert.Equal(1, again.Pending |> List.filter (function Send _ -> true | _ -> false) |> List.length)

    /// O4: a save that fails before it is sent is not retried automatically.
    [<Fact>]
    let ``PERSIST-016-O4`` () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> input SaveInput.SaveRequested
        fake.Server.Script(List.replicate 10 (LoseBeforeApplying FailedBeforeSending))
        let tab = drain fake tab
        Assert.Equal(1, (requests fake).Length)
        Assert.Equal("Offline", stateName tab)
        Assert.Empty(tab.Pending)

    /// O5: under a network that fails every request, one save request causes at most four commit mutations and at
    /// most four reconciliation reads in 60 seconds (the automatic delays total 14 s, all inside the window).
    [<Fact>]
    let ``PERSIST-016-O5`` () =
        let fake, tab = totalFailure ()
        Assert.True(List.sum tab.Delays <= 60000)
        Assert.True((mutations fake).Length <= 4)
        Assert.True(reads fake <= 4)

    /// O6 (PAT-08): request counts under total network failure and under constant branch advance.
    [<Fact>]
    let ``PERSIST-016-O6`` () =
        let fake, _ = totalFailure ()
        Assert.True((mutations fake).Length <= 4 && reads fake <= 4)
        let fake, _ = constantAdvance ()
        Assert.True((mutations fake).Length <= 4)
        // Each rebase costs one head read and one comparison; the confirmed path is never reached.
        Assert.True(reads fake <= 2 * 4)
