// Owner: persistence. WI-R8-PTESTS: PERSIST-011 a genuine conflict is never rebased, merged or overwritten (PAT-05).
namespace Causa.Acceptance.Deterministic

open Xunit
open Causa.Persistence
open Causa.TestSupport
open SaveHarness

module PERSIST011Tests =
    let private isConflict (tab: Tab) =
        match tab.Session.State with
        | SaveState.Conflict _ -> true
        | _ -> false

    /// Another writer changed this investigation's own files after the tab's base head; the tab then saves.
    let private conflicted () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1
        let other = otherWriterChangesInvestigation fake
        fake, other, tab |> input SaveInput.SaveRequested |> drain fake

    /// O1: when the investigation's files differ between the base head and the new head, it is a genuine conflict.
    [<Fact>]
    let ``PERSIST-011-O1`` () =
        let _, _, tab = conflicted ()
        Assert.True(isConflict tab, sprintf "expected Conflict, got %A" tab.Session.State)

    /// O2: another tab of the same user saving the same investigation is a conflict for this tab.
    [<Fact>]
    let ``PERSIST-011-O2`` () =
        let fake = newFake ()
        let tabA = openTab fake |> edit 1
        let tabB = openTab fake |> edit 2
        let tabA = tabA |> input SaveInput.SaveRequested |> drain fake
        Assert.Equal("Saved", stateName tabA)
        let tabB = tabB |> input SaveInput.SaveRequested |> drain fake
        Assert.True(isConflict tabB, sprintf "expected Conflict, got %A" tabB.Session.State)
        Assert.Equal(tabA.Session.BaseHead, InvariantFake.head fake)

    /// O3: no automatic rebase, merge or overwrite: exactly the one refused mutation was sent, and the branch head is
    /// still the other writer's commit.
    [<Fact>]
    let ``PERSIST-011-O3`` () =
        let fake, other, _ = conflicted ()
        Assert.Equal(1, mutations fake |> List.length)
        Assert.Equal(other, InvariantFake.head fake)

    /// O4: Conflict keeps every unsaved change unchanged in memory, still visible in the tab.
    [<Fact>]
    let ``PERSIST-011-O4`` () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1 |> edit 2
        otherWriterChangesInvestigation fake |> ignore
        let tab = tab |> input SaveInput.SaveRequested |> deliverOne fake |> edit 3 |> drain fake
        Assert.True(isConflict tab)
        Assert.Equal<Causa.Domain.CommandEnvelope list>([ command 1; command 2; command 3 ], tab.Session.Unsaved @ tab.Session.AfterInFlight)

    /// O5: an investigation deleted or rewritten on the branch is also a conflict.
    [<Fact>]
    let ``PERSIST-011-O5`` () =
        let fake = newFake ()
        let tab = openTab fake |> edit 1
        let rewritten = fake.Server.Mutate(StatefulFake.rewriteBranch fake.Branch [ "README.md", System.Text.Encoding.UTF8.GetBytes "rewritten" ])
        let tab = tab |> input SaveInput.SaveRequested |> drain fake
        Assert.True(isConflict tab, sprintf "expected Conflict, got %A" tab.Session.State)
        Assert.Equal(1, mutations fake |> List.length)
        Assert.Equal(rewritten, InvariantFake.head fake)
        Assert.Equal<Causa.Domain.CommandEnvelope list>([ command 1 ], tab.Session.Unsaved)

    /// O6 (PAT-05): the unsaved change is kept, nothing more is sent after the refusal, and the other writer's commit
    /// stays on the branch.
    [<Fact>]
    let ``PERSIST-011-O6`` () =
        let fake, other, tab = conflicted ()
        Assert.Equal<Causa.Domain.CommandEnvelope list>([ command 1 ], tab.Session.Unsaved)
        let log = requests fake
        let mutationAt = log |> List.findIndex isMutationRequest
        Assert.DoesNotContain(log |> List.skip (mutationAt + 1), fun r -> isMutationRequest r)
        Assert.True(InvariantFake.reachableFromHead fake other)
