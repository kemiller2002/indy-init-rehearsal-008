// Owner: ui (WI-R8-T22). IF41-ERRORS consumer: every ProviderError case has a message a person can act on, and no
// message carries a credential (UI-010, PF-015).
namespace Causa.Contract.Tests.Errors

open Xunit
open Causa.Persistence
open Causa.App

module ConsumerUi =
    let private oid n = CommitOid(sprintf "%040x" n)

    let private everyCase =
        [ AuthenticationFailed
          PermissionDenied "createCommitOnBranch"
          RateLimited(Primary(Some 1700000000L))
          RateLimited(Primary None)
          RateLimited(Secondary(Some 60))
          RateLimited(Secondary None)
          StaleHead(oid 1, Some(oid 2))
          StaleHead(oid 1, None)
          ValidationFailed [ "invalid" ]
          NotFound "branch"
          HistoryRewritten(oid 1)
          NetworkUnavailable FailedBeforeSending
          NetworkUnavailable LostAfterSending
          NetworkUnavailable TimedOut
          Malformed "body"
          ReadBackMismatch(oid 3)
          Unrecognised 502 ]

    [<Fact>]
    let EveryProviderErrorHasAMessage () =
        for error in everyCase do
            let message = ProviderMessage.ofError error
            Assert.False(System.String.IsNullOrWhiteSpace message, sprintf "%A has no message" error)
            Assert.EndsWith(".", message)
            Assert.DoesNotContain("ghp_", message)
            Assert.DoesNotContain("Bearer", message)

        // Distinct causes read differently, so the person can tell them apart.
        let distinct = everyCase |> List.map ProviderMessage.ofError |> List.distinct
        Assert.True(distinct.Length >= 14)
