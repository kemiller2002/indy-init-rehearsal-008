// Owner: integration. T21 (integration checkpoint 2): PAT-03 stale head rejection, against the stateful fake and live
// against kemiller2002/causa-write-test on a fresh branch of this CI run (live.yml). Without CAUSA_WRITE_TOKEN the
// live half is BLOCKED (fails), never skipped (PF-015, PF-020).
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.Persistence.ErrorMatrix
open Causa.TestSupport

module PERSIST042Tests =
    open ProviderScenario
    open Pat

    type private Scenario =
        { Investigation: InvestigationId
          TabARead: VerifiedInvestigation
          Other: TabSave
          OtherCommit: CommitOid
          TabA: TabSave }

    /// Tab A reads the investigation; another writer (tab B) then saves the same investigation and advances the
    /// branch; tab A saves on the head it read.
    let private scenario (p: ProviderUnderTest) : Scenario =
        let investigation, readByA = createAndOpen p
        let readByB = opened (openAtHead p investigation)
        let other = saveAs (newTab ()) p readByB [ command investigation ]
        let otherCommit = confirmedAt p.Mode other.Session
        let tabA = saveAs (newTab ()) p readByA [ command investigation ]

        { Investigation = investigation
          TabARead = readByA
          Other = other
          OtherCommit = otherCommit
          TabA = tabA }

    let private firstMutation (s: TabSave) =
        match tabMutations s with
        | first :: _ -> first
        | [] -> failwith "tab A sent no mutation"

    /// O1: another writer advances the branch after the tab read it, and GitHub refuses the tab's mutation for the
    /// stale head (the mutation was sent with the head the tab read and was not accepted).
    [<Fact>]
    let ``PERSIST-042-O1`` () =
        onBoth "probe/t21-pat03-o1" (fun p ->
            let s = scenario p
            Assert.NotEqual(s.TabARead.ReadAt, s.OtherCommit)
            let request, answer = firstMutation s.TabA
            Assert.Equal(s.TabARead.ReadAt, expectedHead request)
            Assert.Equal(None, Conformance.commitOid answer)
            Assert.Equal(1, (tabMutations s.TabA).Length)
            record p "PERSIST-042-O1" [ "other-writer", s.OtherCommit; "head-after", head p ])

    /// O2: the refusal is classified as stale (not network, permission, rate limit or validation), the tab shows a
    /// conflict, and nothing of the tab's change was written.
    [<Fact>]
    let ``PERSIST-042-O2`` () =
        onBoth "probe/t21-pat03-o2" (fun p ->
            let s = scenario p
            let _, answer = firstMutation s.TabA
            let c = (classify (Mutation s.TabARead.ReadAt) answer).Class
            Assert.Equal(GhStaleHead, c)

            for notThis in [ GhNetworkBefore; GhNetworkAfter; GhPermission; GhAuth; GhRatePrimary; GhRateSecondary; GhValidation ] do
                Assert.NotEqual(notThis, c)

            match s.TabA.Session.State with
            | SaveState.Conflict _ -> ()
            | other -> failwithf "%A: expected Conflict, got %A" p.Mode other

            // Nothing written: the head is the other writer's commit and holds the other writer's bytes.
            Assert.Equal(s.OtherCommit, head p)
            let atHead = opened (openAtHead p s.Investigation)
            Assert.Equal<byte[]>(s.Other.Written.Events, atHead.Bytes.Events)
            Assert.NotEqual<byte[]>(s.TabA.Written.Events, atHead.Bytes.Events)
            record p "PERSIST-042-O2" [ "other-writer", s.OtherCommit; "head-after", head p ])

    /// O3: each other failure injected at the tab's mutation is classified as itself, never as stale, does not end in
    /// a conflict, and writes nothing (the stateful fake answers with the recorded GitHub forms).
    [<Fact>]
    let ``PERSIST-042-O3`` () =
        let injected =
            [ "GH-AUTH"; "GH-PERMISSION"; "GH-RATE-PRIMARY"; "GH-RATE-SECONDARY"; "GH-VALIDATION" ]
            |> List.map (fun cls ->
                cls,
                AnswerWith(
                    Recordings.all
                    |> List.find (fun r -> r.MatrixClass = cls && (classify (Mutation(CommitOid(String('a', 40)))) (Recordings.asOutcome (RequestId 1L) r)).Class |> classId = cls)
                ))

        let network = [ "GH-NETWORK-BEFORE", LoseBeforeApplying FailedBeforeSending ]

        for (cls, directive) in injected @ network do
            let p, server = scriptableFake ()
            let investigation, read = createAndOpen p
            let before = head p
            server.Script [ directive ]
            let tab = saveAs (newTab ()) p read [ command investigation ]
            let _, answer = firstMutation tab
            let c = (classify (Mutation read.ReadAt) answer).Class
            Assert.Equal(cls, classId c)
            Assert.NotEqual(GhStaleHead, c)

            match tab.Session.State with
            | SaveState.Conflict _
            | SaveState.Saved _ -> failwithf "%s: unexpected state %A" cls tab.Session.State
            | _ -> ()

            Assert.Equal(before, head p)

    /// O4: the PAT-03 scenario passes live on the candidate SHA, with the real commits named in the evidence.
    [<Fact>]
    let ``PERSIST-042-O4`` () =
        let p = live "probe/t21-pat03-o4"
        let s = scenario p

        match s.TabA.Session.State with
        | SaveState.Conflict _ -> ()
        | other -> failwithf "live: expected Conflict, got %A" other

        Assert.Equal(GhStaleHead, (classify (Mutation s.TabARead.ReadAt) (snd (firstMutation s.TabA))).Class)
        Assert.Equal(s.OtherCommit, head p)

        record
            p
            (sprintf "PERSIST-042-O4 source=%s" (Environment.GetEnvironmentVariable "GITHUB_SHA" |> Option.ofObj |> Option.defaultValue "local"))
            [ "tabA-read", s.TabARead.ReadAt; "other-writer", s.OtherCommit; "head-after", head p ]

    /// O5 (PERSIST-008): the stale head is detected from GitHub's typed refusal with the head the tab expected.
    [<Fact>]
    let ``PERSIST-042-O5`` () =
        onBoth "probe/t21-pat03-o5" (fun p ->
            let s = scenario p

            match (classify (Mutation s.TabARead.ReadAt) (snd (firstMutation s.TabA))).Result with
            | Error(StaleHead(expected, _)) -> Assert.Equal(s.TabARead.ReadAt, expected)
            | other -> failwithf "%A: expected StaleHead, got %A" p.Mode other)
