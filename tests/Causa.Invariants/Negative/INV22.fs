// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
// WI-R8-T15 (T35 R-02): driven from T18's recordings (tests/Causa.TestSupport/Recordings.fs) plus the forms they lack.
namespace Causa.Invariants.Negative

open Xunit
open Causa.Persistence
open Causa.Invariants
open Causa.TestSupport

/// INV-22 (PERSIST-008-O2): every recorded GitHub answer form maps to its github-error-matrix.json class, in the
/// matrix's precedence order; in particular a stale head returned as FORBIDDEN is a stale head, never a permission
/// failure, a rate limit (including a 403 secondary-limit message without retry-after, KD-020) is never a missing
/// permission, and a lost answer is a network outcome, never a conflict.
module INV22 =
    let private id = RequestId 1L
    let private oidA = String.replicate 40 "a"
    let private oidB = String.replicate 40 "b"

    /// The request each form answers. The matrix makes some meanings depend on it (404 is PERMISSION during Connect,
    /// HISTORY-REWRITTEN on compare, NOT-FOUND on a read).
    let private contextOf (operation: string) : ErrorMatrix.AnswerContext option =
        match operation with
        | "graphql:createCommitOnBranch" -> Some(ErrorMatrix.Mutation(CommitOid oidB))
        | "rest:repository" -> Some ErrorMatrix.ConnectRepository
        | "rest:compare" -> Some(ErrorMatrix.Compare(CommitOid oidB))
        | "rest:contents" -> Some(ErrorMatrix.Read "contents")
        | "rest:ref" -> Some(ErrorMatrix.Read "ref")
        | "rest:blob" -> Some(ErrorMatrix.Read "blob")
        | _ -> None

    type private Expected =
        | Refused of (ProviderError -> bool)
        /// The matrix says the form is not an error (GH-LARGE-FILE, GH-TRUNCATED-LIST, a successful answer): the frozen
        /// Classifier hands it on as an answer to the request.
        | Answer

    /// The frozen Classifier's outcome each matrix class owes. An unknown class is None, so a recording with a class
    /// this test does not know fails loudly instead of passing.
    let private expectedFor (matrixClass: string) : Expected option =
        match matrixClass with
        | "GH-AUTH" -> Some(Refused(function AuthenticationFailed -> true | _ -> false))
        | "GH-PERMISSION" -> Some(Refused(function PermissionDenied _ -> true | _ -> false))
        | "GH-RATE-PRIMARY" -> Some(Refused(function RateLimited(Primary _) -> true | _ -> false))
        | "GH-RATE-SECONDARY" -> Some(Refused(function RateLimited(Secondary _) -> true | _ -> false))
        | "GH-STALE-HEAD" -> Some(Refused(function StaleHead _ -> true | _ -> false))
        | "GH-VALIDATION" -> Some(Refused(function ValidationFailed _ -> true | _ -> false))
        | "GH-NOT-FOUND" -> Some(Refused(function NotFound _ -> true | _ -> false))
        | "GH-HISTORY-REWRITTEN" -> Some(Refused(function HistoryRewritten _ -> true | _ -> false))
        | "GH-NETWORK-BEFORE" -> Some(Refused(function NetworkUnavailable FailedBeforeSending -> true | _ -> false))
        | "GH-NETWORK-AFTER" -> Some(Refused(function NetworkUnavailable(LostAfterSending | TimedOut) -> true | _ -> false))
        | "GH-UNKNOWN-OUTCOME" -> Some(Refused(function Unrecognised _ -> true | _ -> false))
        | "GH-MALFORMED" -> Some(Refused(function Malformed _ -> true | _ -> false))
        | "GH-LARGE-FILE"
        | "GH-TRUNCATED-LIST"
        | "ACCEPTED" -> Some Answer
        | _ -> None

    type private Form =
        { Name: string
          Class: string
          Operation: string
          Outcome: HttpOutcome }

    let private ofRecording (r: Recording) =
        { Name = "recording " + r.Name
          Class = r.MatrixClass
          Operation = r.Operation
          Outcome = Recordings.asOutcome id r }

    let private answered name cls operation status headers body =
        { Name = name
          Class = cls
          Operation = operation
          Outcome = Answered(id, status, headers, body) }

    let private graphqlError (kind: string) (message: string) =
        sprintf """{"data":null,"errors":[{"type":"%s","message":"%s"}]}""" kind message

    /// Forms the recordings do not hold, restated from the matrix's detection rules (not live captures). Each states
    /// its matrix class; R-02 and R-03 (T35) name the ones that were missing.
    let private matrixForms: Form list =
        [ answered "GH-AUTH wins over rate-limit headers" "GH-AUTH" "rest:contents" 401 [ "x-ratelimit-remaining", "0" ] """{"message":"Bad credentials"}"""
          answered "GH-RATE-PRIMARY 429 remaining 0" "GH-RATE-PRIMARY" "rest:contents" 429 [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1700000000" ] "{}"
          // KD-020: the 403 secondary-limit message observed live without a retry-after header.
          answered "GH-RATE-SECONDARY 403 message without retry-after (KD-020)" "GH-RATE-SECONDARY" "graphql:createCommitOnBranch" 403
              [ "x-ratelimit-remaining", "4000" ] """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}"""
          answered "GH-RATE-SECONDARY 403 'too quickly'" "GH-RATE-SECONDARY" "rest:contents" 403 [] """{"message":"You are creating content too quickly."}"""
          answered "GH-RATE-SECONDARY 403 abuse detection" "GH-RATE-SECONDARY" "rest:contents" 403 [] """{"message":"You have triggered an abuse detection mechanism."}"""
          answered "GH-PERMISSION 404 on the repository during Connect" "GH-PERMISSION" "rest:repository" 404 [ "x-ratelimit-remaining", "4990" ] """{"message":"Not Found"}"""
          answered "GH-HISTORY-REWRITTEN compare 404" "GH-HISTORY-REWRITTEN" "rest:compare" 404 [] """{"message":"Not Found"}"""
          answered "GH-HISTORY-REWRITTEN compare diverged" "GH-HISTORY-REWRITTEN" "rest:compare" 200 [] """{"status":"diverged","ahead_by":1,"behind_by":2,"total_commits":1,"commits":[{}]}"""
          answered "GH-HISTORY-REWRITTEN compare behind" "GH-HISTORY-REWRITTEN" "rest:compare" 200 [] """{"status":"behind","ahead_by":0,"behind_by":2,"total_commits":0,"commits":[]}"""
          answered "GH-TRUNCATED-LIST compare lists fewer commits than it reports" "GH-TRUNCATED-LIST" "rest:compare" 200 []
              (sprintf """{"status":"ahead","ahead_by":300,"behind_by":0,"total_commits":300,"commits":[%s]}""" (String.concat "," (List.replicate 250 "{}")))
          answered "GH-LARGE-FILE 403 too_large" "GH-LARGE-FILE" "rest:contents" 403 [ "x-ratelimit-remaining", "4990" ]
              """{"message":"This API returns blobs up to 1 MB in size.","errors":[{"resource":"Blob","field":"data","code":"too_large"}]}"""
          answered "GH-NOT-FOUND 404 file read" "GH-NOT-FOUND" "rest:contents" 404 [ "x-ratelimit-remaining", "4990" ] """{"message":"Not Found"}"""
          answered "GH-STALE-HEAD STALE_DATA" "GH-STALE-HEAD" "graphql:createCommitOnBranch" 200 [] (graphqlError "STALE_DATA" (sprintf "Expected branch to point to %s but it did not" oidB))
          answered "GH-STALE-HEAD as FORBIDDEN (KD-022)" "GH-STALE-HEAD" "graphql:createCommitOnBranch" 200 [] (graphqlError "FORBIDDEN" (sprintf "is at %s but expected %s" oidA oidB))
          answered "GH-UNKNOWN-OUTCOME unrecognised graphql type" "GH-UNKNOWN-OUTCOME" "graphql:createCommitOnBranch" 200 [] (graphqlError "SOMETHING_NEW" "unexpected")
          answered "GH-MALFORMED 2xx not JSON" "GH-MALFORMED" "graphql:createCommitOnBranch" 200 [] "<html>"
          answered "successful mutation" "ACCEPTED" "graphql:createCommitOnBranch" 200 [] (sprintf """{"data":{"createCommitOnBranch":{"commit":{"oid":"%s"}}}}""" oidA)
          { Name = "GH-NETWORK-BEFORE"; Class = "GH-NETWORK-BEFORE"; Operation = "graphql:createCommitOnBranch"; Outcome = NotAnswered(id, FailedBeforeSending) }
          { Name = "GH-NETWORK-AFTER lost"; Class = "GH-NETWORK-AFTER"; Operation = "graphql:createCommitOnBranch"; Outcome = NotAnswered(id, LostAfterSending) }
          { Name = "GH-NETWORK-AFTER timeout"; Class = "GH-NETWORK-AFTER"; Operation = "rest:contents"; Outcome = NotAnswered(id, TimedOut) } ]

    /// R-03 (T35): a 403 whose message names a primary rate limit while the rate headers are missing or non-zero. The
    /// matrix's GH-PERMISSION row excludes any message naming a rate limit, so it must not be a permission failure;
    /// which rate class (or unknown outcome) it is, is the persistence owner's decision, so any of them passes here.
    let private rateMessageWithoutHeaders: Form list =
        [ answered "403 rate-limit message, no rate headers (R-03)" "R-03" "rest:contents" 403 [] """{"message":"API rate limit exceeded for user ID 1."}"""
          answered "403 rate-limit message, remaining 5 (R-03)" "R-03" "rest:contents" 403 [ "x-ratelimit-remaining", "5" ] """{"message":"API rate limit exceeded for user ID 1."}""" ]

    let private notPermission =
        Refused(function
            | RateLimited _
            | Unrecognised _ -> true
            | _ -> false)

    /// Every form: the recordings first (captured live ones included once the CI probe fills them), then the rest.
    let private forms: (Form * Expected option) list =
        (Recordings.all |> List.map ofRecording |> List.map (fun f -> f, expectedFor f.Class))
        @ (matrixForms |> List.map (fun f -> f, expectedFor f.Class))
        @ (rateMessageWithoutHeaders |> List.map (fun f -> f, Some notPermission))

    /// Forms whose class depends on what was asked (review T35 R-01, R-03, R-06): every 404 meaning per context, a
    /// rate-limit message without the header evidence, a read without content, STALE_DATA outside a mutation.
    let private contextual: (string * ErrorMatrix.AnswerContext * HttpOutcome * Expected) list =
        let notFound = Answered(id, 404, [], """{"message":"Not Found"}""")
        let expected = CommitOid oidA

        [ "GH-PERMISSION 404 on Connect", ErrorMatrix.ConnectRepository, notFound, Refused(function PermissionDenied _ -> true | _ -> false)
          "GH-NOT-FOUND 404 on a read", ErrorMatrix.Read "rest:contents", notFound, Refused(function NotFound _ -> true | _ -> false)
          "GH-HISTORY-REWRITTEN 404 on compare", ErrorMatrix.Compare expected, notFound, Refused(function HistoryRewritten _ -> true | _ -> false)
          "GH-NOT-FOUND 404 on the mutation endpoint", ErrorMatrix.Mutation expected, notFound, Refused(function NotFound _ -> true | _ -> false)
          "GH-RATE 403 rate-limit message without headers (R-03)",
          ErrorMatrix.Read "rest:ref",
          Answered(id, 403, [], """{"message":"API rate limit exceeded for user ID 1."}"""),
          Refused(function RateLimited _ -> true | _ -> false)
          "GH-MALFORMED read without content (R-06)",
          ErrorMatrix.Read "rest:contents",
          Answered(id, 200, [], "{}"),
          Refused(function Malformed _ -> true | _ -> false)
          "STALE_DATA outside a mutation invents no head (R-06)",
          ErrorMatrix.Read "graphql",
          Answered(id, 200, [], graphqlError "STALE_DATA" "stale"),
          Refused(function StaleHead _ -> false | _ -> true) ]

    [<Fact>]
    let RecordedGitHubAnswerForms () =
        // R-01 (T35): the binding takes the request's answer context (T19), so every form is classified in its own context.
        let classifyIn = Require.classifier ()

        Assert.NotEmpty(Recordings.all)

        let wrong =
            (forms
             |> List.choose (fun (form, expected) ->
                match contextOf form.Operation, expected with
                | None, _ -> Some(sprintf "%s: operation '%s' has no answer context in this test" form.Name form.Operation)
                | _, None -> Some(sprintf "%s: matrix class '%s' is not known to this test" form.Name form.Class)
                | Some context, Some expected ->
                    match (classifyIn context) form.Outcome, expected with
                    | Error e, Refused matches when matches e -> None
                    | Ok answer, Answer when answer.Request = id -> None
                    | actual, _ -> Some(sprintf "%s (%s, %A) classified as %A" form.Name form.Class context actual)))
            @ (contextual
               |> List.choose (fun (name, context, outcome, expected) ->
                   match (classifyIn context) outcome, expected with
                   | Error e, Refused matches when matches e -> None
                   | Ok answer, Answer when answer.Request = id -> None
                   | actual, _ -> Some(sprintf "%s classified as %A" name actual)))

        Assert.True(List.isEmpty wrong, String.concat "\n" wrong)
