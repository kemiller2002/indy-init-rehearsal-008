// Owner: persistence. T20: PERSIST-021 reopen from GitHub, on the fake and the live write-test repository.
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.TestSupport

module PERSIST021Tests =
    open ProviderScenario

    /// Two "tabs" save in turn; a third opens. Returns what the new tab opened and the commands each save confirmed.
    let private severalTabs (p: ProviderUnderTest) =
        let investigation = InvestigationId(Guid.NewGuid())
        create p investigation |> ignore
        let a = opened (openAtHead p investigation)
        let sa, _ = save p a [ command investigation ]
        let b = opened (openAtHead p investigation)
        let sb, _ = save p b [ command investigation; command investigation ]
        investigation, sa, sb, opened (openAtHead p investigation)

    /// O1: a new tab loads the latest confirmed commit and verifies it.
    [<Fact>]
    let ``PERSIST-021-O1`` () =
        onBoth "probe/persist021-o1" (fun p ->
            let _, _, sb, reopened = severalTabs p
            Assert.Equal(sb.BaseHead, reopened.ReadAt)
            Assert.True(StorageV1.verify reopened.ReadAt reopened.Bytes |> Result.isOk))

    /// O2: the reopened investigation shows Saved at that commit.
    [<Fact>]
    let ``PERSIST-021-O2`` () =
        onBoth "probe/persist021-o2" (fun p ->
            let _, _, _, reopened = severalTabs p
            let session = SaveProcess.initial reopened

            match session.State with
            | SaveState.Saved(VerifiedAt v) -> Assert.Equal(reopened.ReadAt, v.ReadAt)
            | other -> failwithf "expected Saved, got %A" other)

    /// O3: it contains every confirmed save and nothing that was not confirmed.
    [<Fact>]
    let ``PERSIST-021-O3`` () =
        onBoth "probe/persist021-o3" (fun p ->
            let investigation, _, _, reopened = severalTabs p
            // created (1) + tab A (1) + tab B (2)
            Assert.Equal(4, reopened.History.Length)
            // An edit never saved is not on GitHub.
            let unsaved = command investigation
            let setup = GitHubProvider.saveSetup tab markers (GitHubProvider.fileSetOf StorageV1.codec reopened (decided reopened)) false
            let machine = SaveProcess.create setup
            machine.Step (context p) (machine.Initial reopened) (SaveInput.Edit unsaved) |> ignore
            let again = opened (openAtHead p investigation)
            Assert.Equal(4, again.History.Length)
            Assert.DoesNotContain(again.History, fun e -> e.EventId = EventId unsaved.CommandId))

    /// O4 (PAT-10): reopening after saves from several tabs equals the last confirmed commit's bytes.
    [<Fact>]
    let ``PERSIST-021-O4`` () =
        onBoth "probe/persist021-o4" (fun p ->
            let investigation, sa, sb, reopened = severalTabs p
            Assert.NotEqual(sa.BaseHead, sb.BaseHead)
            let atLast = (readAt p sb.BaseHead investigation).Value
            Assert.Equal<byte[]>(atLast.Events.Value, reopened.Bytes.Events)
            Assert.Equal<byte[]>(atLast.Snapshot.Value, reopened.Bytes.Snapshot))
