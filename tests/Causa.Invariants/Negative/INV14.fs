// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open System.Text.RegularExpressions
open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-14 (PERSIST-053-O2, PERSIST-032): the words saved, synced, stored and safe describe an investigation only in
/// Saved, and no copy promises local, queued or later saving. Every message the save-state projection can produce, for
/// every state and cause, is scanned.
module INV14 =
    let private session state unsaved : InvestigationSession =
        { Id = Fixture.investigationId
          BaseHead = Fixture.head 1
          State = state
          Unsaved = unsaved
          AfterInFlight = []
          Outstanding = None
          Mutations = 0
          RebaseAttempts = 0
          ReconcileReads = 0 }

    let private providerErrors =
        [ AuthenticationFailed
          PermissionDenied "createCommitOnBranch"
          RateLimited(Primary(Some 1700000000L))
          RateLimited(Secondary(Some 60))
          StaleHead(Fixture.head 1, Some(Fixture.head 2))
          ValidationFailed [ "invalid" ]
          NotFound "branch"
          HistoryRewritten(Fixture.head 1)
          NetworkUnavailable FailedBeforeSending
          NetworkUnavailable LostAfterSending
          NetworkUnavailable TimedOut
          Malformed "body"
          ReadBackMismatch(Fixture.head 3)
          Unrecognised 502 ]

    let private failures =
        (providerErrors |> List.map Refused)
        @ [ RepositoryKeptChanging; BytesDiffer(Fixture.head 3); HistoryWasRewritten; CredentialNotAccepted ]

    let private conflictViews =
        let remote = Fixture.verifiedAt 9

        [ ReadingRemote(RequestId 1L)
          Reviewable remote
          Reapplying remote
          Reapplied(remote, [ { Command = Fixture.command 2; Reason = "blocked" } ])
          ConfirmingDiscard remote
          Discarded remote
          RemoteUnavailable
          Unresolvable "deleted" ]

    /// Every SM-SAVE state with every cause it can carry.
    let private notSaved: SaveState list =
        [ SaveState.Unsaved; SaveState.Saving; SaveState.Offline ]
        @ (conflictViews |> List.map SaveState.Conflict)
        @ ([ 0..3 ] |> List.map SaveState.UnknownOutcome)
        @ (failures |> List.map SaveState.SaveFailed)

    let private saved =
        [ SaveState.Saved(Confirmed(Fixture.confirmation 5)); SaveState.Saved(VerifiedAt(Fixture.verifiedAt 6)) ]

    let private claimsSaved = Regex(@"(?<!\bnot |\bnever |\bun)\b(saved|synced|stored|safe)\b", RegexOptions.IgnoreCase)

    let private forbiddenAnywhere =
        Regex(@"saved locally|on this device|queued|will be saved|saved later|save it later|sync later", RegexOptions.IgnoreCase)

    [<Fact>]
    let ForbiddenPhrases () =
        let projections = Require.projections ()

        let copy state =
            let view = projections.SaveIndicator(session state [ Fixture.command 1 ])
            state, view.Label + " | " + view.Text

        let violations =
            [ for state, text in notSaved |> List.map copy do
                  if claimsSaved.IsMatch text then
                      yield sprintf "%s: '%s' says saved, synced, stored or safe outside Saved" (Names.save state) text

                  if forbiddenAnywhere.IsMatch text then
                      yield sprintf "%s: '%s' promises local, queued or later saving" (Names.save state) text

                  match state with
                  | SaveState.Offline
                  | SaveState.Unsaved when not (text.ToLowerInvariant().Contains "this tab") ->
                      yield sprintf "%s: '%s' does not say the change exists only in this tab (PERSIST-032-O3)" (Names.save state) text
                  | _ -> ()
              for state, text in saved |> List.map copy do
                  if forbiddenAnywhere.IsMatch text then
                      yield sprintf "%s: '%s' promises local, queued or later saving" (Names.save state) text ]

        Assert.True(List.isEmpty violations, String.concat "\n" violations)
