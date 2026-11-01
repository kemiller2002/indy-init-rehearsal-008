// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-22 (PERSIST-008-O2): every recorded GitHub answer form maps to its github-error-matrix.json class, in the
/// matrix's precedence order; in particular a stale head returned as FORBIDDEN is a stale head, never a permission
/// failure, and a lost answer is a network outcome, never a conflict.
module INV22 =
    let private id = RequestId 1L
    let private oid1 = String.replicate 40 "a"
    let private oid2 = String.replicate 40 "b"

    let private graphqlError (kind: string) (message: string) =
        sprintf """{"data":null,"errors":[{"type":"%s","message":"%s"}]}""" kind message

    type private Expected =
        | Is of (ProviderError -> bool)
        | Answer

    /// (form, outcome, expected class) from the matrix's detection rules and recorded Rehearsal 7 forms.
    let private forms: (string * HttpOutcome * Expected) list =
        [ "GH-AUTH 401", Answered(id, 401, [], """{"message":"Bad credentials"}"""), Is(function AuthenticationFailed -> true | _ -> false)
          "GH-AUTH wins over rate-limit headers",
          Answered(id, 401, [ "x-ratelimit-remaining", "0" ], """{"message":"Bad credentials"}"""),
          Is(function AuthenticationFailed -> true | _ -> false)
          "GH-RATE-PRIMARY 403 remaining 0",
          Answered(id, 403, [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1700000000" ], """{"message":"API rate limit exceeded"}"""),
          Is(function RateLimited(Primary _) -> true | _ -> false)
          "GH-RATE-PRIMARY 429 remaining 0",
          Answered(id, 429, [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1700000000" ], "{}"),
          Is(function RateLimited(Primary _) -> true | _ -> false)
          "GH-RATE-SECONDARY 403 retry-after (Rehearsal 7 form)",
          Answered(id, 403, [ "x-ratelimit-remaining", "4000"; "retry-after", "60" ], """{"message":"You have exceeded a secondary rate limit"}"""),
          Is(function RateLimited(Secondary _) -> true | _ -> false)
          "GH-RATE-SECONDARY graphql RATE_LIMITED",
          Answered(id, 200, [], graphqlError "RATE_LIMITED" "API rate limit exceeded"),
          Is(function RateLimited(Secondary _) -> true | _ -> false)
          "GH-STALE-HEAD STALE_DATA",
          Answered(id, 200, [], graphqlError "STALE_DATA" (sprintf "Expected branch to point to %s but it did not" oid1)),
          Is(function StaleHead _ -> true | _ -> false)
          "GH-STALE-HEAD as FORBIDDEN (KD-022)",
          Answered(id, 200, [], graphqlError "FORBIDDEN" (sprintf "is at %s but expected %s" oid2 oid1)),
          Is(function StaleHead _ -> true | _ -> false)
          "GH-PERMISSION 403 without rate-limit evidence",
          Answered(id, 403, [ "x-ratelimit-remaining", "4000" ], """{"message":"Resource not accessible by personal access token"}"""),
          Is(function PermissionDenied _ -> true | _ -> false)
          "GH-VALIDATION 422", Answered(id, 422, [], """{"message":"Validation Failed"}"""), Is(function ValidationFailed _ -> true | _ -> false)
          "GH-VALIDATION graphql UNPROCESSABLE",
          Answered(id, 200, [], graphqlError "UNPROCESSABLE" "path is invalid"),
          Is(function ValidationFailed _ -> true | _ -> false)
          "GH-NOT-FOUND graphql NOT_FOUND",
          Answered(id, 200, [], graphqlError "NOT_FOUND" "Could not resolve to a Ref"),
          Is(function NotFound _ -> true | _ -> false)
          "GH-UNKNOWN-OUTCOME 502", Answered(id, 502, [], "Bad Gateway"), Is(function Unrecognised 502 -> true | _ -> false)
          "GH-UNKNOWN-OUTCOME unrecognised graphql type",
          Answered(id, 200, [], graphqlError "SOMETHING_NEW" "unexpected"),
          Is(function Unrecognised _ -> true | _ -> false)
          "GH-MALFORMED 2xx not JSON", Answered(id, 200, [], "<html>"), Is(function Malformed _ -> true | _ -> false)
          "GH-NETWORK-BEFORE", NotAnswered(id, FailedBeforeSending), Is(function NetworkUnavailable FailedBeforeSending -> true | _ -> false)
          "GH-NETWORK-AFTER lost", NotAnswered(id, LostAfterSending), Is(function NetworkUnavailable LostAfterSending -> true | _ -> false)
          "GH-NETWORK-AFTER timeout", NotAnswered(id, TimedOut), Is(function NetworkUnavailable TimedOut -> true | _ -> false)
          "successful mutation", Answered(id, 200, [], sprintf """{"data":{"createCommitOnBranch":{"commit":{"oid":"%s"}}}}""" oid2), Answer ]

    /// Forms whose class depends on what was asked (review T35 R-01, R-03, R-06): every 404 meaning per context, a
    /// rate-limit message without the header evidence, a read without content, STALE_DATA outside a mutation.
    let private contextual: (string * ErrorMatrix.AnswerContext * HttpOutcome * Expected) list =
        let notFound = Answered(id, 404, [], """{"message":"Not Found"}""")
        let expected = CommitOid oid1

        [ "GH-PERMISSION 404 on Connect", ErrorMatrix.ConnectRepository, notFound, Is(function PermissionDenied _ -> true | _ -> false)
          "GH-NOT-FOUND 404 on a read", ErrorMatrix.Read "rest:contents", notFound, Is(function NotFound _ -> true | _ -> false)
          "GH-HISTORY-REWRITTEN 404 on compare", ErrorMatrix.Compare expected, notFound, Is(function HistoryRewritten _ -> true | _ -> false)
          "GH-NOT-FOUND 404 on the mutation endpoint", ErrorMatrix.Mutation expected, notFound, Is(function NotFound _ -> true | _ -> false)
          "GH-RATE 403 rate-limit message without headers (R-03)",
          ErrorMatrix.Read "rest:ref",
          Answered(id, 403, [], """{"message":"API rate limit exceeded for user ID 1."}"""),
          Is(function RateLimited _ -> true | _ -> false)
          "GH-MALFORMED read without content (R-06)",
          ErrorMatrix.Read "rest:contents",
          Answered(id, 200, [], "{}"),
          Is(function Malformed _ -> true | _ -> false)
          "STALE_DATA outside a mutation invents no head (R-06)",
          ErrorMatrix.Read "graphql",
          Answered(id, 200, [], graphqlError "STALE_DATA" "stale"),
          Is(function StaleHead _ -> false | _ -> true) ]

    [<Fact>]
    let RecordedGitHubAnswerForms () =
        let classifierFor = Require.classifier ()
        let mutation = classifierFor (ErrorMatrix.Mutation(CommitOid oid1))

        let wrong =
            (forms |> List.map (fun (form, outcome, expected) -> form, mutation, outcome, expected))
            @ (contextual |> List.map (fun (form, context, outcome, expected) -> form, classifierFor context, outcome, expected))
            |> List.choose (fun (form, classify, outcome, expected) ->
                match classify outcome, expected with
                | Error e, Is matches when matches e -> None
                | Ok answer, Answer when answer.Request = id -> None
                | actual, _ -> Some(sprintf "%s classified as %A" form actual))

        Assert.True(List.isEmpty wrong, String.concat "\n" wrong)
