// Owner: ui (WI-R8-T22). IF41-SAVE consumer: the save-state indicator follows the SM-SAVE state, and only it
// (PERSIST-030-O3/O6, PERSIST-031, PERSIST-032). Every state and cause the machine can hold is projected.
namespace Causa.Contract.Tests.Save

open System
open System.Text.RegularExpressions
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App

module ConsumerUi =
    let private guid (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)
    let private investigation = InvestigationId(guid 1)
    let private oid n = CommitOid(sprintf "%040x" n)

    let private command n : CommandEnvelope =
        { CommandId = guid (5000 + n)
          Investigation = investigation
          ExpectedVersion = None }

    let private verified n : VerifiedInvestigation =
        { Investigation = { Id = investigation; Version = AggregateVersion 1L }
          History = []
          ReadAt = oid n
          Bytes = { Manifest = [||]; Events = [||]; Snapshot = [||] } }

    let private session state : InvestigationSession =
        { Id = investigation
          BaseHead = oid 1
          State = state
          Unsaved = [ command 1 ]
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0 }

    let private conflicts =
        [ ReadingRemote(RequestId 1L)
          Reviewable(verified 9)
          Reapplying(verified 9)
          Reapplied(verified 9, [])
          Reapplied(verified 9, [ { Command = command 2; Reason = "blocked" } ])
          ConfirmingDiscard(verified 9)
          Discarded(verified 9)
          RemoteUnavailable
          Unresolvable "deleted" ]

    let private failures =
        [ SaveFailure.Refused AuthenticationFailed
          SaveFailure.Refused(PermissionDenied "write")
          SaveFailure.Refused(RateLimited(Secondary(Some 60)))
          SaveFailure.Refused(ValidationFailed [ "x" ])
          SaveFailure.Refused(NetworkUnavailable TimedOut)
          RepositoryKeptChanging
          BytesDiffer(oid 3)
          HistoryWasRewritten
          CredentialNotAccepted ]

    let private every: (SaveState * string) list =
        [ SaveState.Saved(VerifiedAt(verified 6)), "Saved"
          SaveState.Unsaved, "Unsaved"
          SaveState.Saving, "Saving"
          SaveState.Offline, "Offline" ]
        @ (conflicts |> List.map (fun c -> SaveState.Conflict c, "Conflict"))
        @ ([ 0..3 ] |> List.map (fun n -> SaveState.UnknownOutcome n, "Unknown commit outcome"))
        @ (failures |> List.map (fun f -> SaveState.SaveFailed f, "Save failed"))

    let private seven =
        set [ "Saved"; "Unsaved"; "Saving"; "Offline"; "Conflict"; "Unknown commit outcome"; "Save failed" ]

    let private claimsSaved = Regex(@"(?<!\bnot |\bnever |\bun)\b(saved|synced|stored|safe)\b", RegexOptions.IgnoreCase)
    let private forbidden = Regex(@"saved locally|on this device|queued|will be saved|saved later|save it later|sync later", RegexOptions.IgnoreCase)

    [<Fact>]
    let IndicatorFollowsMachine () =
        for state, expected in every do
            let view = Projection.value.SaveIndicator(session state)
            // Exactly one of the seven states, named in text and marked with a glyph (PERSIST-030-O3/O4).
            Assert.Equal(expected, view.Label)
            Assert.Contains(view.Label, seven)
            Assert.False(String.IsNullOrWhiteSpace view.Glyph)
            Assert.False(String.IsNullOrWhiteSpace view.Text)
            // Assertive for Conflict, Unknown commit outcome and Save failed; polite otherwise (PERSIST-030-O6).
            let assertive = expected = "Conflict" || expected = "Unknown commit outcome" || expected = "Save failed"
            Assert.Equal((if assertive then Assertive else Polite), view.Politeness)
            // No persistence claim outside Saved; no local, queued or later saving anywhere (PERSIST-032-O1/O2).
            if expected <> "Saved" then
                Assert.False(claimsSaved.IsMatch(view.Label + " " + view.Text), sprintf "%s: %s" expected view.Text)

            Assert.False(forbidden.IsMatch view.Text, view.Text)

            // PERSIST-031 actions and words per state.
            match state with
            | SaveState.Saved _ -> Assert.Empty(view.Actions)
            | SaveState.Unsaved ->
                Assert.Equal<SaveAction list>([ Save ], view.Actions)
                Assert.Contains("only in this tab", view.Text)
            | SaveState.Saving ->
                Assert.Empty(view.Actions)
                Assert.Contains("not yet confirmed", view.Text)
            | SaveState.Offline ->
                Assert.DoesNotContain(Save, view.Actions)
                Assert.Contains("only in this tab", view.Text)
                Assert.Contains("until the connection returns", view.Text)
            | SaveState.Conflict(Reviewable _) ->
                Assert.Contains("nothing of this tab's change was written", view.Text.ToLowerInvariant())
                Assert.Equal<SaveAction list>([ Review; ReapplyMine; DiscardMine; ExportMine ], view.Actions)
            | SaveState.Conflict _ -> Assert.Contains("this tab's changes are kept", view.Text)
            | SaveState.UnknownOutcome n ->
                Assert.DoesNotContain(Save, view.Actions)
                Assert.Contains("may or may not have reached GitHub", view.Text)
                Assert.Equal((n = 0), List.contains CheckAgainAction view.Actions)
            | SaveState.SaveFailed f ->
                Assert.Contains("kept in this tab", view.Text)
                Assert.DoesNotContain(Save, view.Actions)

                match f with
                | CredentialNotAccepted
                | SaveFailure.Refused AuthenticationFailed -> Assert.Equal<SaveAction list>([ Reconnect ], view.Actions)
                | _ -> ()

        // The freshness line carries the commit time only for a confirmed save (PERSIST-031-O3).
        Assert.Equal(None, (Projection.value.SaveIndicator(session (SaveState.Saved(VerifiedAt(verified 2))))).LastSavedCommitTime)

    [<Fact>]
    let HeaderCountsInvestigationsNotSaved () =
        let other = InvestigationId(guid 2)

        let model: Model =
            { Connection = Connected true
              Context = None
              NextRequestId = RequestId 1L
              Network = NetworkState.Online
              Route = Route "#/"
              Loads = Map.empty
              Sessions =
                Map.ofList
                    [ investigation, session SaveState.Unsaved
                      other, { session (SaveState.Saved(VerifiedAt(verified 4))) with Id = other } ] }

        let header = Projection.value.Header model
        Assert.Equal(1, header.NotSaved.Length)
        Assert.Equal(investigation, fst header.NotSaved.Head)
        Assert.StartsWith("#/investigations/00000000-0000-4000-8000-000000000001", snd header.NotSaved.Head)
        Assert.False(header.Offline)
