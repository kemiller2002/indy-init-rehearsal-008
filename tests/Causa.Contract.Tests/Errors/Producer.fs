// Owner: persistence. T18: IF41-ERRORS producer tests: the classifier against every recorded answer form.
namespace Causa.Contract.Tests.Errors

open Xunit
open Causa.Persistence
open Causa.Persistence.ErrorMatrix
open Causa.TestSupport

module Producer =

    let private expected = CommitOid(String.replicate 40 "b")

    let private contextOf (r: Recording) =
        match r.Operation with
        | "graphql:createCommitOnBranch" -> Mutation expected
        | "rest:compare" -> Compare expected
        | op -> Read op

    [<Fact>]
    let EveryMatrixRowClassifiesItsRecordedAnswer () =
        Assert.NotEmpty Recordings.all

        for r in Recordings.all do
            let got = classify (contextOf r) (Recordings.asOutcome (RequestId 1L) r)
            Assert.True((classId got.Class = r.MatrixClass), sprintf "%s: expected %s, classified %s" r.Name r.MatrixClass (classId got.Class))

    [<Fact>]
    let MovedHeadFormIsStaleNotPermission () =
        let actual = String.replicate 40 "a"
        let outcome = Answered(RequestId 2L, 200, [ "x-ratelimit-remaining", "4000" ], Recordings.movedHeadBody actual "bbbb000")
        let got = classify (Mutation expected) outcome
        Assert.Equal(GhStaleHead, got.Class)

        match got.Result with
        | Error(StaleHead(CommitOid e, Some(CommitOid a))) ->
            Assert.Equal("bbbb000", e)
            Assert.Equal(actual, a)
        | other -> failwithf "expected StaleHead with both oids, got %A" other

    [<Fact>]
    let SharedStatusesNeedOtherEvidence () =
        let at status headers body = classify (Read "rest:contents") (Answered(RequestId 3L, status, headers, body))
        // 403 has four meanings; the status alone decides none of them.
        Assert.Equal(GhRatePrimary, (at 403 [ "x-ratelimit-remaining", "0" ] """{"message":"API rate limit exceeded"}""").Class)
        Assert.Equal(GhRateSecondary, (at 403 [ "retry-after", "30"; "x-ratelimit-remaining", "10" ] """{"message":"x"}""").Class)
        Assert.Equal(GhRateSecondary, (at 403 [ "x-ratelimit-remaining", "10" ] """{"message":"You have exceeded a secondary rate limit"}""").Class)
        Assert.Equal(GhPermission, (at 403 [ "x-ratelimit-remaining", "10" ] """{"message":"Resource not accessible"}""").Class)
        Assert.Equal(GhLargeFile, (at 403 [] """{"message":"too large","errors":[{"code":"too_large"}]}""").Class)
        // Precedence: primary evidence (remaining 0) wins over a retry-after header.
        Assert.Equal(GhRatePrimary, (at 429 [ "x-ratelimit-remaining", "0"; "retry-after", "5" ] "{}").Class)
        // 404 means three things by context.
        let notFound = Answered(RequestId 4L, 404, [], """{"message":"Not Found"}""")
        Assert.Equal(GhPermission, (classify ConnectRepository notFound).Class)
        Assert.Equal(GhNotFound, (classify (Read "rest:contents") notFound).Class)
        Assert.Equal(GhHistoryRewritten, (classify (Compare expected) notFound).Class)
        // GraphQL answers 200 for success, stale head, permission, validation and unknown errors alike.
        let gql t = Answered(RequestId 5L, 200, [], sprintf """{"errors":[{"type":"%s","message":"m"}]}""" t)
        Assert.Equal(GhStaleHead, (classify (Mutation expected) (gql "STALE_DATA")).Class)
        Assert.Equal(GhPermission, (classify (Mutation expected) (gql "FORBIDDEN")).Class)
        Assert.Equal(GhValidation, (classify (Mutation expected) (gql "UNPROCESSABLE")).Class)
        Assert.Equal(GhUnknownOutcome, (classify (Mutation expected) (gql "SOMETHING_NEW")).Class)
        // 401 is only ever authentication, even with rate-limit headers.
        Assert.Equal(GhAuth, (at 401 [ "x-ratelimit-remaining", "0" ] "{}").Class)

    [<Fact>]
    let NetworkFailureIsNeverStaleOrConflict () =
        Assert.Equal(GhNetworkBefore, (classify (Mutation expected) (NotAnswered(RequestId 6L, FailedBeforeSending))).Class)
        Assert.Equal(GhNetworkAfter, (classify (Mutation expected) (NotAnswered(RequestId 6L, LostAfterSending))).Class)
        Assert.Equal(GhNetworkAfter, (classify (Mutation expected) (NotAnswered(RequestId 6L, TimedOut))).Class)
        Assert.Equal(GhUnknownOutcome, (classify (Mutation expected) (Answered(RequestId 6L, 502, [], "<html/>"))).Class)

    [<Fact>]
    let CompareBeyond250IsTruncatedNotError () =
        let body = """{"status":"ahead","total_commits":300,"commits":[{"sha":"a"}]}"""
        Assert.Equal(GhTruncatedList, (classify (Compare expected) (Answered(RequestId 7L, 200, [], body))).Class)
        let diverged = """{"status":"diverged","total_commits":1,"commits":[{"sha":"a"}]}"""
        Assert.Equal(GhHistoryRewritten, (classify (Compare expected) (Answered(RequestId 7L, 200, [], diverged))).Class)

    /// maxAttempts as github-error-matrix.json v4.1.1 states each class (bounds: rebases 3, reconciliation reads 3,
    /// large-file re-reads 1). No class may be retried beyond this.
    [<Fact>]
    let RetryBoundsMatchTheMatrix () =
        let matrix =
            [ GhAuth, 0; GhPermission, 0; GhRatePrimary, 3; GhRateSecondary, 0; GhStaleHead, 3; GhValidation, 0
              GhNotFound, 0; GhHistoryRewritten, 0; GhNetworkBefore, 0; GhNetworkAfter, 3; GhUnknownOutcome, 3
              GhMalformed, 3; GhReadBackMismatch, 0; GhLargeFile, 1; GhTruncatedList, 3 ]

        for (c, bound) in matrix do
            Assert.Equal(bound, maxAttempts c)

    /// CR-T21-01: GitHub answers `Commit.file(path:)` for a path the commit lacks with a null field and a NOT_FOUND
    /// error. On a read at a found commit that is an absent file, not a refusal; the matrix's GraphQL GH-NOT-FOUND is
    /// a missing branch or ref.
    [<Fact>]
    let MissingPathInAFoundCommitIsAnAbsentFile () =
        Assert.NotEmpty Recordings.readForms

        for r in Recordings.readForms do
            let got = classify (Read r.Operation) (Recordings.asOutcome (RequestId 8L) r)
            Assert.True((classId got.Class = r.MatrixClass), sprintf "%s: expected %s, classified %s" r.Name r.MatrixClass (classId got.Class))

        let oid = String.replicate 40 "c"
        let dir = "investigations/00000000-0000-4000-8000-000000000001"
        let at body = classify (Read "graphql:fileSet") (Answered(RequestId 9L, 200, [], body))
        Assert.Equal(Accepted, (at (Recordings.missingFileSetBody oid dir)).Class)

        // A missing ref stays GH-NOT-FOUND on a read, and a missing path with no commit in the answer is not absorbed.
        Assert.Equal(GhNotFound, (at """{"data":{"repository":{"ref":null}},"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a Ref"}]}""").Class)

        let noCommit =
            """{"data":{"repository":{"ref":{"target":null}}},"errors":[{"type":"NOT_FOUND","path":["repository","ref","target","manifest"],"message":"Could not resolve file for path 'x/manifest.json'."}]}"""

        Assert.Equal(GhNotFound, (at noCommit).Class)

        // The error must name a field that answered null: a missing-path error on a field that holds a file is refused.
        let present =
            sprintf
                """{"data":{"repository":{"ref":{"target":{"oid":"%s","manifest":{"object":{"text":"{}","isTruncated":false}}}}}},"errors":[{"type":"NOT_FOUND","path":["repository","ref","target","manifest"],"message":"Could not resolve file for path 'x/manifest.json'."}]}"""
                oid

        Assert.Equal(GhNotFound, (at present).Class)

        // Any other error alongside keeps its class; a mutation never absorbs the form.
        let mixed =
            sprintf
                """{"data":{"repository":{"ref":{"target":{"oid":"%s","manifest":null}}}},"errors":[{"type":"NOT_FOUND","path":["repository","ref","target","manifest"],"message":"Could not resolve file for path 'x/manifest.json'."},{"type":"FORBIDDEN","message":"no"}]}"""
                oid

        Assert.Equal(GhPermission, (at mixed).Class)
        Assert.Equal(GhNotFound, (classify (Mutation expected) (Answered(RequestId 9L, 200, [], Recordings.missingFileSetBody oid dir))).Class)

    let private investigation = Causa.Domain.InvestigationId(System.Guid "00000000-0000-4000-8000-000000000001")

    let private connection: ConnectionContext =
        { Repository = { Owner = "fake-owner"; Name = "fake-repo"; Branch = "main" }
          Credential = Credential.create "producer-test-token-not-a-credential" }

    /// CR-T21-01: opening an investigation with no files, against the live answer form and against the stateful fake
    /// (which answers a missing path as GitHub does), is AuthorizedEmpty at the head; never OpenRefused.
    [<Fact>]
    let OpenOfANewInvestigationIsAuthorizedEmpty () =
        let oid = String.replicate 40 "c"
        let dir = "investigations/00000000-0000-4000-8000-000000000001"

        match GitHubProvider.interpretOpen StorageV1.codec (Answered(RequestId 10L, 200, [], Recordings.missingFileSetBody oid dir)) with
        | AuthorizedEmpty(CommitOid at) -> Assert.Equal(oid, at)
        | other -> failwithf "live form: expected AuthorizedEmpty, got %A" other

        let server = Conformance.newFake ()
        let head = (StatefulFake.head "main" server.State).Value
        let answer = server.Execute(GitHubProvider.openRequest (RequestId 11L) connection investigation)

        match answer with
        | Answered(_, 200, _, body) -> Assert.Contains("Could not resolve file for path", body)
        | other -> failwithf "the fake must answer the missing paths with GitHub's errors, got %A" other

        Assert.Equal(AuthorizedEmpty head, GitHubProvider.interpretOpen StorageV1.codec answer)

    /// CR-T21-01: partial presence still fails closed: one file present and two missing is Incompatible, naming the
    /// missing files.
    [<Fact>]
    let PartialFileSetIsIncompatible () =
        let (RepoPath manifest) = StoragePaths.manifest investigation
        let files = StorageV1.encodeHistory investigation []

        let server =
            StatefulFake.Server(StatefulFake.create "fake-owner" "fake-repo" "main" [ manifest, files.Manifest ])

        let answer = server.Execute(GitHubProvider.openRequest (RequestId 12L) connection investigation)

        match GitHubProvider.interpretOpen StorageV1.codec answer with
        | Incompatible(_, failures) -> Assert.Equal(2, List.length failures)
        | other -> failwithf "expected Incompatible, got %A" other
